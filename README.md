# Quest VR Teleoperation

A Meta Quest app that turns the headset into a **peripheral for a robot**: it streams a
stereo camera pair from the robot into the two eyes, sends head, hand, controller and
gaze poses back at 90 Hz, and draws whatever the robot asks it to draw.

The design goal is that teleoperation policy lives on the robot, in Python, and this app
does not need rebuilding when that policy changes. See
**[docs/HEADSET_API.md](docs/HEADSET_API.md)** for the interface that makes that true.

> Renamed from "Guided-Vision". The `Gv` prefix on C# types and the `gvlink` Python
> package are the old initials, kept deliberately: they are a stable, greppable
> namespace, and renaming them would churn every file for no functional gain.

## Layout

| | |
|---|---|
| `Guided-Vision/` | the Unity project (Unity **6000.5.9f1**, Android / ARM64 / IL2CPP / OpenGL ES3) |
| `Guided-Vision/Assets/Scripts/` | `Net/` uplink + control channel · `Video/` receive + display · `UI/` menus, HUD, markers |
| `Guided-Vision/python/` | `gvlink`, the robot side: capture, foveal packing, H.264 encode, wire protocol |
| `Guided-Vision/native/gvnative/` | the JNI shim that hands MediaCodec output to Unity as an OES texture |
| `docs/` | see below |

Two scenes, both in the build: `GvStartScene` (pick a robot) → `GvPassthroughScene`
(the session). Both build their UI in code — there is nothing to wire in the Inspector,
and no EventSystem.

## Run it without a headset

Two terminals in `Guided-Vision/python`:

```bash
uv sync
uv run bench_receiver.py --display --gaze-mouse   # stands in for the headset
uv run mock_robot.py --source webcam              # or --source pattern
```

`uv run mock_robot.py --viser` adds a browser visualiser of everything the headset
reports — head, hands, controllers, gaze, plus stream metrics. `--ui-demo` exercises the
marker/guide/toast API with no robot code at all.

The Unity Editor and the Meta XR Simulator can also receive the real stream: run the
sender with `--codec mjpeg` (MediaCodec is Android-only, so the Editor decodes in C#).

## In the headset

Open the session menu with **hold B/Y**, **tap the Menu button**, or **hold a
middle-finger pinch** in hand-tracking mode. A progress pill appears while the gesture is
being recognised, so "not registering" and "ignored" never look alike. From there:
disconnect, reconnect, refresh rate, telemetry level, and whether the raw controller and
hand meshes are drawn.

## Docs

| | |
|---|---|
| **[docs/PLAN.md](docs/PLAN.md)** | v2 architecture, wire format, and a decision record for every non-obvious choice |
| **[docs/HEADSET_API.md](docs/HEADSET_API.md)** | what the robot can draw in the headset and read back from it |
| **[docs/REMOTE.md](docs/REMOTE.md)** | operating over Tailscale rather than a LAN |
| **[docs/VISION_PIPELINE.md](docs/VISION_PIPELINE.md)** | stereo geometry and comfort tuning; largely predates v2, see its banner |

## Lineage

This began as the Unity app for **AV-ALOHA** — [code](https://github.com/Soltanilara/av-aloha),
[project page](https://soltanilara.github.io/av-aloha/),
[paper](https://arxiv.org/abs/2409.17435) — which used WebRTC for transport and Firestore
for signalling. Both are gone; v2 is a direct UDP video and control stack.
