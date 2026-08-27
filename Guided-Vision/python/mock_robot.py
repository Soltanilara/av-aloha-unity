#!/usr/bin/env python3
"""
Mock robot camera sender.

Takes a webcam (or a synthetic detail chart), duplicates it into a LEFT/RIGHT
pair with a synthetic disparity, packs each eye into a foveal atlas, encodes
H.264 and fires it at the headset over UDP.

--source oak swaps the fake pair for a real OAK-D over USB (rectified, with its
own calibration advertised to the viewer); everything downstream is unchanged,
which is the point of keeping the two sources behind one read().

It also listens for the gaze uplink, so the foveal path can be driven end to end
from a laptop with the mouse standing in for an eye -- run bench_receiver.py with
--gaze-mouse at the other end.

    uv run mock_robot.py --source pattern
    uv run mock_robot.py --source webcam --host 100.64.0.5
    uv run mock_robot.py --source oak        (uv sync --extra oak; needs depthai 3.5.0)
"""

from __future__ import annotations

import argparse
import math
import socket
import threading
import time
from dataclasses import replace

import cv2
import numpy as np

from gvlink.beacon import Beacon, build_payload
from gvlink.foveal import MIN_CANVAS, AtlasLayout, SaccadeWidener, bandwidth_note
from gvlink.camera import CameraParams
from gvlink.ratecontrol import BitrateController
from gvlink.robotlink import RobotLink
from gvlink.pattern import make_chart
from gvlink.protocol import (INPUT_MAGIC, INPUT_SIZE, INPUT_VERSION,
                             INPUT_HEAD_VALID, INPUT_LEFT_VALID, INPUT_RIGHT_VALID,
                             BUTTON_ONE, BUTTON_TWO, CODEC_H264, CODEC_MJPEG,
                             CODEC_NAMES, DEFAULT_PORTS, EYE_LEFT, EYE_RIGHT,
                             MTU_PAYLOAD, MTU_PAYLOAD_TUNNEL, HeadsetInput)
from gvlink.stream import EyeStreamSender, make_udp_socket
from gvlink.ui import HeadsetUi, button, choice, rng, toggle


def parse_wh(s: str) -> tuple[int, int]:
    w, h = s.lower().split("x")
    return int(w), int(h)


class InputListener:
    """
    Receives the headset uplink -- head and controller poses, buttons, gaze -- on a
    background thread.

    Last-wins, never queued: a pose from two frames ago is worse than no pose, so a
    slow consumer must see the newest sample rather than work through a backlog.
    """

    STALE_S = 0.5

    def __init__(self, port: int, on_button=None) -> None:
        self.sock = make_udp_socket(("0.0.0.0", port))
        self.sock.settimeout(0.2)
        self._lock = threading.Lock()
        self._pkt: HeadsetInput | None = None
        self._at = 0.0
        self._prev_buttons = (0, 0)
        self._on_button = on_button
        self.packets = 0
        self.rejected = 0
        self._reported = set()
        self.rate_hz = 0.0
        self._rate_at = time.monotonic()
        self._rate_n = 0
        self._stop = threading.Event()
        self._t = threading.Thread(target=self._run, daemon=True, name="gv-input")
        self._t.start()

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                data, addr = self.sock.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            p = HeadsetInput.unpack(data)
            if p is None:
                self.rejected += 1
                self._report_reject(data, addr)
                continue
            now = time.monotonic()
            with self._lock:
                self._pkt, self._at = p, now
                self.packets += 1
                self._rate_n += 1
                if now - self._rate_at >= 1.0:
                    self.rate_hz = self._rate_n / (now - self._rate_at)
                    self._rate_at, self._rate_n = now, 0
                prev = self._prev_buttons
                self._prev_buttons = (p.left.buttons, p.right.buttons)
            if self._on_button is not None:
                # Edge-triggered: report presses, not the held state, so a caller does
                # not have to debounce a 90 Hz stream itself.
                for hand, was, now_b in (("L", prev[0], p.left.buttons),
                                         ("R", prev[1], p.right.buttons)):
                    pressed = now_b & ~was
                    if pressed:
                        self._on_button(hand, pressed)

    def _report_reject(self, data: bytes, addr) -> None:
        """Say why a datagram was thrown away -- once per distinct reason.

        A rejected packet used to `continue` in silence, so a headset speaking the
        wrong protocol version looked exactly like a headset sending nothing: the
        uplink reads 0 Hz either way, and the one number that would tell them apart
        was in the bytes being dropped. Once per reason, because this is a 90 Hz
        stream and a per-packet warning is a warning nobody reads."""
        magic = bytes(data[:4])
        version = data[4] if len(data) > 4 else None
        if len(data) < INPUT_SIZE:
            key, why = ("short", f"{len(data)} bytes, need {INPUT_SIZE}")
        elif magic != INPUT_MAGIC:
            key, why = ("magic", f"magic {magic!r}, expected {INPUT_MAGIC!r} "
                                 f"-- something else is talking to this port")
        elif version != INPUT_VERSION:
            key, why = (("version", version),
                        f"protocol v{version}, this robot speaks v{INPUT_VERSION} "
                        f"-- the headset app is an older build; rebuild and redeploy it")
        else:
            key, why = ("other", "unpack failed")
        if key in self._reported:
            return
        self._reported.add(key)
        print(f"uplink: dropping packets from {addr[0]}: {why}")

    def fresh(self):
        with self._lock:
            if self._pkt is None or time.monotonic() - self._at > self.STALE_S:
                return None
            return self._pkt

    def stop(self) -> None:
        self._stop.set()
        self.sock.close()


