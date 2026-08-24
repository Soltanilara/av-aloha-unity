"""
Wire protocol for Quest VR Teleoperation.

Both ends -- this package and the Unity/C# headset client -- have to agree on these
bytes exactly, so the struct formats and constants live in one place. Keep in sync
with Assets/Scripts/Net/GvProtocol.cs when that exists.

See docs/PLAN.md section 4.1.
"""

from __future__ import annotations

import struct
import time
from dataclasses import dataclass, replace
from typing import Iterator

# --------------------------------------------------------------------------- ports

DEFAULT_PORTS = {
    "beacon": 15550,
    "control": 15551,
    "video": 15552,
    "input": 15553,
}

# Payload bytes per datagram, sized to sit under a 1500-byte Ethernet MTU once the
# 32-byte video header, 8 bytes of UDP and 20 of IPv4 are added. Exceeding this does
# not fail loudly -- it fragments at the IP layer, where one lost fragment costs the
# whole datagram and our loss statistics stop meaning anything.
MTU_PAYLOAD = 1400

# Over a WireGuard overlay (Tailscale) the tunnel MTU is typically 1280, and the tunnel
# costs ~60 bytes of its own. A 1400-byte payload plus our 40-byte header is 1440 and
# would be IP-fragmented on every single datagram -- and losing one IP fragment destroys
# the whole datagram, so a link with 1% packet loss would show far more than 1% frame
# loss. 1180 leaves room for both headers inside 1280.
MTU_PAYLOAD_TUNNEL = 1180


def now_us() -> int:
    """Monotonic microseconds. Every frame is stamped with this at capture."""
    return time.monotonic_ns() // 1000


def _to_u16(x: float) -> int:
    return max(0, min(65535, int(round(x * 65535.0))))


def _from_u16(v: int) -> float:
    return v / 65535.0


def _newer(a: int, b: int) -> bool:
    """True if frame id `a` is newer than `b`, tolerating u32 wraparound."""
    return ((a - b) & 0xFFFFFFFF) < 0x80000000


# ---------------------------------------------------------------------- video packet

VIDEO_MAGIC = b"GVID"

EYE_LEFT = 0
EYE_RIGHT = 1
EYE_PACKED = 2
EYE_NAMES = {EYE_LEFT: "L", EYE_RIGHT: "R", EYE_PACKED: "LR"}

FLAG_KEYFRAME = 1 << 0
FLAG_FOVEATED = 1 << 1
FLAG_LAST_FRAGMENT = 1 << 2

# Codec of the payload. H.264 is the real transport; MJPEG exists so the Unity Editor
# can display the stream without MediaCodec, which is Android-only -- that keeps the
# shader, the atlas geometry and the stereo layout verifiable on a laptop instead of
# only through a headset.
CODEC_H264 = 0
CODEC_MJPEG = 1
CODEC_NAMES = {CODEC_H264: "h264", CODEC_MJPEG: "mjpeg"}

# 40 bytes. Each layer's stored size travels in the header as an exact pixel count, so
# a frame is self-describing: a receiver places and samples both bands knowing nothing
# but the packet and the canvas size.
#
# Exact pixels rather than a scale fraction, because a fraction has to be quantised and
# any rounding error makes the sampler read slightly outside the stored layer -- into
# the black padding beside it. An 8-bit scale was off by up to two pixels at 1024 wide,
# which is a visible dark fringe, so the size is sent as-is instead.
_VIDEO_HEADER = struct.Struct("!4sIBBHHQHHHHBBHHHH")
VIDEO_HEADER_SIZE = _VIDEO_HEADER.size
assert VIDEO_HEADER_SIZE == 40, VIDEO_HEADER_SIZE


