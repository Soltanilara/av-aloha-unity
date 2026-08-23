# gvnative — decoder-to-Unity texture bridge

The Quest side of the video path. Three pieces:

| | |
|---|---|
| `Assets/Plugins/Android/com/guidedvision/gv/*.java` | Socket, reassembly, MediaCodec. Unity compiles these as Gradle source plugins — no separate AAR project. |
| `native/gvnative/gvnative.c` | Render-thread GL work: attaches the SurfaceTexture, blits it into an ordinary texture. |
| `Assets/Scripts/Video/*.cs` | `GvVideoLink` → two `GvVideoSource` → `GvStereoDisplay`. |

## Build

```bash
./native/gvnative/build.sh
```

Writes `Assets/Plugins/Android/libs/arm64-v8a/libgvnative.so`. Unity infers the ABI
from that directory name. Uses the NDK shipped with the Unity Android module so the
toolchain matches the Editor's, and compiles with `-Wall -Wextra -Werror`.

The Java needs no build step of its own; Unity picks it up at build time. To check it
without a full Unity build:

```bash
U=/Applications/Unity/Hub/Editor/6000.5.9f1/PlaybackEngines/AndroidPlayer
"$U/OpenJDK/bin/javac" --release 11 -Xlint:all \
  -classpath "$U/SDK/platforms/android-34/android.jar" -d /tmp/gvjava \
  Assets/Plugins/Android/com/guidedvision/gv/*.java
```

## Why it is shaped this way

**The OES problem.** MediaCodec decodes into a `SurfaceTexture`, which is a
`GL_TEXTURE_EXTERNAL_OES` texture. Unity cannot sample one from an ordinary shader, and
`updateTexImage()` must run on the thread owning the GL context — under multithreaded
rendering that is Unity's render thread, not the main thread. Calling it anywhere else
is the classic source of a mysterious one-frame stall.

So the native plugin, on the render thread, calls `updateTexImage` and then blits the
external texture into a plain `GL_TEXTURE_2D` that Unity wraps with
`CreateExternalTexture`. Everything Unity-side samples a completely normal `Texture2D`.
No OES shader anywhere in the project, and no dependence on how a given driver treats
an external sampler target. The blit is one textured triangle at video resolution, and
it is also where the SurfaceTexture transform matrix gets applied.

**Why `ASurfaceTexture` and not JNI calls.** The NDK's `ASurfaceTexture_*` API (API 28+;
we target 29) does attach/update in pure C, so the render thread never needs a JNIEnv.

**Colour.** The blit writes the decoder's output verbatim, so the texture holds
sRGB-encoded bits while being declared linear (`CreateExternalTexture(..., linear:true)`).
`StereoEyeView` converts in the shader via `_SrgbDecode`. That keeps the decision
explicit instead of depending on external-texture sRGB bookkeeping.

**One socket, two eyes.** `GvVideoReceiver` owns the socket and demuxes on the header's
eye field, matching `gvlink/protocol.py`. Reassembly happens on the socket thread;
decoding drains on each stream's own thread, so a slow decoder cannot stall the socket
and turn backpressure into UDP loss.

**GL state.** Unity's renderer owns the context. `saveState`/`restoreState` in
gvnative.c put back the framebuffer, viewport, program, VAO, texture bindings, enables
and write masks. Anything missed here shows up somewhere else entirely and looks
nothing like a video bug.

## Bringing it up on device

1. `./native/gvnative/build.sh`
2. Open `Assets/Scenes/GvPassthroughScene.unity` and Build & Run. (The original
   `PassthroughScene` still has the WebRTC path, untouched, as a reference.)
3. On the robot machine: `cd Guided-Vision/python && uv run mock_robot.py --host <headset-ip>`
4. `adb logcat -s GvVideo GvNative Unity`

The in-headset HUD shows per-eye decoded/dropped/fragment-loss counts and whether each
eye is in foveated or plain mode.

### If it does not work

| Symptom | Look at |
|---|---|
| `[no texture]` in the HUD | The render-thread init event never ran. Check `GvNative` logcat for `attachToGLContext failed` or `framebuffer incomplete`. |
| Black quads, HUD counts rising | Decode is fine, display is not — suspect the shader's `_Foveated`/`_FoveaRect` or the quad layout. |
| HUD counts flat at zero | Nothing is arriving. Check the port, and that the sender's `--host` is the headset, not localhost. |
| Both eyes show the same image | Stereo rendering mode is not Multi Pass; per-eye culling masks need per-eye camera passes. The component warns about this at startup. |
| Washed out / too bright | `_SrgbDecode` is off, or the project left Linear colour space. |
| Picture stretched | `sourceWidth`/`sourceHeight` on `GvStereoDisplay` must match the sender's `--src`. The atlas canvas is square; the imagery is not. |
| Stutter with low drop counts | `framesNoInputBuffer` rising means the decoder is behind. Lower the bitrate or the frame rate. |
