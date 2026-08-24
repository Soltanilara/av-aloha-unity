# The headset as a device, not an application

**Status: Phase A is complete.** `ui/guide`, `ui/marker`, `ui/toast`, `hx`, `ui/menu` +
`ui/menu/event`, `ui/recenter` and `hs/state` are implemented, along with the deadman bit
-- so most new teleoperation features are now Python only. `mock_robot.py --ui-demo`
exercises all of it without any robot code.

Still plan only, and marked below: `ui/overlay`, `ui/hud`, `ui/prompt` and `hs/event`.

| | |
|---|---|
| headset | `GvSceneCommands.cs`, `GvRobotRow.cs`, `GvSessionMenu.cs`, `GvHeadsetState.cs`, `GvToast.cs`, `GvInputUplink.cs` |
| robot | `gvlink/ui.py`, `gvlink/robotlink.py`, `gvlink/protocol.py` |

The goal is to stop editing the Unity app. Teleoperation policy — what a wrist pose means,
where the arm should go, when to record — belongs on the robot, in Python, where it can be
changed in a minute. The headset should be a *peripheral* that reports what the operator is
doing and renders what the robot asks for, and should need a rebuild only when the
peripheral itself gains a capability.

That means the interface has to be closed under the things you will plausibly want next.
The rest of this document is an attempt to work out what that set is.

## The one principle

**The robot owns meaning. The headset owns presentation and safety.**

The robot says *"the left wrist should be here"*. It does not say *"draw a red cylinder at
0.3 opacity"*. The headset knows whether the operator is holding controllers or using bare
hands, what a Touch controller looks like, how big the panel should be at 0.7 m, and what a
tracking dropout means — and none of that should have to travel over the wire or be
duplicated in robot code.

This directly answers the guide question. You were unsure whether the robot should say
*where* to place the guides. It should say where, and nothing else: the headset picks a
controller ghost or a hand ghost based on what is actually being tracked at that instant.
The robot never has to know which one the operator picked up, and the guide is correct on
the frame they swap.

The counterweight: there is also a low-level marker layer where the robot *does* control
appearance, because no fixed vocabulary survives contact with a real project. Both layers
exist; reach for the semantic one first.

## Frames and handedness

Everything positional in this document — markers, guides, poses — uses **the same frame and
handedness as the 90 Hz uplink**: the tracking origin, Unity's left-handed Y-up convention.
A pose the robot received can be sent straight back as a marker and land where it came from.

Conversion to a right-handed or Z-up convention stays at the consumer, as it already does
for the viser visualiser. The wire carries what the headset saw.

**Recentring invalidates world-frame geometry.** `hs/state` therefore carries an
`origin_epoch` that increments whenever the operator recentres. Markers in the `origin`
frame are dropped when their epoch is stale, rather than silently relocating to a place
that means nothing. A robot that cares re-sends them.

## Transport

Everything below rides the **existing TCP control channel** (msgpack, short keys, pub/sub
plus call/reply). It is small, ordered, lossless, and already there. A marker update is
under a hundred bytes; even a 60 Hz live target costs a few KB/s.

The 90 Hz pose uplink stays exactly as it is: fixed binary over UDP, unchanged.

One gap to close first: `GvRobotLink.Pump()` handles inbound *replies* but not inbound
*calls*, so the robot cannot currently ask the headset a question and get an answer. Small
fix, needed for prompts.

---

# Robot → headset

## `ui/guide` — "put your hand here"

The red-controller replacement, and the highest-value item in this document.

```python
link.publish("ui/guide", {
    "side": "l",              # l | r
    "p": [0.1, 1.2, -0.3],    # origin frame
    "q": [0, 0, 0, 1],
    "tol": 0.02,              # metres; how close counts as reached
    "ang": 15,                # degrees; orientation tolerance, omit to ignore
    "hold": 0.5,              # seconds inside tolerance before it fires
    "label": "home",
})
```

The headset draws a translucent sphere **sized to `tol`** — so the sphere *is* the
tolerance and touching it is arriving — with an axis gizmo for the orientation, a lead line
from the operator's actual pose to the target, and a live distance readout. It turns green
inside tolerance, publishes `ui/guide/reached` `{"side":"l","label":"home","src":"hand"}`
once held, and buzzes the controller.

It resolves **whatever the operator is currently using** to decide what "left" means and
what to call it — a tracked hand's wrist if hands are live, the controller anchor otherwise,
matching the uplink's rule that the two are alternatives rather than additions. The label
says which. *(A ghost mesh of the actual controller or hand would read better than a sphere
and is the obvious refinement; the sphere is what is built, and it has the advantage of
showing the tolerance itself, which a mesh does not.)*

`{"side":"l","clear":true}` removes it.

