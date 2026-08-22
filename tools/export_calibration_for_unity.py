#!/usr/bin/env python3
"""Export the OAK stereo calibration into the JSON the Unity viewer reads.

Run this in the environment that already has the calibration -- i.e. the GIAVA repo
(`interbotix_ws/src/av_aloha/data_collection_scripts`), which is where depthai and the
checkerboard npz live. Nothing about the OAK pipeline moves into the Unity repo; this
just serialises numbers the Unity display shader needs.

    python export_calibration_for_unity.py --npz /home/devi/foveated_world_model/stereo.npz
    python export_calibration_for_unity.py --eeprom            # read the device directly
    python export_calibration_for_unity.py --eeprom --fisheye  # force the fisheye model

Then push it to the headset (no rebuild needed):

    adb push oak_stereo_calibration.json \
      /sdcard/Android/data/<your.package.name>/files/oak_stereo_calibration.json

...and turn on `gpuUndistort` on the WebRTCStreamer component.

Once Unity is doing the undistort, the sender should stop doing it:
set OAK_RECTIFY["maps"] = None (or skip the cv2.remap call) and
GIAVA_EYE_SCALE=1.0 GIAVA_EYE_INWARD=0, so raw full-resolution frames go over the wire
and are resampled exactly once, at display resolution.
"""

from __future__ import annotations

import argparse
import json
import sys

import numpy as np

try:
    import cv2
except ImportError:
    print("opencv (cv2) is required", file=sys.stderr)
    raise


# The npz key names have drifted between calibration scripts; accept the usual spellings.
_ALIASES = {
    "K1": ("K1", "M1", "cameraMatrix1", "K_left", "mtx_left"),
    "K2": ("K2", "M2", "cameraMatrix2", "K_right", "mtx_right"),
    "D1": ("D1", "dist1", "distCoeffs1", "D_left", "dist_left"),
    "D2": ("D2", "dist2", "distCoeffs2", "D_right", "dist_right"),
    "R": ("R", "R_stereo", "rotation"),
    "T": ("T", "T_stereo", "translation"),
    "R1": ("R1", "Rect1", "R_rect_left"),
    "R2": ("R2", "Rect2", "R_rect_right"),
}


def _pick(data, name):
    for key in _ALIASES[name]:
        if key in data:
            return np.asarray(data[key], dtype=np.float64)
    return None


def from_npz(path, fisheye):
    data = np.load(path)
    keys = list(data.keys())
    print(f"[npz] {path}\n[npz] keys: {keys}")

    K1, K2 = _pick(data, "K1"), _pick(data, "K2")
    D1, D2 = _pick(data, "D1"), _pick(data, "D2")
    if K1 is None or K2 is None:
        raise SystemExit(
            "This npz stores only the baked remap tables, not the intrinsics.\n"
            "The shader needs K/D/R, so either re-run the checkerboard calibration\n"
            "saving cameraMatrix/distCoeffs/R/T, or use --eeprom."
        )

    size = data["image_size"] if "image_size" in data else None
    if size is None:
        raise SystemExit("npz has no image_size")
    w, h = int(size[0]), int(size[1])

    R1, R2 = _pick(data, "R1"), _pick(data, "R2")
    if R1 is None or R2 is None:
        R, T = _pick(data, "R"), _pick(data, "T")
        if R is None or T is None:
            raise SystemExit("npz has neither R1/R2 nor the R/T needed to compute them")
        R1, R2 = _rectify(K1, D1, K2, D2, (w, h), R, T.reshape(3), fisheye)

    return dict(model="fisheye" if fisheye else "pinhole", size=(w, h),
                left=(K1, D1, R1), right=(K2, D2, R2))


