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
"""

from __future__ import annotations

import math

import numpy as np

from .protocol import FINGERS, HeadsetInput

# Meta's hand skeleton, as parent indices. Bone 0 is the wrist; each entry is the joint
# its bone starts from. Used only to draw connecting lines -- the joint positions
# themselves arrive absolute, so a wrong entry here is a cosmetic problem, not a
# geometric one.
_HAND_PARENTS = (
    0, 0, 0,                 # wrist, forearm stub, thumb trapezium
    2, 3, 4,                 # thumb
    0, 6, 7,                 # index
    0, 9, 10,                # middle
    0, 12, 13,               # ring
    0, 15, 16, 17,           # pinky
    5, 8, 11, 14, 18,        # finger tips, parented to the last joint of each finger
)

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

    def __init__(self, port: int = 8080, label: str = "guided-vision") -> None:
        import viser                     # deferred: optional dependency

        self.server = viser.ViserServer(port=port, label=label)
        self.server.scene.world_axes.visible = True
        self._hand_nodes: dict[str, list] = {"L": [], "R": []}
        self._ctrl_nodes: list = []
        self._head = None
        self._source = None
        self.url = f"http://localhost:{port}"

    # ------------------------------------------------------------------ drawing

    def update(self, p: HeadsetInput) -> None:
        if p is None:
            return
        self._draw_head(p)
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
        pos, rot = to_right_handed(p.head_pos, p.head_rot)
        if self._head is None:
            # A frustum rather than bare axes: it shows which way the operator is facing
            # at a glance, which is the thing you actually want to know.
            self._head = self.server.scene.add_camera_frustum(
                "/head", fov=math.radians(64.0), aspect=16 / 10, scale=0.18,
                color=_HEAD, wxyz=_wxyz(rot), position=pos)
        else:
            self._head.position = pos
            self._head.wxyz = _wxyz(rot)

    def _draw_hand(self, side: str, hand) -> None:
        nodes = self._hand_nodes[side]
        if hand is None or not hand.tracked or hand.joint_count == 0:
            for n in nodes:
                n.visible = False
            return

        colour = _LEFT if side == "L" else _RIGHT
        pts = np.array([to_right_handed(j) for j in hand.joints], dtype=np.float32)

        # Bones, as segment pairs. Parents beyond what this skeleton has are skipped
        # rather than clamped, so an unfamiliar skeleton draws fewer lines instead of
        # nonsense ones.
        seg = [(pts[i], pts[par]) for i, par in enumerate(_HAND_PARENTS)
               if i < len(pts) and par < len(pts) and i != par]
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
            pos, rot = to_right_handed(hand.wrist_pos, hand.wrist_rot)
            nodes.append(self.server.scene.add_frame(
                f"/hand{side}/wrist", axes_length=0.06, axes_radius=0.003,
                wxyz=_wxyz(rot), position=pos))
        else:
            nodes[0].points = pts
            nodes[0].point_size = size
            nodes[1].points = segments
            pos, rot = to_right_handed(hand.wrist_pos, hand.wrist_rot)
            nodes[2].position = pos
            nodes[2].wxyz = _wxyz(rot)
        for n in nodes:
            n.visible = True

    def _draw_controllers(self, p: HeadsetInput) -> None:
        pairs = (("L", p.left, _LEFT), ("R", p.right, _RIGHT))
        if not self._ctrl_nodes:
            for side, c, colour in pairs:
                pos, rot = to_right_handed(c.pos, c.rot)
                self._ctrl_nodes.append(self.server.scene.add_frame(
                    f"/controller{side}", axes_length=0.10, axes_radius=0.006,
                    origin_radius=0.012, origin_color=colour,
                    wxyz=_wxyz(rot), position=pos))
        else:
            for node, (_side, c, _colour) in zip(self._ctrl_nodes, pairs):
                pos, rot = to_right_handed(c.pos, c.rot)
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
