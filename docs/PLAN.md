# Guided-Vision v2 — architecture & build plan

Status: proposal, 2026-08-22. Supersedes the transport half of `VISION_PIPELINE.md`;
the *display* half of that document stays valid.

Goal: a Quest teleoperation client that shows a robot's stereo camera one image per
eye at the lowest latency we can reach, with a startup scene for connection/config, a
clean bidirectional robot I/O interface, and optional gaze-driven foveated streaming.

Explicit non-goals of the rewrite: Unity WebRTC, and Firestore signalling. Both go.

---

## 0. What is actually in the repo today (verified)

| | |
|---|---|
| Unity | 6000.5.9f1, Built-in RP, Linear |
| Target | Android / ARM64 / IL2CPP, minSdk 29 |
| **Graphics API** | **OpenGL ES 3 (explicit, auto-API off)** |
| Stereo | Single-pass instanced (multiview) |
| XR | OpenXR + Meta XR SDK 205 (v74); eye-gaze + eye-tracked-foveation features already enabled in `Assets/XR/Settings/OpenXR Package Settings.asset` |
| Scenes | `StartScene` → `PassthroughScene` |
| Transport | `com.unity.webrtc` 3.0.0-pre.7, Firestore REST signalling |
| Code | `WebRTCStreamer.cs` (1369 lines), `StereoCalibration.cs`, two transition scripts |
| Manifest | `supportedDevices = quest|quest2|questpro`, **no eye-tracking permission** |

Two things follow from this that shape everything below.

