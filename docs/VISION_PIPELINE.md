# Vision pipeline — what the Unity app expects from the camera sender

> **This is the v1 record.** It describes the WebRTC/Firestore app, whose code
> (`WebRTCStreamer.cs`, `StereoCalibration.cs`, the transition scripts) was deleted when
> v2 landed. It is kept because §2 (image contract), §3 (stereo geometry — the maths
> behind "too close" and which knob fixes it) and §13 (choosing parameters
> experimentally) are transport-independent and still correct, and that reasoning is not
> written down anywhere else.
>
> **For anything about how v2 actually works, read [PLAN.md](PLAN.md).** Where a section
> below gives instructions that v2 has since overturned, it says so inline.

This document is the contract between:

* **this repository** — the Unity app that runs on the Meta Quest (the *viewer*), and
* **the separate robot-side application** that drives the OAK stereo camera and sends
  the images (the *sender*; in GIAVA this is
  `interbotix_ws/src/av_aloha/data_collection_scripts/webrtc_headset.py` plus
  `camera_manager.py`).

The Unity app owns *display* only. It never touches the camera. It consumes whatever
the sender produces and puts it in front of the operator's eyes as fast as possible.

---

## 1. Transport contract

| | |
|---|---|
| Transport | WebRTC (`com.unity.webrtc` 3.0.0-pre.7) |
| Signalling | Firestore REST, one document per robot |
| Role | **Sender offers, Unity answers.** Unity never creates the offer. |
| Video tracks | **Exactly two**, added in order: **left eye first, then right eye** |
| Codec | H.264 strongly preferred (Quest decodes it in hardware). VP8 works but is decoded in software. |
| Data channel | One, label `control`, **created by the sender** |
| Encoded frame payload | **Raw codec bitstream, nothing appended** (see §4) |

### Track order is the contract

Unity assigns the first video track it sees to the left eye and the second to the
right. `webrtc_headset.py` adds `left_video_track` before `right_video_track`, which
matches. Unity also records each track's transceiver `mid` so the stats readout can
attribute per-eye numbers correctly, but the *display* assignment is still by arrival
order — **do not reorder `addTrack` calls on the sender.**

### Signalling documents

Firestore path: `projects/{ProjectID}/databases/(default)/documents/{Password}/{RobotID}`

The sender writes:

```json
{ "sdp": "<offer sdp>", "type": "offer" }
```

Unity reads it, answers, and writes back to the same document:

```json
{ "sdp": "<answer sdp>", "type": "answer" }
```

`ProjectID`, `Password` and `RobotID` come from the app's start scene and are stored in
`PlayerPrefs`. The Firestore credentials and this flow are unchanged from the working
implementation — nothing in this work touched them.

### ICE

Unity always configures `stun1/stun2.l.google.com:19302`. If `TurnServerURL`,
`TurnServerUsername` and `TurnServerPassword` are set in the start scene, a TURN server
is added as a third ICE server.

> Previously the TURN entry was built but discarded (`iceServers.Append(...)` with the
> result thrown away), so TURN was never actually used. That is fixed. It matters the
> moment the robot and headset are not on the same LAN.

---

## 2. Image contract

| | Current GIAVA sender | What Unity assumes |
|---|---|---|
| Resolution | 1280 × 800 per eye | any; read from the decoded texture |
| Frame rate | 25 fps | any; frames are displayed as they arrive |
| Pixel format | `bgr24` into aiortc | irrelevant after decode |
| Rectification | done on the sender (`cv2.remap`) | **assumed already done** |
| Geometry | see §3 | |

Unity reads width/height straight off the decoded texture and re-derives the quad's
aspect ratio, so a resolution change on the sender needs no change here.

**Both eyes must be sent at the same resolution and aspect ratio.** They are displayed
on two quads of identical size; different aspect ratios would scale the two eyes
differently and break fusion.

### Rectification is the sender's job

The two views must be row-aligned (only horizontal disparity), which is what makes the
pair fusible. Unity has a `stereoVerticalTrimDeg` trim for residual misalignment, but
it is a correction, not a substitute — it applies a single global vertical shift and
cannot fix rotation or scale differences.

---

## 3. Stereo geometry — where "too close" comes from, and which knob fixes it

