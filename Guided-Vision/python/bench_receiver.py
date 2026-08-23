#!/usr/bin/env python3
"""
Bench receiver: the headset, minus the headset.

Receives the stereo stream, reassembles it under the same drop-never-wait policy the
Unity client uses, decodes it, and reports the numbers that decide whether the
transport is good enough -- end-to-end latency, jitter, fragment loss, dropped frames.

It also reconstructs the foveal atlas on the CPU using gvlink.foveal.reconstruct,
which is the reference for what the display shader has to do.

    uv run bench_receiver.py --duration 10
    uv run bench_receiver.py --display --gaze-mouse

Structure note: receiving runs on its own thread and the display runs at its own
bounded rate, mirroring the real client (Java socket thread, Unity render loop). Doing
the display work inline in the receive loop caps the loop at the display's rate --
roughly a thousand iterations a second because cv2.waitKey(1) sleeps a millisecond --
while several thousand datagrams a second keep arriving. The socket buffer then backs
up and reported latency climbs without bound.

Latency caveat: the figure is a true one-way capture-to-decode measurement only when
sender and receiver share a monotonic clock, i.e. both on this machine. Across hosts
it is offset by the clock difference and only the jitter is meaningful.
"""

from __future__ import annotations

import argparse
import json
import socket
import threading
import time

import cv2
import numpy as np

from gvlink.foveal import reconstruct
from gvlink.protocol import (CODEC_H264, CODEC_MJPEG, DEFAULT_PORTS, EYE_LEFT,
                             EYE_NAMES, EYE_RIGHT, INPUT_GAZE_VALID, HeadsetInput,
                             now_us)
from gvlink.robotlink import ControlClient
from gvlink.stream import EyeStreamReceiver, make_udp_socket


def parse_wh(s: str) -> tuple[int, int]:
    w, h = s.lower().split("x")
    return int(w), int(h)


class GazeSender:
    """Mouse position -> gaze uplink, standing in for the Quest Pro eye tracker."""

    def __init__(self, port: int) -> None:
        self.sock = make_udp_socket()
        self.port = port
        self.seq = 0
        self.xy = (0.5, 0.5)
        self.peer: str | None = None

    def set_from_mouse(self, x: float, y: float) -> None:
        self.xy = (min(1.0, max(0.0, x)), min(1.0, max(0.0, y)))

    def tick(self) -> None:
        if self.peer is None:
            return
        p = HeadsetInput(seq=self.seq, ts_us=now_us(), flags=INPUT_GAZE_VALID,
                         gaze_l=self.xy, gaze_r=self.xy, gaze_confidence=1.0)
        self.seq += 1
        try:
            self.sock.sendto(p.pack(), (self.peer, self.port))
        except OSError:
            pass


class ReceiveThread(threading.Thread):
    """
    Socket -> reassembly -> decode, off the main thread.

    Keeps only the newest decoded frame per eye. Nothing queues: if the display is
    slower than the stream, frames are overwritten, which is the same policy the
    headset uses and the reason latency cannot accumulate here.
    """

    def __init__(self, sock, eyes: dict, gaze: GazeSender | None) -> None:
        super().__init__(daemon=True, name="gv-bench-rx")
        self.sock = sock
        self.eyes = eyes
        self.gaze = gaze
        self.running = True
        self.lock = threading.Lock()
        self.latest: dict[int, tuple] = {}
        self.first_seen: float | None = None
        self.last_seen: float | None = None

    def run(self) -> None:
        while self.running:
            try:
                data, peer = self.sock.recvfrom(65535)
            except socket.timeout:
                continue
            except OSError:
                return
            if self.gaze is not None and self.gaze.peer is None:
                self.gaze.peer = peer[0]

            now = time.monotonic()
            if self.first_seen is None:
                self.first_seen = now
            self.last_seen = now

            if len(data) <= 8:
                continue
            rx = self.eyes.get(data[8])
            if rx is None:
                continue
            got = rx.push(data)
            if got is not None and got[1] is not None:
                with self.lock:
                    self.latest[rx.eye] = got

    def snapshot(self) -> dict:
        with self.lock:
            return dict(self.latest)

    def stop(self) -> None:
        self.running = False