**GLES3 is a gift — keep it.** The entire difficulty of a native video path on
Android/Unity is getting a decoder's output texture into Unity without a CPU copy. On
GLES3 that is `SurfaceTexture` → `GL_TEXTURE_EXTERNAL_OES` → `Texture2D.CreateExternalTexture`,
a well-trodden path (Oculus's own MediaSurface plugin, `consti10/livevideo10ms`). On
Vulkan it becomes `ImageReader` → `AHardwareBuffer` → external-memory import, which is
several times the work. Do not let anything flip this project to Vulkan mid-build.
(Risk noted in §9.)

**Most of `WebRTCStreamer.cs` is worth keeping.** Only ~250 lines of it are transport.
The rest — per-eye quad layout from `videoVFOV`, `stereoSeparationDeg`, vertical trim,
`edgeFeather`, `outerEdgeMask`, per-eye layer isolation, dynamic-resolution off,
max display frequency, the GPU undistort hookup, the stats HUD — is hard-won tuning.
It gets lifted into a new `StereoVideoDisplay` component, not rewritten.

---

## 1. What XRoboToolkit actually does (and what we take from it)

Worth being precise, since it is the reference point:

* **No WebRTC.** Video is an H.264 elementary stream over plain TCP or UDP, framed by
  their own packet format. The Orin sender uses GStreamer to encode.
* **Hardware decode via JNI.** Unity C# ↔ Android Java bridge, MediaCodec decoding into
  a Surface, GL thread does the OES texture work. That is the `RobotVisionUnityPluginQuest`
  AAR.
* **Split channels.** TCP for control/handshake, UDP at 90 Hz for pose.
* **Manual pairing.** Headset shows a list of service IPs, user picks one, it is stored
  in `PlayerPrefs`. No cloud, no signalling server.
* **Measured latency** (their paper, LAN): ZED Mini → Quest 3 **94.5 ms ± 7.3**, vs
  Open-TeleVision (WebRTC-ish) at 121.5 ms. PICO 4 Ultra hits 82 ms.
* **No foveation.** Their pose channel is JSON at 90 Hz, which is wasteful.

Take: the transport shape (own framing over UDP, MediaCodec+OES, split control/data
channels, manual+discovered pairing). Improve on: binary pose encoding, and foveation,
which is ours.

Do **not** plan on reusing their AAR — the Quest plugin lives in a separate repo with no
stated license, and a binary blob we cannot modify is exactly the trap we are leaving
WebRTC to escape.

---

## 2. Target architecture

```
  ROBOT SIDE (python, uv)                    HEADSET (Unity, Quest Pro/3)
  ┌───────────────────────────┐              ┌──────────────────────────────┐
  │ camera capture            │              │ StereoVideoDisplay           │
  │   ↓                       │              │   ↑ OES texture x2           │
  │ foveal compositor ────────┼── UDP 15552 ─┼─→ GvDecoder (AAR)            │
  │   ↓ (atlas frame)         │   video      │   MediaCodec → SurfaceTexture│
  │ H.264 encoder (x264/NVENC)│              │                              │
  │                           │              │                              │
  │ RobotLink server ─────────┼── TCP 15551 ─┼─→ RobotLink client           │
  │  handlers / pub / sub     │   control    │   Publish/Subscribe/Call     │
  │                           │              │                              │
  │ pose+gaze sink ←──────────┼── UDP 15553 ─┼── input uplink @ 72-90 Hz    │
  │                           │   uplink     │   (head, hands, buttons,gaze)│
  │ beacon ───────────────────┼── UDP 15550 ─┼─→ LAN discovery              │
  └───────────────────────────┘  broadcast   └──────────────────────────────┘
```

Four ports, one process on each side. No browser stack, no SDP, no ICE, no DTLS,
no jitter buffer we do not control.

### 2.1 Why not WebRTC (recording the reasoning)

WebRTC's value is NAT traversal, congestion control, and encryption for the open
internet. Its cost is a jitter buffer tuned for smooth playback rather than low latency,
an opaque pacer, an SDP/signalling dance that forced the Firestore dependency in the
first place, and — on `com.unity.webrtc` specifically — a decode path we do not control.
We are giving up congestion control and encryption. §5 says what replaces them.

---

## 3. Transport: killing Firestore

### 3.1 On a LAN — no signalling at all

The robot broadcasts a small beacon on UDP 15550 every second:

```json
{"gv":1,"name":"aloha-left","host":"192.168.1.42","ports":{"ctl":15551,"vid":15552,"in":15553},
 "cams":[{"id":"stereo0","w":1280,"h":800,"fps":60}],"fovea":true}
```

The StartScene listens for ~2 s and lists whatever it heard. That is a strictly better
UX than the Firestore dropdown, with zero cloud. Manual `host:port` entry and a
saved-hosts list cover everything the beacon misses.

### 3.2 From anywhere — the recommendation is an overlay network, not app code

The question "free, low-latency, works remotely, still direct on LAN" has an answer that
requires **no application code at all**: put both ends on a WireGuard mesh and let the
app keep speaking plain UDP to an IP address.

**Tailscale** (free personal tier, 100 devices) is the strongest fit:

* WireGuard, so encryption comes free — which is what we gave up by dropping DTLS.
* Direct peer-to-peer via UDP hole punching whenever the NATs allow; relays through
  Tailscale's free DERP network only as fallback.
* **On the same LAN it connects directly over the LAN**, so the local case stays local
  and fast. One code path for both.
* The app is unaware. It connects to `100.x.y.z:15552` exactly as it would `192.168.1.42:15552`.

ZeroTier is an equivalent alternative (free to 25 devices).

**Free-tier limits — checked, and not a constraint here.** The Personal plan is free
forever with 6 users and unlimited user devices; there is no data cap, session limit, or
time limit. Structurally there cannot be one on the path we care about: Tailscale
separates the control plane (coordination, which they run) from the data plane (our
packets, which go peer to peer and never touch their servers). They cannot meter a
direct stream because they never see it.

The one rate-limited path is the DERP relay fallback, where Tailscale applies "fair
queuing, overload protection, and rate limiting" and documents DERP as optimised for
availability and coverage rather than raw throughput. Sustained stereo video through
DERP could be throttled — but **being on DERP is already a failure for us**, since
relayed traffic detours through a Tailscale node and adds exactly the latency this whole
design exists to avoid. Treat "we are relayed" as a latency alarm, not a billing one.

If NAT traversal does fail persistently, prefer **Peer Relays** (Tailscale 1.86+) over
DERP: designate a machine on the robot's network as the relay, so it is our hardware and
our bandwidth with no shared-infrastructure throttle, and it is tried before DERP falls
in. Note the relay node cannot be Android, so the Quest cannot serve this role — a Linux
box on the robot side can.

**Unverified assumption, and it is the one blocking item:** Tailscale's Android client
must run on Quest, which requires Quest's OS to permit a sideloaded app's `VpnService`.
I could not confirm this from documentation, and it is worth a 30-minute test before
committing (Phase 0). If it fails, fall back in this order:

1. **Rendezvous + UDP hole punch, built in.** A ~150-line server on a free tier
   (fly.io / Oracle Always Free / a Raspberry Pi at home). Both ends register, learn
   each other's public endpoint, then blast UDP at each other directly. This is ICE's
   useful 10% without the other 90%. Works for most NATs; fails on symmetric NAT.
2. **Relay fallback.** Same free box forwards UDP both directions. Costs one extra hop
   of latency (add roughly the smaller of the two RTTs), but always works.

Both fallbacks slot behind the same interface — the app asks for "a socket to robot X"
and does not care which path produced it. Deferring this to Phase 6 costs nothing.

**Honest expectation for remote operation:** on a good residential connection, direct
P2P adds maybe 10–40 ms RTT over LAN. Teleoperating a robot across a continent will feel
different regardless of protocol; the transport is not the limiting factor there.

---

## 4. Video path

### 4.1 Wire format

Each encoded frame is fragmented to fit MTU (1400-byte payload), with a 32-byte header:

```
u32  magic 'GVID'
u32  frame_id            monotonic, per stream
u8   eye                 0=left 1=right 2=packed-both
u8   flags               bit0 keyframe, bit1 foveated, bit2 last-fragment
u16  fragment_idx
u16  fragment_count
u64  capture_ts_us       sender monotonic clock — this is how latency gets measured
u16  fovea_x, fovea_y    normalised gaze centre used for THIS frame (×65535)
u16  fovea_w, fovea_h    foveal crop extent in full-frame normalised units
```

Receiver policy, which is the entire latency argument: **reassemble, and if any fragment
of a frame is missing when the next frame's first fragment arrives, discard the whole
frame and (if it was a keyframe or we have dropped N in a row) send a keyframe request
on the control channel.** Never retransmit, never wait, never buffer more than one frame.
This is what `livevideo10ms` does and why it hits sub-10 ms decode.

The fovea rect travels in the header rather than as H.264 SEI because MediaCodec does not
hand SEI back to the app. With B-frames disabled (which we require anyway) decode output
order equals input order, so a FIFO of headers matches frames exactly.

### 4.2 Encoder settings (non-negotiable for latency)

`x264`: `preset=ultrafast tune=zerolatency bframes=0 scenecut=0 intra-refresh=1
sliced-threads=1 rc=cbr`. Intra-refresh instead of periodic IDR keyframes avoids the
bitrate spike that shows up as a periodic latency hiccup. On a Jetson/NVENC box, the
equivalent NVENC low-latency preset.

### 4.3 Decode + display on Quest

New Android library module (Kotlin/Java, built to an `.aar` we own):

* `GvDecoder`: MediaCodec `video/avc` configured with `KEY_LOW_LATENCY=1` (API 30+,
  Quest is well past that), output to a `Surface` made from a `SurfaceTexture`.
* Feed it NAL units from the C# receiver via `ByteBuffer` — or better, do the socket
  read in Java too, so no video bytes ever cross into managed memory.
* Tiny C plugin (`GL.IssuePluginEvent`) to call `updateTexImage()` on Unity's render
  thread; calling it from the C# main thread is the classic source of a one-frame stall.

Unity side:

* `Texture2D.CreateExternalTexture(w, h, TextureFormat.RGBA32, false, false, texPtr)`,
  one per eye.
* A shader with `#extension GL_OES_EGL_image_external_essl3 : require` and
  `samplerExternalOES`, plus the existing feather/outer-mask/undistort logic and the
  foveal composite from §5. Guard it so the editor still renders with a normal sampler.

**Latency target: beat XRoboToolkit's 94.5 ms.** We should land near it or slightly
under, since we are dropping their relay indirection. Every phase gets measured, not
guessed — §8.

---

## 5. Foveated streaming

The design that keeps this simple: **one video stream per eye, at a fixed canvas size,
containing both layers packed as an atlas.** One decoder per eye, no inter-stream
synchronisation problem, no MediaCodec reconfigure at runtime.

Per-eye canvas, e.g. 1024×1024, constant for the life of the session:

```
┌────────────────────────────┐
│  COARSE  1024×512          │  entire camera FOV, downscaled
│  (whole field, low detail) │
├────────────────────────────┤
│  FOVEA   1024×512          │  native-resolution crop centred on gaze
│  (crop at 1:1)             │
└────────────────────────────┘
```

The display shader samples COARSE for the whole quad, and where the display pixel falls
inside the fovea rect (from the packet header), cross-fades to FOVEA over a soft ring.
Turning foveation off just means the sender fills the whole canvas with COARSE and clears
the `foveated` flag — the shader falls through to a plain path, and **Quest 2/3 or a
Quest Pro with eye tracking denied works unchanged**. That is the switch you asked for,
and it is one boolean on the sender.

**Each layer is stored at its own size inside its band**, anchored top-left, with the
remainder left black; the exact pixel sizes travel in the packet header so a frame is
self-describing. Shrinking the coarse layer is what actually controls the detail ratio
between periphery and fovea, and it works without ever changing the canvas -- so the
decoder is never reconfigured. Sizes are sent as exact pixels rather than a quantised
scale because rounding makes the sampler read into the black padding beside a layer; an
8-bit scale was off by up to two pixels at 1024 wide, which is a visible dark fringe.

Measured at 1920x1200, 60 fps, H.264: plain full frame 20.0 Mbit/s, foveated at the
defaults (coarse 358x178, fovea 512x256 at 1:1) **6.8 Mbit/s** -- 3x less bandwidth for
a sharper centre.

### 5.1 The latency reality, stated plainly

Gaze → uplink (1–15 ms) → sender re-crops the *next* captured frame (up to one frame
period) → encode (5–10 ms) → network (5–20 ms) → decode (10–15 ms) → display (up to one
refresh, 11–14 ms). **Call it 50–90 ms from eye movement to the fovea following.**

A saccade completes in 30–80 ms, so the fovea *will* lag it. Two things make this work
anyway:

1. **Saccadic suppression** — vision is substantially attenuated for roughly 50–100 ms
   around a saccade, which is most of our budget.
2. **Make the patch generous.** The high-acuity fovea is ~5° but useful detail extends
   to ~15–20°. Size the foveal crop for **~30° of visual angle**, not 5°. The bandwidth
   maths still works out strongly in our favour, and the patch then tolerates the lag.

Additions if it still feels wrong: velocity-extrapolate the gaze by the measured RTT
(the `capture_ts_us` round trip gives us that number for free), and widen the crop
temporarily when gaze velocity is high.

### 5.2 Prerequisites

* Add `<uses-permission android:name="com.oculus.permission.EYE_TRACKING" />` to the
  manifest, and handle runtime denial gracefully (fall back to non-foveated).
* Gaze comes from `OVREyeGaze` / OpenXR `XR_EXT_eye_gaze_interaction` — both already
  enabled in the OpenXR settings.
* Note this is separate from *eye-tracked foveated rendering* (also already enabled),
  which reduces GPU shading cost. Ours reduces *bandwidth*. They compose fine.

---

## 6. Robot I/O interface

The thing you want to be able to reach for without thinking. Symmetric API on both sides.

**Unity:**
```csharp
robot.Publish("gripper/cmd", new GripperCmd { width = 0.04f });
robot.Subscribe<JointState>("arm/state", s => armVisual.Apply(s));
var res = await robot.Call<HomeReq, HomeRes>("arm/home", new HomeReq { arm = "left" });
robot.OnConnectionChanged += state => hud.Show(state);
```

**Python:**
```python
link = RobotLink()

@link.handler("arm/home")
async def home(req): ...

link.subscribe("gripper/cmd", lambda m: gripper.set(m["width"]))
link.publish("arm/state", {"q": q.tolist(), "t": t})
link.run()
```

Wire: length-prefixed msgpack, `{topic, kind, seq}` header. Two lanes on the same
namespace — **reliable** messages go over the TCP control channel, **high-rate**
messages (head/hand/controller poses, gaze, joint state) go over UDP with a sequence
number and last-wins semantics. `Publish` picks the lane from a per-topic registration;
you never think about it.

C# payload types are plain `[Serializable]` structs; Python sees dicts. No IDL, no
codegen — if that becomes a problem later, add schema validation, not a compiler.

The existing `HeadsetData` struct becomes the built-in `headset/input` topic at 72–90 Hz,
binary rather than the current JSON.

---

## 7. Startup scene & configuration

Replace `TransitionPassthroughScene.cs` entirely:

* **Connection page** — discovered robots (§3.1) + saved hosts + manual entry, with a
  live "connecting / handshaking / streaming" state and a real error surface.
* **Config page** — the display tuning knobs that currently hide in `PlayerPrefs` string
  keys: video vFOV, plane distance, scale, stereo separation, vertical trim, edge
  feather, outer mask, HUD distance, calibration file, plus new ones for foveation
  on/off, target bitrate, and resolution.
* **Storage** — a JSON profile in `Application.persistentDataPath`, adb-pushable so you
  can retune without a rebuild, replacing 15 scattered `PlayerPrefs.GetFloat` calls.
  Named profiles per robot.
* **In-headset retune** — keep the existing tuning mode; being able to adjust stereo
  separation while looking at the actual image matters more than any menu.

---

## 8. Python side (`Guided-Vision/python/`, uv)

```
python/
  pyproject.toml            # uv; opencv-python, av, msgpack, numpy
  gvlink/
    __init__.py
    protocol.py             # packet structs, shared with the Unity C# definitions
    video_sender.py         # capture → foveal atlas → PyAV H.264 → UDP fragmenter
    robotlink.py            # the §6 server
    beacon.py               # §3.1 LAN broadcast
    discovery.py
  mock_robot.py             # webcam → duplicated as L/R with "LEFT"/"RIGHT" drawn on,
                            # a fake arm state publisher, a couple of call handlers
  bench_receiver.py         # decodes and reports latency/jitter/loss — no headset needed
  tools/latency_probe.py
```

`bench_receiver.py` matters more than it looks: it lets the whole protocol, the foveal
compositor, and the encoder settings be developed and measured on the Mac before any
Unity or Android code exists. Phase 1 ends with a number for encode+network latency.

`mock_robot.py` also fakes gaze-driven foveation by accepting the uplink gaze packets, so
the foveal path can be exercised from a laptop with the mouse standing in for the eye.

---

## 9. Phases

| # | Work | Est. | Exit criterion |
|---|---|---|---|
| **0** | ✅ *done, except the spike.* Decisions settled (§10); GLES3 confirmed; eye-tracking permission and `quest3` added to the manifest. **Outstanding: does Tailscale run on Quest?** — needs the headset, 30 min. | 0.5 d | Go/no-go on the remote-access route |
| **1** | ✅ *done.* `gvlink` (protocol / foveal / video / stream / pattern), `mock_robot.py`, `bench_receiver.py`, `selftest.py`. No Unity. | 2–3 d | **Met: 660/660 frame pairs at 60 fps, 0 dropped, 0 fragments lost, capture→decode mean 4.7 ms / p95 5.1 ms, ~20 Mbit/s for the pair** (1920×1200 → 1024×1024/eye, loopback). Foveal band measured at 3.6× the high-frequency detail of the upscaled coarse layer. |
| **2** | 🔶 *code complete; desktop-verifiable, on-device pending.* Java decoder plugin (Gradle source, no prebuilt AAR), `libgvnative.so` render-thread bridge, foveal composite in `StereoEyeView`, `GvVideoLink`/`GvVideoSource`/`GvStereoDisplay`, `GvPassthroughScene`. Compiles clean: javac `-Xlint:all`, clang `-Werror`, Unity 0 errors/0 warnings, shader 0 messages, shader↔reference UV parity tested. Plus an Editor video path (`--codec mjpeg` + `GvEditorVideoSource`) so the shader, atlas geometry and stereo layout can be checked on a laptop or in the Meta XR Simulator. **Outstanding: run it on the headset and measure.** | 3–5 d | Webcam visible in-headset, one image per eye. **Measure end-to-end latency and compare to 94.5 ms.** |
| **3** | ✅ *done (LAN).* Verified in the Editor end to end: robot appears in the menu, keyboard-selected, video on screen. LAN beacon (`gvlink/beacon.py`), `GvDiscovery`, `GvConfig` JSON profiles, `GvStartMenu` (built at runtime, stick-or-keyboard navigation), `GvHello` (tells the robot where to send video), `GvStartScene`. Beacon->Unity discovery verified live. **Deletion of `WebRTCStreamer.cs` / `com.unity.webrtc` / Firestore still deferred until the device test** -- there is no proven fallback to delete it in favour of yet. |
| **4** | ✅ *done.* `gvlink/robotlink.py`, `GvRobotLink`, `GvRobotSession`, and a hand-written `GvMsgPack` validated 39/39 against Python's msgpack both ways. Publish/subscribe and request/response verified end to end. Binary uplink (`HeadsetInput`, 158 bytes, v2) carries head, both controllers, buttons and gaze at 90 Hz; every field verified to cross the C#/Python boundary intact, and the gaze projection checked against known angles. The session message replaced the hello datagram, now deleted. |
| **5** | 🔶 *loop closed and desktop-verifiable; on-device tuning pending.* Gaze capture, uplink, sender compositor, shader composite and the on/off toggle are all in place and connected, so the round trip eye → crop → display runs. Plus a mouse gaze source (`simulateGazeWithMouse`) that drives the *same* projection the eyes do, so the whole loop is exercisable in the Editor, and a `patch trails` figure in the HUD that shows eye-to-fovea lag in image units. `SaccadeWidener` implements §5.1's mitigation: while gaze is moving fast the patch covers 2.5× its width and height at unchanged stored size — **measured at 0.40× the bytes**, because a downscaled patch compresses better. **Outstanding: the A/B against a real eye.** | 3–4 d | Foveation on/off comparison at fixed bitrate, with the perceived-quality difference recorded |
| **6** | 🔶 *adaptive bitrate done and tested; remote route documented, unverified.* `gvlink/ratecontrol.py` (AIMD on delay-then-loss), viewer stats published on the existing control channel at 2 Hz, encoder retargeting measured at **0.49 ms** per change. `docs/REMOTE.md` covers Tailscale setup, MTU, and fallbacks; `--tunnel` fixes the 1280-byte WireGuard MTU that would otherwise IP-fragment every datagram. **Outstanding: an actual session over the internet, which needs the Quest `VpnService` spike first.** | 3–4 d | Session over the internet, measured |
| **7** | 🔶 *pointer done, on-device feel pending.* In-headset interaction: `GvPointer` laser from either controller, either tracked hand, or the Editor mouse; hover moves the selection so the three input paths never disagree; minus/plus hit zones so settings are adjustable by pointing; haptics on click; hints that name the device actually in your hands. **Outstanding: how it feels on a head, and whether poke is worth adding.** | 1–2 d | Menu fully drivable by hand and by controller without touching a key |

Phases 1–2 are the risk. Everything after is comparatively mechanical.

**Phase 1 result (2026-08-22).** The software half of the latency budget costs 4.7 ms:
atlas packing, encode, fragmentation, socket, reassembly, decode. Against
XRoboToolkit's measured 94.5 ms LAN total that leaves roughly 90 ms for the real
network, MediaCodec and display — so Phase 2 is where the budget is actually spent, and
the protocol is not the thing to optimise next. Encode is 4.0-4.6 ms per stereo pair on
an M-series Mac; a Jetson with NVENC should beat that comfortably.

Numbers are reproducible with `cd Guided-Vision/python && uv run selftest.py`, and end
to end with `bench_receiver.py` + `mock_robot.py`.

**Unity API on a socket thread, found and fixed 2026-08-22.** `GvDiscovery` stamped
each beacon with `Time.realtimeSinceStartup` on its receive thread. Every UnityEngine
API throws off the main thread, so the first datagram killed the thread before the
counter incremented -- presenting as `datagrams=0, parseFailures=0`, which is
indistinguishable from "no robots on this network" and sent the investigation straight
at the network. Timestamps now come from a static `Stopwatch`, the receive loop can no
longer die silently, and `GvProtocol` was made engine-free on the hot path
(`Mathf.NextPowerOfTwo` is an internal engine call and `GvReassembler.Push` runs on the
socket thread). **Rule for this codebase: nothing under a socket thread touches
UnityEngine except `Debug`.**

**Beacon went out the VPN, found and fixed 2026-08-22.** The beacon derived its address
by asking the routing table how to reach 8.8.8.8, which answers "how do I reach the
internet" -- with a tunnel up that is a `utun` address no one on the LAN can use -- and
broadcast to 255.255.255.255, which follows the same default route. It also never
reached a listener on the same machine, because macOS does not loop 255.255.255.255
back. The beacon now sends to every interface's own subnet broadcast plus 127.0.0.1,
and receivers treat the address a datagram *arrived from* as authoritative over
anything in its payload. Same lesson twice, from both ends: the only address worth
trusting is the one traffic actually came from.

**Coarse-layer downscale cliff, found and fixed 2026-08-22.** `cv2.resize` with
`INTER_AREA` has a fast exact-box path only at integer reduction factors; off it, cost
explodes. Packing the periphery at 358x178 from 1920x1200 cost **11.6 ms per eye**
against 1.5 ms for the same size at an exact 5x -- which halved the sender's frame rate
the moment foveation was turned up. Halving with `pyrDown` until the remaining
reduction is under 2x and finishing with `INTER_AREA` costs 1.3 ms for any source and
any target, and the foveated path is now *faster* than the full-scale one (1.4 ms vs
2.8 ms). `INTER_LINEAR` would have been faster still but aliases hard at these ratios,
and a shimmering periphery is the one artefact foveation cannot afford -- peripheral
motion is exactly what the eye is most sensitive to.

**Bench display bug, found and fixed 2026-08-22.** `bench_receiver.py --display` did
its drawing inline in the receive loop, so `cv2.imshow` + `waitKey(1)` + the atlas
reconstruction ran once per *datagram* rather than once per *frame*. `waitKey(1)`
sleeps a millisecond, capping the loop near a thousand iterations a second against
several thousand datagrams arriving — the socket backed up and reported latency climbed
without bound (0.5 fps, 500 ms and rising). Receiving now runs on its own thread with
drawing capped by `--display-fps`, mirroring the real client's split between the Java
socket thread and the Unity render loop: 30 fps sustained, 7.4 ms, zero loss at
169 Mbit/s. The headless numbers above were never affected — which is exactly why the
bug survived: the display path had not been run.

**Editor run, 2026-08-22.** Beacon -> menu -> selection -> hello -> MJPEG -> foveal
composite -> stereo quads, confirmed working on a laptop with no headset. That
retires most of Phase 2's risk: the wire protocol (all three implementations agree),
the foveal atlas geometry, the display shader, the stereo layout, discovery, profiles
and the menu are all now *observed* working rather than merely compiling. What remains
device-only is exactly one thing: the MediaCodec/OES bridge in `gvnative.c`.

**Phase 2 status (2026-08-22).** Everything that can be verified without a headset is
verified — every layer compiles warning-free, and `selftest.py` checks the display
shader's atlas UV maths against the Python reference implementation by landing sample
points on exact source pixels. What cannot be checked off a device: that
`ASurfaceTexture_attachToGLContext` succeeds on Quest's driver, that the GL state
save/restore leaves Unity's renderer undisturbed, and the actual end-to-end latency.
Bring-up and a troubleshooting table are in `Guided-Vision/native/gvnative/README.md`.

Because MediaCodec is Android-only, the protocol carries a codec byte and the sender
can emit MJPEG instead of H.264 (`--codec mjpeg`). Unity decodes that in C# via
`GvEditorVideoSource`, which shares the wire format, the reassembly policy and the
fovea rect with the device path -- so the display shader, the atlas geometry and the
stereo layout are all checkable in the Editor or the Meta XR Simulator. Only the
MediaCodec/OES bridge itself now requires a headset.
The original `PassthroughScene` and its WebRTC path are untouched, so there is a
working build to fall back to and compare against.

### Ordering note

Phase 2 before Phase 3 is deliberate: keep the working WebRTC build alive as a reference
and a fallback until the new video path is proven in-headset. Delete nothing until there
is something better running next to it.

---

## 10. Risks and open decisions

**Vulkan vs OpenGL ES — asked and answered 2026-08-22.** Meta recommends Vulkan and
treats GLES as legacy-but-supported (no announced cutoff). For this app the trade is
unusually lopsided: the zero-copy decode bridge is a GL concept, and the Vulkan
equivalent is MediaCodec -> ImageReader -> AHardwareBuffer -> `VK_ANDROID_external_memory_android_hardware_buffer`
with a `VkSamplerYcbcrConversion` built from the buffer's queried external format --
several times the native code, and the debug-by-validation-layer kind. Meanwhile the
Vulkan-only wins mostly do not apply: Application SpaceWarp extrapolates 3D content
from motion vectors and a head-locked video quad is the case it helps least; we draw
two quads and are nowhere near CPU- or GPU-bound; eye-tracked foveated *rendering*
cuts shading cost, whereas our foveation cuts bandwidth. **Staying on GLES3.** The
`IGvVideoSource` interface built for the Editor path is exactly the seam a
`GvVulkanVideoSource` would plug into, leaving display, shader, protocol and Python
untouched -- so this is a contained, reversible decision. Wanting passthrough camera
access or ASW would force a deliberate revisit.

**Decisions — settled 2026-08-22:**

1. **Remote access: overlay network (Tailscale).** The app speaks plain UDP to an IP and
   is unaware of how the packet gets there; WireGuard supplies the encryption we gave up
   with DTLS. Gated on the Phase 0 Quest `VpnService` spike — if that fails, fall back to
   the in-app hole-punch + relay described in §3.2, which slots behind the same interface.
2. **Decoder: the real thing — MediaCodec AAR + OES zero-copy.** No MJPEG or CPU-buffer
   stopgap. This is the whole reason for leaving WebRTC and the only route to beating
   94.5 ms. Budget 3-5 days and accept that Phase 2 is where the risk lives.
3. **Camera: the OAK stereo pair.** `StereoCalibration.cs` and
   `tools/export_calibration_for_unity.py` therefore stay directly applicable, and the
   foveal canvas dimensions in §5 get pinned to the OAK's native stereo resolution once
   the exact mode is chosen. The `python/` capture layer still gets a webcam backend for
   `mock_robot.py`.

**Gaze travels as image coordinates, not a direction (2026-08-22).** The headset is the
only end that knows the quad's field of view, so it projects the gaze ray onto the image
itself and sends normalised coordinates. The robot then needs to know nothing about the
display geometry in order to crop for it -- and if the FOV is ever retuned in-headset,
the crop follows automatically instead of silently drifting away from where the operator
is actually looking.

**Fixed binary for the uplink, msgpack for control (2026-08-22).** The uplink is the
90 Hz hot path and its shape -- a head, two controllers, a gaze -- is genuinely stable,
so it is a 158-byte struct that packs and parses with no allocation at either end,
against roughly 400 for the equivalent map (and 459 for the JSON the previous
generation sent at a fifth the rate). Anything outside that shape rides in an optional
msgpack tail, or on the control channel if it must arrive. The offsets bit twice during
development -- each controller contributes thirteen fields, not twelve, because of its
padding byte -- which is why both a Python round-trip and a C#-to-Python byte check are
now in the suite.

**Control channel supersedes the hello datagram (2026-08-22).** Phase 3 shipped a small
UDP hello so the menu could actually start a stream before Phase 4 existed. Phase 4's TCP
control channel does the same job strictly better -- the connection *is* the session, so
the robot learns the address from the socket and stops when it closes, with no keepalive
or staleness window to tune -- so `GvHello.cs` and `gvlink/hello.py` are gone rather than
left as a second way to do the same thing.

**The robot describes its own cameras (2026-08-22).** The viewer used to be told the
field of view by a slider the operator set by eye -- a guess made from inside a headset,
where there is nothing to compare it against. The robot now publishes the rectified
intrinsics on `camera/params` as soon as a viewer connects, and each image is placed so a
pixel appears in the direction its camera ray actually pointed.

Rectification stays on the robot: it owns the calibration, it resamples the frames
anyway, and doing it once at the source beats shipping a distortion model to every
viewer. So no distortion coefficients cross the wire, and a rectified pair needs very
little to describe -- focal length and principal point per eye, image size, baseline.
`rect: false` is carried explicitly so the viewer can *say* the frames are unrectified
rather than silently drawing a subtly wrong picture. The bridge from OpenCV is
`from_projection_matrices(P1, P2)`: those are what `stereoRectify` returns, and the
baseline is `-P2[0,3] / fx`.

The principal point is per eye and generally *not* the image centre even after
rectification, so the picture is shifted sideways from the optical axis. That offset is
now applied. Ignoring it looks perfectly fine in either eye alone and makes the pair
tiring to fuse, which is close to impossible to diagnose by feel.

Baseline mismatch against the operator's own IPD is not fixable without depth, and is
not attempted. What the baseline *is* used for is convergence: shifting each eye by
`b*d/(2*Z)` puts distance `Z` at zero disparity. Note it does not depend on focal
length. Left alone, infinity sits on the screen plane and everything real is nearer than
it, which is precisely the arrangement that causes eye strain.

**One geometry, not two (2026-08-22).** The display placement and the gaze projection
were independent formulas that happened to agree. They no longer can disagree: gaze is
projected onto the quad that was actually placed, whatever placed it. Both directions are
pure static functions on `GvCameraParams`, so the round trip is testable without a scene
-- pixel to camera ray to uv, back to the same pixel, worst case **3.8e-5 px** with
`fx != fy` and both `cx` and `cy` off-centre. The previous arrangement would have let a
change to convergence move the picture without moving the crop, so the fovea would
sharpen a spot the operator was not looking at.

**Two bring-up affordances (2026-08-22).** Both exist because of how the device test is
going to fail.

*Software decode on device.* MediaCodec, the native OES blit and the transport all fail
the same way -- a black screen -- and separating them by staring at one is hopeless. The
C# MJPEG path was already written for the Editor and was gated on `Application.isEditor`
for no reason beyond it being the only caller; it is now selectable on device from the
settings page. Turning it on answers "is this the transport or the decoder bridge" in one
step, and if it works you have a picture to tune the display geometry against while the
decoder is sorted out separately. It is slow and wasteful and says so in the log.

*An outline on the foveal patch.* Foveation is meant to be invisible when it works, which
means a fovea rect frozen at the centre looks exactly like a correctly blended one --
both are just a picture. The border makes "is the crop where I am looking?" a question
you can answer by looking. Drawn in source uv so its apparent thickness does not change
with the patch size. Off by default; it is a tuning tool, not something to fly with.

**Delay before loss, and a clock we do not trust (2026-08-22).** Bitrate adaptation reacts
to queueing delay first and packet loss second, because a queue builds before it
overflows: by the time fragments are missing the viewer has already seen a broken frame.
AIMD, the same shape as TCP's, for the same reason -- multiplicative decrease is what
makes it stable, additive increase is what stops it re-congesting the path immediately.

The delay signal is measured against a rolling *minimum*, never an absolute threshold,
because the absolute number is not a latency at all: the robot stamps frames with its own
monotonic clock and the headset has no idea what that epoch is, so the figure is routinely
large and negative. Subtracting the best sample recently seen cancels the offset and
leaves queueing delay, which is the part worth reacting to. The wire format sends the raw
figure and lets the controller do that subtraction, rather than pretending either end can
measure something it cannot. A test drives it with an offset of -1.234e8 ms for this
reason.

One bug the tests caught: the controller would *raise* the bitrate on a report with no
delay sample, since "no loss" alone read as clean. That is inferring health from silence.
Raising now requires a delay measurement; loss alone can only ever cut.

**MTU is not 1400 over a tunnel (2026-08-22).** WireGuard carries about 1280 bytes and
spends ~60 on its own header. The 1400-byte payload plus our 40-byte header is 1440, so
every datagram over Tailscale would be IP-fragmented -- and losing one IP fragment
destroys the whole datagram, turning 1% packet loss into far more than 1% of frames
broken. `--tunnel` selects 1180. This would have presented on the first remote test as
"the video is unusable over Tailscale" with nothing obviously wrong at either end.

**Own pointer rather than the Interaction SDK (2026-08-22).** The menu is built in code
at runtime, which is what makes the Interaction SDK a poor fit here rather than the
obvious win it usually is: ISDK is designed around inspector wiring -- a `RayInteractable`
and `PlaneSurface` per canvas, a `PointableCanvasModule` on an EventSystem, Unity UI
`Button`s carrying the behaviour. With no canvas at edit time all of that has to be built
from script against an API shaped for the inspector, *and* `GvMenuItem`'s Activate/Adjust
model has to become `Button.onClick`. That is more work and more moving parts than the
plane intersection `GvPointer` does directly.

What ISDK would genuinely add is **poke** -- reaching out and touching the panel -- and
hand-grab for manipulating virtual objects in-session. Neither is needed for a list of
rows. The ray itself it would not improve: `OVRHand.PointerPose` is the platform-computed
UI ray that ISDK's own ray interactor consumes, and `GetFingerIsPinching` is the same
pinch signal. Revisit if in-session object manipulation appears; `GvPointer` is one file
behind a small surface, so the ray source can be swapped without touching the menu.

**One selection, three ways to move it (2026-08-22).** Hover moves the selection rather
than living beside it. A separate hover state and selection state means two highlights and
a question about which one Enter applies to; folding them together means the laser, the
stick and the arrow keys are all just moving one cursor. Whichever device was touched last
owns it -- the pointer claims it only once it has actually moved a few millimetres, so a
laser resting on a row does not fight someone using the stick.

The source is chosen by **who is aiming at the panel**, not by a handedness setting. That
comes out right for left- and right-handed users with nothing to configure, and putting a
controller down and raising a hand simply works. Hysteresis stops two sources aimed at
nearly the same row from trading the beam every frame.

The laser is drawn in `LateUpdate`, not `Update`. The consumer's hit test also runs in
`Update` and the order of two components' `Update` is undefined, so drawing early would
show a stale beam on whichever frames the consumer happened to run first. Same trap that
left the input uplink idle, caught this time by looking for it.

Trigger clicks the pointer; A/X does not. Both would fire on one press -- the hovered row
via the pointer and the selected row via nav -- and "Connect" would load the scene twice.

**Saccade widening: constant pixels, variable coverage (2026-08-22).** §5.1 names two
possible mitigations for eye-to-fovea lag -- velocity-extrapolate the gaze, or widen the
crop when gaze velocity is high. Widening is the one built, because it is the one that
is free. The patch keeps its exact stored pixel count and simply covers more of the
source at lower magnification, so the bitrate does not move; extrapolation, by contrast,
guesses where the eye is going and is wrong in a way that puts the sharp patch somewhere
the user is not looking.

Measured: at 2.5x coverage the patch cost **0.40x** the bytes of the 1:1 crop, not 1.0x
as predicted -- a downscaled patch carries less high-frequency content, so it compresses
better. Widening during a saccade is therefore cheaper than not widening.

The shader needed no change at all, which is the sign the atlas design was right: the
header already describes the covered rect in normalised source coordinates, so the
display has never needed to know the magnification.

Attack is instant, release decays over 120 ms. Being late to widen defeats the purpose;
being late to narrow costs a little sharpness for a moment. The velocity threshold is set
*high* (5 image widths per second) on purpose -- at 90 Hz, 5% tracker jitter between
samples reads as a velocity of 4.5, and a threshold jitter can trip would leave the patch
permanently widened, which spends detail and buys no coverage. The cost is that small
saccades widen only partially, and those are the ones a 30-degree patch most likely
already covers. **All of this is reasoned from saccade timing, not measured against a
real eye** -- `--saccade-zoom 1.0` disables it, which is the control arm for Phase 5's A/B.

**The mouse is a gaze source (2026-08-22, corrected same day).** With no eye tracker in
the Editor, the foveal loop could only ever be tested one end at a time.
`simulateGazeWithMouse` maps the cursor's position across the Game view linearly onto the
source image. Everything downstream is untouched -- uplink, crop, header, shader -- so it
exercises the real loop.

The first attempt routed the cursor through `GvStereoDisplay.DirectionToImageUV`, on the
theory that sharing the real projection was strictly better. **It did not work, and the
reason is worth recording.** Under XR the two ends speak different coordinate systems:
`Input.mousePosition` is in Game-view pixels (measured 1241x608) while an eye camera's
`ScreenPointToRay` expects its own render target (1440x1584, aspect 0.91, 96 deg FOV
against the quad's 55). Screen centre came out as a ray pointing 43 degrees below the
horizon, which pinned the fovea to the bottom edge -- indistinguishable from the feature
simply not being wired up, which is how it was first reported.

The lesson generalises: sharing a code path is only a virtue when both callers share its
preconditions. `DirectionToImageUV` assumes a ray in head space from the video quad's
geometry; a mouse in an XR editor supplies neither. It stays covered by its own
known-angle test and by real gaze rays on device.

**Two more bugs the same report surfaced (2026-08-22).** The uplink read
`GvRobotSession.Profile` once in `Start()`, but that field is only populated when
`GvStereoDisplay.Start()` calls `Connect()` -- and the order of two components' `Start()`
is undefined. Losing that coin flip left the uplink idle for the entire session with a
single log line to show for it. It now resolves the address from `Update()`, which also
lets it pick up a session established later, and falls back to the display's
`fallbackHost` so a directly-played scene works without a second field to keep in sync.

And `OVREyeGaze.EyeTrackingEnabled` reports **true in the Editor** with a fixed
straight-ahead gaze. The mouse was written as a fallback for when no tracker reports, so
it never got a turn. The mouse now takes precedence in the Editor.

All three failures presented identically -- the patch does not follow the pointer -- which
is why the HUD now distinguishes `idle` (no address, nothing sent), `none` (no gaze) and
`eyes` vs `mouse`, and prints the sent count. One glance now separates what previously
needed three hypotheses.

Paired with it, the HUD reports **`patch trails`**: the distance between where gaze
currently points and where the displayed patch is centred. That is the eye-to-fovea
latency expressed in image units, it costs nothing because both numbers were already on
the client, and a saccade shows up as a spike that decays as the crop catches up. It is
the number Phase 5's A/B should be recorded against.

**Latitude granted 2026-08-22:** the existing UI and the old verbose-JSON control
messages are explicitly *not* worth preserving — Phase 3's StartScene gets designed
fresh rather than ported, and Phase 4's wire format stays binary/msgpack with short
keys, never long-named JSON. What still carries over is the *stereo geometry*: quad
sizing from vFOV, separation and vertical trim, edge feather and outer mask, per-eye
layer isolation. That is tuning learned by wearing the thing, and it is independent of
both the transport and the UI.

**Risks:**

* **GLES3 deprecation.** Meta is steering new apps to Vulkan, and Application SpaceWarp
  requires it. Mitigation: put the texture import behind an interface (`IVideoSurface`)
  from day one so a Vulkan/`AHardwareBuffer` backend can be added without touching the
  display or protocol layers. Do not pre-build it.
* **No congestion control.** Dropping WebRTC means a fixed-bitrate stream that will
  behave badly on a congested link. Fine on LAN. Mitigation for Phase 6: a simple
  loss-driven bitrate ladder, driven by the receiver's loss report on the control
  channel — perhaps 100 lines, and we control the policy, which was the point.
* **No encryption** on the raw sockets. Acceptable on a LAN, not acceptable over the
  internet. The Tailscale route resolves this for free; the self-built route needs an
  answer (Noise handshake, or WireGuard on both hosts anyway).
* **Eye-tracking permission denial** must degrade to non-foveated rather than failing.
* **`SurfaceTexture.updateTexImage` on the wrong thread** is the single most likely
  source of a mysterious one-frame stall in Phase 2. Get the render-thread plugin event
  right first, before debugging anything else.

---

## 11. What gets deleted

* `com.unity.webrtc` package dependency
* All Firestore REST code in `WebRTCStreamer.cs` and `TransitionPassthroughScene.cs`,
  and the ProjectID/Password/RobotID `PlayerPrefs` keys
* TURN server config fields in the StartScene
* `webrtc_robot.py`, `webrtc_user.py` at the repo root

## 12. What gets kept

* All stereo layout and comfort maths from `WebRTCStreamer.cs` (§0)
* `StereoCalibration.cs` and `tools/export_calibration_for_unity.py` — GPU undistort is
  orthogonal to transport and still the right call
* The display half of `VISION_PIPELINE.md`, updated to point at the new transport
* The per-eye layer isolation, dynamic-resolution and display-frequency handling
