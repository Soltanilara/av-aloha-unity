// Per-eye video display shader for the Guided-Vision stereo viewer.
//
// Two jobs, both of which exist because a wide-angle stereo pair shown raw is
// uncomfortable to fuse:
//
//  1. Edge treatment. A hard rectangular border is itself a fusion cue, and the
//     OUTER strip of each eye's image shows scene the other eye never saw (the
//     cameras are laterally offset, so the left camera sees extra world on the
//     left and the right camera extra on the right). Those monocular strips have
//     nothing to fuse with and read as flicker/rivalry at the edge of vision.
//     _EdgeFeather softens the whole border; _OuterMask additionally fades the
//     eye's outer strip -- the "floating window" trick from stereo cinema.
//
//  2. Optional calibrated undistort + rectification, done per display pixel on
//     the GPU. When _UndistortMode is non-zero the shader treats the quad as a
//     virtual rectified pinhole camera of half-angles (_HalfTanX, _HalfTanY),
//     turns each display pixel into a ray, rotates it into the physical camera
//     frame with _RectInv, projects it through the calibrated lens model, and
//     samples the RAW frame. That is exactly what cv2.initUndistortRectifyMap
//     bakes into a lookup table, but evaluated at display resolution instead of
//     source resolution -- so the sender can stop resampling entirely and ship
//     raw frames, and the only resampling in the whole path happens once, here.
//
// Conventions: OpenCV camera frame (x right, y down, z forward), OpenCV pixel
// coordinates (row 0 at the top). The received texture is already flipped
// upright by the WebRTC plugin, hence the 1-y at the end.

Shader "GuidedVision/StereoEyeView"
{
    Properties
    {
        [PerRendererData] _MainTex ("Source", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)

        _EdgeFeather ("Edge feather", Range(0, 0.5)) = 0.03
        _OuterMask ("Outer edge mask", Range(0, 0.5)) = 0.0
        _OuterSign ("Outer side: -1 left eye, +1 right eye", Float) = -1

        _UndistortMode ("0 off, 1 pinhole, 2 fisheye", Float) = 0
        _HalfTanX ("tan(HFOV/2)", Float) = 1
        _HalfTanY ("tan(VFOV/2)", Float) = 1
        _Intrinsics ("fx, fy, cx, cy (pixels)", Vector) = (1,1,0,0)
        _Dist ("k1, k2, k3, k4", Vector) = (0,0,0,0)
        _Tangential ("p1, p2", Vector) = (0,0,0,0)
        _SourceSize ("source width, height", Vector) = (1,1,0,0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;

            float _EdgeFeather;
            float _OuterMask;
            float _OuterSign;

            float _UndistortMode;
            float _HalfTanX;
            float _HalfTanY;
            float4 _Intrinsics;
            float4 _Dist;
            float4 _Tangential;
            float4 _SourceSize;
            float4x4 _RectInv;

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            // Display pixel -> source texture uv. Returns uv outside [0,1] for
            // directions the physical camera never saw, which the caller masks.
            float2 SourceUV(float2 uv)
            {
                if (_UndistortMode < 0.5)
                    return uv;

                // Quad uv -> ray in the virtual rectified camera (OpenCV axes,
                // so y flips: uv.y grows upward, OpenCV y grows downward).
                float2 p = (uv - 0.5) * 2.0;
                float3 dir = float3(p.x * _HalfTanX, -p.y * _HalfTanY, 1.0);

                // Rectified frame -> physical camera frame.
                float3 c = mul((float3x3)_RectInv, dir);
                if (c.z <= 1e-6)
                    return float2(-1.0, -1.0);

                float2 xy;
                if (_UndistortMode < 1.5)
                {
                    // Pinhole with OpenCV radial + tangential distortion.
                    float2 a = c.xy / c.z;
                    float r2 = dot(a, a);
                    float radial = 1.0 + _Dist.x * r2 + _Dist.y * r2 * r2 + _Dist.z * r2 * r2 * r2;
                    float p1 = _Tangential.x;
                    float p2 = _Tangential.y;
                    xy = a * radial
                       + float2(2.0 * p1 * a.x * a.y + p2 * (r2 + 2.0 * a.x * a.x),
                                p1 * (r2 + 2.0 * a.y * a.y) + 2.0 * p2 * a.x * a.y);
                }
                else
                {
                    // OpenCV fisheye / equidistant. The right model for the wide
                    // OAK lenses -- a pinhole fit blows the periphery up so far
                    // that the corners cost more pixels than they are worth.
                    float rxy = length(c.xy);
                    float theta = atan2(rxy, c.z);
                    float t2 = theta * theta;
                    float thetaD = theta * (1.0
                        + _Dist.x * t2
                        + _Dist.y * t2 * t2
                        + _Dist.z * t2 * t2 * t2
                        + _Dist.w * t2 * t2 * t2 * t2);
                    xy = (rxy > 1e-6) ? (c.xy / rxy) * thetaD : float2(0.0, 0.0);
                }

                float2 pix = float2(_Intrinsics.x * xy.x + _Intrinsics.z,
                                    _Intrinsics.y * xy.y + _Intrinsics.w);
                float2 src = pix / max(_SourceSize.xy, 1.0);
                src.y = 1.0 - src.y;
                return src;
            }

            float EdgeMask(float2 uv)
            {
                float f = max(_EdgeFeather, 1e-4);
                float2 m = smoothstep(0.0, f, uv) * smoothstep(0.0, f, 1.0 - uv);
                float mask = m.x * m.y;

                if (_OuterMask > 1e-4)
                {
                    // Distance from this eye's OUTER edge: x=0 for the left eye,
                    // x=1 for the right.
                    float t = (_OuterSign < 0.0) ? uv.x : (1.0 - uv.x);
                    mask *= smoothstep(0.0, _OuterMask, t);
                }
                return mask;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 src = SourceUV(i.texcoord);

                // Directions the camera never saw are transparent, not smeared
                // edge pixels.
                float inside = step(0.0, src.x) * step(src.x, 1.0)
                             * step(0.0, src.y) * step(src.y, 1.0);

                fixed4 col = tex2D(_MainTex, saturate(src)) * i.color;
                col.a *= inside * EdgeMask(i.texcoord);
                return col;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
