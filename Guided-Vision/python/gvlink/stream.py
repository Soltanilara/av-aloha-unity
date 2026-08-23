"""Network glue: encode+fragment+send on one side, receive+reassemble+decode on the other."""

from __future__ import annotations

import socket
import time
from collections import deque

import numpy as np

from .foveal import AtlasLayout, build_atlas
from .protocol import (CODEC_H264, CODEC_MJPEG, FLAG_FOVEATED, FLAG_KEYFRAME,
                       MTU_PAYLOAD, Reassembler, VideoHeader, fragment, now_us)
from .video import EyeDecoder, EyeEncoder, MjpegDecoder, MjpegEncoder


def make_udp_socket(bind=None, sndbuf: int = 4 << 20, rcvbuf: int = 8 << 20):
    """
    A UDP socket with buffers big enough that a burst of fragments from one frame
    does not overflow the kernel queue and show up as 'network loss' that never
    touched the network.
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    for opt, val in ((socket.SO_SNDBUF, sndbuf), (socket.SO_RCVBUF, rcvbuf)):
        try:
            s.setsockopt(socket.SOL_SOCKET, opt, val)
        except OSError:
            pass
    if bind is not None:
        try:
            s.bind(bind)
        except OSError as e:
            s.close()
            # Deliberately not SO_REUSEADDR: on macOS that lets a second receiver bind
            # the same UDP port and silently split the datagrams between them, which is
            # far harder to diagnose than a refusal. A clear message is the better
            # trade -- the usual cause is simply a previous run still holding the port.
            raise OSError(
                f"could not bind UDP {bind[0]}:{bind[1]} ({e.strerror}). "
                f"Another gvlink process is probably still running -- "
                f"try: pkill -f mock_robot; pkill -f bench_receiver"
            ) from e
    return s


class Rolling:
    """Fixed-window sample buffer for latency/rate readouts."""

    def __init__(self, n: int = 600) -> None:
        self._v: deque[float] = deque(maxlen=n)

    def add(self, x: float) -> None:
        self._v.append(x)

    def __len__(self) -> int:
        return len(self._v)

    def mean(self) -> float:
        return sum(self._v) / len(self._v) if self._v else float("nan")

    def pct(self, q: float) -> float:
        if not self._v:
            return float("nan")
        s = sorted(self._v)
        return s[min(len(s) - 1, max(0, int(round(q * (len(s) - 1)))))]

    def max(self) -> float:
        return max(self._v) if self._v else float("nan")


class EyeStreamSender:
    """Source image -> foveal atlas -> H.264 -> fragmented UDP, for one eye."""

    def __init__(self, sock, addr, eye: int, layout: AtlasLayout, fps: int = 60,
                 bitrate_kbps: int = 12000, intra_refresh: bool = True,
                 mtu_payload: int = MTU_PAYLOAD,
                 keyframe_interval_s: float = 2.0,
                 codec: int = CODEC_H264, jpeg_quality: int = 85) -> None:
        self.sock, self.addr, self.eye = sock, addr, eye
        self.layout = layout
        self.mtu = mtu_payload
        self.codec = codec
        if codec == CODEC_MJPEG:
            self.enc = MjpegEncoder(jpeg_quality)
        else:
            self.enc = EyeEncoder(layout.canvas_w, layout.canvas_h, fps,
                                  bitrate_kbps, intra_refresh)
        self.frame_id = 0
        self._force_key = True          # the first frame always
        # Intra-refresh alone gives a late joiner nothing to start from, so an IDR
        # still goes out periodically. Set to 0 once the control channel can request
        # one on demand (docs/PLAN.md phase 4) -- that is strictly better, because
        # every unrequested IDR is a bitrate spike nobody asked for.
        self.keyframe_interval_s = keyframe_interval_s
        self._last_key = 0.0
        self.frames_sent = 0
        self.datagrams_sent = 0
        self.bytes_sent = 0
        self.encode_ms = Rolling(300)

    def request_keyframe(self) -> None:
        self._force_key = True

    def set_bitrate(self, kbps: int) -> bool:
        """
        Retarget the encoder. No-op for codecs without a rate target (MJPEG), so a
        caller adapting the stream does not have to know which codec is running.
        """
        setter = getattr(self.enc, "set_bitrate", None)
        return bool(setter(kbps)) if setter is not None else False

    @property
    def bitrate_kbps(self) -> int:
        return int(getattr(self.enc, "bitrate_kbps", 0))

    def send(self, src_bgr: np.ndarray, gaze, fovea_zoom: float = 1.0):
        """Returns the canvas that was transmitted (handy for a local preview)."""
        # Stamped before any of our own processing, so the receiver's measurement is
        # genuinely end-to-end from capture and includes our encode time.
        ts = now_us()
        wall = time.monotonic()
        if self.keyframe_interval_s and wall - self._last_key >= self.keyframe_interval_s:
            self._force_key = True
        canvas, fovea, coarse_px, fovea_px = build_atlas(src_bgr, self.layout, gaze,
                                                         fovea_zoom)

        t0 = time.perf_counter()
        packets = self.enc.encode(canvas, force_key=self._force_key)
        self.encode_ms.add((time.perf_counter() - t0) * 1000.0)
        if self._force_key:
            self._last_key = wall
        self._force_key = False

        for payload, is_key in packets:
            flags = (FLAG_KEYFRAME if is_key else 0) | (FLAG_FOVEATED if fovea else 0)
            hdr = VideoHeader(self.frame_id, self.eye, flags, 0, 0, ts,
                              *(fovea if fovea else (0.0, 0.0, 0.0, 0.0)),
                              codec=self.codec,
                              coarse_px_w=coarse_px[0], coarse_px_h=coarse_px[1],
                              fovea_px_w=fovea_px[0], fovea_px_h=fovea_px[1])
            for dg in fragment(hdr, payload, self.mtu):
                self.sock.sendto(dg, self.addr)
                self.datagrams_sent += 1
                self.bytes_sent += len(dg)
            self.frame_id = (self.frame_id + 1) & 0xFFFFFFFF
            self.frames_sent += 1
        return canvas


class EyeStreamReceiver:
    """Fragmented UDP -> reassembled frame -> decoded canvas, for one eye."""

    def __init__(self, eye: int, decode: bool = True) -> None:
        self.eye = eye
        self.re = Reassembler()
        self.decode_enabled = decode
        # Chosen from the first frame's header rather than configured: the receiver
        # should not need to be told what the sender is doing.
        self.dec = None
        self.codec = None
        self.latency_ms = Rolling(600)
        self.decode_ms = Rolling(600)
        self.frames_decoded = 0
        self.last_header: VideoHeader | None = None

    def push(self, datagram: bytes):
        """Returns (header, canvas_bgr) on a completed frame, else None."""
        got = self.re.push(datagram)
        if got is None:
            return None
        hdr, payload = got

        # Only meaningful when both ends share a monotonic clock, i.e. the same
        # host. Across machines this needs the control channel's RTT estimate; see
        # docs/PLAN.md section 4.1.
        self.latency_ms.add((now_us() - hdr.capture_ts_us) / 1000.0)
        self.last_header = hdr

        if not self.decode_enabled:
            return hdr, None
        if self.dec is None or hdr.codec != self.codec:
            self.codec = hdr.codec
            self.dec = MjpegDecoder() if hdr.codec == CODEC_MJPEG else EyeDecoder()
        t0 = time.perf_counter()
        frames = self.dec.decode(payload)
        self.decode_ms.add((time.perf_counter() - t0) * 1000.0)
        if not frames:
            return None
        self.frames_decoded += 1
        return hdr, frames[-1]
