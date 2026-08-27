"""
OAK-D stereo camera source: the real left/right pair over USB, replacing the mock's
single webcam duplicated with synthetic disparity.

CAM_B and CAM_C are the OAK-D's stereo mono sockets (left, right). Requested as
BGR888p rather than native GRAY8 so the frames drop straight into the same
foveal-atlas / H.264 path (`gvlink.foveal.build_atlas`) as the webcam source, which
expects a 3-channel BGR uint8 image, with no separate greyscale case downstream.
"""

from __future__ import annotations

import depthai as dai


class OakStereoCamera:
    def __init__(self, width: int, height: int, fps: float) -> None:
        self.pipeline = dai.Pipeline()
        cam_l = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_B)
        cam_r = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_C)
        out_l = cam_l.requestOutput(size=(width, height), type=dai.ImgFrame.Type.BGR888p, fps=fps)
        out_r = cam_r.requestOutput(size=(width, height), type=dai.ImgFrame.Type.BGR888p, fps=fps)
        self.q_l = out_l.createOutputQueue()
        self.q_r = out_r.createOutputQueue()
        self.pipeline.start()

    def read(self):
        """Blocking get on both queues -- returns (ok, left_bgr, right_bgr)."""
        if not self.pipeline.isRunning():
            return False, None, None
        msg_l = self.q_l.get()
        msg_r = self.q_r.get()
        if msg_l is None or msg_r is None:
            return False, None, None
        return True, msg_l.getCvFrame(), msg_r.getCvFrame()

    def close(self) -> None:
        self.pipeline.stop()