Each eye gets its own head-locked quad, parented under that eye's `OVRCameraRig`
anchor, sitting `videoPlaneDistance` metres in front of it.

The quad is **sized so it subtends `videoVFOV` degrees vertically at that distance**:

```
halfHeight = tan(videoVFOV / 2) * videoPlaneDistance * videoScale
```

That has a consequence worth stating plainly, because it explains a symptom that was
reported as a bug:

> **Changing `videoPlaneDistance` cannot change how far away the imagery looks.**
> The quad scales with the distance, so the angular image on the retina is *identical*
> at 0.5 m and at 5 m. Both eyes see exactly the same picture as before. Moving the
> plane is not a stereo control and never was.

Perceived depth in this setup is determined by exactly three things:

**1. The disparity already baked into the two camera images.** Set by the OAK's
physical baseline (~7.5 cm, wider than a typical 6.3 cm IPD, which by itself makes the
world look slightly closer and smaller — "hyperstereo"). Not adjustable from Unity.

**2. Angular magnification — `videoVFOV` (and `videoScale`).** This is also the
single biggest reason the video looks soft while the app's own menus look razor sharp.

Displaying the image wider than the camera actually saw magnifies every angle
*including the disparities*, so the scene appears both bigger and closer — and each
source pixel is smeared over more display pixels.

Do the arithmetic. The scene shipped with `videoVFOV: 105` on a 1280x800 image, so the
horizontal extent is

```
HFOV = 2 * atan( tan(105/2) * 1280/800 ) = 129 degrees
```

1280 source pixels spread over 129 degrees is **9.9 pixels per degree**. A Quest 3
panel resolves roughly **25 ppd**. The video was undersampled by about 2.5x before it
ever reached the display — and with the sender's `EYE_VIEW_SCALE = 0.825` letterboxing,
the real content was only 1056 px, i.e. **8.2 ppd**, a 3x shortfall.

That is why the menus look perfect and the camera feed does not. TextMeshPro glyphs and
UI sprites are rasterised at eye-buffer resolution; the video is a fixed-size texture
being stretched. No amount of display-side sharpening fixes a 3x undersample.

At a correct 55 deg VFOV the same 1280 px cover 80 degrees -> **16 ppd**, a 60%
improvement in angular resolution for free, just by not magnifying.

The default is now **55**.

To get the exact number for your calibration, take the rectified projection matrix
`P1` from `cv2.stereoRectify` and compute:

```python
vfov_deg = 2 * math.degrees(math.atan(height / (2 * P1[1, 1])))
```

**3. Horizontal offset between the two quads — `stereoSeparationDeg`.** This is the
real convergence control (the "horizontal image translation" of stereo cinema).

* **positive** pulls the two images apart → lines of sight become less convergent →
  the whole scene **moves away** from the viewer. This is the fix for "too close".
* **negative** pushes them together → the scene **moves closer**.
* Keep it small. A couple of degrees is a large change. Past a few degrees positive the
  eyes are asked to diverge, which is worse than being too close.

### Why a distance that "worked in one frame" broke in the next

A single global convergence offset can only put **one** depth plane where it belongs.
Everything nearer and everything further scales away from it. So tuning the offset
against a close-up bench scene and then moving the camera somewhere with distant
objects in view will always come apart — that is a property of horizontal image
translation, not a bug.

But the *rate* at which it comes apart is set by the magnification. With
`videoVFOV` at 105 against a ~55 deg camera, every disparity in the image was
roughly doubled, so the depth error grew about twice as fast with distance as it
should have. Getting `videoVFOV` right is what makes depth behave *consistently*
across a scene; `stereoSeparationDeg` then only has to place the whole thing at a
comfortable distance, and it stays placed.

If depth still misbehaves after the FOV is correct, the remaining suspect is the
baseline: the OAK pair is ~7.5 cm apart while a typical IPD is ~6.3 cm, so the world
is rendered about 20% "too small and too close" no matter what. That is hyperstereo
and it cannot be corrected by a 2D offset — it needs depth-aware reprojection (see
§3.4).

### Objects one eye can see and the other cannot

The cameras are laterally offset, so the left camera sees a strip of world on the
left that the right camera misses, and vice versa. Those strips are **monocular** --
there is nothing in the other eye to fuse them with -- and at the edge of vision they
read as flicker and pull the eyes around. Wide-angle lenses make the strips wider.