@dataclass
class VideoHeader:
    frame_id: int
    eye: int
    flags: int
    fragment_idx: int
    fragment_count: int
    capture_ts_us: int
    # Centre and extent of the foveal crop this frame's atlas carries, normalised
    # into the SOURCE image (not the canvas). Meaningless unless FLAG_FOVEATED.
    #
    # This rides in the packet header rather than an H.264 SEI because MediaCodec does
    # not hand SEI back to the application. With B-frames disabled -- which we require
    # anyway -- decode output order equals input order, so the headset can match
    # headers to decoded frames with a plain FIFO.
    fovea_x: float = 0.0
    fovea_y: float = 0.0
    fovea_w: float = 0.0
    fovea_h: float = 0.0
    codec: int = CODEC_H264
    # Pixels of its band each layer actually occupies, anchored top-left. Smaller than
    # the band means the rest is black, which costs almost no bitrate and lets the
    # coarse/foveal detail ratio be retuned at runtime without ever reconfiguring the
    # decoder. 0 means "fills the band".
    coarse_px_w: int = 0
    coarse_px_h: int = 0
    fovea_px_w: int = 0
    fovea_px_h: int = 0

    @property
    def foveated(self) -> bool:
        return bool(self.flags & FLAG_FOVEATED)

    @property
    def keyframe(self) -> bool:
        return bool(self.flags & FLAG_KEYFRAME)

    def pack(self) -> bytes:
        return _VIDEO_HEADER.pack(
            VIDEO_MAGIC,
            self.frame_id & 0xFFFFFFFF,
            self.eye,
            self.flags,
            self.fragment_idx,
            self.fragment_count,
            self.capture_ts_us & 0xFFFFFFFFFFFFFFFF,
            _to_u16(self.fovea_x), _to_u16(self.fovea_y),
            _to_u16(self.fovea_w), _to_u16(self.fovea_h),
            self.codec, 0,
            self.coarse_px_w & 0xFFFF, self.coarse_px_h & 0xFFFF,
            self.fovea_px_w & 0xFFFF, self.fovea_px_h & 0xFFFF,
        )

    @classmethod
    def unpack(cls, buf: bytes):
        if len(buf) < VIDEO_HEADER_SIZE:
            return None
        magic, fid, eye, flags, fidx, fcount, ts, fx, fy, fw, fh, codec, _r, cw, chh, fpw, fph = \
            _VIDEO_HEADER.unpack_from(buf)
        if magic != VIDEO_MAGIC:
            return None
        return cls(fid, eye, flags, fidx, fcount, ts,
                   _from_u16(fx), _from_u16(fy), _from_u16(fw), _from_u16(fh), codec,
                   cw, chh, fpw, fph)


