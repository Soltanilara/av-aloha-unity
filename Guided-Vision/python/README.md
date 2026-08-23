# gvlink — Guided-Vision robot side

The robot half of the Guided-Vision v2 stereo teleoperation link: capture, foveal
atlas packing, H.264 encode, and the UDP wire protocol the Quest client speaks.

Wire format and the reasoning behind it: [`docs/PLAN.md`](../../docs/PLAN.md) §4–5.
`gvlink/protocol.py` is the normative definition — the C# side must match it byte for
byte.

## Setup

```bash
uv sync
```

## Try it

Two terminals, both in this directory:

```bash
# terminal 1 — the "headset"
uv run bench_receiver.py --display --gaze-mouse

# terminal 2 — the "robot"
uv run mock_robot.py --source webcam        # or --source pattern
```

Move the mouse over the bench window and the foveal patch follows it, exactly as gaze
will drive it from the headset. To confirm the uplink is actually arriving, watch the
*sender's* status line -- it reads `gaze=remote (N pkts)` with N climbing. `gaze=center`
means nothing is getting through. `--source pattern` draws a synthetic detail chart
instead of using a camera, which is the honest way to judge what the coarse layer
throws away — a smooth scene flatters foveation.

To stream to a real headset, point the sender at it: `--host 192.168.1.50`, or a
Tailscale address for the remote case. Nothing in the code knows the difference.

## The tools

| | |
|---|---|
| `mock_robot.py` | Webcam or test chart → LEFT/RIGHT pair → foveal atlas → H.264 → UDP. Listens for the gaze uplink. |
| `bench_receiver.py` | The headset minus the headset: reassemble, decode, reconstruct the atlas, and report latency/loss. `--json` writes a summary. Receiving runs on its own thread and drawing is capped by `--display-fps` (30 by default), so the display can never throttle the socket. |
| `selftest.py` | In-process protocol checks — fovea rect round trip, gaze steering, loss handling, sender restart. Run this after touching `protocol.py`. |

Useful flags: `--no-fovea` sends the plain full-frame canvas (the Quest 2/3 path, and
the A/B baseline), `--auto-gaze` sweeps the fovea with no headset attached,
`--raw` on the bench shows the transmitted atlas instead of the reconstruction.

### Tuning how obvious the fovea is

Two knobs, both on the sender, both changeable without ever reconfiguring the decoder
because the canvas size never moves:

| flag | default | effect |
|---|---|---|
| `--coarse-scale` | 0.35 | fraction of the coarse band the periphery fills. Lower = blurrier surroundings = far more obvious foveal patch. |
| `--fovea-scale` | 0.5 | fraction of the fovea band the 1:1 crop fills. Lower = a smaller, more eye-like patch. |

The sender prints what it settled on, e.g.

```
source 1920x1200 -> canvas 1024x1024; coarse 358x178 (5.4x downscaled),
fovea 512x256 at 1:1 (27% x 21% of the frame)
```

At `--coarse-scale 1.0` the periphery is only 1.9x downscaled and the patch covers half
the frame, which is why it is easy to miss. Push it to 0.25 to make the effect
unmissable while judging the blend.

What it costs, 1920x1200 at 60 fps, H.264, same quality settings:

| | bitrate |
|---|---|
| `--no-fovea` (plain full frame) | 20.0 Mbit/s |
| foveated, defaults | 6.8 Mbit/s |

**3x less bandwidth for a sharper centre** -- the unused remainder of each band is
black and costs an inter-coded stream almost nothing.

### Saccade widening

`--saccade-zoom` (default 2.5, `1.0` disables) is the one mitigation for a problem the
architecture cannot avoid: gaze reaches the robot, the robot crops the *next* frame, and
that frame takes an encode/network/decode trip back. Call it 50-90 ms. A saccade
completes in 30-80 ms, so the patch always lands behind the eye -- and if the patch is a
tight crop, "behind" means the eye arrives on blurry periphery and waits.

So while gaze is moving fast the patch covers 2.5x its width and height, at exactly the
same stored size. The trade is coverage for sharpness, and during a saccade that is the
right way round: vision is substantially suppressed through most of the movement, while
the extra coverage means wherever the eye lands is already better than periphery. As the
eye settles, coverage decays back over ~120 ms and full detail returns.

It is free. Measured over 20 frames of the detail chart:

| | bytes/frame |
|---|---|
| 1:1 crop | 12923 |
| 2.5x coverage | 5142 |

**0.40x, not 1.0x** -- a downscaled patch carries less high-frequency content, so it
compresses better. Widening during a saccade is cheaper than not widening.