Two controls handle this:

* `edgeFeather` (default 0.03) softens the whole border. A hard rectangular edge is
  itself a strong depth cue and competes with the imagery behind it.
* `outerEdgeMask` (default 0) additionally fades each eye's **outer** edge — the left
  edge of the left image, the right edge of the right image. This is the "floating
  window" of stereo cinema: it hides the monocular strips so the fusible region is all
  that remains. 0.05–0.12 is typical for a wide pair. Tune it in the headset (hold the
  right index trigger in tuning mode).

Both are implemented in `Assets/Resources/StereoEyeView.shader` as an alpha mask, so
they cost nothing.

### 3.4 Optional: calibrated undistort and rectification on the GPU

`gpuUndistort` moves the lens correction from the sender's `cv2.remap` into the display
shader, evaluated per display pixel.

Why that is better for wide-angle lenses:

* **One resampling instead of two.** Today the sender resamples the frame (remap),
  the encoder resamples again, and Unity stretches the result. With `gpuUndistort` the
  raw frame goes over the wire and is sampled exactly once, at the resolution it is
  actually viewed at.
* **The sender's critical path gets shorter.** Two `cv2.remap` calls per frame come
  out of the capture loop.
* **A rectilinear undistort is the wrong target for a very wide lens.** Straightening
  a 127 deg horizontal view into a pinhole image stretches the periphery enormously —
  the corners consume a huge share of the pixels to show very little. The shader
  supports the OpenCV **fisheye/equidistant** model directly, and renders into whatever
  FOV `videoVFOV` asks for, so you choose how much of the lens to spend pixels on
  instead of taking whatever the remap baked in.
* **`videoVFOV` becomes real.** With undistort on, the quad *is* the virtual camera:
  widening the FOV genuinely reveals more of the frame rather than magnifying a fixed
  crop. Widen until the corners go transparent (directions the lens never saw), then
  back off.

Setting it up:

```bash
# in the GIAVA env, where depthai and the calibration live
python tools/export_calibration_for_unity.py --eeprom --fisheye
adb push oak_stereo_calibration.json \
  /sdcard/Android/data/<package>/files/oak_stereo_calibration.json
```

> **Superseded in v2.** GPU undistort in the viewer was never finished, and v2 decided
> against it: the robot owns the calibration and has to resample anyway, so it rectifies
> and the viewer never sees a distortion model. What the viewer receives instead is the
> *rectified* projection — see `gvlink/camera.py`, whose `CameraParams.from_stereo_rectify`
> takes `P1`/`P2` straight out of `cv2.stereoRectify`. `StereoCalibration.cs` and
> `tools/export_calibration_for_unity.py` are deleted.

It is **off by default** and falls back cleanly: no calibration file, or no shader, and
the display behaves exactly as it does today.

### 3.5 What it would take to go further than a flat pair

Everything above still shows two flat images. The information in the stereo pair is
richer than that, and the ceiling is set by the flat-image model:

* A global convergence offset places one depth correctly; a **per-pixel disparity map**
  would place all of them.
* The ~7.5 cm baseline vs ~6.3 cm IPD mismatch can only be corrected by re-rendering
  the scene from the viewer's actual eye positions.
* Head motion currently does nothing to the imagery (it is head-locked), so there is no
  motion parallax at all.

The real version of "a clearer 3D view" is **depth-based reprojection**: compute
disparity (the OAK can do this on-device via `StereoDepth`, which is the cheap way —
no extra CPU on the host), send depth alongside colour as a second stream or packed
channel, and in Unity render a per-eye mesh or point cloud displaced by that depth
instead of a flat quad. That gives correct disparity everywhere, baseline correction,
and true parallax on small head movements.

That is a significant project — a depth transport format, a disparity-to-mesh shader,
hole filling where depth is missing, and a latency budget for the depth stream that
does not undo the work done here. It is deliberately **not** part of this change, but
nothing in the current architecture blocks it: the transport already carries two
independent tracks, and the display path is now a per-eye material that a displacement
shader could replace.

### Do the stereo comfort adjustment in Unity, not on the sender

