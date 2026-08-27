"""
OAK-D stereo camera source: the real left/right pair over USB, replacing the mock's
single webcam duplicated with synthetic disparity.

CAM_B and CAM_C are the OAK-D's stereo sockets (left, right). Two things about this
device are not discoverable and have to be asserted here, or the stream comes up
looking broken in ways that do not read as a configuration fault:

  * **The sensor lies about itself.** The OV9782 (colour) and the OV9282 (mono) share
    an I2C interface and there is no way to probe the Bayer filter, so depthai reports
    OV9282 for both and configures a MONO pipeline. The frames then arrive greyscale,
    blown out, and carpeted in a dot pattern -- the Bayer mosaic read as luminance.
    `setSensorType(CameraSensorType.COLOR)` overrides the probe and is the whole fix.
    It needs depthai 3.5.0; see the version note below.

  * **1280x800 is the only clean mode.** It is the OV9782's native readout, so no ISP
    crop or rescale happens. Asking for something larger cannot be satisfied, and the
    smaller modes crop to 4:3 and downscale, which is visibly soft. Two colour streams
    at native need the USB3 link; on USB2 the pipeline stalls rather than degrading, so
    the negotiated speed is checked and reported up front.

depthai version: pin **3.5.0**. 3.6+ ships RVC2 firmware that heap-crashes this device
on any stream config, and <3.5 has no setSensorType, i.e. no fix for the mosaic above.

Frames come out BGR uint8, which is what `gvlink.foveal.build_atlas` and the rest of
the send path already expect -- getCvFrame() converts to interleaved BGR whatever pixel
type was requested, so the requested RGB888p is about what crosses the USB link, not
about the array you get back.
"""

from __future__ import annotations

import glob
import time

import cv2
import numpy as np

import depthai as dai

from gvlink.camera import CameraParams


# The OV9782's native readout. Anything larger is not a mode; anything smaller is a
# crop-and-downscale of this one.
NATIVE_W, NATIVE_H = 1280, 800

# Two colour streams at native are a USB3 load. 25 is what the robot rig runs and is
# known to hold; higher at this resolution starves the link and the queues stall.
NATIVE_MAX_FPS = 25

# Movidius/Luxonis vendor id, and the product ids the device shows *before* depthai
# uploads its firmware. An unbooted bootloader enumerates at USB2 and then re-enumerates
# at 5000M a second later, so reading its speed and calling that "the link" is a false
# alarm that fires on every cold start.
OAK_USB_VID = "03e7"
OAK_BOOTLOADER_PIDS = {"2485", "2486", "f63c"}


def usb_speed_mbps():
    """Negotiated USB speed of the OAK from sysfs, or None if not knowable yet.

    None means "don't know" -- typically the device has not booted -- and the caller
    must not warn on it."""
    for vend in glob.glob("/sys/bus/usb/devices/*/idVendor"):
        try:
            if open(vend).read().strip() != OAK_USB_VID:
                continue
            dev_dir = vend.rsplit("/", 1)[0]
            try:
                pid = open(dev_dir + "/idProduct").read().strip().lower()
            except OSError:
                pid = ""
            if pid in OAK_BOOTLOADER_PIDS:
                continue          # not booted; its speed says nothing about the link
            return int(float(open(dev_dir + "/speed").read().strip()))
        except (OSError, ValueError):
            continue
    return None


class OakNotFound(RuntimeError):
    """No OAK on the bus. Carries the checks worth running, because 'device not found'
    on its own sends people to reinstall depthai when the cable is the problem."""

    def __init__(self):
        super().__init__(
            "no OAK device found. Check, in this order:\n"
            "  lsusb | grep -i 03e7                       (Movidius/Luxonis VID)\n"
            "  python -c 'import depthai as dai; print(dai.Device.getAllAvailableDevices())'\n"
            "  unplug/replug -- an earlier X_LINK crash can leave the device wedged\n"
            "  prefer a USB3 port and cable; two colour streams at native need it\n"
            "  pip show depthai -- this needs 3.5.0 exactly (3.6+ crashes RVC2)")


def _stereo_rectify(K1, D1, K2, D2, size, R, T, fisheye):
    """The one stereoRectify call, so the maps and the advertised geometry agree.

    Both the remap applied to the frames and the intrinsics sent to the viewer come
    from here. Computing them separately is how a sender ends up describing a picture
    it is not actually sending."""
    if fisheye:
        d1, d2 = D1[:4].reshape(4, 1), D2[:4].reshape(4, 1)
        R1, R2, P1, P2, _Q = cv2.fisheye.stereoRectify(
            K1, d1, K2, d2, size, R, T, flags=cv2.CALIB_ZERO_DISPARITY, balance=0.0)
        return R1, R2, P1, P2
    return cv2.stereoRectify(K1, D1, K2, D2, size, R, T,
                             flags=cv2.CALIB_ZERO_DISPARITY, alpha=0)[:4]


