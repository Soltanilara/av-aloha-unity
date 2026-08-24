#!/usr/bin/env python3
"""
Mock robot camera sender.

Stands in for the OAK stereo pair until that side exists: takes a webcam (or a
synthetic detail chart), duplicates it into a LEFT/RIGHT pair with a synthetic
disparity, packs each eye into a foveal atlas, encodes H.264 and fires it at the
headset over UDP.

It also listens for the gaze uplink, so the foveal path can be driven end to end
from a laptop with the mouse standing in for an eye -- run bench_receiver.py with
--gaze-mouse at the other end.

    uv run mock_robot.py --source pattern
    uv run mock_robot.py --source webcam --host 100.64.0.5
"""

from __future__ import annotations

import argparse
import math
import socket
import threading
import time

import cv2
import numpy as np

from gvlink.beacon import Beacon, build_payload
from gvlink.foveal import AtlasLayout, SaccadeWidener, bandwidth_note
from gvlink.camera import CameraParams
from gvlink.ratecontrol import BitrateController
from gvlink.robotlink import RobotLink
from gvlink.pattern import make_chart
from gvlink.protocol import (BUTTON_ONE, BUTTON_TWO, CODEC_H264, CODEC_MJPEG,
                             CODEC_NAMES, DEFAULT_PORTS, EYE_LEFT, EYE_RIGHT,
                             MTU_PAYLOAD, MTU_PAYLOAD_TUNNEL, HeadsetInput)
from gvlink.stream import EyeStreamSender, make_udp_socket


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
        self.rate_hz = 0.0
        self._rate_at = time.monotonic()
        self._rate_n = 0
        self._stop = threading.Event()
        self._t = threading.Thread(target=self._run, daemon=True, name="gv-input")
        self._t.start()

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                data, _ = self.sock.recvfrom(4096)
            except socket.timeout:
                continue
            except OSError:
                break
            p = HeadsetInput.unpack(data)
            if p is None:
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
    ap.add_argument("--source", choices=("pattern", "webcam"), default="pattern")
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
    ap.add_argument("--viser", action="store_true",
                    help="serve a live 3D view of the headset, hands and controllers "
                         "(needs the viz extra: uv sync --extra viz)")
    ap.add_argument("--viser-port", type=int, default=8080)
    ap.add_argument("--hfov", type=float, default=90.0, metavar="DEG",
                    help="horizontal field of view to advertise when there is no "
                         "calibration file (the mock camera has none)")
    ap.add_argument("--baseline", type=float, default=0.075, metavar="M",
                    help="metres between the two optical centres, advertised to the viewer")
    ap.add_argument("--calib", metavar="JSON",
                    help="rectified camera parameters to advertise; see gvlink/camera.py")
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
    ap.add_argument("--name", default="mock-robot", help="name shown in the headset's robot list")
    ap.add_argument("--no-beacon", action="store_true",
                    help="do not advertise on the LAN (the headset must be told the address)")
    args = ap.parse_args()

    src_w, src_h = args.src if isinstance(args.src, tuple) else parse_wh(args.src)
    base_layout = AtlasLayout(*(args.canvas if isinstance(args.canvas, tuple)
                                else parse_wh(args.canvas)),
                              coarse_scale=args.coarse_scale,
                              fovea_scale=args.fovea_scale)

    # Boxed so the send loop and the session handler share one value. The viewer may ask
    # for a different shape -- it is the end that knows its own decoder and its own link
    # -- and anything it does not specify keeps the command-line default.
    layout = [base_layout]

    def layout_for(sess) -> AtlasLayout:
        if sess is None:
            return base_layout
        try:
            w, h = sess.canvas or (base_layout.canvas_w, base_layout.canvas_h)
            return AtlasLayout(
                max(256, min(2048, int(w))), max(256, min(2048, int(h))),
                coarse_scale=sess.coarse_scale if sess.coarse_scale else base_layout.coarse_scale,
                fovea_scale=sess.fovea_scale if sess.fovea_scale else base_layout.fovea_scale)
        except ValueError as e:
            # A viewer asking for something impossible must not take the robot down.
            print(f"  ignoring requested stream shape: {e}")
            return base_layout

    cap = None
    if args.source == "webcam":
        cap = cv2.VideoCapture(args.cam)
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, src_w)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, src_h)
        if not cap.isOpened():
            print(f"could not open camera {args.cam}")
            return 1
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

    def on_viewer_stats(data):
        if args.no_adapt or not isinstance(data, dict):
            return
        loss = float(data.get("loss", 0.0))
        lat = data.get("lat")
        want = rate.update(time.monotonic(), loss,
                           float(lat) if lat is not None else None)
        for snd in senders.values():
            snd.set_bitrate(want)

    link.subscribe("viewer/stats", on_viewer_stats)

    # What the viewer needs to render these images at the right angular size and in the
    # right place. Published as soon as a viewer connects, and answerable on request so
    # a client that reconnects mid-session does not have to wait for the next one.
    camera = (CameraParams.load(args.calib) if args.calib else
              CameraParams.from_hfov(src_w, src_h, args.hfov, args.baseline))
    print(f"  camera: {camera.describe()}")

    @link.handler("camera/params")
    def _camera_params(_data):
        return camera.to_wire()

    link.on_session(lambda s: s and link.publish("camera/params", camera.to_wire()))

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
                      f"coarse={want_layout.coarse_scale:.2f} "
                      f"fovea_scale={want_layout.fovea_scale:.2f}")
            want_fovea = req_fovea

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

            # One widener for both eyes: they saccade together, and giving each its own
            # would let them disagree about coverage for a frame or two.
            zoom = widener.update(gl, now)
            canvas_l = senders[EYE_LEFT].send(eye_view(frame, EYE_LEFT, args.disparity), gl, zoom)
            canvas_r = senders[EYE_RIGHT].send(eye_view(frame, EYE_RIGHT, args.disparity), gr, zoom)
            n += 1

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
        cv2.destroyAllWindows()

    el = time.monotonic() - (stream_started or started)
    total = sum(s.bytes_sent for s in senders.values()) if senders else 0
    print(f"\nsent {n} frame pairs in {el:.1f}s of streaming ({n/max(el,1e-9):.1f} fps), "
          f"{total/1e6:.1f} MB, {total*8/max(el,1e-9)/1e6:.2f} Mbit/s")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