def fragment(header: VideoHeader, payload: bytes,
             mtu_payload: int = MTU_PAYLOAD) -> Iterator[bytes]:
    """Yield ready-to-send datagrams for one encoded frame."""
    n = max(1, (len(payload) + mtu_payload - 1) // mtu_payload)
    if n > 0xFFFF:
        raise ValueError(f"frame of {len(payload)} bytes needs {n} fragments (max 65535)")
    for i in range(n):
        chunk = payload[i * mtu_payload:(i + 1) * mtu_payload]
        flags = header.flags | (FLAG_LAST_FRAGMENT if i == n - 1 else 0)
        h = replace(header, fragment_idx=i, fragment_count=n, flags=flags)
        yield h.pack() + chunk


class Reassembler:
    """
    Rebuilds encoded frames from fragments, for one eye.

    The policy here is the entire latency argument (docs/PLAN.md 4.1): a frame is
    either completed or abandoned, never waited for. The moment a fragment of a newer
    frame arrives, any older incomplete frame is dead. Holding it would buy a marginal
    recovery rate at the cost of a frame of delay on every frame that follows -- which
    is precisely the trade a WebRTC jitter buffer makes and we do not want.
    """

    # A frame id this far behind the current one is a restarted sender, not a
    # straggler -- otherwise a sender restart would stall the receiver forever.
    RESTART_GAP = 64

    def __init__(self) -> None:
        self._fid: int | None = None
        self._parts: dict[int, bytes] = {}
        self._count = 0
        self._header: VideoHeader | None = None

        self.frames_completed = 0
        self.frames_dropped = 0
        self.fragments_received = 0
        self.fragments_lost = 0
        self.bytes_received = 0

    def _reset(self) -> None:
        self._fid = None
        self._parts = {}
        self._count = 0
        self._header = None

    def _abandon(self) -> None:
        if self._fid is not None and self._count:
            self.fragments_lost += self._count - len(self._parts)
            self.frames_dropped += 1
        self._reset()

    def push(self, datagram: bytes):
        """Feed one datagram. Returns (header, payload) when a frame completes."""
        h = VideoHeader.unpack(datagram)
        if h is None:
            return None
        self.fragments_received += 1
        self.bytes_received += len(datagram)

        if self._fid is not None and h.frame_id != self._fid:
            if _newer(h.frame_id, self._fid) or \
                    ((self._fid - h.frame_id) & 0xFFFFFFFF) > self.RESTART_GAP:
                self._abandon()
            else:
                return None  # straggler from a frame already given up on

        if self._fid is None:
            self._fid = h.frame_id
            self._count = h.fragment_count
            self._header = h

        # Prefer fragment 0's header; they carry identical frame metadata, but only
        # the last one has FLAG_LAST_FRAGMENT set and it should not leak upward.
        if h.fragment_idx == 0:
            self._header = h

        self._parts[h.fragment_idx] = datagram[VIDEO_HEADER_SIZE:]

        if len(self._parts) == self._count:
            payload = b"".join(self._parts[i] for i in range(self._count))
            hdr = replace(self._header, flags=self._header.flags & ~FLAG_LAST_FRAGMENT)
            self._reset()
            self.frames_completed += 1
            return hdr, payload
        return None


# ---------------------------------------------------------------------- input packet

INPUT_MAGIC = b"GVIN"

# 3 adds the optional hand block. The fixed 158-byte head is byte-identical to 2, but a
# v2 reader would take the hand block for msgpack and produce garbage, so the version
# moves and old readers reject the packet outright instead of misreading it.
INPUT_VERSION = 3

INPUT_GAZE_VALID = 1 << 0
INPUT_HEAD_VALID = 1 << 1
INPUT_LEFT_VALID = 1 << 2
INPUT_RIGHT_VALID = 1 << 3
INPUT_HAS_EXTRAS = 1 << 4
INPUT_HAS_HANDS = 1 << 5

# Finger order in the pinch array, and the joint order the headset sends.
FINGERS = ("thumb", "index", "middle", "ring", "pinky")

BUTTON_ONE = 1 << 0          # A / X
BUTTON_TWO = 1 << 1          # B / Y
BUTTON_STICK = 1 << 2        # thumbstick click
BUTTON_MENU = 1 << 3

# Fixed layout, not msgpack: this is the 90 Hz hot path and its shape -- a head, two
# controllers and a gaze -- is genuinely stable. 158 bytes packs and parses with no
# allocation at either end, against roughly 400 for the equivalent map. Anything that
# does NOT fit this shape rides in the optional msgpack tail (INPUT_HAS_EXTRAS) or, if
# it needs to arrive at all, on the control channel instead.
#
# Replaces the JSON blob the previous generation sent at 20 Hz with field names like
# "rightArmRotation".
_INPUT = struct.Struct(
    "!4sBBIQ"        # magic, version, flags, seq, ts_us
    "7f"             # head: pos xyz, rot xyzw
    "7f2f2fBB"       # left controller: pose, thumbstick, triggers, buttons, pad
    "7f2f2fBB"       # right controller
    "5f"             # gaze: left xy, right xy, confidence
)
INPUT_SIZE = _INPUT.size
assert INPUT_SIZE == 158, INPUT_SIZE

# Field offsets into the unpacked tuple. Written out rather than counted by hand: each
# controller contributes THIRTEEN fields, not twelve, because of its padding byte, and
# an off-by-one there silently shifts gaze into the middle of a controller.
_F_SEQ = 3
_F_TS = 4
_F_HEAD = 5            # 3 pos + 4 rot
_F_LEFT = 12           # 3 pos + 4 rot + 2 stick + 2 triggers + buttons + pad = 13
_F_RIGHT = _F_LEFT + 13
_F_GAZE = _F_RIGHT + 13
assert _F_GAZE == 38, _F_GAZE


# --- hands -------------------------------------------------------------------
#
# Hand tracking and controllers are alternatives, not additions: the runtime gives you
# one or the other, so both travel in the same packet and whichever is live is the one
# marked valid. The hand block is appended only when hands are actually tracked, which
# keeps a controller session at exactly the 158 bytes it was.
#
# Joints are sent as POSITIONS in tracking space rather than as rotations against a
# bind pose. Positions are what a visualiser draws and what a retargeter solves against,
# and they mean the robot needs to know nothing about Meta's skeleton -- no bone
# lengths, no parent table, no handedness convention. Rotations would be smaller on the
# wire and are the better choice for driving an articulated hand model directly; if that
# is ever wanted, add them beside these rather than instead of them.
#
# The joint count is sent per hand rather than fixed, because the SDK ships more than one
# hand skeleton (24 bones for the classic one, 26 for the XR one) and hard-coding either
# would break the moment the runtime picked the other.

_HAND_HEAD = struct.Struct("!BBBB7f5B")   # tracked, conf, joints, pad, wrist pose, pinch
HAND_HEAD_SIZE = _HAND_HEAD.size


@dataclass
class HandState:
    """One tracked hand, as the headset saw it."""
    tracked: bool = False
    confidence: float = 0.0                     # 0..1
    wrist_pos: tuple = (0.0, 0.0, 0.0)
    wrist_rot: tuple = (0.0, 0.0, 0.0, 1.0)
    pinch: tuple = (0.0, 0.0, 0.0, 0.0, 0.0)    # per FINGERS, 0..1
    joints: tuple = ()                          # (n, 3) positions in tracking space

    @property
    def joint_count(self) -> int:
        return len(self.joints)

    def pinch_of(self, finger: str) -> float:
        return self.pinch[FINGERS.index(finger)] if finger in FINGERS else 0.0

    def pack(self) -> bytes:
        n = min(255, len(self.joints))
        head = _HAND_HEAD.pack(
            1 if self.tracked else 0,
            max(0, min(255, int(round(self.confidence * 255)))),
            n, 0,
            *self.wrist_pos, *self.wrist_rot,
            *[max(0, min(255, int(round(v * 255)))) for v in self.pinch],
        )
        flat = [c for j in self.joints[:n] for c in j]
        return head + struct.pack(f"!{3 * n}f", *flat)

    @staticmethod
    def unpack_from(buf: bytes, off: int):
        """Returns (HandState, next_offset) or (None, off) if the buffer is short."""
        if len(buf) < off + HAND_HEAD_SIZE:
            return None, off
        f = _HAND_HEAD.unpack_from(buf, off)
        off += HAND_HEAD_SIZE
        n = f[2]
        need = 12 * n
        if len(buf) < off + need:
            return None, off
        joints = ()
        if n:
            flat = struct.unpack_from(f"!{3 * n}f", buf, off)
            joints = tuple(tuple(flat[i:i + 3]) for i in range(0, 3 * n, 3))
        off += need
        return HandState(
            tracked=bool(f[0]), confidence=f[1] / 255.0,
            wrist_pos=f[4:7], wrist_rot=f[7:11],
            pinch=tuple(v / 255.0 for v in f[11:16]),
            joints=joints,
        ), off


@dataclass
class ControllerState:
    pos: tuple = (0.0, 0.0, 0.0)
    rot: tuple = (0.0, 0.0, 0.0, 1.0)
    stick: tuple = (0.0, 0.0)
    trigger: float = 0.0          # index
    grip: float = 0.0             # hand / middle
    buttons: int = 0

    def held(self, mask: int) -> bool:
        return bool(self.buttons & mask)


@dataclass
class HeadsetInput:
    """
    Everything the headset sends upstream, at display rate.

    Gaze is in normalised SOURCE-IMAGE coordinates -- x right, y DOWN from the top --
    not a direction. The headset already knows the quad geometry needed to project a
    gaze ray onto the image, and doing it there means the robot needs to know nothing
    about the display to crop for it.
    """
    seq: int = 0
    ts_us: int = 0
    flags: int = 0

    head_pos: tuple = (0.0, 0.0, 0.0)
    head_rot: tuple = (0.0, 0.0, 0.0, 1.0)

    left: ControllerState = None
    right: ControllerState = None

    gaze_l: tuple = (0.5, 0.5)
    gaze_r: tuple = (0.5, 0.5)
    gaze_confidence: float = 0.0

    # Present only when the runtime is tracking hands. None means a controller session.
    hand_l: HandState = None
    hand_r: HandState = None

    extras: dict = None

    def __post_init__(self):
        if self.left is None:
            self.left = ControllerState()
        if self.right is None:
            self.right = ControllerState()

    @property
    def hands_valid(self) -> bool:
        """
        Whether this carries hands.

        Checks the objects, not only the flag. The flag is written during `pack`, so a
        packet built in memory -- by a test, or by a robot synthesising input -- has hand
        data and a zero flag, and asking the flag would call that a controller session.
        """
        return (self.hand_l is not None or self.hand_r is not None
                or bool(self.flags & INPUT_HAS_HANDS))

    @property
    def source(self) -> str:
        """
        Which of the two the operator is actually using: "hands", "controllers", or
        "none". The distinction is worth making explicit rather than left to be inferred
        from empty poses, because a robot that maps a wrist to an end effector needs to
        know which stream to believe on the frame the operator puts a controller down.
        """
        if self.hands_valid and (
                (self.hand_l is not None and self.hand_l.tracked)
                or (self.hand_r is not None and self.hand_r.tracked)):
            return "hands"
        if self.flags & (INPUT_LEFT_VALID | INPUT_RIGHT_VALID):
            return "controllers"
        return "none"

    @property
    def gaze_valid(self) -> bool:
        return bool(self.flags & INPUT_GAZE_VALID)

    def pack(self) -> bytes:
        def ctrl(c: ControllerState):
            return (*c.pos, *c.rot, *c.stick, c.trigger, c.grip, c.buttons & 0xFF, 0)

        flags = self.flags
        hands = b""
        if self.hand_l is not None or self.hand_r is not None:
            hands = ((self.hand_l or HandState()).pack()
                     + (self.hand_r or HandState()).pack())
            flags |= INPUT_HAS_HANDS

        blob = b""
        if self.extras:
            import msgpack
            blob = msgpack.packb(self.extras, use_bin_type=True)
            flags |= INPUT_HAS_EXTRAS

        return _INPUT.pack(
            INPUT_MAGIC, INPUT_VERSION, flags & 0xFF,
            self.seq & 0xFFFFFFFF, self.ts_us & 0xFFFFFFFFFFFFFFFF,
            *self.head_pos, *self.head_rot,
            *ctrl(self.left), *ctrl(self.right),
            *self.gaze_l, *self.gaze_r, self.gaze_confidence,
        ) + hands + blob

    @classmethod
    def unpack(cls, buf: bytes):
        if len(buf) < INPUT_SIZE:
            return None
        f = _INPUT.unpack_from(buf)
        if f[0] != INPUT_MAGIC or f[1] != INPUT_VERSION:
            return None

        def ctrl(o):
            return ControllerState(
                pos=f[o:o + 3], rot=f[o + 3:o + 7], stick=f[o + 7:o + 9],
                trigger=f[o + 9], grip=f[o + 10], buttons=f[o + 11])


        flags = f[2]
        off = INPUT_SIZE

        # Order matters: hands first, then msgpack. The hand block is fixed-shape and
        # self-describing, so it can be skipped exactly; msgpack cannot be, which is why
        # it has to be last.
        hand_l = hand_r = None
        if flags & INPUT_HAS_HANDS:
            hand_l, off = HandState.unpack_from(buf, off)
            hand_r, off = HandState.unpack_from(buf, off)
            if hand_l is None or hand_r is None:
                return None      # truncated mid-hand: better nothing than half a pose

        extras = None
        if flags & INPUT_HAS_EXTRAS and len(buf) > off:
            import msgpack
            try:
                extras = msgpack.unpackb(buf[off:], raw=False)
            except Exception:
                extras = None

        return cls(
            seq=f[_F_SEQ], ts_us=f[_F_TS], flags=flags,
            head_pos=f[_F_HEAD:_F_HEAD + 3], head_rot=f[_F_HEAD + 3:_F_HEAD + 7],
            left=ctrl(_F_LEFT), right=ctrl(_F_RIGHT),
            gaze_l=(f[_F_GAZE], f[_F_GAZE + 1]),
            gaze_r=(f[_F_GAZE + 2], f[_F_GAZE + 3]),
            gaze_confidence=f[_F_GAZE + 4],
            hand_l=hand_l, hand_r=hand_r, extras=extras,
        )