Why the headset resolves the ghost and the tolerance check: both need per-frame pose data at
tracking rate. Doing it robot-side means round-tripping at 90 Hz to answer a question the
headset can answer locally, and getting a stale answer.

## `ui/marker` — the general layer

For everything the semantic types do not cover. Upsert by id.

```python
link.publish("ui/marker", {"m": [
    {"id": "traj", "t": "line", "f": "origin",
     "pts": [[0,1,0], [0.2,1.1,-0.1]], "c": [0.2,0.8,1,0.7], "w": 0.004},
    {"id": "bounds", "t": "box", "f": "origin", "p": [0,1,-0.4],
     "q": [0,0,0,1], "s": [0.6,0.4,0.3], "c": [1,0.5,0,0.15]},
    {"id": "note", "t": "text", "f": "view", "p": [0,-0.2,1],
     "txt": "cartesian mode", "size": 0.03},
]})
```

| field | meaning |
|---|---|
| `t` | `pose` (axis gizmo) · `sphere` · `box` · `line` · `arrow` · `text` · `del` |
| `f` | `origin` · `head` · `view` (head-locked billboard) · `hand_l` · `hand_r` |
| `ttl` | seconds until it disappears on its own |

**`ttl` is not optional thinking.** A robot that crashes leaves its markers on screen telling
the operator to do something that is no longer true, and the failure looks exactly like
working guidance. Anything advisory should carry a ttl of a few seconds and be re-sent.

`{"clear": "traj"}` removes by id prefix; `{"clear": "*"}` removes everything.

## `ui/overlay` — *not built* — drawing on the video, not in the room

Markers live in the room. Detections live in the *image*. Normalised uv on the eye quads,
so the robot can annotate what its own camera saw without knowing the display geometry:

```python
link.publish("ui/overlay", {"o": [
    {"id": "obj", "t": "box", "uv": [0.42, 0.31, 0.18, 0.22],
     "c": [0,1,0,0.9], "txt": "mug 0.94"},
]})
```

`eye`: `both` (default), `l`, `r`. This is the natural home for detections, grasp
candidates, a target reticle, or a segmentation outline.

## `ui/hud` — *not built* — persistent status

```python
link.publish("ui/hud", {"rows": [["gripper", "0.03 m"], ["mode", "cartesian"]]})
```

Small key/value block in the corner. Replaces the whole block each time, so there is no
stale-row problem to reason about.

## `ui/toast` — transient message

```python
link.publish("ui/toast", {"txt": "joint 4 near limit", "sev": "warn", "secs": 3})
```

`sev`: `info` · `warn` · `error`. Errors also buzz.

## `ui/menu` — robot-defined controls

**This is the lever that makes "never touch the Unity app again" true.** The robot declares
rows; they appear in the session menu below the display settings.

```python
link.publish("ui/menu", {"rows": [
    {"id": "rec",  "label": "Record episode", "type": "button"},
    {"id": "keep", "label": "Keep last",      "type": "button"},
    {"id": "mode", "label": "Control mode",   "type": "choice",
     "options": ["cartesian", "joint"], "value": "cartesian"},
    {"id": "spd",  "label": "Speed",          "type": "range",
     "min": 0.1, "max": 1.0, "step": 0.1, "value": 0.5},
    {"id": "grip", "label": "Gripper open",   "type": "toggle", "value": False},
]})
```

In practice, build the rows with the helpers rather than by hand:

```python
from gvlink.ui import HeadsetUi, button, toggle, choice, rng

ui.menu([button("rec", "Record episode"),
         toggle("grip", "Gripper open", False),
         choice("mode", "Control mode", ["cartesian", "joint"], "cartesian"),
         rng("spd", "Speed", 0.1, 1.0, 0.1, 0.5, fmt="{0:0.0}x")])
ui.on_menu_event(lambda e: print(e["id"], e["value"]))
```

The headset publishes `ui/menu/event` `{"id": "spd", "value": 0.7}` on interaction. Rows are
replaced wholesale on each publish, so the robot can enable, disable and relabel them freely
— including greying one out mid-task by re-sending the list with `"enabled": False`.

**Values move locally first, then get corrected.** A row that waited for the robot to
confirm would feel broken over any real link, so the headset applies the change at once
and sends the event; the robot's next publish is authoritative. `mock_robot.py --ui-demo`
demonstrates the loop: pressing *Record episode* relabels that row and disables *Home the
arms* in the same reply.

A row without an `id` is dropped silently — it would be a control the headset had no way
to report back.

Episode recording, calibration routines, gripper presets, mode switches and homing are all
robot-side code now. None of them need a Unity change.

## `ui/prompt` — *not built* — ask the operator a question

A **call**, not a publish, so the robot gets an answer:

```python
answer = await link.call("ui/prompt", {
    "txt": "Discard the last episode?", "options": ["Discard", "Keep"]})
```

Blocks a modal in front of the operator. Needs the inbound-call fix noted above.

## `hx` — haptics

```python
link.publish("hx", {"side": "r", "amp": 0.6, "secs": 0.05})
```

The cheapest realism win available. A pulse on contact, a double tap on grasp, a rising
buzz near a joint limit — all of it is robot-side policy over one primitive.

## `ui/recenter`

Re-origins the view. Bumps `origin_epoch`.

---

# Headset → robot

## `hs/state` — what the device is doing, ~2 Hz

```python
ui.on_state(lambda s: ...)

{"batt": 0.62, "mounted": True, "src": "hands", "origin_epoch": 3,
 "hz": 90, "fps": 88.4, "missed": 2, "deadman": "auto", "deadman_held": False,
 "hand_conf": {"l": 1.0, "r": 0.5}, "eye_tracking": False,
 "uplink_hz": 90, "sent": 41233, "gaze": False}
```

A change in `mounted` is published immediately rather than waiting for the next tick.

`mounted: False` — **the operator has taken the headset off.** That is a safety event, not
telemetry. The robot should treat it exactly as it treats a deadman release.

`missed` is the frame meter already built for the stereo investigation. Exposing it lets the
robot log rendering health alongside episodes, which is how you find out afterwards that the
one bad demonstration was a judder problem and not the policy.

## `hs/event` — *not built* — discrete things that happened

Edge-triggered, delivered reliably, unlike the 90 Hz held-state in the uplink:

```python
{"e": "button", "name": "a", "side": "r", "down": True}
{"e": "guide", "label": "home", "side": "l"}
{"e": "tracking", "src": "controllers", "was": "hands"}
```

The last one matters more than it looks. A robot mapping a wrist to an end effector needs to
know on the *frame* the operator sets a controller down, not to infer it from poses going
quiet.

## Deadman — a bit in the uplink, deliberately

Not a topic. **Bit 6 of the uplink flags** (bits 0–5 are in use), driven by a configurable
control — grip held, pinch held, or disabled entirely.

It belongs in the 90 Hz packet rather than on the control channel because it must be current
and it must fail safe: if the uplink stops arriving, the bit stops arriving with it, and a
robot gating motion on "deadman set in a packet newer than 100 ms" stops on its own with no
timeout logic to get wrong. A TCP event saying "released" can be the thing that fails to
arrive.

Read it as `inp.deadman` (`INPUT_DEADMAN`, bit 6). The control is chosen from the session
menu — off, grip, trigger, pinch, or **auto**, which follows whatever the operator is
actually holding and so stays correct when they put a controller down mid-session.

With the control set to `off` the bit is set on **every** packet, and `hs/state` reports
`deadman: "off"`. Reporting "never held" instead would freeze any robot that gates on it,
with nothing on screen to explain why; this way "held" and "not in use" stay
distinguishable, and the fail-safe property is unaffected because it comes from the packets
stopping, not from the bit clearing.

---

# Also worth doing

**~~Hide the controller and hand meshes in the teleop scene by default.~~** *Done.* The
red `ErrorMaterial` arm meshes and the `KeyboardInteractor` selection dots are deleted
outright; the remaining controller/hand meshes are off by default and behind a
"Show controllers / hands" toggle in the session menu. Guides are the intended replacement.

**A "where am I" affordance.** After a recentre or a long session, a faint floor grid or an
origin gizmo on demand costs nothing and answers a question that is otherwise unanswerable
from inside the headset. (`ui/recenter` and the menu's *Recentre tracking* row exist now,
and report `origin_epoch`; the grid does not.)

**Log the input source into episodes.** Hands and controllers produce measurably different
demonstrations. If that is not recorded, it becomes an unexplained variance in the data.
`hs/state.src` carries it now; writing it into the episode record is robot-side work.

---

# Phasing

**A — unblocks robot-side iteration.** ✅ *Done.* `ui/guide`, `ui/marker`, `ui/toast`, `hx`,
`ui/menu` + `ui/menu/event`, `hs/state`, and the deadman bit (pulled forward from B, since
it is four lines of packing and the safety argument does not wait). Most new teleop features
are Python only from here.

**B — completes the loop.** `ui/overlay`, `ui/prompt` (with the inbound-call fix), `ui/hud`,
`hs/event`.

**C — pure robot-side.** Episode recording, calibration routines, guided workflows. No Unity
changes; if any are needed, A or B was wrong and that is worth knowing early.

A single `GvSceneCommands` component owns the marker pool, the guide ghosts and the frame
resolution. `GvSessionMenu` grows a robot-rows section. Nothing else in the app needs to know
this exists.
