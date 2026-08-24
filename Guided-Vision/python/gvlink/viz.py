"""
Live view of what the headset is sending, in a browser.

Useful for the thing that is otherwise very hard to check: whether the poses arriving
at the robot mean what you think they mean. A wrist that drifts, a hand that mirrors,
an axis that points the wrong way -- all of these are obvious in three dimensions and
close to invisible in a column of numbers.

Optional. `pip install viser`, or `uv sync --extra viz`; without it the mock robot runs
exactly as before and says so once.

**Handedness.** Unity is left-handed with Y up; essentially everything in robotics is
right-handed. The conversion happens here rather than on the headset, so the wire format
stays "exactly what the headset saw" and each consumer picks its own convention -- ROS
wants Z up, this viewer wants Y up, and a headset guessing between them would be wrong
for somebody. Flipping Z gives a right-handed frame: positions become (x, y, -z) and a
rotation (x, y, z, w) becomes (-x, -y, z, w).

One consequence worth stating, because it caused a bug here: Unity's forward is +Z, so
after the flip the operator looks down **-Z**, the OpenGL camera convention. viser's
camera frustum uses the OpenCV one (+Z forward, +Y down), so drawing the head pose
straight into a frustum points it backwards and upside down. The frustum therefore gets
an extra 180 degrees about X. A short forward ray is drawn alongside it so that "which
way is the operator facing" never depends on remembering that.
"""

from __future__ import annotations

import math
import time

import numpy as np

from .protocol import FINGERS, HeadsetInput

# No hard-coded skeleton. Meta ships more than one hand rig (24 bones for the classic,
# 26 for the XR one) and the parent table differs between them, so guessing produced
# lines that connected the wrong joints -- which looks like broken tracking rather than
# a broken drawing. The headset knows its own topology and now sends it on the control
# channel (`hand/skeleton`); until it arrives, joints are drawn without bones. Fewer
# lines is a far better failure than wrong ones.


# --------------------------------------------------------------------- quaternions
# (x, y, z, w) throughout, matching the wire. Small and self-contained: pulling in a
# rotation library for four operations would be a dependency for the robot to carry.

def _qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _qconj(q):
    x, y, z, w = q
    return (-x, -y, -z, w)


def _qrot(q, v):
    """Rotate v by unit quaternion q."""
    x, y, z, w = q
    tx = 2.0 * (y * v[2] - z * v[1])
    ty = 2.0 * (z * v[0] - x * v[2])
    tz = 2.0 * (x * v[1] - y * v[0])
    return (v[0] + w * tx + (y * tz - z * ty),
            v[1] + w * ty + (z * tx - x * tz),
            v[2] + w * tz + (x * ty - y * tx))


def _yaw_only(q):
    """
    The heading part of a rotation, about the world up axis.

    Used for the view anchor: anchoring to the full head pose would tilt the entire
    scene by whatever the operator's neck happened to be doing at the instant the button
    was pressed, which is the opposite of making it easier to read.
    """
    _x, y, _z, w = q
    n = math.hypot(y, w)
    return (0.0, 0.0, 0.0, 1.0) if n < 1e-9 else (0.0, y / n, 0.0, w / n)


# 180 degrees about X: -Z-forward/+Y-up (what the conversion above yields) into
# +Z-forward/+Y-down (what viser's camera frustum expects).
_GL_TO_CV = (1.0, 0.0, 0.0, 0.0)


_LEFT = (90, 170, 255)
_RIGHT = (255, 150, 90)
_HEAD = (200, 200, 200)


def to_right_handed(pos, rot=None):
    """Unity's left-handed Y-up into a right-handed Y-up frame."""
    p = (float(pos[0]), float(pos[1]), -float(pos[2]))
    if rot is None:
        return p
    x, y, z, w = (float(v) for v in rot)
    return p, (-x, -y, z, w)


def _wxyz(rot_xyzw):
    """viser wants (w, x, y, z); the wire carries (x, y, z, w)."""
    x, y, z, w = rot_xyzw
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return (w / n, x / n, y / n, z / n)