The sender prints the live figure as `zoom 1.00`. Watch it sit at 1.00 during fixation
and jump to 2.50 the instant you look somewhere else.

Worth being straight about: the thresholds are reasoned from saccade timing, not measured
against a real eye. `--saccade-zoom 1.0` is the control arm when you get to compare them.

## Driving the Unity Editor (no headset)

MediaCodec is Android-only, so the Editor cannot decode H.264. `--codec mjpeg` sends
the same packets with JPEG payloads instead, which Unity decodes in C#
(`GvEditorVideoSource`). Everything above the codec is identical — same fragmentation,
same reassembly policy, same fovea rect in the same header field — so this exercises
the display shader, the atlas geometry and the stereo layout for real:

```bash
uv run mock_robot.py --source webcam --codec mjpeg --fps 30 --auto-gaze
```

Then press Play on `Assets/Scenes/GvPassthroughScene.unity` (or launch it under the
Meta XR Simulator). The HUD marks the run `[editor mjpeg]`.

**Steer the fovea with the mouse.** There is no eye tracker in the Editor, so
`GvInputUplink.simulateGazeWithMouse` (on by default, Editor only) maps the cursor's
position across the Game view straight onto the source image: cursor a quarter of the way
across, patch a quarter of the way across. Everything downstream is the real thing -- the
uplink, the robot's crop, the packet header, the shader composite -- so moving the mouse
exercises the actual loop.

Drop `--auto-gaze` when you do this; the robot prefers real gaze over the sweep anyway,
and the HUD will say `gaze 0.42,0.31 (mouse, 4711 sent)` once packets are arriving. If it
says **`idle`**, the uplink never found a robot address -- see below. If it says
**`eyes`**, something is reporting eye tracking and is winning; untick the box or check
the simulator.

**Playing `GvPassthroughScene` directly** (not via the menu) needs
`GvStereoDisplay.fallbackHost` set to the robot's address -- `127.0.0.1` for a local mock.
That one field drives the control channel, the video request *and* the uplink, so with it
set you do not need `--host` on the sender at all. Left empty, video still arrives if you
pass `--host`, but there is no return path and the uplink stays idle.

Next to it the HUD shows **`patch trails 0.031`** -- how far the displayed patch is from
where you are currently looking, in image widths. Fixate and it falls towards zero; flick
the mouse across the view and watch it spike and recover. That is the eye-to-fovea
latency made visible, and it is the number to record when comparing settings.

MJPEG is a development tool, not a transport: several times the bitrate of H.264 for
worse pictures, and it decodes on the main thread. Keep it to loopback or a wired LAN,
and drop `--jpeg-quality` / `--fps` if it saturates. On the detail chart at quality 85
it runs near 190 Mbit/s; a webcam is far lower.

## Reading the numbers

Measured on an M-series Mac over loopback, 1920×1200 source → 1024×1024 canvas per
eye, 60 fps, 12 Mbps/eye:

```
eye L: 660 frames (60.0 fps), dropped 0 (0.00%), fragments lost 0/9783
       latency mean 4.7 ms  p50 4.7  p95 5.1   decode 1.15 ms
aggregate ~20 Mbit/s for the pair
```

That 4.7 ms is capture-stamp → decoded frame: atlas packing, encode, fragmentation,
socket round trip, reassembly and decode. It is the part of the budget this code owns.
Real network, MediaCodec and display latency are added on the headset side — against
XRoboToolkit's measured 94.5 ms LAN total, this leaves ample room.

**The latency figure is only a true one-way measurement when sender and receiver share
a monotonic clock, i.e. both on this machine.** Across hosts the clocks differ and only
the *jitter* is meaningful until the control channel supplies an RTT estimate.

## Adapting to the link

A fixed bitrate is wrong in both directions on anything but a LAN: too high and the queue
fills until frames arrive late and then not at all, too low and the picture is needlessly
soft on a link that could carry more. So the viewer reports what actually arrived --
fragment loss and how late frames are running -- on the control channel twice a second,
and the sender follows.

```
tx  59.8 fps avg |   6.8 Mbit/s | ... | 7392 kbps (delay, queue 61 ms, -3/+9)
```

The control law is AIMD, the same shape as TCP's: cut multiplicatively, recover
gradually. Two signals, and the order matters -- **delay first, loss second**. A queue
builds before it overflows, so latency rises before a single packet is lost; by the time
fragments are missing the viewer has already seen a broken frame.

The delay figure is measured against a rolling minimum rather than a threshold, because
the absolute number is not a latency: the robot stamps frames with its own monotonic
clock and the headset does not know that epoch, so the raw value is routinely large and
negative. The minimum cancels the offset and what is left is queueing delay.

