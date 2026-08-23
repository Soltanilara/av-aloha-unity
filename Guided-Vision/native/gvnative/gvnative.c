/*
 * gvnative -- SurfaceTexture to Unity texture bridge for Guided-Vision.
 *
 * The problem this solves: MediaCodec decodes into a SurfaceTexture, which is an
 * external (GL_TEXTURE_EXTERNAL_OES) texture. Unity cannot sample one of those from
 * an ordinary shader, and updateTexImage must run on the thread that owns the GL
 * context -- which under multithreaded rendering is Unity's render thread, not the
 * main thread. Calling it anywhere else is the classic source of a mysterious
 * one-frame stall (docs/PLAN.md section 10).
 *
 * So: on the render thread, we updateTexImage and then blit the external texture
 * into an ordinary GL_TEXTURE_2D that Unity owns. Everything Unity-side then samples
 * a completely normal Texture2D -- no OES shader anywhere in the project, no reliance
 * on Unity binding an external sampler target correctly. The blit is one textured
 * triangle at video resolution; it costs almost nothing and it is also where the
 * SurfaceTexture's transform matrix gets applied.
 *
 * Everything here runs through GL.IssuePluginEvent, so no Unity PluginAPI headers are
 * needed -- the event callback is just void(*)(int).
 */

#include <jni.h>
#include <android/log.h>
#include <android/surface_texture.h>
#include <android/surface_texture_jni.h>
#include <GLES3/gl3.h>
#include <stdint.h>
#include <string.h>

#define TAG "GvNative"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO,  TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

/* Declared here rather than pulling in GLES2/gl2ext.h alongside gl3.h. */
#ifndef GL_TEXTURE_EXTERNAL_OES
#define GL_TEXTURE_EXTERNAL_OES 0x8D65
#endif
#ifndef GL_TEXTURE_BINDING_EXTERNAL_OES
#define GL_TEXTURE_BINDING_EXTERNAL_OES 0x8D67
#endif

#define MAX_SLOTS 4

#define ACTION_INIT     1
#define ACTION_UPDATE   2
#define ACTION_SHUTDOWN 3

typedef struct {
    ASurfaceTexture *ast;
    jobject          globalRef;
    GLuint           oesTex, dstTex, fbo;
    int              width, height;
    int              wantInit;      /* set on the main thread, consumed on the render thread */
    int              ready;         /* GL objects live */
    long             updateCount;
    long             updateErrors;
} GvSlot;

static GvSlot   g_slots[MAX_SLOTS];
static JavaVM  *g_vm      = NULL;
static GLuint   g_program = 0;
static GLuint   g_vao     = 0;
static GLint    g_uTransform = -1;
static GLint    g_uTexture   = -1;

/* ------------------------------------------------------------------ GL helpers */

static const char *VS_SRC =
    "#version 300 es\n"
    "uniform mat4 uTransform;\n"
    "out vec2 vUV;\n"
    "void main() {\n"
    /* fullscreen triangle from gl_VertexID -- no vertex buffers to bind or restore */
    "  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));\n"
    "  vUV = (uTransform * vec4(p, 0.0, 1.0)).xy;\n"
    "  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);\n"
    "}\n";

static const char *FS_SRC =
    "#version 300 es\n"
    "#extension GL_OES_EGL_image_external_essl3 : require\n"
    "precision mediump float;\n"
    "uniform samplerExternalOES uTexture;\n"
    "in vec2 vUV;\n"
    "out vec4 fragColor;\n"
    "void main() { fragColor = texture(uTexture, vUV); }\n";

static GLuint compile(GLenum type, const char *src) {
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, NULL);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) {
        char log[1024];
        glGetShaderInfoLog(s, sizeof(log), NULL, log);
        LOGE("shader compile failed: %s", log);
        glDeleteShader(s);
        return 0;
    }
    return s;
}

static int ensureProgram(void) {
    if (g_program) return 1;
    GLuint vs = compile(GL_VERTEX_SHADER, VS_SRC);
    GLuint fs = compile(GL_FRAGMENT_SHADER, FS_SRC);
    if (!vs || !fs) return 0;
    GLuint p = glCreateProgram();
    glAttachShader(p, vs);
    glAttachShader(p, fs);
    glLinkProgram(p);
    GLint ok = 0;
    glGetProgramiv(p, GL_LINK_STATUS, &ok);
    glDeleteShader(vs);
    glDeleteShader(fs);
    if (!ok) {
        char log[1024];
        glGetProgramInfoLog(p, sizeof(log), NULL, log);
        LOGE("program link failed: %s", log);
        glDeleteProgram(p);
        return 0;
    }
    g_program    = p;
    g_uTransform = glGetUniformLocation(p, "uTransform");
    g_uTexture   = glGetUniformLocation(p, "uTexture");
    glGenVertexArrays(1, &g_vao);
    LOGI("blit program ready");
    return 1;
}