def overlay(frame: np.ndarray, frame_no: int) -> None:
    """Motion and an identifiable frame number, so latency is visible on camera too."""
    h, w = frame.shape[:2]
    t = frame_no / 60.0
    # sweeping bar: gives the encoder real motion to deal with
    x = int((0.5 + 0.5 * math.sin(t * 1.7)) * (w - 40))
    cv2.rectangle(frame, (x, 0), (x + 40, h), (0, 0, 255), -1)
    # rotating hand
    cx, cy, r = w - 130, 130, 100
    a = t * 4.0
    cv2.circle(frame, (cx, cy), r, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.line(frame, (cx, cy),
             (int(cx + r * math.cos(a)), int(cy + r * math.sin(a))),
             (0, 255, 255), 4, cv2.LINE_AA)
    cv2.putText(frame, f"#{frame_no:06d}", (20, 60),
                cv2.FONT_HERSHEY_SIMPLEX, 1.6, (0, 255, 0), 3, cv2.LINE_AA)


def eye_view(src: np.ndarray, eye: int, disparity: int) -> np.ndarray:
    """
    Synthetic stereo pair from one image. The shift is fake parallax -- it exists
    only so a wrong left/right assignment in the headset is immediately obvious.
    """
    shift = -disparity if eye == EYE_LEFT else disparity
    out = np.roll(src, shift, axis=1)
    label = "LEFT" if eye == EYE_LEFT else "RIGHT"
    colour = (80, 220, 80) if eye == EYE_LEFT else (80, 160, 255)
    h, w = out.shape[:2]
    cv2.putText(out, label, (w // 2 - 190, h // 2),
                cv2.FONT_HERSHEY_SIMPLEX, 4.0, colour, 10, cv2.LINE_AA)
    return out


def format_input(p, hs_state=None) -> str:
    """One line summarising the freshest uplink sample -- whichever of hands or
    controllers is live -- for --print-poses.

    Carries the validity flags and the headset's own `hs/state` because the failure
    this line exists to diagnose is "everything reads zero", and a zero has two very
    different causes that look identical in the numbers: a pose the headset never
    filled in, and a headset sitting on a desk with tracking paused. `flags` separates
    the first; `mounted` separates the second."""
    if p is None:
        return "pose: no uplink (nothing received from the headset in the last 0.5s)"
    hx, hy, hz = p.head_pos
    parts = [f"head=({hx:+.2f},{hy:+.2f},{hz:+.2f})"
             + ("" if p.flags & INPUT_HEAD_VALID else "!invalid"),
             f"src={p.source}", f"flags={p.flags:#04x}"]
    if hs_state:
        # Tracking pauses when the headset comes off, and every pose then reads zero
        # while the packets keep arriving at full rate -- which looks exactly like a
        # wiring bug in the sender. Say which it is.
        worn = hs_state.get("mounted")
        if worn is not None:
            parts.append("worn" if worn else "NOT WORN (tracking paused)")
        if hs_state.get("eye_tracking") is False:
            parts.append("eye-tracking off")
    if p.source == "hands":
        for name, h in (("L", p.hand_l), ("R", p.hand_r)):
            if h is not None and h.tracked:
                wx, wy, wz = h.wrist_pos
                parts.append(f"{name} wrist=({wx:+.2f},{wy:+.2f},{wz:+.2f}) "
                             f"pinch={h.pinch_of('index'):.2f} joints={len(h.joints)}")
    else:
        for name, c, valid in (("L", p.left, INPUT_LEFT_VALID),
                               ("R", p.right, INPUT_RIGHT_VALID)):
            # An untracked controller still occupies its slot in the fixed-layout
            # packet, so its pose reads as a perfectly good origin. Say "untracked"
            # rather than print a zero that looks like a measurement.
            if not p.flags & valid:
                parts.append(f"{name} untracked")
                continue
            cx, cy, cz = c.pos
            qx, qy, qz, qw = c.rot
            parts.append(f"{name} pos=({cx:+.2f},{cy:+.2f},{cz:+.2f}) "
                         f"rot=({qx:+.2f},{qy:+.2f},{qz:+.2f},{qw:+.2f}) "
                         f"trig={c.trigger:.2f} grip={c.grip:.2f} btn={c.buttons:#04x}")
    return "pose: " + " ".join(parts)


def register_demo_io(link: RobotLink) -> None:
    """
    A worked example of the control interface -- this is the shape real robot I/O takes.

    Adding an output is `link.publish(topic, dict)`. Adding an input is a subscriber or
    a handler. There is no schema to declare and nothing to keep in step across the two
    languages.
    """
    state = {"gripper": 0.04, "homed": False}

    @link.handler("arm/home")
    def home(req):
        arm = (req or {}).get("arm", "both")
        state["homed"] = True
        print(f"  [robot] homing {arm}")
        return {"ok": True, "arm": arm}

    @link.handler("arm/info")
    def info(_req):
        return {"dof": 6, "name": "mock", "gripper": state["gripper"]}

    def set_gripper(msg):
        state["gripper"] = float((msg or {}).get("width", state["gripper"]))
        print(f"  [robot] gripper -> {state['gripper']:.3f} m")

    link.subscribe("gripper/cmd", set_gripper)

    def publish_state():
        t = 0.0
        while True:
            time.sleep(0.05)                       # 20 Hz, reliable lane
            t += 0.05
            if not link.connected:
                continue
            link.publish("arm/state", {
                "q": [math.sin(t + i) * 0.5 for i in range(6)],
                "grip": state["gripper"],
                "homed": state["homed"],
                "t": t,
            })

    threading.Thread(target=publish_state, daemon=True, name="gv-demo-state").start()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="",
                    help="headset address. Left empty (the default) the sender waits "
                         "for the headset to say hello and streams back to wherever "
                         "that came from, which is what the in-headset menu does.")
    ap.add_argument("--video-port", type=int, default=DEFAULT_PORTS["video"])
    ap.add_argument("--input-port", type=int, default=DEFAULT_PORTS["input"])
    ap.add_argument("--control-port", type=int, default=DEFAULT_PORTS["control"],
                    help="control channel; the headset connects here")
    ap.add_argument("--source", choices=("pattern", "webcam", "oak"), default="pattern",
                    help="oak needs the depthai extra: uv sync --extra oak")
    ap.add_argument("--cam", type=int, default=0)
    ap.add_argument("--src", type=parse_wh, default="1920x1200",
                    help="per-eye source resolution (default 1920x1200)")
    ap.add_argument("--canvas", type=parse_wh, default="1024x1024",
                    help="transmitted canvas per eye (default 1024x1024)")
    ap.add_argument("--coarse-scale", type=float, default=0.35,
                    help="fraction of the coarse band the periphery fills; lower is "
                         "blurrier and makes the foveal patch far more obvious")
    ap.add_argument("--fovea-scale", type=float, default=0.5,
                    help="fraction of the fovea band the 1:1 crop fills; lower is a "
                         "smaller, more eye-like patch")
    ap.add_argument("--fps", type=int, default=60)
    ap.add_argument("--bitrate", type=int, default=12000, help="kbps per eye")
    ap.add_argument("--no-fovea", action="store_true",
                    help="send the plain full-frame canvas (the Quest 2/3 path)")
    ap.add_argument("--auto-gaze", action="store_true",
                    help="sweep the fovea when no real gaze is arriving")
    ap.add_argument("--ui-demo", action="store_true",
                    help="publish a moving guide, a couple of markers and a "
                         "robot-defined session menu, so the headset's ui/* rendering "
                         "can be checked without writing robot code first")
    ap.add_argument("--viser", action="store_true",
                    help="serve a live 3D view of the headset, hands and controllers "
                         "(needs the viz extra: uv sync --extra viz)")
    ap.add_argument("--viser-port", type=int, default=8080)
    ap.add_argument("--hfov", type=float, default=90.0, metavar="DEG",
                    help="horizontal field of view to advertise when there is no "
                         "calibration file (the mock camera has none)")
    ap.add_argument("--baseline", type=float, default=None, metavar="M",
                    help="metres between the two optical centres (default 0.075). "
                         "Advertised to the viewer when there is no calibration, and "
                         "with --oak-calib it is the rig's hand-measured baseline that "
                         "the calibration file is checked against.")
    ap.add_argument("--calib", metavar="JSON",
                    help="rectified camera parameters to advertise; see gvlink/camera.py")
    ap.add_argument("--oak-calib", metavar="NPZ",
                    help="checkerboard stereo calibration for --source oak (as written "
                         "by the foveated_world_model calibration, e.g. stereo.npz). "
                         "Preferred over the device EEPROM; --baseline is then used as "
                         "the rig's measured baseline to check the file belongs to it.")
    ap.add_argument("--no-oak-rectify", action="store_true",
                    help="send the OAK pair raw and distorted instead of rectifying it")
    ap.add_argument("--mtu", type=int, default=None, metavar="BYTES",
                    help=f"UDP payload per datagram (default {MTU_PAYLOAD}); use "
                         f"{MTU_PAYLOAD_TUNNEL} over Tailscale/WireGuard, whose 1280-byte "
                         f"tunnel would otherwise IP-fragment every datagram")
    ap.add_argument("--tunnel", action="store_true",
                    help=f"shorthand for --mtu {MTU_PAYLOAD_TUNNEL}")
    ap.add_argument("--no-adapt", action="store_true",
                    help="hold the bitrate fixed instead of adapting it to the link")
    ap.add_argument("--min-bitrate", type=int, default=800, metavar="KBPS")
    ap.add_argument("--saccade-zoom", type=float, default=2.5, metavar="X",
                    help="while the eye is moving fast, widen the foveal patch to cover "
                         "X times its width and height, at no bandwidth cost "
                         "(1.0 disables)")
    ap.add_argument("--disparity", type=int, default=24)
    ap.add_argument("--no-intra-refresh", action="store_true")
    ap.add_argument("--codec", choices=("h264", "mjpeg"), default="h264",
                    help="mjpeg lets the Unity Editor display the stream without "
                         "MediaCodec; use it for desktop verification, not for real use")
    ap.add_argument("--jpeg-quality", type=int, default=85)
    ap.add_argument("--duration", type=float, default=0.0, help="0 = until Ctrl-C")
    ap.add_argument("--preview", action="store_true", help="show what is being sent")
    ap.add_argument("--print-poses", action="store_true",
                    help="print head/hand/controller poses from the uplink to the terminal")
    ap.add_argument("--name", default="mock-robot", help="name shown in the headset's robot list")
    ap.add_argument("--no-beacon", action="store_true",
                    help="do not advertise on the LAN (the headset must be told the address)")
    ap.add_argument("--no-tight-canvas", action="store_true",
                    help="encode the full requested canvas even where the two layers "
                         "do not fill it. At the defaults that is 81%% black, which "
                         "costs about half the encode time for nothing -- this flag "
                         "exists to measure that, not because you want it")
    args = ap.parse_args()

    src_w, src_h = args.src if isinstance(args.src, tuple) else parse_wh(args.src)
    def tighten(lay: AtlasLayout) -> AtlasLayout:
        """
        Shrink the canvas to what the two layers actually occupy.

        The canvas size and the layer scales are chosen independently, so the default
        combination leaves 81% of the canvas black -- and the encoder pays for every one
        of those macroblocks. Tightening keeps the layer pixels identical and the wire
        format identical (the shader works in canvas fractions, so it cannot tell), and
        is worth roughly 2x encode, 1.3x decode and 14% bitrate.

        The requested canvas becomes an upper bound rather than an instruction. Nothing
        downstream needs to agree: the headset reads the canvas size out of the stream.
        """
        return lay if args.no_tight_canvas else lay.tightened()

    base_layout = tighten(AtlasLayout(*(args.canvas if isinstance(args.canvas, tuple)
                                        else parse_wh(args.canvas)),
                                      coarse_scale=args.coarse_scale,
                                      fovea_scale=args.fovea_scale))

    # Boxed so the send loop and the session handler share one value. The viewer may ask
    # for a different shape -- it is the end that knows its own decoder and its own link
    # -- and anything it does not specify keeps the command-line default.
    layout = [base_layout]

    def layout_for(sess) -> AtlasLayout:
        if sess is None:
            return base_layout
        try:
            if not sess.canvas:
                # Nothing requested, so this is the robot's own choice to make.
                return base_layout
            w, h = sess.canvas
            # A canvas the viewer asked for is honoured as given -- **not** tightened.
            # The viewer allocates its decoder texture at the size it asked for and
            # divides every layer span by that number, so a canvas we changed underneath
            # it would leave it sampling the wrong part of the atlas: a wrong picture
            # rather than a failed one, and one that would look like a display bug. It
            # has already tightened its own request (GvAtlas.Tighten); tightening it
            # again here would be us second-guessing the end that owns the decoder.
            #
            # The clamp is a guard against a broken client, not a negotiation. Its floor
            # matches GvAtlas.MinCanvas precisely so it never fires on a real request.
            return AtlasLayout(
                max(MIN_CANVAS, min(2048, int(w))), max(MIN_CANVAS, min(2048, int(h))),
                coarse_scale=sess.coarse_scale if sess.coarse_scale else base_layout.coarse_scale,
                fovea_scale=sess.fovea_scale if sess.fovea_scale else base_layout.fovea_scale)
        except ValueError as e:
            # A viewer asking for something impossible must not take the robot down.
            print(f"  ignoring requested stream shape: {e}")
            return base_layout

    cap = None
    oak_cam = None
    if args.source == "webcam":
        cap = cv2.VideoCapture(args.cam)
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, src_w)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, src_h)
        if not cap.isOpened():
            print(f"could not open camera {args.cam}")
            return 1
    elif args.source == "oak":
        try:
            from gvlink.oakcam import OakStereoCamera
        except ImportError:
            print("  --source oak needs depthai; run: uv sync --extra oak")
            return 1
        try:
            oak_cam = OakStereoCamera(src_w, src_h, args.fps,
                                      rectify=not args.no_oak_rectify,
                                      calib_npz=args.oak_calib,
                                      # Only checked when the operator actually
                                      # measured it; the default is a mock's number
                                      # and would reject a good calibration.
                                      expect_baseline_m=args.baseline)
        except Exception as e:
            print(f"  {e}")
            return 1
        # The device is the authority on what it actually gave us: it silently refuses
        # modes it cannot serve, and everything below -- encoder, atlas, the shape
        # advertised to the headset -- has to describe the frames that will really
        # arrive, not the ones that were asked for.
        src_w, src_h, args.fps = oak_cam.width, oak_cam.height, oak_cam.fps
    chart = make_chart(src_w, src_h)

    sock = make_udp_socket()
    # Only meaningful when --host was given; otherwise the destination comes from the
    # headset's hello.
    addr = (args.host, args.video_port) if args.host else None
    codec = CODEC_MJPEG if args.codec == "mjpeg" else CODEC_H264
    senders = {}

    mtu = args.mtu if args.mtu else (MTU_PAYLOAD_TUNNEL if args.tunnel else MTU_PAYLOAD)

    def build_senders(dest, use_codec):
        return {
            eye: EyeStreamSender(sock, dest, eye, layout[0], args.fps, args.bitrate,
                                 not args.no_intra_refresh, codec=use_codec,
                                 jpeg_quality=args.jpeg_quality, mtu_payload=mtu)
            for eye in (EYE_LEFT, EYE_RIGHT)
        }

    link = RobotLink(args.control_port, args.name)
    register_demo_io(link)
    link.start()
    def on_button(hand, mask):
        names = []
        if mask & BUTTON_ONE:
            names.append("A/X")
        if mask & BUTTON_TWO:
            names.append("B/Y")
        if names:
            print(f"  [robot] {hand} button {'+'.join(names)}")

    viz = None
    if args.viser:
        try:
            from gvlink.viz import HeadsetViz
            viz = HeadsetViz(port=args.viser_port)
            print(f"  viser: {viz.url}")
        except ImportError:
            # Missing an optional dependency must not stop a robot from streaming.
            print("  viser requested but not installed; run: uv sync --extra viz")

    input_rx = InputListener(args.input_port, on_button=on_button)

    beacon = None
    if not args.no_beacon:
        payload = build_payload(
            args.name,
            [{"id": "stereo0", "w": src_w, "h": src_h, "fps": args.fps,
              "canvasW": base_layout.canvas_w, "canvasH": base_layout.canvas_h,
              "codec": args.codec}],
            ports={"control": args.control_port,
                   "video": args.video_port, "input": args.input_port},
            foveation=not args.no_fovea)
        beacon = Beacon(payload).start()
        print(f"  beacon: '{args.name}' on :{DEFAULT_PORTS['beacon']} -> "
              f"{', '.join(beacon.addresses)}")

    if args.host:
        print(f"sending to {args.host}:{args.video_port} (fixed), "
              f"gaze uplink on :{args.input_port}")
    else:
        print(f"control channel on :{args.control_port} (the headset connects here), "
              f"gaze uplink on :{args.input_port}")
    print(f"  {bandwidth_note(src_w, src_h, base_layout)}")
    print(f"  {args.fps} fps, {args.bitrate} kbps/eye, codec={CODEC_NAMES[codec]}, "
          f"fovea={'off' if args.no_fovea else 'on'}, "
          f"intra-refresh={'off' if args.no_intra_refresh else 'on'}")

    period = 1.0 / args.fps
    active = None            # (session id, addr, codec, shape) the senders are built for
    stream_started = None    # when the current viewer appeared; idle time is not fps
    want_fovea = not args.no_fovea
    idle_notice = 0.0
    last_ui = 0.0
    last_pose_print = 0.0
    started = time.monotonic()
    next_at = started
    n = 0
    last_report = started
    last_bytes = 0
    gaze_src = "center"
    widener = SaccadeWidener(max_zoom=args.saccade_zoom)

    # The viewer is the only end that can see what actually arrived, so it reports and
    # the sender reacts. Published on the control channel that already exists rather
    # than a new socket: it is a handful of bytes twice a second.
    rate = BitrateController(start_kbps=args.bitrate,
                             min_kbps=args.min_bitrate,
                             max_kbps=max(args.bitrate, args.min_bitrate))

    # Boxed, and read by the send loop. Subscriber callbacks run on the control
    # channel's socket thread, so this one decides and records; it does not act.
    wanted_kbps = [args.bitrate]

    def on_viewer_stats(data):
        if args.no_adapt or not isinstance(data, dict):
            return
        loss = float(data.get("loss", 0.0))
        lat = data.get("lat")
        # Deliberately does NOT call set_bitrate here. Retargeting rebuilds the x264
        # encoder, and doing that from this thread does it underneath the send loop's
        # in-flight encode() -- a data race on libx264's internal frame pool that does
        # not raise but aborts the process, seconds later, once the two threads happen
        # to collide. The send loop applies this between frames instead, which is also
        # where the resulting keyframe belongs.
        wanted_kbps[0] = rate.update(time.monotonic(), loss,
                                     float(lat) if lat is not None else None)

    link.subscribe("viewer/stats", on_viewer_stats)

    # The headset tells us how its hand rig connects; the visualiser cannot draw bones
    # correctly without it and deliberately draws none until it arrives.
    def on_skeleton(d):
        if viz is None or not isinstance(d, dict):
            return
        parents = d.get("parents")
        if not parents:
            return
        viz.set_skeleton(str(d.get("side", "l")), parents)
        print(f"hand skeleton: {d.get('side')} {len(parents)} bones")

    link.subscribe("hand/skeleton", on_skeleton)

    # A moving target and a couple of markers, so the headset end can be checked on a
    # device without writing any robot code first. Guidance the operator can walk to is
    # the only way to tell a working guide from one that merely renders.
    ui = HeadsetUi(link)
    demo_warned = [False]
    def _reached(d):
        print(f"guide reached: {d}")
        ui.toast(f"reached with your {d.get('src', 'hand')}", "info", 1.5)
        ui.buzz(d.get("side", "r"), 0.7, 0.08)

    ui.on_guide_reached(_reached)

    # The robot-defined session menu. This is the part worth trying on a device: these
    # rows are declared here, in Python, and appear in the headset's menu without the
    # Unity app knowing what any of them mean. Adding a control to a real robot is
    # editing this list.
    menu_state = {"mode": "cartesian", "speed": 0.5, "gripper": False, "recording": False}

    def publish_menu():
        ui.menu([
            button("rec", "Stop recording" if menu_state["recording"] else "Record episode"),
            toggle("grip", "Gripper open", menu_state["gripper"]),
            choice("mode", "Control mode", ["cartesian", "joint"], menu_state["mode"]),
            rng("spd", "Speed", 0.1, 1.0, 0.1, menu_state["speed"], fmt="{0:0.0}x"),
            button("home", "Home the arms", enabled=not menu_state["recording"]),
        ])

    def on_menu_event(e):
        if not isinstance(e, dict):
            return
        mid, value = e.get("id"), e.get("value")
        print(f"menu: {mid} = {value!r}")
        if mid == "rec":
            menu_state["recording"] = not menu_state["recording"]
            ui.toast("recording" if menu_state["recording"] else "stopped", "info", 1.5)
        elif mid == "grip":
            menu_state["gripper"] = bool(value)
        elif mid == "mode":
            menu_state["mode"] = str(value)
        elif mid == "spd":
            menu_state["speed"] = float(value)
        elif mid == "home":
            ui.toast("homing", "warn", 2.0)
        # Re-publish so the headset's optimistic local value is corrected, and so rows
        # that depend on state -- the record label, whether Home is allowed -- follow.
        publish_menu()

    ui.on_menu_event(on_menu_event)

    def on_headset_state(s):
        """
        Taking the headset off is a safety event, not telemetry: the poses keep arriving
        from a device on a desk, so without this a robot goes on tracking a wrist nobody
        is wearing.
        """
        if not isinstance(s, dict):
            return
        worn = bool(s.get("mounted", True))
        if worn != on_headset_state.worn:
            on_headset_state.worn = worn
            print("headset: " + ("put on" if worn else "TAKEN OFF -- a robot should stop here"))
        if viz is not None:
            viz.set_state(s)
        batt = s.get("batt")
        if isinstance(batt, (int, float)) and 0 <= batt < 0.15 and not on_headset_state.warned:
            on_headset_state.warned = True
            print(f"headset battery {batt * 100:.0f}% -- session will not last")
    on_headset_state.worn = True
    on_headset_state.warned = False
    ui.on_state(on_headset_state)

    def _rotate(q, v):
        """Rotate v by unit quaternion q=(x,y,z,w). Handedness-agnostic."""
        qx, qy, qz, qw = q
        # t = 2 * (q.xyz x v);  v' = v + qw*t + (q.xyz x t)
        tx = 2.0 * (qy * v[2] - qz * v[1])
        ty = 2.0 * (qz * v[0] - qx * v[2])
        tz = 2.0 * (qx * v[1] - qy * v[0])
        return (v[0] + qw * tx + (qy * tz - qz * ty),
                v[1] + qw * ty + (qz * tx - qx * tz),
                v[2] + qw * tz + (qx * ty - qy * tx))

    def _flat(v):
        """Drop the pitch, keep the heading. A demo that tilts with a glance is unreadable."""
        n = math.hypot(v[0], v[2])
        return (0.0, 0.0, 1.0) if n < 1e-4 else (v[0] / n, 0.0, v[2] / n)

    def ui_demo(t: float) -> None:
        """
        Everything is placed relative to the operator's own head pose, which the uplink
        is already sending us. Nothing here assumes where the floor is or which tracking
        origin the headset was configured with -- the first version of this demo did, got
        it wrong, and put a "floor" plate at eye level.

        It also happens to be the round trip the protocol is built around: a pose that
        arrived from the headset, offset, and sent straight back as geometry.
        """
        p = input_rx.fresh()
        if p is None or not (p.flags & INPUT_HEAD_VALID):
            # Nothing to anchor to. Said out loud once, because silently drawing nothing
            # is indistinguishable from the headset failing to render what was sent --
            # which is exactly the confusion this demo exists to remove.
            if not demo_warned[0]:
                demo_warned[0] = True
                print("ui-demo: no head pose on the uplink yet; nothing to anchor to. "
                      f"Is the headset sending to :{args.input_port}?")
            return
        if demo_warned[0]:
            demo_warned[0] = False
            print("ui-demo: head pose arriving; drawing.")
        head = p.head_pos
        fwd = _flat(_rotate(p.head_rot, (0.0, 0.0, 1.0)))
        rgt = _flat(_rotate(p.head_rot, (1.0, 0.0, 0.0)))

        def at(f, r, u):
            return (head[0] + fwd[0] * f + rgt[0] * r,
                    head[1] + u,
                    head[2] + fwd[2] * f + rgt[2] * r)

        side = "l" if int(t / 10) % 2 == 0 else "r"
        lateral = -0.22 if side == "l" else 0.22
        # Drifts gently so it is obviously live, but stays inside comfortable reach.
        target = at(0.45 + 0.05 * math.sin(t * 0.6), lateral, -0.28 + 0.06 * math.sin(t))

        ui.guide_clear("r" if side == "l" else "l")
        ui.guide(side, target, tol=0.06, hold=0.5,
                 label=f"reach with your {'left' if side == 'l' else 'right'} hand",
                 ttl=1.5)

        ui.markers([
            # What to do, at reading distance and just below the eye line.
            {"id": "demo/say", "t": "text", "f": "origin", "p": list(at(1.0, 0.0, -0.10)),
             "q": [0, 0, 0, 1], "txt": "gvlink demo - reach the sphere",
             "size": 0.055, "c": [0.65, 0.85, 1.0, 0.95], "ttl": 1.5},
            # A workspace volume around where the target roams, so the box marker is
            # visibly a different thing from the guide.
            {"id": "demo/box", "t": "box", "f": "origin", "p": list(at(0.45, lateral, -0.28)),
             "q": [0, 0, 0, 1], "s": [0.34, 0.30, 0.28],
             "c": [0.30, 0.55, 0.95, 0.10], "ttl": 1.5},
            # A shoulder-width rail at target height: the line marker, and a depth cue.
            {"id": "demo/rail", "t": "line", "f": "origin", "p": [0, 0, 0],
             "q": [0, 0, 0, 1], "w": 0.005,
             "pts": [list(at(0.45, -0.40, -0.28)), list(at(0.45, 0.40, -0.28))],
             "c": [0.55, 0.75, 1.0, 0.55], "ttl": 1.5},
            # The head's own axes, at arm's length ahead: proves the frame round-trips.
            {"id": "demo/axes", "t": "pose", "f": "origin", "p": list(at(0.7, 0.0, -0.45)),
             "q": list(p.head_rot), "s": 0.10, "ttl": 1.5},
        ])

    # What the viewer needs to render these images at the right angular size and in the
    # right place. Published as soon as a viewer connects, and answerable on request so
    # a client that reconnects mid-session does not have to wait for the next one.
    # An explicit --calib always wins; otherwise a real camera's own calibration beats
    # the synthesised field of view, which is only ever a stand-in for not knowing.
    if args.calib:
        camera = CameraParams.load(args.calib)
    elif oak_cam is not None and oak_cam.camera_params is not None:
        camera = oak_cam.camera_params
    else:
        camera = CameraParams.from_hfov(src_w, src_h, args.hfov,
                                        0.075 if args.baseline is None else args.baseline)
        if oak_cam is not None:
            # Unrectified frames, and a guessed field of view on top: say both, since
            # neither is visible to the operator from inside the headset.
            camera = replace(camera, rectified=oak_cam.rectified)
    print(f"  camera: {camera.describe()}")

    @link.handler("camera/params")
    def _camera_params(_data):
        return camera.to_wire()

    # What the device itself is doing: worn, eye tracking, battery. Published a couple
    # of times a second, so this is a last-known value, not a live one.
    hs_state = {"last": None}
    link.subscribe("hs/state", lambda msg: hs_state.__setitem__("last", msg or {}))

    def on_new_session(s):
        if not s:
            return
        link.publish("camera/params", camera.to_wire())
        # Rows have to be (re)published per session: the headset's list is whatever it
        # was last told, and a headset that just connected has been told nothing.
        if args.ui_demo:
            publish_menu()

    link.on_session(on_new_session)

    try:
        while True:
            now = time.monotonic()
            if args.duration and now - started >= args.duration:
                break
            if now < next_at:
                time.sleep(min(period, next_at - now))
                continue
            next_at += period
            if now - next_at > 0.5:       # fell far behind; resynchronise
                next_at = now + period

            # Before the "is anyone watching" check, deliberately: the headset's input
            # arrives whether or not video is flowing, and being able to watch poses
            # while nothing is streaming is exactly when this is most useful. Drawn from
            # the freshest sample on the main loop rather than from the receive thread --
            # a browser cannot keep up with 90 Hz, and a visualiser has no business
            # adding work to the path that reassembles packets.
            if viz is not None:
                viz.update(input_rx.fresh())

            if args.ui_demo and now - last_ui >= 0.1:
                last_ui = now
                ui_demo(now - started)

            # Who are we sending to, and in what shape?
            if args.host:
                dest, req_fovea, req_codec = addr, not args.no_fovea, codec
                sess_id = 0
            else:
                sess = link.session
                if sess is None:
                    # Nobody is connected. Encoding into the void wastes a core and,
                    # on a robot, battery -- so idle instead.
                    if now - idle_notice >= 5.0:
                        idle_notice = now
                        print(f"idle: no viewer (control on :{args.control_port})")
                    active = None
                    stream_started = None
                    continue
                dest = (sess.addr, sess.video_port)
                req_codec = sess.codec
                req_fovea = sess.foveation and not args.no_fovea
                sess_id = sess.id

            want_layout = layout_for(link.session if not args.host else None)
            shape = (want_layout.canvas_w, want_layout.canvas_h,
                     want_layout.coarse_scale, want_layout.fovea_scale)
            # Keyed on the session id, not only on the settings. A reconnect that lands
            # between two frames of this loop -- which is what "Reconnect" in the headset
            # menu produces on a LAN -- never shows up as a None session here, so an
            # identical address and codec looked like nothing had happened at all. The
            # encoders were left running mid-GOP while the headset's freshly built
            # decoder waited for a keyframe that was never coming: a reconnect that
            # reliably ended in a black screen.
            if active != (sess_id, dest, req_codec, shape):
                active = (sess_id, dest, req_codec, shape)
                layout[0] = want_layout
                stream_started = now
                # Defer the first rate report a full interval; measuring a frame rate
                # over the microsecond since streaming began yields a nine-digit number.
                last_report = now
                last_bytes = 0
                n = 0
                senders = build_senders(dest, req_codec)
                # A new viewer gets whatever the controller last learned about the link,
                # not the command-line default -- otherwise every reconnect starts by
                # flooding a path already known to be too slow.
                for snd in senders.values():
                    snd.set_bitrate(rate.target_kbps)
                want_fovea = req_fovea
                print(f"streaming to {dest[0]}:{dest[1]} "
                      f"codec={CODEC_NAMES[req_codec]} "
                      f"fovea={'on' if req_fovea else 'off'} "
                      f"canvas={want_layout.canvas_w}x{want_layout.canvas_h} "
                      f"({want_layout.waste:.0%} padding) "
                      f"coarse={want_layout.coarse_px()[0]}x{want_layout.coarse_px()[1]} "
                      f"fovea={want_layout.fovea_px()[0]}x{want_layout.fovea_px()[1]}")
            want_fovea = req_fovea

            if oak_cam is not None:
                ok, frame_l, frame_r = oak_cam.read()
                if not ok:
                    print("oak camera read failed")
                    break
            else:
                if cap is not None:
                    ok, frame = cap.read()
                    if not ok:
                        print("camera read failed")
                        break
                    if frame.shape[1] != src_w or frame.shape[0] != src_h:
                        frame = cv2.resize(frame, (src_w, src_h))
                else:
                    frame = chart.copy()
                overlay(frame, n)
                frame_l = eye_view(frame, EYE_LEFT, args.disparity)
                frame_r = eye_view(frame, EYE_RIGHT, args.disparity)

            if not want_fovea:
                gl = gr = None
                gaze_src = "disabled"
            else:
                p = input_rx.fresh()
                if p is not None and p.gaze_valid:
                    gl, gr = tuple(p.gaze_l), tuple(p.gaze_r)
                    gaze_src = "remote"
                elif args.auto_gaze:
                    t = time.monotonic() - started
                    g = (0.5 + 0.35 * math.sin(t * 0.7), 0.5 + 0.30 * math.sin(t * 1.1))
                    gl = gr = g
                    gaze_src = "auto"
                else:
                    gl = gr = (0.5, 0.5)
                    gaze_src = "center"

            # Apply whatever the rate controller last decided, on this thread, between
            # frames. A no-op unless the target actually moved.
            for snd in senders.values():
                snd.set_bitrate(wanted_kbps[0])

            # One widener for both eyes: they saccade together, and giving each its own
            # would let them disagree about coverage for a frame or two.
            zoom = widener.update(gl, now)
            canvas_l = senders[EYE_LEFT].send(frame_l, gl, zoom)
            canvas_r = senders[EYE_RIGHT].send(frame_r, gr, zoom)
            n += 1

            if args.print_poses and now - last_pose_print >= 0.2:
                last_pose_print = now
                print(format_input(input_rx.fresh(), hs_state.get("last")))

            if args.preview:
                cv2.imshow("mock_robot: transmitted canvas (L | R)",
                           cv2.resize(np.hstack([canvas_l, canvas_r]), (1024, 512)))
                if cv2.waitKey(1) & 0xFF == 27:
                    break

            if now - last_report >= 1.0:
                total = sum(s.bytes_sent for s in senders.values())
                mbps = (total - last_bytes) * 8 / max(0.25, now - last_report) / 1e6
                enc = sum(s.encode_ms.mean() for s in senders.values())
                el_stream = max(0.25, now - (stream_started or now))
                if viz is not None:
                    sess = link.session
                    lay = layout[0]
                    viz.set_metrics({
                        "fps": f"{n / el_stream:.1f} sent  ({args.fps} target)",
                        "rate": f"{mbps:.2f} Mbit/s",
                        "encode": f"{enc:.2f} ms/pair",
                        "bitrate": rate.describe(),
                        "uplink": f"{input_rx.rate_hz:.0f} Hz",
                        "gaze": f"{gaze_src}  zoom {widener.zoom:.2f}",
                        "canvas": f"{lay.canvas_w}x{lay.canvas_h}"
                                  f"  coarse {lay.coarse_scale:.2f}"
                                  f"  fovea {lay.fovea_scale:.2f}",
                        "viewer": str(sess) if sess else "none",
                        "sent": f"{n} pairs, {total / 1e6:.1f} MB",
                    })
                print(f"tx {n/el_stream:5.1f} fps avg | {mbps:6.2f} Mbit/s | "
                      f"encode {enc:5.2f} ms/pair | gaze={gaze_src} "
                      f"zoom {widener.zoom:.2f} "
                      f"| {rate.describe()} "
                      f"({input_rx.rate_hz:.0f} Hz uplink) | {link.session or 'no viewer'}")
                last_report, last_bytes = now, total
    except KeyboardInterrupt:
        pass
    finally:
        if beacon is not None:
            beacon.stop()
        link.stop()
        input_rx.stop()
        if viz is not None:
            viz.stop()
        if cap is not None:
            cap.release()
        if oak_cam is not None:
            oak_cam.close()
        cv2.destroyAllWindows()

    el = time.monotonic() - (stream_started or started)
    total = sum(s.bytes_sent for s in senders.values()) if senders else 0
    print(f"\nsent {n} frame pairs in {el:.1f}s of streaming ({n/max(el,1e-9):.1f} fps), "
          f"{total/1e6:.1f} MB, {total*8/max(el,1e-9)/1e6:.2f} Mbit/s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