`camera_manager.py` currently does the same job on the sender with
`EYE_VIEW_SCALE` (0.825) and `EYE_VIEW_INWARD_FRAC` (0.10): it shrinks each frame onto
a black canvas and shifts it toward the nose.

That works, but it costs real image quality — at scale 0.825 about **32 % of the
transmitted pixels are black border**, and the encoder spends bitrate on it. It also
costs a `cv2.resize` plus a full-frame copy per eye per frame on the sender's critical
path.

Unity's `videoVFOV` / `videoScale` / `stereoSeparationDeg` do the identical thing for
free, as pure quad geometry, with no resolution loss.

**Recommended:** set `GIAVA_EYE_SCALE=1.0` and `GIAVA_EYE_INWARD=0` on the sender and
tune in the headset instead. Note the sign convention is inverted between the two:
sender `inward` positive == Unity `stereoSeparationDeg` negative.

### Tuning it live, in the headset

These values can only be judged while looking at the real scene through the headset, so
the app has a tuning mode:

| Control | Action |
|---|---|
| **Left Menu button** | toggle tuning mode on/off (saves to `PlayerPrefs` on exit) |
| Right thumbstick X | `stereoSeparationDeg` — push the scene away / pull it closer |
| Right thumbstick Y | `videoVFOV` — magnification |
| Left thumbstick Y | `stereoVerticalTrimDeg` — vertical alignment trim |
| Left thumbstick X | `videoPlaneDistance` — where the quad physically sits |
| Hold right index trigger | switch to the edge page: R stick X = `outerEdgeMask`, R stick Y = `edgeFeather` |
| Right **A** | reset to defaults |

**While tuning mode is active, controller state is not forwarded to the robot**, so the
arms hold still while the thumbsticks are being used. A banner says so.

### Per-eye rendering

> **Replaced in v2.** The layer/culling-mask scheme described here needs one camera per
> eye, which needs MultiPass. The project renders **Single Pass Instanced**, and the
> two-camera rig is what made the controllers painful to look at. v2 does the same job in
> the fragment shader: `StereoEyeView` takes an `_EyeIndex` and discards fragments whose
> `unity_StereoEyeIndex` does not match, which works in either stereo mode. Layers 8 and
> 9 are free again. See PLAN.md for the full account.

Each video quad was moved onto its own layer (`LeftEyeOnly` / `RightEyeOnly`, layers 8
and 9) and culled from the other eye's camera.

Both `OVRCameraRig` eye cameras ship with a culling mask of "everything", so without
this **each eye also draws the other eye's quad**, offset by the IPD — a ghosted band
at the lateral edges and twice the canvas overdraw for no benefit.

---

## 4. The 4-byte frame metadata — read this before re-enabling it

The Unity app supports a sender that appends a fixed-size big-endian metadata blob to
every **encoded** video frame (added in Oct 2024 for eye-tracking/crosshair sync). When
`metadataLength > 0`, Unity installs an `RTCRtpScriptTransform` on each receiver that
strips that many bytes off every frame before handing it to the decoder, and reports
the recovered `uint32` back over the data channel as `LeftTimestamp` / `RightTimestamp`.

**The current GIAVA sender does not append anything.** `webrtc_headset.py` sends plain
H.264. But the scene still had `metadataLength: 4`, so Unity was **truncating four
bytes off every single encoded frame** — corrupting the bitstream on every frame,
which shows up as macroblock garbage, smearing, and "pixels dropping out".

`metadataLength` is now **0** by default, in both the code and the scene.

Only set it back to 4 if the sender genuinely appends four big-endian bytes to each
encoded frame. If you do, the two sides must agree exactly — there is no negotiation
and no way for Unity to detect a mismatch.

---

## 5. Data channel

Label `control`, created by the sender. Both directions are JSON, UTF-8.

**Unity → sender**, at `DataSendFrequency` Hz (default 20), `JsonUtility.ToJson` of
`HeadsetData`: head/controller poses (`HPosition`/`HRotation`, `L*`, `R*`), thumbsticks,
triggers, buttons, per-eye gaze pixel (`LEyePixel` / `REyePixel`), and
`LeftTimestamp`/`RightTimestamp` (0 unless frame metadata is enabled).

