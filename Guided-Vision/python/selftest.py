#!/usr/bin/env python3
"""
In-process end-to-end check of the gvlink protocol. No subprocesses, no camera.

Covers the things that would silently corrupt the picture rather than crash:
the fovea rect surviving the wire round trip, gaze actually steering the crop,
the non-foveated fallback, and the reassembler's behaviour under packet loss.

    uv run selftest.py
"""

from __future__ import annotations

import math
import socket
import sys
import threading
import time

import numpy as np

import cv2
from gvlink.foveal import AtlasLayout, SaccadeWidener, build_atlas, reconstruct
from gvlink.camera import CameraParams, EyeIntrinsics
from gvlink.protocol import HandState
from gvlink.ratecontrol import BitrateController
from gvlink.pattern import make_chart
from gvlink.protocol import (BUTTON_ONE, BUTTON_STICK, BUTTON_TWO, EYE_LEFT,
                             INPUT_GAZE_VALID, INPUT_HEAD_VALID, INPUT_LEFT_VALID,
                             INPUT_SIZE, HeadsetInput, Reassembler, VideoHeader,
                             fragment, now_us)
from gvlink.robotlink import ControlClient, RobotLink
from gvlink.ui import HeadsetUi
from gvlink.stream import EyeStreamReceiver, EyeStreamSender, make_udp_socket

FAILS: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{'  ' + detail if detail else ''}")
    if not ok:
        FAILS.append(name)


def _free_port() -> int:
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    p = s.getsockname()[1]
    s.close()
    return p