def _eeprom_rectification(calib, w, h):
    """(maps, CameraParams) from the device's own factory calibration.

    Good enough to make the pair fuse; a checkerboard calibration of the actual rig is
    better and `--oak-calib` takes one."""
    left, right = dai.CameraBoardSocket.CAM_B, dai.CameraBoardSocket.CAM_C
    K1 = np.array(calib.getCameraIntrinsics(left, w, h), dtype=np.float64)
    K2 = np.array(calib.getCameraIntrinsics(right, w, h), dtype=np.float64)
    D1 = np.array(calib.getDistortionCoefficients(left), dtype=np.float64)
    D2 = np.array(calib.getDistortionCoefficients(right), dtype=np.float64)
    ext = np.array(calib.getCameraExtrinsics(left, right), dtype=np.float64)
    R, T = ext[:3, :3], ext[:3, 3]
    if K1[0, 0] < 10 or K2[0, 0] < 10 or not np.any(T):
        raise ValueError("EEPROM holds no real calibration")

    try:
        fisheye = calib.getDistortionModel(left) == dai.CameraModel.Fisheye
    except Exception:
        fisheye = False

    size = (w, h)
    R1, R2, P1, P2 = _stereo_rectify(K1, D1, K2, D2, size, R, T, fisheye)
    if fisheye:
        m1 = cv2.fisheye.initUndistortRectifyMap(K1, D1[:4].reshape(4, 1), R1, P1,
                                                 size, cv2.CV_32FC1)
        m2 = cv2.fisheye.initUndistortRectifyMap(K2, D2[:4].reshape(4, 1), R2, P2,
                                                 size, cv2.CV_32FC1)
    else:
        m1 = cv2.initUndistortRectifyMap(K1, D1, R1, P1, size, cv2.CV_32FC1)
        m2 = cv2.initUndistortRectifyMap(K2, D2, R2, P2, size, cv2.CV_32FC1)
    return {"left": m1, "right": m2}, CameraParams.from_projection_matrices(P1, P2, w, h)


def _npz_rectification(path, w, h, expect_baseline_m=None, tol_m=0.002):
    """(maps, CameraParams) from a checkerboard stereo calibration npz.

    Expects the cv2.initUndistortRectifyMap outputs stored directly as
    map1x/map1y (left/CAM_B) and map2x/map2y (right/CAM_C), plus P1/P2 and image_size.

    The baseline check exists because an npz records intrinsics, extrinsics and image
    size but nothing identifying the rig it was shot on. Re-case the cameras and the
    old file still loads, still matches on resolution, and is silently wrong -- and
    wrong extrinsics do not blur the picture, they leave residual VERTICAL disparity,
    the one stereo error the operator cannot fuse. It reads as eye strain, not as a
    fault. The baseline is the single number that is both in the file and measurable
    by hand, which is what makes it a usable check. Pass None to skip it."""
    d = np.load(path)
    cw, ch = (int(v) for v in d["image_size"])
    if (cw, ch) != (w, h):
        raise ValueError(f"calibration is {cw}x{ch} but the stream is {w}x{h}")

    P1, P2 = d["P1"], d["P2"]
    params = CameraParams.from_projection_matrices(P1, P2, w, h)
    if expect_baseline_m is not None and abs(params.baseline_m - expect_baseline_m) > tol_m:
        raise ValueError(
            f"calibration baseline is {params.baseline_m * 1000:.1f} mm but this rig "
            f"measures {expect_baseline_m * 1000:.1f} mm (tolerance {tol_m * 1000:.1f} "
            f"mm) -- {path} was shot on a DIFFERENT physical arrangement and its "
            "extrinsics do not describe this one. Re-run the stereo calibration, or "
            "pass the rig's real --baseline if the rig itself changed.")
    return {"left": (d["map1x"], d["map1y"]), "right": (d["map2x"], d["map2y"])}, params