Gaze pixels are in **image pixel coordinates of the received frame**, origin top-left,
computed by intersecting the eye's gaze ray with that eye's quad. They account for
`videoVFOV`, `videoScale` and the stereo shift, so they stay correct while tuning.

**Sender → Unity**, JSON object with:

```
headOutOfSync, leftOutOfSync, rightOutOfSync   bool
info                                            string   -> shown in the HUD
leftArmPosition, rightArmPosition               [x,y,z]
leftArmRotation, rightArmRotation               [x,y,z,w]
```

Extra keys are ignored (the sender also sends `middleArm*`; Unity does not use it).
Positions/rotations are in Unity world space and are used to draw the ghost arm
visuals when an arm is out of sync.

---

## 6. Receive → display path in Unity

```
sender ──RTP──► libwebrtc (native, own threads)
                     │
                     │  [script transform, worker thread, only if
                     │   metadataLength > 0 or enableFrameStats]
                     │     • read metadata bytes (no alloc, no logging)
                     │     • stamp arrival time
                     ▼
                  decoder (hardware H.264 on Quest)
                     │
                     │  single "latest frame" slot per track — a new frame
                     │  OVERWRITES the previous one, it is never queued
                     ▼
       WebRTC.Update() coroutine, WaitForEndOfFrame
       uploads that slot into the track's Texture2D
                     │
                     ▼
       RawImage.texture ── points DIRECTLY at that Texture2D
                     │
                     ▼
       world-space Canvas quad, one per eye, per-eye layer
```

### Why no frames pile up

There is no queue anywhere in the Unity side. The plugin holds exactly one decoded
frame per track and overwrites it; `WebRTC.Update()` uploads whatever is in that slot
once per Unity frame. If the sender is at 25 fps and Unity renders at 90, the same
frame is shown ~3.6 times. If the sender ever outruns the renderer, the intermediate
frames are dropped in the slot, not buffered. The viewer always converges on the newest
frame by construction.

The stats line reports `coal` (coalesced) — arrivals that were superseded before Unity
drew them. A rising `coal` with a flat `age` is the healthy "dropping, not queueing"
signature.

### What was removed from this path

* A per-frame `Graphics.CopyTexture` of every eye into a second `Texture2D`, driven by
  two never-terminating `WaitForEndOfFrame` coroutines. Two full 720p+ GPU copies per
  rendered frame on a mobile tile GPU, for nothing — the `RawImage` can point at the
  plugin's texture directly.
* The `Texture2D` and coroutine that leaked on *every* `OnVideoReceived` invocation.
  That callback fires again on any resolution change, so each one leaked a texture and
  added another permanent copy loop.
* `Debug.Log` on every left-eye encoded frame, on the WebRTC worker thread, in front of
  the decoder.
* `data.Skip(length).Reverse().ToArray()` per frame per eye — LINQ over a `NativeArray`
  walks the entire encoded frame through a boxed enumerator and allocates three
  collections, again on the decode-critical worker thread.
* Two `lock`s per frame for the metadata timestamps, replaced by `volatile` /
  `Interlocked`.
* Main-thread `while (!www.isDone) {}` busy-waits around both Firestore round trips.

---

## 7. Diagnostics

The HUD (`debugText`) shows, once per frame:

```
connected
L 1280x800  rx 24.9  age 41ms  jb 58ms  drop 0  frz 0  coal 0  VideoToolbox
R 1280x800  rx 24.9  age 39ms  jb 61ms  drop 0  frz 0  coal 0  VideoToolbox
draw 90fps  vfov 55  sep 0.00  zoom 1.00  d 1.0
```

| Field | Meaning |
|---|---|
| `rx` | encoded frames arriving per second, per eye |
| `age` | ms since the newest encoded frame arrived — the staleness measure |
| `jb` | **mean jitter buffer delay**, from `jitterBufferDelay / jitterBufferEmittedCount`. Usually the single largest latency contributor. |
| `drop` | `framesDropped` from inbound-RTP stats |
| `frz` | `freezeCount` — user-visible stalls |
| `coal` | arrivals superseded before being drawn (see above) |
| last token | `decoderImplementation` — **check this is a hardware decoder**, not a software fallback |
| `STALE` | appended when `age > staleFrameMs` (default 250 ms) |
| `draw` | Unity render rate |