/*
 * Unity's renderer owns this context. Anything we change we put back, or the symptom
 * turns up somewhere else entirely and looks nothing like a video bug.
 */
typedef struct {
    GLint fbo, program, vao, activeTex, tex2D, texOES, viewport[4];
    GLboolean depth, blend, cull, scissor, stencil, colorMask[4], depthMask;
} GlState;

static void saveState(GlState *s) {
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &s->fbo);
    glGetIntegerv(GL_CURRENT_PROGRAM, &s->program);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &s->vao);
    glGetIntegerv(GL_ACTIVE_TEXTURE, &s->activeTex);
    glGetIntegerv(GL_VIEWPORT, s->viewport);
    glActiveTexture(GL_TEXTURE0);
    glGetIntegerv(GL_TEXTURE_BINDING_2D, &s->tex2D);
    glGetIntegerv(GL_TEXTURE_BINDING_EXTERNAL_OES, &s->texOES);
    s->depth   = glIsEnabled(GL_DEPTH_TEST);
    s->blend   = glIsEnabled(GL_BLEND);
    s->cull    = glIsEnabled(GL_CULL_FACE);
    s->scissor = glIsEnabled(GL_SCISSOR_TEST);
    s->stencil = glIsEnabled(GL_STENCIL_TEST);
    glGetBooleanv(GL_COLOR_WRITEMASK, s->colorMask);
    glGetBooleanv(GL_DEPTH_WRITEMASK, &s->depthMask);
}

static void restoreState(const GlState *s) {
    glBindFramebuffer(GL_FRAMEBUFFER, (GLuint) s->fbo);
    glUseProgram((GLuint) s->program);
    glBindVertexArray((GLuint) s->vao);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, (GLuint) s->tex2D);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, (GLuint) s->texOES);
    glActiveTexture((GLenum) s->activeTex);
    glViewport(s->viewport[0], s->viewport[1], s->viewport[2], s->viewport[3]);
    if (s->depth)   glEnable(GL_DEPTH_TEST);   else glDisable(GL_DEPTH_TEST);
    if (s->blend)   glEnable(GL_BLEND);        else glDisable(GL_BLEND);
    if (s->cull)    glEnable(GL_CULL_FACE);    else glDisable(GL_CULL_FACE);
    if (s->scissor) glEnable(GL_SCISSOR_TEST); else glDisable(GL_SCISSOR_TEST);
    if (s->stencil) glEnable(GL_STENCIL_TEST); else glDisable(GL_STENCIL_TEST);
    glColorMask(s->colorMask[0], s->colorMask[1], s->colorMask[2], s->colorMask[3]);
    glDepthMask(s->depthMask);
}

/* ------------------------------------------------------- render-thread actions */

static void slotInit(GvSlot *s) {
    if (s->ready || !s->ast) return;
    if (!ensureProgram()) return;

    glGenTextures(1, &s->oesTex);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, s->oesTex);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_EXTERNAL_OES, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, 0);

    if (ASurfaceTexture_attachToGLContext(s->ast, s->oesTex) != 0) {
        LOGE("attachToGLContext failed");
        glDeleteTextures(1, &s->oesTex);
        s->oesTex = 0;
        return;
    }

    glGenTextures(1, &s->dstTex);
    glBindTexture(GL_TEXTURE_2D, s->dstTex);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, s->width, s->height, 0,
                 GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glBindTexture(GL_TEXTURE_2D, 0);

    glGenFramebuffers(1, &s->fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, s->fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0,
                           GL_TEXTURE_2D, s->dstTex, 0);
    GLenum st = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    if (st != GL_FRAMEBUFFER_COMPLETE) {
        LOGE("framebuffer incomplete: 0x%x", st);
        return;
    }

    s->ready = 1;
    LOGI("slot ready: oes=%u dst=%u fbo=%u %dx%d",
         s->oesTex, s->dstTex, s->fbo, s->width, s->height);
}

static void slotUpdate(GvSlot *s) {
    if (!s->ready) return;
    if (ASurfaceTexture_updateTexImage(s->ast) != 0) {
        s->updateErrors++;
        return;
    }
    float mtx[16];
    ASurfaceTexture_getTransformMatrix(s->ast, mtx);

    GlState saved;
    saveState(&saved);

    glBindFramebuffer(GL_FRAMEBUFFER, s->fbo);
    glViewport(0, 0, s->width, s->height);
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_BLEND);
    glDisable(GL_CULL_FACE);
    glDisable(GL_SCISSOR_TEST);
    glDisable(GL_STENCIL_TEST);
    glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
    glDepthMask(GL_FALSE);

    glUseProgram(g_program);
    glBindVertexArray(g_vao);
    glUniformMatrix4fv(g_uTransform, 1, GL_FALSE, mtx);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_EXTERNAL_OES, s->oesTex);
    glUniform1i(g_uTexture, 0);
    glDrawArrays(GL_TRIANGLES, 0, 3);

    restoreState(&saved);
    s->updateCount++;
}