class OakStereoCamera:
    """The OAK-D pair as a `read() -> (ok, left_bgr, right_bgr)` source.

    After construction, `width`, `height` and `fps` report what the device actually
    gave, which may not be what was asked for -- read them back rather than assuming.
    `camera_params` is the rectified geometry to advertise to the viewer, or None when
    nothing usable was found, in which case the frames are passed through raw and
    `rectified` is False.
    """

    def __init__(self, width=NATIVE_W, height=NATIVE_H, fps=NATIVE_MAX_FPS,
                 rectify=True, calib_npz=None, expect_baseline_m=None):
        devices = dai.Device.getAllAvailableDevices()
        if not devices:
            raise OakNotFound()
        print(f"[oak] found: {[(d.deviceId, str(d.state)) for d in devices]}")

        self.width, self.height, self.fps = self._pick_mode(width, height, fps)

        device = dai.Device()
        self.camera_params = None
        self._maps = None
        if rectify:
            self._setup_rectification(device, calib_npz, expect_baseline_m)
        self.rectified = self._maps is not None

        self.pipeline = dai.Pipeline(device)
        cam_l = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_B)
        cam_r = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_C)

        # THE FIX: without this the OV9782 is driven as the mono OV9282 it claims to be
        # and every frame comes back grey and dot-patterned. See the module docstring.
        try:
            cam_l.setSensorType(dai.CameraSensorType.COLOR)
            cam_r.setSensorType(dai.CameraSensorType.COLOR)
        except AttributeError as e:
            raise RuntimeError(
                f"this depthai has no Camera.setSensorType ({e}); without it the "
                "OV9782 is configured as a mono OV9282 and the frames come out grey "
                "with a Bayer dot pattern. Install depthai==3.5.0.") from e

        size = (self.width, self.height)
        out_l = cam_l.requestOutput(size, type=dai.ImgFrame.Type.RGB888p, fps=self.fps)
        out_r = cam_r.requestOutput(size, type=dai.ImgFrame.Type.RGB888p, fps=self.fps)
        self.q_l = out_l.createOutputQueue()
        self.q_r = out_r.createOutputQueue()

        self.pipeline.start()
        time.sleep(0.5)          # first frames after start are not worth waiting on
        print(f"[oak] streaming {self.width}x{self.height}@{self.fps} colour x2, "
              f"{'rectified' if self.rectified else 'RAW (unrectified)'}")

    @staticmethod
    def _pick_mode(width, height, fps):
        w, h, f = int(width), int(height), int(fps)
        if w > NATIVE_W or h > NATIVE_H:
            print(f"[oak] {w}x{h} is above the OV9782's native readout; "
                  f"using {NATIVE_W}x{NATIVE_H}")
            w, h = NATIVE_W, NATIVE_H
        if (w, h) == (NATIVE_W, NATIVE_H) and f > NATIVE_MAX_FPS:
            print(f"[oak] {f} fps at native resolution outruns the USB link; "
                  f"using {NATIVE_MAX_FPS}")
            f = NATIVE_MAX_FPS

        speed = usb_speed_mbps()
        if speed is None:
            # Almost always "not booted yet" -- this runs before dai.Device(). Guessing
            # here would fire a USB2 warning on every cold start.
            print("[oak] USB link speed unknown (device not booted yet)")
        elif speed < 5000:
            print(f"[oak] WARNING: USB link is {speed}M, not USB3. Two colour streams "
                  f"at {w}x{h} may stall -- move to a USB3 port/cable, or "
                  "pass --src 640x480.")
        else:
            print(f"[oak] USB link is {speed}M")
        return w, h, f

    def _setup_rectification(self, device, calib_npz, expect_baseline_m):
        """Prefer a checkerboard npz of this rig; fall back to the device EEPROM;
        stream raw if neither is usable. Streaming raw and saying so beats warping
        confidently through the wrong transform, whose failure does not look like one."""
        if calib_npz:
            try:
                self._maps, self.camera_params = _npz_rectification(
                    calib_npz, self.width, self.height, expect_baseline_m)
                print(f"[oak] rectifying from {calib_npz}")
                return
            except Exception as e:
                print(f"[oak] checkerboard calibration unusable ({e}); trying EEPROM")
        try:
            self._maps, self.camera_params = _eeprom_rectification(
                device.readCalibration(), self.width, self.height)
            print("[oak] rectifying from the device EEPROM")
        except Exception as e:
            self._maps = self.camera_params = None
            print(f"[oak] no usable calibration ({e}); streaming raw and distorted")

    def read(self):
        """Blocking get on both queues -- returns (ok, left_bgr, right_bgr).

        getCvFrame() gives interleaved BGR uint8 regardless of the requested pixel
        type, which is what the atlas/encode path downstream expects."""
        if not self.pipeline.isRunning():
            return False, None, None
        msg_l = self.q_l.get()
        msg_r = self.q_r.get()
        if msg_l is None or msg_r is None:
            return False, None, None
        left, right = msg_l.getCvFrame(), msg_r.getCvFrame()
        if self._maps is not None:
            left = cv2.remap(left, *self._maps["left"], cv2.INTER_LINEAR)
            right = cv2.remap(right, *self._maps["right"], cv2.INTER_LINEAR)
        return True, left, right

    def close(self):
        self.pipeline.stop()