`age` needs the frame-arrival hook, which is the `enableFrameStats` pass-through script
transform. It costs one managed callback per frame in front of the decoder. Turn
`enableFrameStats` off (with `metadataLength` at 0) once tuning is done and no script
transform is installed at all — encoded frames then go straight from the network to the
decoder with no managed hop.

---

## 8. Latency budget, and what is left

Measured/known contributors, roughly largest first:

| Stage | Typical | Owner |
|---|---|---|
| Sender capture + rectify + encode | 20–60 ms | sender |
| Network | 1–5 ms wired, tens of ms over internet | network |
| **libwebrtc jitter buffer** | **30–150 ms** | not currently tunable from Unity — see below |
| Decode | 3–10 ms hardware, much worse in software | Quest |
| Texture upload (end of Unity frame) | ≤ 1 frame | plugin |
| Display (one frame at the refresh rate) | 11 ms @ 90 Hz, 14 @ 72 Hz | Quest |

### Remaining work for robust internet streaming

1. **Jitter buffer.** On a wired LAN this is the dominant remaining latency and it is
   pure overhead. `com.unity.webrtc` 3.0.0-pre.7 exposes `jitterBufferDelay` as a
   *metric* but not `jitterBufferTarget` as a *control*, and does not implement the
   `playout-delay` RTP header extension. Options: have the sender negotiate
   `playout-delay` with min=0, or move to a newer `com.unity.webrtc` if it gains
   `RTCRtpReceiver.jitterBufferTarget`. Watch `jb` in the HUD to know how much is on
   the table.
2. **Confirm hardware decode.** Read the decoder name in the HUD. Software H.264 on
   Quest costs both latency and battery. Keep the sender forcing H.264.
3. **Sender-side bitrate/GOP.** The sender already raises aiortc's VP8 cap; for H.264
   over the internet, add congestion-aware bitrate and a short keyframe interval so a
   loss recovers quickly. Watch `pliCount`/`nackCount`.
4. **TURN.** Now actually wired up, but untested. It is required for most
   NAT-to-NAT paths. Test it explicitly before relying on it — a relay also adds a
   round trip, so measure.
5. **Reconnection.** Unity currently answers once. `OnConnectionStateChange` is
   surfaced in the HUD but there is no automatic re-offer/re-answer loop; a dropped
   connection needs an app restart. The sender has a restart path; the two are not
   coordinated.
6. **Compositor layer (optional, biggest remaining quality win).** The quads currently
   render into the eye buffer and are then resampled by TimeWarp. Submitting the video
   as an `OVROverlay` stereo quad instead would let the compositor sample the source
   texture directly — sharper (no eye-buffer resampling) and slightly lower photon
   latency. This is a real architectural change to the display path and is deliberately
   *not* done here.

---

## 9. Settings reference

> **v1.** Settings now live in a JSON profile (`GvConfig`) chosen in `GvStartScene`, and
> the stereo-comfort values are edited from the in-session menu. The *meanings* below
> still hold; the plumbing does not.

Start scene → `PlayerPrefs` → `WebRTCStreamer`:

| PlayerPref | Default | Meaning |
|---|---|---|
| `ProjectID`, `Password`, `RobotID` | — | Firestore signalling document |
| `TurnServerURL` / `Username` / `Password` | empty | optional TURN relay |
| `DataSendFrequency` | 20 | Hz, headset → robot |
| `VideoRenderFrequency` | 30 | **unused** — video is never throttled |
| `VideoPlaneDistance` | 1.0 | metres; does not affect perceived depth |
| `VideoVFOV` | 55 | degrees; should match the camera |
| `VideoScale` | 1.0 | extra magnification |
| `StereoSeparationDeg` | 0 | + pushes the scene away |
| `StereoVerticalTrimDeg` | 0 | vertical alignment trim |
| `EdgeFeather` | 0.03 | border softening, fraction of the image |
| `OuterEdgeMask` | 0 | floating-window mask on each eye's outer edge |

The last six are written by the in-headset tuning mode, not by the start scene UI.

Inspector-only equivalents now live on `GvStereoDisplay` in `GvPassthroughScene`.
`isolateEyeLayers`, `gpuUndistort` and `calibrationFile` no longer exist.

---

## 10. Project settings that matter

