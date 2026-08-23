"""
H.264 encode/decode wrappers tuned for teleoperation latency, not for file size.

Every option here is chosen against one rule: no stage may hold a frame in order to
improve a later frame. That rules out B-frames, frame-level threading, lookahead, and
a large VBV buffer -- each of which is standard practice for streaming video and each
of which spends latency we cannot afford.
"""

from __future__ import annotations

from fractions import Fraction

import numpy as np

# PyAV is imported lazily. It bundles its own ffmpeg, and so does opencv-python; on
# macOS loading both prints "Class AVFFrameReceiver is implemented in both ..." and
# warns about mysterious crashes. The MJPEG path needs only OpenCV, so not importing
# PyAV at all in that mode removes both the noise and the hazard.
_av = None
_PictureType = None


def _load_av():
    global _av, _PictureType
    if _av is None:
        import av
        from av.video.frame import PictureType
        # libav is chatty about the first frames of an intra-refresh stream, which is
        # expected rather than interesting.
        av.logging.set_level(av.logging.FATAL)
        _av, _PictureType = av, PictureType
    return _av


class EyeEncoder:
    """One H.264 encoder, one eye. Feed BGR, get Annex-B access units."""

    def __init__(self, width: int, height: int, fps: int = 60,
                 bitrate_kbps: int = 12000, intra_refresh: bool = True,
                 gop_seconds: float = 2.0, threads: int = 2) -> None:
        self.width, self.height = width, height
        self._fps = fps
        self._intra_refresh = intra_refresh
        self._gop_seconds = gop_seconds
        self._threads = threads
        self.bitrate_kbps = bitrate_kbps
        self.rebuilds = 0
        self._pts = 0
        self._tb = Fraction(1, fps)
        self._open()

    def set_bitrate(self, bitrate_kbps: int) -> bool:
        """
        Retarget the encoder. Returns True if it actually rebuilt.

        libx264's rate control is fixed at open, and PyAV exposes no reconfigure, so a
        new target means a new encoder. That costs an IDR and a millisecond or two, which
        is why the caller is expected to rate-limit and to ignore small changes -- see
        `BitrateController`. The decoder is untouched: the canvas size never varies, so
        this is invisible on the far end beyond one keyframe.
        """
        kbps = max(200, int(bitrate_kbps))
        if kbps == self.bitrate_kbps:
            return False
        self.close()
        self.bitrate_kbps = kbps
        self._open()
        self.rebuilds += 1
        return True

    def _open(self) -> None:
        av = _load_av()
        width, height = self.width, self.height
        fps, threads = self._fps, self._threads
        intra_refresh, gop_seconds = self._intra_refresh, self._gop_seconds
        bitrate_kbps = self.bitrate_kbps
        params = [
            "bframes=0",        # reordering would cost a frame of delay outright
            "scenecut=0",       # an unscheduled IDR is a bitrate spike, i.e. a latency spike
            "sliced-threads=1",
            "sync-lookahead=0",
            "rc-lookahead=0",
            # SPS/PPS in-band with every IDR. Without this x264 emits them once, and a
            # headset that connects after the stream started decodes nothing at all --
            # a failure that never shows up in a loopback test where the receiver is
            # always listening first.
            "repeat-headers=1",
        ]
        if intra_refresh:
            # Spread the intra data across every frame instead of concentrating it in
            # periodic IDRs. Removes the sawtooth in frame size that otherwise shows
            # up as a periodic hitch. Cost: a receiver joining mid-stream takes one
            # refresh cycle to become clean.
            params += ["intra-refresh=1", "keyint=infinite"]
        else:
            params += [f"keyint={max(1, int(fps * gop_seconds))}"]
        params += [
            f"vbv-maxrate={bitrate_kbps}",
            f"vbv-bufsize={max(1, bitrate_kbps // 10)}",  # ~100 ms, deliberately small
        ]

        cc = av.CodecContext.create("libx264", "w")
        cc.width, cc.height = width, height
        cc.pix_fmt = "yuv420p"
        cc.framerate = Fraction(fps, 1)
        cc.time_base = Fraction(1, fps)
        cc.bit_rate = bitrate_kbps * 1000
        cc.thread_count = threads
        cc.thread_type = "SLICE"   # NOT "FRAME" -- frame threading buffers frames
        cc.options = {"preset": "ultrafast", "tune": "zerolatency",
                      "x264-params": ":".join(params)}

        self._cc = cc

    def encode(self, bgr: np.ndarray, force_key: bool = False):
        """Returns a list of (annexb_bytes, is_keyframe). Normally exactly one."""
        frame = _av.VideoFrame.from_ndarray(bgr, format="bgr24").reformat(format="yuv420p")
        frame.pts = self._pts
        frame.time_base = self._tb
        self._pts += 1
        if force_key:
            # Works even with intra-refresh on, which is why keyframe requests from
            # the receiver stay meaningful in both modes.
            frame.pict_type = _PictureType.I
        return [(bytes(p), bool(p.is_keyframe)) for p in self._cc.encode(frame)]

    def close(self) -> None:
        try:
            self._cc.encode(None)
        except Exception:
            pass


class EyeDecoder:
    """One H.264 decoder, one eye. The bench-side stand-in for MediaCodec."""

    def __init__(self, threads: int = 2) -> None:
        av = _load_av()
        cc = av.CodecContext.create("h264", "r")
        cc.thread_count = threads
        cc.thread_type = "SLICE"
        self._cc = cc
        self.decode_errors = 0

    def decode(self, payload: bytes) -> list[np.ndarray]:
        try:
            frames = self._cc.decode(_av.Packet(payload))
        except Exception:
            self.decode_errors += 1
            return []
        return [f.to_ndarray(format="bgr24") for f in frames]


class MjpegEncoder:
    """
    Intra-only JPEG, one frame per packet.

    Not a serious transport -- several times the bitrate of H.264 for worse quality --
    but every frame stands alone, and Unity can decode it with ImageConversion in the
    Editor. That buys a desktop dev loop for the display shader and the atlas geometry,
    which otherwise could only ever be looked at through a headset.
    """

    def __init__(self, quality: int = 85) -> None:
        import cv2
        self._cv2 = cv2
        self._params = [cv2.IMWRITE_JPEG_QUALITY, int(quality)]

    def encode(self, bgr: np.ndarray, force_key: bool = False):
        del force_key                     # every JPEG frame is a keyframe
        ok, buf = self._cv2.imencode(".jpg", bgr, self._params)
        if not ok:
            return []
        return [(buf.tobytes(), True)]

    def close(self) -> None:
        pass


class MjpegDecoder:
    def __init__(self) -> None:
        import cv2
        self._cv2 = cv2
        self.decode_errors = 0

    def decode(self, payload: bytes) -> list:
        img = self._cv2.imdecode(np.frombuffer(payload, np.uint8),
                                 self._cv2.IMREAD_COLOR)
        if img is None:
            self.decode_errors += 1
            return []
        return [img]