Retargeting the encoder costs **0.49 ms** and one keyframe (libx264 fixes rate control at
open, so a new target means a new encoder; the canvas never changes size, so the decoder
never notices). `--no-adapt` holds it fixed, `--min-bitrate` sets the floor.

## Going remote

See [docs/REMOTE.md](../../docs/REMOTE.md). Short version: a remote robot is just an
address, Tailscale supplies the route, and **run the sender with `--tunnel`** -- a
WireGuard tunnel carries ~1280 bytes, so the default 1400-byte payload would be
IP-fragmented on every datagram, and a datagram that loses one IP fragment is lost whole.

## Robot I/O

The control channel is the interface you program against. Adding an input or an output
is a topic name and a dict on each side -- no schema, no type declared twice.

```python
from gvlink.robotlink import RobotLink

link = RobotLink()

@link.handler("arm/home")            # request/response
def home(req):
    return {"ok": True, "arm": req.get("arm")}

link.subscribe("gripper/cmd", lambda m: gripper.set(m["width"]))   # commands in
link.publish("arm/state", {"q": q, "grip": w})                     # telemetry out
link.start()
```

and on the headset:

```csharp
var link = GvRobotSession.Instance.Link;
link.Subscribe("arm/state", d => { ... });
link.Publish("gripper/cmd", GvRobotSession.Map("width", 0.04f));
link.Call("arm/home", GvRobotSession.Map("arm", "left"), reply => { ... });
```

`mock_robot.py`'s `register_demo_io()` is a worked example of all three.

### The uplink

Head, controllers and gaze go the other way on UDP 15553, at display rate:

```python
p = input_rx.fresh()                 # newest sample, never a backlog
if p and p.gaze_valid:
    crop_at(p.gaze_l)                # normalised image coords, y down from the top
if p.left.held(BUTTON_ONE):
    ...
```

158 fixed bytes at 90 Hz, against the 459-byte JSON blob the previous generation sent
at 20 Hz with names like `rightArmRotation`. Anything that does not fit that shape
rides in an optional msgpack tail (`extras`), or on the control channel if it has to
arrive at all.

Gaze is sent as **image coordinates, not a direction**. The headset is the only end
that knows the quad geometry needed to project a gaze ray onto the picture, so it does
it there and the robot needs to know nothing about the display in order to crop for it.

**Two lanes, on purpose.** Anything that must arrive and arrive in order -- commands,
replies, configuration -- goes over TCP on 15551. High-rate telemetry that would rather
be late than queued (gaze now; head and hand poses next) goes over the UDP uplink on
15553 with last-wins semantics. A dropped gripper command is not something to paper over
with a retry policy invented at the call site, and a stale pose is worse than a missing
one.

**The connection is the session.** Connecting tells the robot where to send video and in
what codec; dropping it stops the stream. There is no hello, no keepalive and no timeout
to tune. It also means the robot learns the headset's address from the socket, so NAT
and Tailscale need no configuration.

**msgpack, short keys.** The envelope is four one-character keys (`t`, `k`, `i`, `d`).
The C# codec is hand-written (Unity ships none, and codegen serialisers fight IL2CPP)
and is validated byte-for-byte against Python's `msgpack` in `selftest`-adjacent
tooling -- see `tools_msgpack_vectors.py`.

## Design notes worth not re-litigating

* **Frames are dropped, never waited for.** If a fragment is missing when the next
  frame starts arriving, the whole frame is discarded. Holding it would buy a marginal
  recovery rate at the cost of a frame of delay on everything after — the exact trade a
  WebRTC jitter buffer makes, and the reason we left.
* **The canvas size never changes.** Foveation moves the crop, not the resolution, so
  MediaCodec never reconfigures mid-session.
* **The fovea rect rides in the packet header**, not an H.264 SEI, because MediaCodec
  does not hand SEI back to the app. B-frames are off, so decode order equals input
  order and a FIFO matches headers to frames.
* **No B-frames, no frame threading, no lookahead, small VBV.** Each would trade
  latency for quality-per-bit. See `gvlink/video.py`.

## Known noise

On macOS, `cv2` and `av` each bundle a copy of ffmpeg, so loading both prints an
`objc: Class AVFFrameReceiver is implemented in both ...` warning. PyAV is imported
lazily, so `--codec mjpeg` never loads it and stays quiet; the H.264 path still prints
it. Harmless in practice — OpenCV uses its own AVFoundation capture backend, not
ffmpeg's — but not loading two ffmpegs at once is better than reasoning about it.