* **Graphics API: OpenGLES3, not Vulkan.** (v1 reason: Vulkan crashed the WebRTC
  package. v2 keeps ES3 because `gvnative` hands MediaCodec output over as a GL_OES
  external texture.)
* **Stereo Rendering Mode: Single Pass Instanced.** The paragraph that used to sit here
  concluded the build was MultiPass and asked you to keep it that way. **That was wrong**
  — it read the *Editor's* `XRSettings.stereoRenderingMode`. The Android build has always
  been Single Pass Instanced. Keep it: the per-eye split is done in the shader
  (`_EyeIndex`), which is correct in either mode and costs one camera instead of two.
* **Layers 8 and 9 are free.** They were `LeftEyeOnly` / `RightEyeOnly`; nothing uses
  them now.
* **Dynamic resolution is disabled at runtime**, by `GvStereoDisplay`. The scene's
  `OVRCameraRig` had it on with a floor of 0.7, which renders the eye buffers at 70 %
  resolution under load — the most visible softening a full-FOV video quad can suffer.

---

## 11. Files

| Path | What it is |
|---|---|
| `Guided-Vision/Assets/Scripts/Video/GvStereoDisplay.cs` | stereo layout, eye materials, display config, telemetry |
| `Guided-Vision/Assets/Scripts/Video/GvVideoLink.cs` | receive + decode, MediaCodec on device or C# MJPEG in the Editor |
| `Guided-Vision/Assets/Scripts/Video/GvCameraParams.cs` | the rectified projection the robot sends, and the quad it implies |
| `Guided-Vision/Assets/Resources/StereoEyeView.shader` | per-eye selection (`_EyeIndex`) and edge treatment |
| `Guided-Vision/Assets/Scripts/UI/GvStartMenu.cs` | connection UI, built at runtime |
| `Guided-Vision/python/gvlink/camera.py` | the sender's half of the camera contract |
| `docs/VISION_PIPELINE.md` | this file (v1 record) |

---

## 12. Testing

### Wired / bench first

1. Start the sender on the robot host (`oak_to_headset.py`, or the full
   `data_collection` path). Keep the headset on the same LAN — ideally the headset on
   Wi-Fi 6 and the host wired, or the headset tethered via `adb` + a Link cable.
2. Launch the app, enter Project ID / password, **Load**, pick the robot, **Connect**.
3. `adb logcat -s Unity` while it connects. You want to see the offer fetched, the
   answer posted, and no `RTCRtpScriptTransform` warnings.

### What to look at, in order

**a. Is the stream intact?** Look at the HUD. `drop` and `frz` should stay at 0 and the
decoder name should be a hardware decoder. If the image is full of macroblock garbage,
check `metadataLength` is 0 (see §4) — that alone was corrupting every frame.

**b. Is it sharp?** Point the camera at something with fine detail (printed text, a
ruler). Compare against the app's own menu text, which is the sharpness ceiling. If the
video is much softer, the FOV is too wide — the pixels-per-degree arithmetic in §3 tells
you by how much.

**c. Stale frames are dropped, not queued.** This is the important one:

* Watch `age`. It should sit at roughly one sender frame interval (~40 ms at 25 fps) and
  stay there. A steadily *climbing* `age` means the stream stopped; a *sawtoothing*
  `age` that keeps growing would mean a backlog — it should never do that.
* Watch `coal`. It counts arrivals superseded before Unity drew them. Now pause or stall
  the sender for a second and resume: `age` should spike, `STALE` should appear, and
  then `age` should drop straight back to normal on the very next frame — **not** work
  through a queue of old images. That is the behaviour to confirm.
* Force the sender faster than the renderer (raise its fps above the headset refresh) and
  confirm `coal` climbs while `age` stays flat. Frames are being discarded, not buffered.

**d. Latency, end to end.** The honest measurement is a clock: point the camera at a
phone stopwatch, look at it through the headset, photograph both at once, subtract. Do
it a few times. The HUD's `jb` tells you how much of the total is jitter buffer, which
is the part still on the table.

**e. Stereo.** Press the left Menu button and tune (§3). Check depth is consistent at
several distances, not just one — if a setting that works close up falls apart on
distant objects, `videoVFOV` is still wrong, not `stereoSeparationDeg`.