class HeadsetViz:
    """
    A viser scene showing the head and whichever of hands or controllers is live.

    Deliberately draws only the source the packet says is active. Showing both means
    showing a controller lying on a table as though someone were holding it, which is
    exactly the confusion this is meant to remove.
    """

    def __init__(self, port: int = 8080, label: str = "quest-teleop") -> None:
        import viser                     # deferred: optional dependency

        self.server = viser.ViserServer(port=port, label=label)
        # The wire frame is Y-up. viser defaults to Z-up, so without this the whole scene
        # arrives lying on its side -- which is most of why it was hard to read.
        try:
            self.server.scene.set_up_direction("+y")
        except Exception:
            pass                            # older viser; the axes still say which way
        self.server.scene.world_axes.visible = True
        self._hand_nodes: dict[str, list] = {"L": [], "R": []}
        self._ctrl_nodes: list = []
        self._head = None
        self._ray = None
        self._source = None
        self._parents: dict[str, tuple] = {}
        self._anchor = None                 # (position, yaw quaternion) or None
        self._last_head = None
        self._metrics: dict = {}
        self._panel_text: dict = {}
        self._panel_at = 0.0
        self._seq_at = None
        self.url = f"http://localhost:{port}"
        self._build_gui()

    # ------------------------------------------------------------------ panel

    @staticmethod
    def _bar(v: float, width: int = 8) -> str:
        """A fixed-width analogue bar. Inside a code fence it stays aligned, which is
        what makes a column of them readable at a glance rather than a wall of digits."""
        n = max(0, min(width, int(round(float(v) * width))))
        return "\u2588" * n + "\u00b7" * (width - n)

    @staticmethod
    def _euler(q):
        """Yaw / pitch / roll in degrees, from the right-handed quaternion. Shown instead
        of the raw quaternion because nobody reads a quaternion for orientation."""
        x, y, z, w = q
        sp = max(-1.0, min(1.0, 2.0 * (w * x - y * z)))
        pitch = math.degrees(math.asin(sp))
        yaw = math.degrees(math.atan2(2.0 * (w * y + x * z),
                                      1.0 - 2.0 * (x * x + y * y)))
        roll = math.degrees(math.atan2(2.0 * (w * z + x * y),
                                       1.0 - 2.0 * (x * x + z * z)))
        return yaw, pitch, roll

    def set_metrics(self, metrics: dict) -> None:
        """Stream numbers from the sender, shown beside the poses they belong to."""
        self._metrics = dict(metrics or {})

    def _push(self, handle, text: str) -> None:
        """Only send when it changed. Every assignment is a websocket message, and at
        30 Hz across four panels that is a lot of traffic to spend on identical text."""
        if handle is None:
            return
        if self._panel_text.get(id(handle)) == text:
            return
        self._panel_text[id(handle)] = text
        handle.content = text

    def _update_panel(self, p: HeadsetInput) -> None:
        now = time.monotonic()
        if now - self._panel_at < 0.12:      # ~8 Hz; the numbers are unreadable faster
            return
        self._panel_at = now

        hp, hr = to_right_handed(p.head_pos, p.head_rot)
        yaw, pitch, roll = self._euler(hr)
        rate = 0.0
        if self._seq_at is not None and now > self._seq_at[1]:
            rate = (p.seq - self._seq_at[0]) / (now - self._seq_at[1])
        self._seq_at = (p.seq, now)

        flags = [n for n, bit in (("gaze", 1), ("head", 2), ("left", 4),
                                  ("right", 8), ("extras", 16), ("hands", 32))
                 if p.flags & bit]
        self._push(self._md_head, "```\n"
            f"source   {p.source}\n"
            f"uplink   {rate:6.1f} Hz   seq {p.seq}\n"
            f"flags    {' '.join(flags) or '-'}\n"
            f"head xyz {hp[0]:+6.2f} {hp[1]:+6.2f} {hp[2]:+6.2f}  m\n"
            f"head ypr {yaw:+6.1f} {pitch:+6.1f} {roll:+6.1f}  deg\n"
            f"gaze L   {p.gaze_l[0]:.3f}, {p.gaze_l[1]:.3f}"
            f"{'' if p.gaze_valid else '   (invalid)'}\n"
            f"gaze R   {p.gaze_r[0]:.3f}, {p.gaze_r[1]:.3f}\n"
            f"gaze conf {self._bar(p.gaze_confidence)} {p.gaze_confidence:.2f}\n"
            "```")

        rows = ["```", "        trig     grip     stick        buttons"]
        for name, c in (("left ", p.left), ("right", p.right)):
            btns = " ".join(n for n, b in (("A/X", 1), ("B/Y", 2), ("stick", 4),
                                           ("menu", 8)) if c.buttons & b) or "-"
            rows.append(f"{name}  {self._bar(c.trigger, 5)} {c.trigger:.2f} "
                        f"{self._bar(c.grip, 5)} {c.grip:.2f} "
                        f"{c.stick[0]:+.2f},{c.stick[1]:+.2f}  {btns}")
        rows.append("```")
        self._push(self._md_ctrl, "\n".join(rows))

        rows = ["```", "      trk conf joints  " + "  ".join(f"{f[:4]:>4}" for f in FINGERS)]
        for name, h in (("left ", p.hand_l), ("right", p.hand_r)):
            if h is None or not h.tracked:
                rows.append(f"{name}  -    -     -")
                continue
            pinch = "  ".join(f"{h.pinch_of(f):4.2f}" for f in FINGERS)
            rows.append(f"{name}  y  {h.confidence:4.2f}  {h.joint_count:3d}   {pinch}")
        rows.append("```")
        self._push(self._md_hands, "\n".join(rows))

        m = self._metrics
        if m:
            self._push(self._md_stream, "```\n" + "\n".join(
                f"{k:<10} {v}" for k, v in m.items()) + "\n```")

    def _build_gui(self) -> None:
        with self.server.gui.add_folder("Headset"):
            self._md_head = self.server.gui.add_markdown("```\nwaiting\n```")
        with self.server.gui.add_folder("Controllers"):
            self._md_ctrl = self.server.gui.add_markdown("```\nwaiting\n```")
        with self.server.gui.add_folder("Hands"):
            self._md_hands = self.server.gui.add_markdown("```\nwaiting\n```")
        with self.server.gui.add_folder("Stream"):
            self._md_stream = self.server.gui.add_markdown("```\nwaiting\n```")
        with self.server.gui.add_folder("View"):
            centre = self.server.gui.add_button("Centre on headset")
            reset = self.server.gui.add_button("Clear anchor")
            self._status = self.server.gui.add_text("Anchor", initial_value="world",
                                                    disabled=True)

        @centre.on_click
        def _(_evt) -> None:
            if self._last_head is None:
                self._status.value = "no head pose yet"
                return
            pos, rot = self._last_head
            self._anchor = (pos, _yaw_only(rot))
            self._status.value = "headset"

        @reset.on_click
        def _(_evt) -> None:
            self._anchor = None
            self._status.value = "world"

    def set_skeleton(self, side: str, parents) -> None:
        """
        Tell the viewer how this hand's joints connect.

        Sent by the headset rather than assumed here: it is the only end that knows which
        of Meta's hand rigs the runtime picked, and a parent table that is right for one
        of them draws confident nonsense for the other.
        """
        key = side.upper()[:1]
        self._parents[key] = tuple(int(v) for v in parents)
        # Nothing to redraw here: the next update() rebuilds the segments from it.

    # ------------------------------------------------------------------ anchoring

    def _anchored(self, pos, rot=None):
        """Re-express a pose relative to the anchor, if one is set."""
        if self._anchor is None:
            return pos if rot is None else (pos, rot)
        apos, arot = self._anchor
        inv = _qconj(arot)
        d = (pos[0] - apos[0], pos[1] - apos[1], pos[2] - apos[2])
        p = _qrot(inv, d)
        if rot is None:
            return p
        return p, _qmul(inv, rot)

    # ------------------------------------------------------------------ drawing

    def update(self, p: HeadsetInput) -> None:
        if p is None:
            return
        self._draw_head(p)
        self._update_panel(p)
        source = p.source
        if source == "hands":
            self._clear_controllers()
            self._draw_hand("L", p.hand_l)
            self._draw_hand("R", p.hand_r)
        elif source == "controllers":
            self._clear_hands()
            self._draw_controllers(p)
        else:
            self._clear_hands()
            self._clear_controllers()
        self._source = source

    def _draw_head(self, p: HeadsetInput) -> None:
        world = to_right_handed(p.head_pos, p.head_rot)
        self._last_head = world             # what "centre on headset" captures
        pos, rot = self._anchored(*world)

        # The frustum's own convention is +Z forward, +Y down; ours is -Z forward,
        # +Y up. Without this the head points behind the operator and upside down.
        frust = _qmul(rot, _GL_TO_CV)
        # Forward is -Z after the handedness flip, so the ray runs that way.
        tip = _qrot(rot, (0.0, 0.0, -0.35))
        ray = np.array([[pos, (pos[0] + tip[0], pos[1] + tip[1], pos[2] + tip[2])]],
                       dtype=np.float32)

        if self._head is None:
            self._head = self.server.scene.add_camera_frustum(
                "/head", fov=math.radians(64.0), aspect=16 / 10, scale=0.18,
                color=_HEAD, wxyz=_wxyz(frust), position=pos)
            # Drawn as well as the frustum, not instead: a line is unambiguous about
            # direction whatever convention the frustum turns out to use, and this is
            # exactly the question the view exists to answer.
            self._ray = self.server.scene.add_line_segments(
                "/head/gaze", points=ray, colors=np.array((255, 230, 120), np.uint8),
                thickness=3.0, thickness_units="screen")
        else:
            self._head.position = pos
            self._head.wxyz = _wxyz(frust)
            self._ray.points = ray

    def _draw_hand(self, side: str, hand) -> None:
        nodes = self._hand_nodes[side]
        if hand is None or not hand.tracked or hand.joint_count == 0:
            for n in nodes:
                n.visible = False
            return

        colour = _LEFT if side == "L" else _RIGHT
        pts = np.array([self._anchored(to_right_handed(j)) for j in hand.joints],
                       dtype=np.float32)

        # Bones only when the headset has told us how this rig connects. No table means
        # no lines, which reads as "topology unknown" rather than as broken tracking.
        parents = self._parents.get(side, ())
        seg = [(pts[i], pts[par]) for i, par in enumerate(parents)
               if i < len(pts) and 0 <= par < len(pts) and i != par]
        segments = np.array(seg, dtype=np.float32) if seg else np.zeros((0, 2, 3), np.float32)

        # Pinch strength drives the joint size, so a grasp is visible without reading a
        # number off a HUD.
        grip = max(hand.pinch_of("index"), hand.pinch_of("thumb"))
        size = 0.006 + 0.010 * float(grip)

        if not nodes:
            nodes.append(self.server.scene.add_point_cloud(
                f"/hand{side}/joints", points=pts, colors=np.array(colour, np.uint8),
                point_size=size, point_shape="circle"))
            nodes.append(self.server.scene.add_line_segments(
                f"/hand{side}/bones", points=segments,
                colors=np.array(colour, np.uint8), thickness=2.5,
                thickness_units="screen"))
            pos, rot = self._anchored(*to_right_handed(hand.wrist_pos, hand.wrist_rot))
            nodes.append(self.server.scene.add_frame(
                f"/hand{side}/wrist", axes_length=0.06, axes_radius=0.003,
                wxyz=_wxyz(rot), position=pos))
        else:
            nodes[0].points = pts
            nodes[0].point_size = size
            nodes[1].points = segments
            pos, rot = self._anchored(*to_right_handed(hand.wrist_pos, hand.wrist_rot))
            nodes[2].position = pos
            nodes[2].wxyz = _wxyz(rot)
        for n in nodes:
            n.visible = True

    def _draw_controllers(self, p: HeadsetInput) -> None:
        pairs = (("L", p.left, _LEFT), ("R", p.right, _RIGHT))
        if not self._ctrl_nodes:
            for side, c, colour in pairs:
                pos, rot = self._anchored(*to_right_handed(c.pos, c.rot))
                self._ctrl_nodes.append(self.server.scene.add_frame(
                    f"/controller{side}", axes_length=0.10, axes_radius=0.006,
                    origin_radius=0.012, origin_color=colour,
                    wxyz=_wxyz(rot), position=pos))
        else:
            for node, (_side, c, _colour) in zip(self._ctrl_nodes, pairs):
                pos, rot = self._anchored(*to_right_handed(c.pos, c.rot))
                node.position = pos
                node.wxyz = _wxyz(rot)
                node.visible = True

    def _clear_hands(self) -> None:
        for nodes in self._hand_nodes.values():
            for n in nodes:
                n.visible = False

    def _clear_controllers(self) -> None:
        for n in self._ctrl_nodes:
            n.visible = False

    def stop(self) -> None:
        try:
            self.server.stop()
        except Exception:
            pass