static void slotShutdown(GvSlot *s) {
    if (s->fbo)    { glDeleteFramebuffers(1, &s->fbo); s->fbo = 0; }
    if (s->dstTex) { glDeleteTextures(1, &s->dstTex);  s->dstTex = 0; }
    if (s->ast && s->oesTex) ASurfaceTexture_detachFromGLContext(s->ast);
    if (s->oesTex) { glDeleteTextures(1, &s->oesTex);  s->oesTex = 0; }
    s->ready = 0;
}

static void onRenderEvent(int eventId) {
    int slot   = eventId & 0xFF;
    int action = (eventId >> 8) & 0xFF;
    if (slot < 0 || slot >= MAX_SLOTS) return;
    GvSlot *s = &g_slots[slot];

    switch (action) {
        case ACTION_INIT:     slotInit(s); s->wantInit = 0; break;
        case ACTION_UPDATE:
            if (s->wantInit) { slotInit(s); s->wantInit = 0; }
            slotUpdate(s);
            break;
        case ACTION_SHUTDOWN: slotShutdown(s); break;
        default: break;
    }
}

/* --------------------------------------------------------------------- JNI API */

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    (void) reserved;
    g_vm = vm;
    memset(g_slots, 0, sizeof(g_slots));
    LOGI("loaded");
    return JNI_VERSION_1_6;
}

JNIEXPORT jboolean JNICALL
Java_com_guidedvision_gv_GvNative_register(JNIEnv *env, jclass clazz,
                                           jint slot, jobject surfaceTexture,
                                           jint width, jint height) {
    (void) clazz;
    if (slot < 0 || slot >= MAX_SLOTS || surfaceTexture == NULL) return JNI_FALSE;
    GvSlot *s = &g_slots[slot];
    if (s->ast) return JNI_TRUE;

    s->globalRef = (*env)->NewGlobalRef(env, surfaceTexture);
    s->ast = ASurfaceTexture_fromSurfaceTexture(env, s->globalRef);
    if (!s->ast) {
        LOGE("ASurfaceTexture_fromSurfaceTexture failed");
        (*env)->DeleteGlobalRef(env, s->globalRef);
        s->globalRef = NULL;
        return JNI_FALSE;
    }
    s->width  = width;
    s->height = height;
    /* GL objects can only be made on the render thread; flag it and let the next
       event do the work, so the caller never has to order the two. */
    s->wantInit = 1;
    s->ready = 0;
    s->updateCount = 0;
    s->updateErrors = 0;
    LOGI("registered slot %d %dx%d", slot, width, height);
    return JNI_TRUE;
}

JNIEXPORT void JNICALL
Java_com_guidedvision_gv_GvNative_unregister(JNIEnv *env, jclass clazz, jint slot) {
    (void) clazz;
    if (slot < 0 || slot >= MAX_SLOTS) return;
    GvSlot *s = &g_slots[slot];
    if (s->ast) { ASurfaceTexture_release(s->ast); s->ast = NULL; }
    if (s->globalRef) { (*env)->DeleteGlobalRef(env, s->globalRef); s->globalRef = NULL; }
    s->ready = 0;
    s->wantInit = 0;
}

JNIEXPORT jint JNICALL
Java_com_guidedvision_gv_GvNative_getDstTexture(JNIEnv *env, jclass clazz, jint slot) {
    (void) env; (void) clazz;
    if (slot < 0 || slot >= MAX_SLOTS) return 0;
    return (jint) g_slots[slot].dstTex;
}

JNIEXPORT jlong JNICALL
Java_com_guidedvision_gv_GvNative_getUpdateCount(JNIEnv *env, jclass clazz, jint slot) {
    (void) env; (void) clazz;
    if (slot < 0 || slot >= MAX_SLOTS) return 0;
    return (jlong) g_slots[slot].updateCount;
}

JNIEXPORT jlong JNICALL
Java_com_guidedvision_gv_GvNative_getUpdateErrors(JNIEnv *env, jclass clazz, jint slot) {
    (void) env; (void) clazz;
    if (slot < 0 || slot >= MAX_SLOTS) return 0;
    return (jlong) g_slots[slot].updateErrors;
}

JNIEXPORT jlong JNICALL
Java_com_guidedvision_gv_GvNative_getRenderEventFunc(JNIEnv *env, jclass clazz) {
    (void) env; (void) clazz;
    return (jlong) (intptr_t) &onRenderEvent;
}
