"""
What the headset needs to know about the robot's cameras to render them at the right
angular size and in the right place.

Everything here describes the images **as sent**, which means *after* rectification.
Rectification belongs on the robot: it owns the calibration, it has to resample the
frames anyway, and doing it once at the source beats shipping distortion coefficients
and a warp to every viewer. So no distortion model travels over the wire -- if the
headset needed one, something upstream has gone wrong.

The useful consequence is that a rectified pair is described by very little: a focal
length and a principal point per eye, the image size, and the baseline. From those the
viewer can place each image so that a pixel appears in the direction the camera ray
for that pixel actually pointed, which is the whole point -- otherwise the operator is
dialling a field-of-view slider until it "looks about right", and "about right" is not
a thing you can judge from inside a headset.

Note the principal point is per eye and generally *not* the image centre, even after
rectification. That offset is a horizontal shift of the picture relative to the optical
axis, and ignoring it is exactly the error that makes a stereo pair uncomfortable to
fuse while looking perfectly fine in a single eye.
"""

from __future__ import annotations

import json
import math
from dataclasses import dataclass


@dataclass(frozen=True)
class EyeIntrinsics:
    fx: float
    fy: float
    cx: float
    cy: float

    def to_wire(self) -> dict:
        return {"fx": self.fx, "fy": self.fy, "cx": self.cx, "cy": self.cy}

    @staticmethod
    def from_wire(d: dict) -> "EyeIntrinsics":
        return EyeIntrinsics(float(d["fx"]), float(d["fy"]),
                             float(d["cx"]), float(d["cy"]))


@dataclass(frozen=True)
class CameraParams:
    width: int
    height: int
    left: EyeIntrinsics
    right: EyeIntrinsics

    # Metres between the two optical centres, positive. The viewer cannot do anything
    # about a baseline that differs from the operator's own -- that is a real change in
    # perceived scale and no reprojection fixes it without depth -- but it can report
    # it, and it can use it to put a chosen distance at zero disparity, which is the
    # difference between a comfortable pair and eye strain.
    baseline_m: float = 0.0

    # False means the sender is shipping unrectified frames, which this viewer does not
    # attempt to correct. Sent explicitly so the viewer can say so rather than silently
    # rendering a subtly wrong picture.
    rectified: bool = True

    def vfov_deg(self, eye: str = "left") -> float:
        """Vertical field of view, for display and as the manual-mode fallback."""
        f = (self.left if eye == "left" else self.right).fy
        return math.degrees(2.0 * math.atan(self.height / (2.0 * max(f, 1e-6))))

    def hfov_deg(self, eye: str = "left") -> float:
        f = (self.left if eye == "left" else self.right).fx
        return math.degrees(2.0 * math.atan(self.width / (2.0 * max(f, 1e-6))))

    def to_wire(self) -> dict:
        return {
            "w": int(self.width), "h": int(self.height),
            "b": float(self.baseline_m), "rect": bool(self.rectified),
            "l": self.left.to_wire(), "r": self.right.to_wire(),
        }

    @staticmethod
    def from_wire(d: dict) -> "CameraParams":
        return CameraParams(
            width=int(d["w"]), height=int(d["h"]),
            left=EyeIntrinsics.from_wire(d["l"]),
            right=EyeIntrinsics.from_wire(d["r"]),
            baseline_m=float(d.get("b", 0.0)),
            rectified=bool(d.get("rect", True)),
        )

    @staticmethod
    def from_hfov(width: int, height: int, hfov_deg: float,
                  baseline_m: float = 0.0) -> "CameraParams":
        """
        Synthesise plausible parameters for a camera with no calibration.

        For the mock robot and for any webcam whose numbers nobody has measured. It
        assumes square pixels and a centred principal point -- which is what the viewer
        would have assumed anyway from a field-of-view slider, so this is not pretending
        to know more than it does. It just moves the assumption to the end that owns
        the camera.
        """
        fx = width / (2.0 * math.tan(math.radians(hfov_deg) / 2.0))
        eye = EyeIntrinsics(fx=fx, fy=fx, cx=width / 2.0, cy=height / 2.0)
        return CameraParams(width, height, eye, eye, baseline_m, rectified=True)

    @staticmethod
    def from_projection_matrices(P1, P2, width: int, height: int) -> "CameraParams":
        """
        Build from what `cv2.stereoRectify` actually returns.

        This is the bridge most people need, because P1 and P2 *are* the rectified
        projection -- the calibration you started with (K, D, R, T) describes the raw
        cameras and is the wrong thing to send. The baseline falls out of P2's fourth
        column: P2[0,3] is -fx * b, so b = -P2[0,3] / fx.

            import cv2, numpy as np
            R1, R2, P1, P2, Q, _, _ = cv2.stereoRectify(K1, D1, K2, D2, (w, h), R, T)
            params = CameraParams.from_projection_matrices(P1, P2, w, h)
        """
        P1 = [[float(v) for v in row] for row in P1]
        P2 = [[float(v) for v in row] for row in P2]
        fx = P1[0][0]
        baseline = abs(P2[0][3] / fx) if fx else 0.0
        return CameraParams(
            width=int(width), height=int(height),
            left=EyeIntrinsics(P1[0][0], P1[1][1], P1[0][2], P1[1][2]),
            right=EyeIntrinsics(P2[0][0], P2[1][1], P2[0][2], P2[1][2]),
            baseline_m=baseline,
            rectified=True,
        )

    @staticmethod
    def load(path: str) -> "CameraParams":
        """
        Read rectified parameters from JSON, in the same shape as the wire message.

        Deliberately *not* a raw calibration dump: those carry distortion coefficients
        and rectification rotations, which are the inputs to rectification, not its
        output. After `cv2.stereoRectify` the matrices you want are P1 and P2 --
        fx = P1[0,0], cx = P1[0,2], and the baseline is -P2[0,3] / fx. Use
        `from_stereo_rectify` if that is what you have.
        """
        with open(path) as f:
            return CameraParams.from_wire(json.load(f))

    def describe(self) -> str:
        return (f"{self.width}x{self.height} "
                f"f={self.left.fx:.0f}px ({self.hfov_deg():.0f}x{self.vfov_deg():.0f} deg) "
                f"baseline {self.baseline_m * 1000:.0f} mm "
                f"{'rectified' if self.rectified else 'UNRECTIFIED'}")