def from_eeprom(fisheye_flag):
    import depthai as dai

    device = dai.Device()
    calib = device.readCalibration()
    left, right = dai.CameraBoardSocket.CAM_B, dai.CameraBoardSocket.CAM_C

    # Match the sender's stream resolution -- intrinsics are resolution dependent.
    w, h = 1280, 800
    K1 = np.array(calib.getCameraIntrinsics(left, w, h), dtype=np.float64)
    K2 = np.array(calib.getCameraIntrinsics(right, w, h), dtype=np.float64)
    D1 = np.array(calib.getDistortionCoefficients(left), dtype=np.float64)
    D2 = np.array(calib.getDistortionCoefficients(right), dtype=np.float64)
    ext = np.array(calib.getCameraExtrinsics(left, right), dtype=np.float64)
    R, T = ext[:3, :3], ext[:3, 3]

    fisheye = fisheye_flag
    if not fisheye:
        try:
            fisheye = calib.getDistortionModel(left) == dai.CameraModel.Fisheye
        except Exception:
            pass

    print(f"[eeprom] {w}x{h}  model={'fisheye' if fisheye else 'pinhole'}")
    R1, R2 = _rectify(K1, D1, K2, D2, (w, h), R, T, fisheye)
    return dict(model="fisheye" if fisheye else "pinhole", size=(w, h),
                left=(K1, D1, R1), right=(K2, D2, R2))


def _rectify(K1, D1, K2, D2, size, R, T, fisheye):
    """Same call the sender makes, so Unity and the sender agree on the rectified frame."""
    if fisheye:
        d1 = np.asarray(D1).ravel()[:4].reshape(4, 1)
        d2 = np.asarray(D2).ravel()[:4].reshape(4, 1)
        R1, R2, _P1, _P2, _Q = cv2.fisheye.stereoRectify(
            K1, d1, K2, d2, size, R, T,
            flags=cv2.CALIB_ZERO_DISPARITY, balance=0.0)
        return R1, R2
    R1, R2 = cv2.stereoRectify(
        K1, D1, K2, D2, size, R, T,
        flags=cv2.CALIB_ZERO_DISPARITY, alpha=0)[:2]
    return R1, R2


def _eye(K, D, Rrect, fisheye):
    K = np.asarray(K, dtype=np.float64).reshape(3, 3)
    d = np.asarray(D, dtype=np.float64).ravel()

    if fisheye:
        # OpenCV fisheye: theta * (1 + k1 t^2 + k2 t^4 + k3 t^6 + k4 t^8)
        dist = list(d[:4]) + [0.0] * (4 - len(d[:4]))
        tangential = [0.0, 0.0]
    else:
        # OpenCV pinhole: k1 k2 p1 p2 k3 [...]
        k = list(d) + [0.0] * max(0, 5 - len(d))
        dist = [k[0], k[1], k[4], 0.0]
        tangential = [k[2], k[3]]

    return {
        "fx": float(K[0, 0]), "fy": float(K[1, 1]),
        "cx": float(K[0, 2]), "cy": float(K[1, 2]),
        "dist": [float(v) for v in dist],
        "tangential": [float(v) for v in tangential],
        # camera frame -> rectified frame, row-major. Unity transposes it on load.
        "R": [float(v) for v in np.asarray(Rrect, dtype=np.float64).reshape(9)],
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--npz", help="checkerboard stereo calibration npz")
    src.add_argument("--eeprom", action="store_true", help="read the OAK device EEPROM")
    ap.add_argument("--fisheye", action="store_true",
                    help="force the equidistant/fisheye model (right choice for the wide lenses)")
    ap.add_argument("-o", "--out", default="oak_stereo_calibration.json")
    args = ap.parse_args()

    bundle = from_npz(args.npz, args.fisheye) if args.npz else from_eeprom(args.fisheye)
    fisheye = bundle["model"] == "fisheye"
    w, h = bundle["size"]

    out = {
        "model": bundle["model"],
        "image_size": [w, h],
        "left": _eye(*bundle["left"], fisheye),
        "right": _eye(*bundle["right"], fisheye),
    }

    with open(args.out, "w") as f:
        json.dump(out, f, indent=2)

    # A starting value for videoVFOV: the vertical half-angle the left lens actually
    # covers. Dial it in from there in the headset -- widen until the corners go
    # black, then back off.
    fy, cy = out["left"]["fy"], out["left"]["cy"]
    if fisheye:
        vfov = np.degrees(2 * max(cy, h - cy) / fy)
    else:
        vfov = np.degrees(2 * np.arctan(max(cy, h - cy) / fy))
    print(f"\nwrote {args.out}")
    print(f"suggested videoVFOV starting point: {vfov:.1f} degrees")
    print("push it to the headset with:")
    print(f"  adb push {args.out} /sdcard/Android/data/<package>/files/{args.out}")


if __name__ == "__main__":
    main()