def _await(fn, timeout: float = 3.0):
    """Poll until fn() returns something truthy, or give up. Returns None on timeout."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        v = fn()
        if v is not None:
            return v
        time.sleep(0.005)
    return None


def spans(header, canvas):
    """
    Port of the shader's layer-span maths: each layer's stored size as a fraction of
    the canvas, which is what turns a source uv into an atlas uv.
    """
    ch, cw = canvas.shape[:2]
    return ((header.coarse_px_w or cw) / cw, (header.coarse_px_h or ch // 2) / ch,
            (header.fovea_px_w or cw) / cw, (header.fovea_px_h or ch // 2) / ch)


def round_trip(sender, rx_sock, receiver, src, gaze, zoom=1.0):
    """Send one frame and pull it back out; returns (header, canvas)."""
    sender.send(src, gaze, zoom)
    while True:
        data, _ = rx_sock.recvfrom(65535)
        got = receiver.push(data)
        if got is not None and got[1] is not None:
            return got


def main() -> int:
    # Deliberately non-default scales: at 1.0/1.0 the layer sub-rects degenerate to
    # whole bands and the general mapping would go untested.
    layout = AtlasLayout(1024, 1024, coarse_scale=0.35, fovea_scale=0.5)
    src_w, src_h = 1920, 1200
    src = make_chart(src_w, src_h)

    rx_sock = make_udp_socket(("127.0.0.1", 0))
    rx_sock.settimeout(2.0)
    tx_sock = make_udp_socket()
    addr = rx_sock.getsockname()

    sender = EyeStreamSender(tx_sock, addr, EYE_LEFT, layout, fps=60, bitrate_kbps=12000)
    receiver = EyeStreamReceiver(EYE_LEFT)

    print("gaze steering")
    for gx, gy in ((0.5, 0.5), (0.2, 0.3), (0.85, 0.7)):
        hdr, canvas = round_trip(sender, rx_sock, receiver, src, (gx, gy))
        # The crop is clamped to the source, so the reported centre is the gaze only
        # where the crop actually fits around it.
        fw_px, fh_px = layout.fovea_px()
        half_w = min(fw_px, src_w) / 2 / src_w
        half_h = min(fh_px, src_h) / 2 / src_h
        want_x = min(max(gx, half_w), 1 - half_w)
        want_y = min(max(gy, half_h), 1 - half_h)
        check(f"fovea centre tracks gaze ({gx}, {gy})",
              abs(hdr.fovea_x - want_x) < 0.01 and abs(hdr.fovea_y - want_y) < 0.01,
              f"got ({hdr.fovea_x:.3f}, {hdr.fovea_y:.3f}) want ({want_x:.3f}, {want_y:.3f})")
        check(f"  canvas shape at ({gx}, {gy})",
              canvas.shape == (layout.canvas_h, layout.canvas_w, 3), str(canvas.shape))
        check(f"  FLAG_FOVEATED set at ({gx}, {gy})", hdr.foveated)

    print("non-foveated fallback")
    hdr, canvas = round_trip(sender, rx_sock, receiver, src, None)
    check("flag clear", not hdr.foveated)
    check("reconstruct takes the plain path",
          reconstruct(canvas, hdr, 1280, 800).shape == (800, 1280, 3))

    print("foveal detail actually survives")
    # The foveal band is a 1:1 crop, so against the same region of the source it must
    # be sharper than the coarse layer scaled back up. Gradient energy is the proxy.
    hdr, canvas = round_trip(sender, rx_sock, receiver, src, (0.5, 0.5))
    bh = layout.band_h
    coarse_w, coarse_h = hdr.coarse_px_w, hdr.coarse_px_h
    fov_w, fov_h = hdr.fovea_px_w, hdr.fovea_px_h

    coarse_up = cv2.resize(canvas[0:coarse_h, 0:coarse_w], (src_w, src_h),
                           interpolation=cv2.INTER_LINEAR)
    x0 = int(round((hdr.fovea_x - hdr.fovea_w / 2) * src_w))
    y0 = int(round((hdr.fovea_y - hdr.fovea_h / 2) * src_h))
    region = coarse_up[y0:y0 + fov_h, x0:x0 + fov_w].astype(np.float32)
    fov = canvas[bh:bh + fov_h, 0:fov_w].astype(np.float32)
    e_coarse = float(np.abs(np.diff(region, axis=1)).mean())
    e_fovea = float(np.abs(np.diff(fov, axis=1)).mean())
    check("fovea band has more high-frequency detail than upscaled coarse",
          e_fovea > e_coarse * 1.5, f"fovea {e_fovea:.2f} vs coarse {e_coarse:.2f} "
                                    f"({e_fovea/max(e_coarse,1e-6):.1f}x)")
    check("header reports exact layer sizes",
          (hdr.coarse_px_w, hdr.coarse_px_h) == (coarse_w, coarse_h)
          and (hdr.fovea_px_w, hdr.fovea_px_h) == (fov_w, fov_h),
          f"coarse={hdr.coarse_px_w}x{hdr.coarse_px_h} fovea={hdr.fovea_px_w}x{hdr.fovea_px_h}")

    print("shader atlas geometry matches the reference")
    # Ports the UV maths out of Assets/Resources/StereoEyeView.shader (SampleAtlas)
    # and checks it lands on the exact source pixel. The foveal band is a 1:1 crop, so
    # a correct mapping is byte-identical -- no tolerance needed. If this drifts, the
    # symptom on device is a subtly misplaced sharp patch, which is miserable to debug
    # through a headset.
    hdr, canvas = round_trip(sender, rx_sock, receiver, src, (0.42, 0.63))
    ch, cw = canvas.shape[:2]
    bh = ch // 2
    cuSpan, cvSpan, fuSpan, fvSpan = spans(hdr, canvas)
    # GvVideoSource flips the centre into GL uv convention before the shader sees it.
    rect = np.array([hdr.fovea_x, 1.0 - hdr.fovea_y, hdr.fovea_w, hdr.fovea_h])

    fov_w, fov_h = hdr.fovea_px_w, hdr.fovea_px_h
    x0 = int(round((hdr.fovea_x - hdr.fovea_w / 2) * src_w))
    y0 = int(round((hdr.fovea_y - hdr.fovea_h / 2) * src_h))

    mismatches = []
    for (px, py) in ((x0, y0), (x0 + fov_w - 1, y0), (x0, y0 + fov_h - 1),
                     (x0 + fov_w - 1, y0 + fov_h - 1), (x0 + fov_w // 3, y0 + fov_h // 2)):
        u = (px + 0.5) / src_w
        v = 1.0 - (py + 0.5) / src_h                       # image row -> GL uv
        extent = np.maximum(rect[2:], 1e-5)
        t = np.clip((np.array([u, v]) - (rect[:2] - extent * 0.5)) / extent, 0.0, 1.0)
        au = t[0] * fuSpan                                 # shader: fuv
        av = (0.5 - fvSpan) + fvSpan * t[1]
        col = int(au * cw)
        row = int((1.0 - av) * ch)                         # GL uv -> canvas row
        want = (bh + (py - y0), px - x0)
        if (row, col) != want:
            mismatches.append(f"({px},{py}) -> ({row},{col}) want {want}")
    check("foveal band uv lands on the exact source pixel",
          not mismatches, "; ".join(mismatches))

    # And the coarse layer must cover the whole field inside its own sub-rect.
    coarse_w, coarse_h = hdr.coarse_px_w, hdr.coarse_px_h
    cmis = []
    for (u, v, want) in ((0.0, 1.0, (0, 0)),                       # source top-left
                         (1.0, 0.0, (coarse_h - 1, coarse_w - 1)),  # source bottom-right
                         (0.5, 0.5, (coarse_h // 2, coarse_w // 2))):
        cu = u * cuSpan                                    # shader: cuv
        cv_ = (1.0 - cvSpan) + cvSpan * v
        col = min(coarse_w - 1, int(cu * cw))
        row = min(coarse_h - 1, int((1.0 - cv_) * ch))
        if (row, col) != want:
            cmis.append(f"src({u},{v}) -> ({row},{col}) want {want}")
    check("coarse layer uv spans exactly its sub-rect", not cmis, "; ".join(cmis))

    print("headset input packet")
    # The uplink is a hand-laid binary struct on both sides, and its field offsets are
    # exactly the kind of thing that goes wrong silently: an off-by-one puts gaze
    # inside a controller and every value still parses as a plausible float.
    inp = HeadsetInput(seq=7, ts_us=123456,
                       flags=INPUT_GAZE_VALID | INPUT_HEAD_VALID | INPUT_LEFT_VALID,
                       head_pos=(0.1, 1.6, -0.2), head_rot=(0.0, 0.1, 0.2, 0.9),
                       gaze_l=(0.31, 0.62), gaze_r=(0.33, 0.64), gaze_confidence=0.9)
    inp.left.stick = (0.5, -0.25)
    inp.left.trigger, inp.left.grip = 0.8, 0.4
    inp.left.buttons = BUTTON_ONE | BUTTON_STICK
    inp.right.stick = (-1.0, 1.0)
    inp.right.buttons = BUTTON_TWO

    blob = inp.pack()
    check("packet is the agreed size", len(blob) == INPUT_SIZE, f"{len(blob)} bytes")
    out = HeadsetInput.unpack(blob)
    r3 = lambda t: [round(v, 3) for v in t]
    check("  head pose", r3(out.head_pos) == [0.1, 1.6, -0.2]
          and r3(out.head_rot) == [0.0, 0.1, 0.2, 0.9])
    check("  gaze does not land inside a controller",
          r3(out.gaze_l) == [0.31, 0.62] and r3(out.gaze_r) == [0.33, 0.64]
          and round(out.gaze_confidence, 3) == 0.9,
          f"L{r3(out.gaze_l)} R{r3(out.gaze_r)} conf {out.gaze_confidence:.3f}")
    check("  left controller", r3(out.left.stick) == [0.5, -0.25]
          and round(out.left.trigger, 3) == 0.8 and round(out.left.grip, 3) == 0.4)
    check("  buttons are per-hand", out.left.held(BUTTON_ONE) and out.left.held(BUTTON_STICK)
          and not out.left.held(BUTTON_TWO) and out.right.held(BUTTON_TWO)
          and not out.right.held(BUTTON_ONE))

    inp.extras = {"hand_joints": [0.5] * 26}
    big = inp.pack()
    out2 = HeadsetInput.unpack(big)
    check("  optional msgpack tail survives",
          len(big) > INPUT_SIZE and out2.extras is not None
          and len(out2.extras["hand_joints"]) == 26
          and r3(out2.gaze_l) == [0.31, 0.62],
          f"{len(big)} bytes")
    check("  a foreign version is rejected, not misread",
          HeadsetInput.unpack(b"GVIN\x01" + blob[5:]) is None)

    print("saccade widening")
    # The claim being tested is the one that makes this worth doing: widening the patch
    # buys coverage for *no* bandwidth, because the stored pixel count never moves.
    plain_hdr, _ = round_trip(sender, rx_sock, receiver, src, (0.5, 0.5), 1.0)
    wide_hdr, wide_canvas = round_trip(sender, rx_sock, receiver, src, (0.5, 0.5), 2.5)
    check("stored patch size is identical at any zoom",
          (wide_hdr.fovea_px_w, wide_hdr.fovea_px_h)
          == (plain_hdr.fovea_px_w, plain_hdr.fovea_px_h),
          f"1:1 {plain_hdr.fovea_px_w}x{plain_hdr.fovea_px_h} vs "
          f"2.5x {wide_hdr.fovea_px_w}x{wide_hdr.fovea_px_h}")
    grew_w = wide_hdr.fovea_w / max(plain_hdr.fovea_w, 1e-6)
    grew_h = wide_hdr.fovea_h / max(plain_hdr.fovea_h, 1e-6)
    check("covered source area grows with zoom",
          abs(grew_w - 2.5) < 0.1 and abs(grew_h - 2.5) < 0.1,
          f"width x{grew_w:.2f}, height x{grew_h:.2f}")

    # Bytes, measured rather than asserted. A widened patch is a downscaled one, so if
    # anything it compresses slightly better; what must not happen is it costing more.
    before = sender.bytes_sent
    for _ in range(20):
        round_trip(sender, rx_sock, receiver, src, (0.5, 0.5), 1.0)
    tight = sender.bytes_sent - before
    before = sender.bytes_sent
    for _ in range(20):
        round_trip(sender, rx_sock, receiver, src, (0.5, 0.5), 2.5)
    wide = sender.bytes_sent - before
    check("widening does not cost bandwidth",
          wide <= tight * 1.15,
          f"{tight/20:.0f} B/frame at 1:1 vs {wide/20:.0f} B/frame at 2.5x "
          f"({wide/max(tight,1):.2f}x)")

    # And the geometry the shader will use must still be right when the patch is no
    # longer a 1:1 crop: the corners of the *covered* rect map to the corners of the
    # stored patch, whatever the magnification in between.
    fw, fh = wide_hdr.fovea_px_w, wide_hdr.fovea_px_h
    cuSpan, cvSpan, fuSpan, fvSpan = spans(wide_hdr, wide_canvas)
    chh, cww = wide_canvas.shape[:2]
    rect = np.array([wide_hdr.fovea_x, 1.0 - wide_hdr.fovea_y,
                     wide_hdr.fovea_w, wide_hdr.fovea_h])
    zmis = []
    for (fx, fy, want) in ((0.0, 0.0, (chh // 2, 0)),
                           (1.0, 0.0, (chh // 2, fw - 1)),
                           (1.0, 1.0, (chh // 2 + fh - 1, fw - 1))):
        # a point at fraction (fx, fy) across the covered rect, in image coords
        u = (wide_hdr.fovea_x - wide_hdr.fovea_w / 2) + fx * wide_hdr.fovea_w
        v = 1.0 - ((wide_hdr.fovea_y - wide_hdr.fovea_h / 2) + fy * wide_hdr.fovea_h)
        extent = np.maximum(rect[2:], 1e-5)
        t = np.clip((np.array([u, v]) - (rect[:2] - extent * 0.5)) / extent, 0.0, 1.0)
        col = min(fw - 1, int(t[0] * fuSpan * cww))
        row = min(chh // 2 + fh - 1, int((1.0 - ((0.5 - fvSpan) + fvSpan * t[1])) * chh))
        if (row, col) != want:
            zmis.append(f"({fx},{fy}) -> ({row},{col}) want {want}")
    check("widened patch uv still maps corner to corner", not zmis, "; ".join(zmis))

    w = SaccadeWidener(max_zoom=2.5)
    t = 0.0
    for _ in range(6):
        t += 1 / 90
        w.update((0.5, 0.5), t)
    check("fixation stays at 1:1", abs(w.zoom - 1.0) < 1e-6, f"zoom {w.zoom:.3f}")
    t += 1 / 90
    w.update((0.9, 0.55), t)
    check("a saccade widens immediately", w.zoom > 2.4, f"zoom {w.zoom:.3f}")
    for _ in range(30):
        t += 1 / 90
        w.update((0.9, 0.55), t)
    check("and settles back once the eye lands", w.zoom < 1.15, f"zoom {w.zoom:.3f}")
    check("no gaze means no widening", abs(w.update(None, t + 0.01) - 1.0) < 1e-6)
    off = SaccadeWidener(max_zoom=1.0)
    off.update((0.1, 0.1), 0.0)
    check("max_zoom=1.0 disables it", abs(off.update((0.9, 0.9), 0.011) - 1.0) < 1e-6)

    print("hands and controllers")
    joints = tuple((i * 0.01, i * 0.02, i * 0.03) for i in range(24))
    hl = HandState(True, 0.9, (0.11, 1.22, 0.33), (0, 0.7071, 0, 0.7071),
                   pinch=(0.0, 0.8, 0.25, 0.0, 1.0), joints=joints)
    hr = HandState(False)

    plain = HeadsetInput(seq=1, flags=INPUT_LEFT_VALID)
    check("a controller session still costs exactly 158 bytes",
          len(plain.pack()) == 158, f"{len(plain.pack())} bytes")
    check("  and reports controllers",
          HeadsetInput.unpack(plain.pack()).source == "controllers")

    withhands = HeadsetInput(seq=2, head_pos=(0, 1.6, 0), hand_l=hl, hand_r=hr,
                             extras={"note": "tail still works"})
    blob = withhands.pack()
    g = HeadsetInput.unpack(blob)
    check("hands ride along without disturbing the fixed part",
          g is not None and g.seq == 2 and abs(g.head_pos[1] - 1.6) < 1e-6,
          f"{len(blob)} bytes")
    check("  source says hands", g.source == "hands", g.source if g else "-")
    check("  joints survive", g.hand_l.joint_count == 24
          and abs(g.hand_l.joints[23][2] - 0.69) < 1e-4)
    check("  pinch survives", abs(g.hand_l.pinch_of("index") - 0.8) < 0.01
          and abs(g.hand_l.pinch_of("pinky") - 1.0) < 0.01)
    check("  wrist pose survives", abs(g.hand_l.wrist_pos[1] - 1.22) < 1e-4)
    check("  an untracked hand costs almost nothing",
          not g.hand_r.tracked and g.hand_r.joint_count == 0)
    check("  the msgpack tail still parses after the hand block",
          g.extras == {"note": "tail still works"}, str(g.extras))

    # The flag is written by pack(); a packet built in memory has hands and a zero flag.
    # Reading the flag alone called that a controller session, which is how the
    # visualiser came to draw nothing at all.
    check("hands are detected on an unpacked-in-memory packet too",
          HeadsetInput(seq=3, hand_l=hl).source == "hands")

    truncated = blob[:200]
    check("a packet cut mid-hand is rejected, not half-read",
          HeadsetInput.unpack(truncated) is None)

    bad = bytearray(plain.pack())
    bad[4] = 2                     # the previous version
    check("a v2 packet is rejected rather than misread as hands",
          HeadsetInput.unpack(bytes(bad)) is None)

    print("camera parameters")
    cam = CameraParams(1920, 1200,
                       EyeIntrinsics(1050.0, 990.0, 930.0, 615.0),
                       EyeIntrinsics(1050.0, 990.0, 947.0, 615.0),
                       baseline_m=0.075)
    check("wire round trip", CameraParams.from_wire(cam.to_wire()) == cam)
    check("fov derives from focal length",
          abs(cam.hfov_deg() - 84.5) < 1.0 and abs(cam.vfov_deg() - 62.4) < 1.0,
          f"{cam.hfov_deg():.1f} x {cam.vfov_deg():.1f} deg")

    # The bridge people will actually use: what cv2.stereoRectify hands back. The
    # baseline hides in P2's fourth column as -fx*b, and getting that wrong silently
    # scales the whole scene.
    fx, b = 1050.0, 0.075
    P1 = [[fx, 0, 930.0, 0], [0, 990.0, 615.0, 0], [0, 0, 1, 0]]
    P2 = [[fx, 0, 947.0, -fx * b], [0, 990.0, 615.0, 0], [0, 0, 1, 0]]
    r = CameraParams.from_projection_matrices(P1, P2, 1920, 1200)
    check("recovers the baseline from P2", abs(r.baseline_m - b) < 1e-9,
          f"{r.baseline_m * 1000:.1f} mm, want {b * 1000:.0f}")
    check("keeps each eye's own principal point",
          r.left.cx == 930.0 and r.right.cx == 947.0,
          f"L {r.left.cx} R {r.right.cx}")

    # A centred pinhole is the degenerate case the manual FOV slider assumed.
    syn = CameraParams.from_hfov(1920, 1200, 90.0, 0.06)
    check("synthesised params are centred and square",
          abs(syn.left.cx - 960) < 1e-6 and abs(syn.left.fx - syn.left.fy) < 1e-6
          and abs(syn.hfov_deg() - 90.0) < 1e-3,
          f"cx={syn.left.cx} fx={syn.left.fx:.1f} hfov={syn.hfov_deg():.2f}")

    print("bitrate control")
    c = BitrateController(start_kbps=8000, min_kbps=800, max_kbps=20000)
    t = 0.0
    for _ in range(6):
        t += 1.05
        c.update(t, 0.0, 30.0)
    check("a clean path climbs", c.target_kbps > 8000, f"{c.target_kbps} kbps")
    high = c.target_kbps

    # Delay must act before loss does: a queue builds before it overflows, and reacting
    # only to loss means the viewer has already seen a broken frame.
    for _ in range(3):
        t += 0.4
        c.update(t, 0.0, 140.0)
    check("rising delay cuts the rate before any loss",
          c.target_kbps < high * 0.6 and c.reason == "delay",
          f"{c.target_kbps} kbps, reason={c.reason}")

    mid = c.target_kbps
    for _ in range(3):
        t += 0.4
        c.update(t, 0.10, 150.0)
    check("loss cuts it further", c.target_kbps < mid, f"{c.target_kbps} kbps")
    check("and never below the floor", c.target_kbps >= 800, f"{c.target_kbps} kbps")

    low = c.target_kbps
    for _ in range(12):
        t += 1.05
        c.update(t, 0.0, 30.0)
    check("it recovers when the path clears", c.target_kbps > low, f"{c.target_kbps} kbps")
    check("and never above the ceiling", c.target_kbps <= 20000, f"{c.target_kbps} kbps")

    # The clocks at the two ends have unrelated epochs, so this number is routinely
    # large and negative. Nothing may assume it is a positive latency.
    c2 = BitrateController(start_kbps=8000)
    t = 0.0
    base = -1.234e8
    for _ in range(6):
        t += 1.05
        c2.update(t, 0.0, base)
    climbed = c2.target_kbps > 8000
    for _ in range(3):
        t += 0.4
        c2.update(t, 0.0, base + 200.0)
    check("a negative clock offset still yields real queueing delay",
          climbed and c2.reason == "delay" and c2.queue_ms > 100,
          f"climbed={climbed} reason={c2.reason} queue={c2.queue_ms:.0f} ms")

    c3 = BitrateController(start_kbps=8000)
    check("no latency sample yet is not a congestion signal",
          c3.update(1.0, 0.0, None) == 8000, f"{c3.target_kbps} kbps")

    # A path that is permanently slower must re-baseline, or one lucky early sample
    # pins the floor and every later sample reads as congestion forever.
    c4 = BitrateController(start_kbps=8000, baseline_decay_ms_per_s=2.0)
    c4.update(0.0, 0.0, 20.0)
    c4.update(60.0, 0.0, 90.0)
    check("the delay baseline drifts up on a slower path",
          c4.baseline_ms > 20.0, f"baseline {c4.baseline_ms:.0f} ms")

    print("encoder under concurrent retarget")
    # The adaptive-bitrate decision arrives on the control channel's socket thread while
    # the send loop is inside encode(). Retargeting rebuilds the x264 encoder, and doing
    # that underneath an in-flight encode returns frames to libx264's internal pool
    # twice. It does not raise -- it aborts the process, seconds later, wherever the two
    # threads happened to collide, which is why it read as "the robot just dies".
    from gvlink.video import EyeEncoder
    enc = EyeEncoder(256, 256, fps=30, bitrate_kbps=4000, threads=2)
    stop = threading.Event()
    err = []

    def hammer():
        k = 4000
        while not stop.is_set():
            try:
                k = 6000 if k == 4000 else 4000
                enc.set_bitrate(k)
            except Exception as e:       # noqa: BLE001 -- reported, not swallowed
                err.append(repr(e))
                return
            time.sleep(0.001)

    t = threading.Thread(target=hammer, daemon=True)
    t.start()
    px = np.zeros((256, 256, 3), np.uint8)
    for i in range(240):
        px[(i * 5) % 200:(i * 5) % 200 + 40] = (i * 7) % 255
        enc.encode(px)
    stop.set()
    t.join(2.0)
    check("survives retargeting from another thread while encoding",
          not err and enc.rebuilds > 0, f"{enc.rebuilds} rebuilds {'; '.join(err)}")
    enc.close()

    print("viz frames")
    from gvlink.viz import (_GL_TO_CV, _qconj, _qmul, _qrot, _yaw_only,
                            to_right_handed)

    # Unity is left-handed with +Z forward. Flipping Z gives a right-handed frame in
    # which the operator looks down -Z. Getting this backwards pointed the headset
    # frustum behind the operator, which is exactly how it looked.
    ident = (0.0, 0.0, 0.0, 1.0)
    pos, rot = to_right_handed((1.0, 2.0, 3.0), ident)
    check("position flips Z", pos == (1.0, 2.0, -3.0), str(pos))

    fwd = _qrot(rot, (0.0, 0.0, -1.0))
    check("an unrotated head looks down -Z", abs(fwd[2] + 1) < 1e-6, str(r3(fwd)))

    # A 90 degree yaw in Unity (about +Y, left-handed) turns the operator to their right.
    yaw90 = (0.0, math.sin(math.pi / 4), 0.0, math.cos(math.pi / 4))
    _, r = to_right_handed((0, 0, 0), yaw90)
    f = _qrot(r, (0.0, 0.0, -1.0))
    check("a Unity yaw turns the right way", abs(f[0] - 1.0) < 1e-6 and abs(f[2]) < 1e-6,
          f"forward {r3(f)}")

    # The frustum correction must take -Z-forward/+Y-up to +Z-forward/+Y-down.
    fr = _qmul(rot, _GL_TO_CV)
    cam_fwd, cam_up = _qrot(fr, (0.0, 0.0, 1.0)), _qrot(fr, (0.0, 1.0, 0.0))
    check("frustum +Z lands on the operator's forward",
          abs(cam_fwd[2] + 1) < 1e-6, str(r3(cam_fwd)))
    check("frustum +Y lands on the operator's down",
          abs(cam_up[1] + 1) < 1e-6, str(r3(cam_up)))

    # The view anchor must put the head at the origin without tilting the world.
    tilted = _qmul((0.0, math.sin(0.3), 0.0, math.cos(0.3)),
                   (math.sin(0.25), 0.0, 0.0, math.cos(0.25)))     # yaw then pitch
    y = _yaw_only(tilted)
    check("the anchor keeps heading and drops tilt",
          abs(y[0]) < 1e-9 and abs(y[2]) < 1e-9 and abs(_qrot(y, (0, 1, 0))[1] - 1) < 1e-9,
          str(r3(y[:3])))

    apos, arot = (0.5, 1.6, -2.0), y
    inv = _qconj(arot)
    d = tuple(a - b for a, b in zip(apos, apos))
    check("the anchored head sits at the origin",
          max(abs(v) for v in _qrot(inv, d)) < 1e-9)
    moved = (apos[0] + 1.0, apos[1], apos[2])
    rel = _qrot(inv, tuple(m - a for m, a in zip(moved, apos)))
    check("  and a metre away stays a metre away",
          abs(math.sqrt(sum(v * v for v in rel)) - 1.0) < 1e-9, str(r3(rel)))

    print("headset ui wire format")
    # A recording link: HeadsetUi is a front for publish(), so what matters is that the
    # bytes it produces are the ones GvSceneCommands.cs parses. Field names are checked
    # against that file by eye; the point of this test is that they stop changing by
    # accident.
    class _Rec:
        def __init__(self):
            self.sent = []
        def publish(self, topic, data):
            self.sent.append((topic, data))
            return True
        def subscribe(self, topic, fn):
            pass

    rec = _Rec()
    ui = HeadsetUi(rec)
    ui.guide("l", (0.1, 1.2, -0.3), (0, 0, 0, 1), tol=0.02, ang=15, label="home")
    topic, g = rec.sent[-1]
    check("guide topic and shape", topic == "ui/guide"
          and g["side"] == "l" and g["p"] == [0.1, 1.2, -0.3]
          and g["q"] == [0, 0, 0, 1] and g["tol"] == 0.02 and g["ang"] == 15.0
          and g["label"] == "home")
    # Absent, not zero: a zero angular tolerance would mean "match the orientation
    # exactly", which no hand ever does, so a guide that only cares about position would
    # silently never be reachable.
    ui.guide("r", (0, 0, 0))
    check("  an omitted angular tolerance is absent, not zero",
          "ang" not in rec.sent[-1][1])

    ui.marker("b", "box", pos=(0, 1, -0.4), scale=(0.6, 0.4, 0.3),
              colour=(1, 0.5, 0, 0.15), ttl=2.0)
    topic, m = rec.sent[-1]
    spec = m["m"][0]
    check("marker topic and shape", topic == "ui/marker" and spec["id"] == "b"
          and spec["t"] == "box" and spec["s"] == [0.6, 0.4, 0.3]
          and spec["c"] == [1.0, 0.5, 0.0, 0.15] and spec["ttl"] == 2.0)

    ui.marker("l", "line", points=[(0, 1, 0), (0.2, 1.1, -0.1)], width=0.005)
    spec = rec.sent[-1][1]["m"][0]
    check("  a line carries its points and width",
          spec["pts"] == [[0, 1, 0], [0.2, 1.1, -0.1]] and spec["w"] == 0.005)

    ui.marker("t", "sphere", scale=0.08)
    check("  a scalar scale stays scalar", rec.sent[-1][1]["m"][0]["s"] == 0.08)

    ui.toast("hello", "warn", 2)
    check("toast", rec.sent[-1] == ("ui/toast", {"txt": "hello", "sev": "warn",
                                                 "secs": 2.0}))
    ui.buzz("r", 0.6, 0.05)
    check("haptics", rec.sent[-1] == ("hx", {"side": "r", "amp": 0.6, "secs": 0.05}))

    ui.marker_clear("traj")
    check("clear by prefix", rec.sent[-1] == ("ui/marker", {"clear": "traj"}))
    ui.guide_clear("l")
    check("clear a guide", rec.sent[-1] == ("ui/guide", {"side": "l", "clear": True}))

    # Everything must survive msgpack, since that is how it actually travels.
    import msgpack as _mp
    ok = all(_mp.unpackb(_mp.packb(d, use_bin_type=True), raw=False) == d
             for _, d in rec.sent)
    check("every message round-trips through msgpack unchanged", ok)

    print("reconnect")
    # The headset menu's Reconnect drops the control channel and immediately redials with
    # identical settings. On a LAN both transitions land inside a single frame of the send
    # loop, so it never observes a None session -- and when the sender keyed its "is this
    # a new viewer" decision on (address, codec, shape), a reconnect was indistinguishable
    # from nothing happening. The encoders kept running mid-GOP while the headset's newly
    # built decoder waited for a keyframe that never came: reconnect, then a black screen.
    port = _free_port()
    link = RobotLink(port=port).start()
    try:
        def dial():
            return ControlClient("127.0.0.1", port, video_port=15552, codec=1,
                                 foveation=True, name="Oculus Quest 2").start()

        def settings_of(sess):
            return ((sess.addr, sess.video_port), sess.codec, sess.layout_key())

        c1 = dial()
        s1 = _await(lambda: link.session)
        check("a viewer is seen", s1 is not None)

        c1.stop()
        s2 = _await(lambda: link.session if (link.session is not None
                                             and link.session.id != s1.id) else None)
        c2_alive = dial() if s2 is None else None
        if s2 is None:
            s2 = _await(lambda: link.session if (link.session is not None
                                                 and link.session.id != s1.id) else None)

        check("a reconnect produces a second session", s2 is not None)
        if s1 is not None and s2 is not None:
            check("  whose settings are byte-identical to the first",
                  settings_of(s1) == settings_of(s2))
            check("  so only the session id distinguishes them",
                  s1.id != s2.id, f"{s1.id} vs {s2.id}")
            check("  and the sender's change key therefore differs",
                  (s1.id,) + settings_of(s1) != (s2.id,) + settings_of(s2))
        if c2_alive is not None:
            c2_alive.stop()
    finally:
        link.stop()

    # A rebuilt sender must open on a keyframe, or rebuilding it would not have helped.
    rx_sock = make_udp_socket(("127.0.0.1", 0))
    rx_sock.settimeout(2.0)
    tx_sock = make_udp_socket()
    fresh = EyeStreamSender(tx_sock, rx_sock.getsockname(), EYE_LEFT,
                            AtlasLayout(512, 512, 0.25, 0.75), fps=30,
                            bitrate_kbps=8000)
    _, canvas = round_trip(fresh, rx_sock, EyeStreamReceiver(EYE_LEFT),
                           make_chart(1280, 800), (0.5, 0.5))
    check("a freshly built sender's first frame decodes on its own",
          canvas is not None and canvas.size > 0)
    tx_sock.close()
    rx_sock.close()

    print("reassembler under loss")
    r = Reassembler()
    h = VideoHeader(1, EYE_LEFT, 0, 0, 0, now_us())
    frags = list(fragment(h, b"A" * 5000))
    for d in frags[:-1]:              # frame 1 arrives incomplete
        r.push(d)
    h2 = VideoHeader(2, EYE_LEFT, 0, 0, 0, now_us())
    out = None
    for d in fragment(h2, b"B" * 2000):   # frame 2 must complete regardless
        out = r.push(d) or out
    check("incomplete frame abandoned, next frame still completes", out is not None)
    check("  drop counted", r.frames_dropped == 1, f"dropped={r.frames_dropped}")
    check("  lost fragments counted", r.fragments_lost == 1,
          f"lost={r.fragments_lost}")
    check("  payload intact", out is not None and out[1] == b"B" * 2000)

    print("sender restart does not wedge the receiver")
    r2 = Reassembler()
    stale = list(fragment(VideoHeader(9000, EYE_LEFT, 0, 0, 0, now_us()), b"C" * 3000))
    for d in stale[:-1]:              # leave the receiver mid-frame at a high id
        r2.push(d)
    out2 = None
    for d in fragment(VideoHeader(0, EYE_LEFT, 0, 0, 0, now_us()), b"D" * 1000):
        out2 = r2.push(d) or out2
    check("frame id reset accepted", out2 is not None and out2[1] == b"D" * 1000)

    lat = receiver.latency_ms
    print(f"\nloopback latency over {len(lat)} frames: "
          f"mean {lat.mean():.2f} ms  p95 {lat.pct(0.95):.2f} ms")
    print(f"{'FAILED: ' + ', '.join(FAILS) if FAILS else 'all checks passed'}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