def summarise(eye: EyeStreamReceiver) -> dict:
    r = eye.re
    attempted = r.frames_completed + r.frames_dropped
    return {
        "eye": EYE_NAMES[eye.eye],
        "frames_completed": r.frames_completed,
        "frames_dropped": r.frames_dropped,
        "frame_loss_pct": 100.0 * r.frames_dropped / attempted if attempted else 0.0,
        "fragments_received": r.fragments_received,
        "fragments_lost": r.fragments_lost,
        "bytes": r.bytes_received,
        "decode_errors": eye.dec.decode_errors if eye.dec else 0,
        "latency_ms_mean": eye.latency_ms.mean(),
        "latency_ms_p50": eye.latency_ms.pct(0.50),
        "latency_ms_p95": eye.latency_ms.pct(0.95),
        "latency_ms_max": eye.latency_ms.max(),
        "decode_ms_mean": eye.decode_ms.mean(),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--robot", metavar="HOST", default="127.0.0.1",
                    help="connect to the robot's control channel and ask it to stream "
                         "here, the way the headset does. Use --no-connect to just "
                         "listen, for a sender launched with its own --host.")
    ap.add_argument("--control-port", type=int, default=DEFAULT_PORTS["control"])
    ap.add_argument("--no-connect", action="store_true",
                    help="listen only; do not negotiate a session")
    ap.add_argument("--codec", choices=("h264", "mjpeg"), default="h264",
                    help="what to ask the robot to send")
    ap.add_argument("--no-fovea", action="store_true",
                    help="ask the robot for a plain full-frame stream")
    ap.add_argument("--port", type=int, default=DEFAULT_PORTS["video"])
    ap.add_argument("--input-port", type=int, default=DEFAULT_PORTS["input"])
    ap.add_argument("--duration", type=float, default=0.0, help="0 = until Ctrl-C")
    ap.add_argument("--display", action="store_true")
    ap.add_argument("--gaze-mouse", action="store_true",
                    help="drive the sender's fovea from the mouse (implies --display)")
    ap.add_argument("--display-fps", type=float, default=30.0,
                    help="how often to redraw; independent of the stream rate")
    ap.add_argument("--gaze-hz", type=float, default=90.0)
    ap.add_argument("--out", type=parse_wh, default="1280x800",
                    help="reconstruction size per eye")
    ap.add_argument("--feather", type=float, default=0.15)
    ap.add_argument("--raw", action="store_true",
                    help="show the transmitted atlas instead of the reconstruction")
    ap.add_argument("--json", metavar="PATH", help="write the final summary as JSON")
    args = ap.parse_args()

    display = args.display or args.gaze_mouse
    out_w, out_h = args.out if isinstance(args.out, tuple) else parse_wh(args.out)

    sock = make_udp_socket(("0.0.0.0", args.port))
    sock.settimeout(0.25)
    eyes = {EYE_LEFT: EyeStreamReceiver(EYE_LEFT), EYE_RIGHT: EyeStreamReceiver(EYE_RIGHT)}
    gaze = GazeSender(args.input_port) if args.gaze_mouse else None

    win = "gv-bench  (L | R)"
    # Each eye is drawn at half its reconstruction size, side by side.
    eye_w, eye_h = out_w // 2, out_h // 2
    if display:
        cv2.namedWindow(win, cv2.WINDOW_AUTOSIZE)
        if gaze is not None:
            def on_mouse(event, x, y, flags, param):
                # The window holds both eyes side by side; map a position anywhere in
                # it back to a normalised point within one eye.
                gaze.set_from_mouse((x % max(1, eye_w)) / max(1, eye_w),
                                    y / max(1, eye_h))
            cv2.setMouseCallback(win, on_mouse)

    rx = ReceiveThread(sock, eyes, gaze)
    rx.start()

    # Behave like the headset: announce a session so the robot knows where to send.
    # Without this the robot waits for a viewer forever and this prints zeros, which
    # is a miserable thing to debug.
    control = None
    if not args.no_connect:
        control = ControlClient(args.robot, args.control_port,
                                video_port=args.port,
                                codec=CODEC_MJPEG if args.codec == "mjpeg" else CODEC_H264,
                                foveation=not args.no_fovea,
                                name="bench").start()

    print(f"listening on :{args.port}" +
          (f", session -> {args.robot}:{args.control_port}" if control else " (listen only)") +
          (f", gaze uplink -> :{args.input_port}" if gaze else "") +
          (f", display {args.display_fps:.0f} fps" if display else ""))

    warned_no_session = False
    last_stats = 0.0
    prev_lost = prev_done = 0

    started = time.monotonic()
    last_report = started
    last_draw = 0.0
    last_gaze = 0.0
    last_bytes = 0
    last_frames = 0
    draw_ms = 0.0
    draws = 0

    try:
        while True:
            now = time.monotonic()
            if args.duration and now - started >= args.duration:
                break

            if gaze is not None and now - last_gaze >= 1.0 / max(1.0, args.gaze_hz):
                gaze.tick()
                last_gaze = now

            if display and now - last_draw >= 1.0 / max(1.0, args.display_fps):
                last_draw = now
                snap = rx.snapshot()
                if len(snap) == 2:
                    t0 = time.perf_counter()
                    views = []
                    for e in (EYE_LEFT, EYE_RIGHT):
                        hdr, canvas = snap[e]
                        img = canvas if args.raw else reconstruct(
                            canvas, hdr, out_w, out_h, args.feather)
                        views.append(cv2.resize(img, (eye_w, eye_h)))
                    cv2.imshow(win, np.hstack(views))
                    draw_ms += (time.perf_counter() - t0) * 1000.0
                    draws += 1
                if cv2.waitKey(1) & 0xFF == 27:
                    break

            if now - last_report >= 1.0:
                el = now - last_report
                tb = sum(e.re.bytes_received for e in eyes.values())
                tf = sum(e.re.frames_completed for e in eyes.values())
                mbps = (tb - last_bytes) * 8 / el / 1e6
                fps = (tf - last_frames) / el / 2.0
                l, r = eyes[EYE_LEFT], eyes[EYE_RIGHT]
                extra = f" | draw {draw_ms/draws:4.1f} ms" if draws else ""
                print(f"rx {fps:5.1f} fps | {mbps:6.2f} Mbit/s | "
                      f"lat L {l.latency_ms.mean():6.1f} R {r.latency_ms.mean():6.1f} ms "
                      f"(p95 {max(l.latency_ms.pct(.95), r.latency_ms.pct(.95)):6.1f}) | "
                      f"dec {l.decode_ms.mean():4.2f} ms | "
                      f"dropped L{l.re.frames_dropped} R{r.re.frames_dropped} | "
                      f"fraglost {l.re.fragments_lost + r.re.fragments_lost}{extra}")
                last_report, last_bytes, last_frames = now, tb, tf
                draw_ms, draws = 0.0, 0

                # Zeros for several seconds almost always means the session never
                # started. Say so, rather than printing an unbroken column of nought.
                if tf == 0 and not warned_no_session and now - started > 3.0:
                    warned_no_session = True
                    print("  nothing received yet."
                          + ("  Is the robot running, and is --robot pointing at it?"
                             if control is not None else
                             "  Listening only: the sender needs its own --host."))

            # Report what actually arrived so the sender can pick a bitrate for the
            # link -- the same two numbers, on the same topic, as the headset sends.
            if control is not None and now - last_stats >= 0.5:
                last_stats = now
                lost = sum(e.re.fragments_lost for e in eyes.values())
                done = sum(e.re.frames_completed for e in eyes.values())
                d_lost, d_done = lost - prev_lost, done - prev_done
                prev_lost, prev_done = lost, done
                if d_done > 0:
                    lat = max(eyes[EYE_LEFT].latency_ms.mean(),
                              eyes[EYE_RIGHT].latency_ms.mean())
                    control.publish("viewer/stats", {
                        "loss": min(1.0, d_lost / max(1.0, d_done * 8.0)),
                        "lat": float(lat) if lat == lat else 0.0,   # NaN before any frame
                    })

            # Nothing to do but wait for the receive thread or the next redraw.
            time.sleep(0.002)
    except KeyboardInterrupt:
        pass
    finally:
        rx.stop()
        rx.join(timeout=1.0)
        if control is not None:
            control.stop()
        cv2.destroyAllWindows()
        sock.close()

    el = (rx.last_seen - rx.first_seen) if (rx.first_seen and rx.last_seen) else 0.0
    summary = {
        "seconds": round(el, 2),
        "eyes": [summarise(eyes[EYE_LEFT]), summarise(eyes[EYE_RIGHT])],
        "note": ("latency is true one-way only when sender and receiver share a "
                 "monotonic clock, i.e. both on this host"),
    }
    total_bytes = sum(e.re.bytes_received for e in eyes.values())
    summary["mbit_s"] = total_bytes * 8 / el / 1e6 if el else 0.0

    print("\n--- summary ---")
    for s in summary["eyes"]:
        print(f"  eye {s['eye']}: {s['frames_completed']} frames "
              f"({s['frames_completed']/el if el else 0:.1f} fps), "
              f"dropped {s['frames_dropped']} ({s['frame_loss_pct']:.2f}%), "
              f"fragments lost {s['fragments_lost']}/{s['fragments_received']}, "
              f"decode errors {s['decode_errors']}")
        print(f"           latency mean {s['latency_ms_mean']:.1f} ms  "
              f"p50 {s['latency_ms_p50']:.1f}  p95 {s['latency_ms_p95']:.1f}  "
              f"max {s['latency_ms_max']:.1f}   decode {s['decode_ms_mean']:.2f} ms")
    print(f"  aggregate {summary['mbit_s']:.2f} Mbit/s over {el:.1f}s")

    if args.json:
        with open(args.json, "w") as f:
            json.dump(summary, f, indent=2)
        print(f"  wrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