**f. Per-eye isolation.** Close one eye. You should see exactly one image with no
ghosted band at the outer edge.

### Then over the internet

Put the headset on a different network from the robot, configure a TURN server in the
start scene, and repeat (c) and (d). Expect `jb` to rise; that is the jitter buffer
doing its job against a worse network, and it is the number to attack next.

### Regression checks after any change here

* Project builds for Android (`OpenGLES3`, Single Pass Instanced).
* Both eyes show video, and closing one eye at a time shows *different* images.
* The robot still receives headset data — move a controller, watch the sender.

---

## 13. Choosing parameter values experimentally

Most of these are not matters of taste — there is a number that tells you the answer.
The HUD reports them so you can turn the knob and watch.

### Frame size and FOV: use `ppd`

`ppd` on the HUD is source pixels per displayed degree, horizontally. The Quest 3 panel
resolves roughly **25 ppd**; the Quest 2 about 20. Below that the video is undersampled
and *no display-side setting recovers it*.

```
ppd = frame_width / HFOV_degrees      HFOV = 2 * atan( tan(vfov/2) * aspect )
```

The useful consequence: **for a fixed camera, resolution and field of view trade off
directly.** At 1280 px wide:

| displayed HFOV | ppd | verdict |
|---|---|---|
| 129 deg (the old `videoVFOV: 105`) | 9.9 | ~2.5x undersampled |
| 80 deg (`videoVFOV: 55`) | 16 | usable, still under the panel |
| 51 deg | 25 | matches the panel, narrow view |

So: **do not downscale the stream.** `HEADSET_SEND_SCALE = 1.0` and
`EYE_VIEW_SCALE = 1.0`. 1280x800 is already the tight end for a wide lens; halving it
halves `ppd`. If you want genuinely sharp, the lever is a *narrower* displayed FOV (a
crop), not a bigger frame — the camera has no more pixels to give.

Procedure: point the camera at printed text at working distance. Sweep `videoVFOV` in
tuning mode and find the narrowest FOV you can still work in. Read `ppd` there. That is
your ceiling; note the number.

### Bitrate: use `kbps` and the freeze counters

`kbps` is what actually arrived, per eye. Raise the sender's
`GIAVA_HEADSET_BITRATE` until `kbps` stops rising — at that point you are limited by the
encoder or the link, not the cap. Then back off ~20%.

Watch `frz` and `drop` while you do it. If they start climbing you have exceeded what
the link carries and you are trading latency for bitrate, which is the wrong trade here.
Two streams share the link, so budget for both.

### Frame rate

Higher fps lowers motion-to-photon: at 25 fps a frame is already up to 40 ms stale
before it is even encoded. If the OAK sustains 30-60 fps at 1280x800 over USB3, take it
— but confirm `kbps` scales and `frz` stays at 0, since more frames at the same bitrate
means more compression per frame.

Check `rx` on the HUD matches what the sender thinks it is sending. A gap means frames
are being dropped upstream of Unity.

### Latency: use `jb`, then measure the whole thing

`jb` is the jitter buffer's mean delay and is usually the biggest single number you have
no direct control over. Note it wired; note it again over the internet. The difference
is what the network is costing you.

For end-to-end, use a stopwatch on a phone: camera pointed at it, photograph the phone
and the headset view together, subtract. Repeat 5 times and take the median — a single
sample is worthless because it quantises to the display frame.

### Stereo: tune at two distances, not one

The failure you already saw. Set `videoVFOV` first (it is the thing that makes depth
*consistent*), then set `stereoSeparationDeg` while looking at something roughly at
working distance. Then look at the far side of the room without changing anything. If
far objects now diverge uncomfortably, `videoVFOV` is still too wide — go back and
narrow it. Only when both distances are tolerable at the same setting is the geometry
right.

### Edges

Raise `outerEdgeMask` (hold right index trigger in tuning mode) until the flickering
strip at the outer edge of each eye stops drawing attention, then stop. Typical 0.05 to
0.12 for a wide pair. Too much and you are throwing away field of view.

### One change at a time

All of these interact. `videoVFOV` moves `ppd` *and* perceived depth. Bitrate moves
`frz` *and* latency. Change one, write down the HUD line, change it back if it did not
help.
