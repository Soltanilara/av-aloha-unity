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

        // Foveal atlas (docs/PLAN.md section 5). All default to the inert values, so
        // a caller that never sets them gets exactly the pre-foveation behaviour.
        _Foveated ("Atlas carries a foveal band", Float) = 0
        _FoveaRect ("centre.xy, extent.zw in source uv (GL convention)", Vector) = (0.5,0.5,0,0)
        _FoveaFeather ("Foveal blend ring", Range(0.001, 0.5)) = 0.15
        _LayerSpans ("coarse u,v span then fovea u,v span, as canvas fractions", Vector) = (1,0.5,1,0.5)
        _FoveaOutline ("Draw the foveal patch border", Float) = 0
        _FoveaOutlineColor ("Foveal border colour", Color) = (0.35,0.9,0.45,0.85)
        _FoveaOutlineWidth ("Foveal border width, in source uv", Range(0.0005, 0.02)) = 0.003
        _SrgbDecode ("Source holds sRGB-encoded bits in a linear texture", Float) = 0

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

            float _Foveated;
            float4 _FoveaRect;
            float _FoveaFeather;
            float4 _LayerSpans;
            float _FoveaOutline;
            float4 _FoveaOutlineColor;
            float _FoveaOutlineWidth;
            float _SrgbDecode;
            float4 _MainTex_TexelSize;

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

            // Resolve a source-image uv against the transmitted atlas.
            //
            // The atlas is one canvas carrying two bands: the whole field downscaled,
            // and a native-resolution crop centred on gaze. In GL uv terms the coarse
            // band is the upper half (v in [0.5, 1]) because it is the upper half of
            // the image, and the foveal band is the lower half.
            //
            // Each layer sits in the top-left of its band and fills only part of it;
            // the rest is black. Shrinking the coarse layer is how the periphery is
            // made genuinely low-resolution, and it works without ever changing the
            // canvas size -- so the decoder is never reconfigured.
            //
            // _LayerSpans holds each layer's stored size as a fraction of the whole
            // canvas (coarse u,v then fovea u,v), computed from the exact pixel sizes
            // the packet header carries. Exact sizes, not a quantised scale: rounding
            // here makes the sampler read into the black padding beside the layer.
            //
            // Where the display pixel falls inside the crop we cross-fade to it over a
            // soft ring. A hard edge would be far more visible than the resolution
            // difference itself -- the eye finds the seam instantly.
            float3 SampleAtlas(float2 src)
            {
                // Single exit, initialised up front: early returns here make the
                // cross-compiler warn about uninitialised paths, and skipping the
                // foveal fetch per-pixel would trade one fetch for a divergent branch,
                // which is the worse deal on a tiler. _Foveated is a uniform, so the
                // outer branch is coherent across the whole draw.
                float3 outc = float3(0.0, 0.0, 0.0);

                if (_Foveated > 0.5)
                {
                    // Half a texel of inset: bilinear sampling right on a layer's
                    // edge would otherwise pull in the black padding beside it.
                    float2 inset = _MainTex_TexelSize.xy * 0.5;

                    float2 cSpan = _LayerSpans.xy;
                    float2 cuv = float2(src.x * cSpan.x,
                                        (1.0 - cSpan.y) + cSpan.y * src.y);
                    cuv = clamp(cuv,
                                float2(inset.x, 1.0 - cSpan.y + inset.y),
                                float2(cSpan.x - inset.x, 1.0 - inset.y));
                    float3 coarse = tex2D(_MainTex, cuv).rgb;

                    float2 extent = max(_FoveaRect.zw, 1e-5);
                    float2 t = (src - (_FoveaRect.xy - extent * 0.5)) / extent;

                    float f = max(_FoveaFeather, 1e-4);
                    float2 e = smoothstep(0.0, f, t) * smoothstep(0.0, f, 1.0 - t);
                    float w = saturate(e.x * e.y);

                    float2 fSpan = _LayerSpans.zw;
                    float2 ts = saturate(t);
                    float2 fuv = float2(ts.x * fSpan.x,
                                        (0.5 - fSpan.y) + fSpan.y * ts.y);
                    fuv = clamp(fuv,
                                float2(inset.x, 0.5 - fSpan.y + inset.y),
                                float2(fSpan.x - inset.x, 0.5 - inset.y));
                    float3 fovea = tex2D(_MainTex, fuv).rgb;

                    outc = lerp(coarse, fovea, w);

                    // A diagnostic border on the patch. Foveation is meant to be
                    // invisible when it works, which makes it very hard to tell a
                    // correctly blended patch from a fovea rect that is not moving at
                    // all -- both simply look like a picture. The outline answers "is
                    // the crop where I am looking?" directly. Off by default; this is
                    // a tool for tuning, not something to fly with.
                    if (_FoveaOutline > 0.5)
                    {
                        // Border width is given in source uv so it stays a constant
                        // apparent thickness however large the patch is.
                        float2 bw = _FoveaOutlineWidth / extent;
                        float2 d = min(t, 1.0 - t);          // 0 at an edge, 0.5 mid
                        float inside = step(0.0, d.x) * step(0.0, d.y);
                        float core = step(bw.x, d.x) * step(bw.y, d.y);
                        outc = lerp(outc, _FoveaOutlineColor.rgb,
                                    inside * (1.0 - core) * _FoveaOutlineColor.a);
                    }
                }
                else
                {
                    outc = tex2D(_MainTex, src).rgb;
                }
                return outc;
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

                float3 rgb = SampleAtlas(saturate(src));

                // The MediaCodec path blits the decoder's output verbatim into an
                // RGBA8 texture, so the bits are sRGB-encoded but the texture is
                // declared linear. Converting here keeps that decision explicit
                // instead of depending on how a driver treats an external texture's
                // sRGB state. The WebRTC path leaves this at 0 and is unaffected.
                if (_SrgbDecode > 0.5)
                    rgb = GammaToLinearSpace(rgb);

                fixed4 col = fixed4(rgb, 1.0) * i.color;
                col.a *= inside * EdgeMask(i.texcoord);
                return col;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
