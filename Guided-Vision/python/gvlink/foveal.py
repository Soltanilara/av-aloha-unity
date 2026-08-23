"""
Foveal atlas packing and its inverse.

One video stream per eye, at a canvas size that never changes for the life of the
session, carrying both layers packed together (docs/PLAN.md section 5):

    +----------------------------+
    |  COARSE band   canvas_w x h/2   the entire camera FOV, downscaled
    +----------------------------+
    |  FOVEA band    canvas_w x h/2   a native-resolution crop centred on gaze
    +----------------------------+

Each layer occupies the top-left of its band at a configurable fraction of it
(`coarse_scale`, `fovea_scale`); the remainder is left black, which an inter-coded
stream compresses to almost nothing. Storing the layers smaller than their bands is
what actually controls the detail ratio between periphery and fovea, and it can be
retuned mid-session because the canvas -- and therefore the decoder -- never changes
size. Both scales travel in the packet header, so a frame is self-describing.

Two properties matter and both come from that fixed canvas: one MediaCodec instance
per eye with no runtime reconfigure, and no synchronisation problem between layers
because they are the same frame.

`reconstruct` is the CPU reference implementation of what the headset's fragment
shader will do. It exists so the atlas geometry can be validated on a laptop before
anyone writes GLSL against it -- if the two ever disagree, this one is the spec.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import cv2
import numpy as np


@dataclass(frozen=True)
class AtlasLayout:
    canvas_w: int = 1024
    canvas_h: int = 1024

    # Fraction of the coarse band the downscaled full field occupies. Lower means a
    # blurrier periphery and a more pronounced foveal effect.
    coarse_scale: float = 1.0

    # Fraction of the fovea band the 1:1 crop occupies. Lower means a smaller, more
    # eye-like patch -- and a smaller slice of the frame carried at full detail.
    fovea_scale: float = 1.0

    @property
    def band_h(self) -> int:
        """Height of each of the two bands. The canvas is split in half."""
        return self.canvas_h // 2

    def __post_init__(self) -> None:
        if self.canvas_h % 4 or self.canvas_w % 2:
            raise ValueError("canvas dims must keep both bands even for yuv420p")
        for name in ("coarse_scale", "fovea_scale"):
            v = getattr(self, name)
            if not 0.05 <= v <= 1.0:
                raise ValueError(f"{name} must be in [0.05, 1.0], got {v}")

    def coarse_px(self) -> tuple[int, int]:
        return (_even(self.canvas_w * self.coarse_scale),
                _even(self.band_h * self.coarse_scale))

    def fovea_px(self) -> tuple[int, int]:
        return (_even(self.canvas_w * self.fovea_scale),
                _even(self.band_h * self.fovea_scale))


def _even(x: float) -> int:
    """Round down to an even count -- yuv420p subsamples chroma 2x2."""
    return max(2, int(x) & ~1)


def _downscale(src: np.ndarray, w: int, h: int) -> np.ndarray:
    """
    Area-quality downscale that stays fast at large reduction factors.

    cv2.resize with INTER_AREA has a fast exact-box path only when the reduction is an
    integer factor. Off that path it degrades badly: 1920x1200 -> 358x178 costs 11.6 ms,
    against 1.5 ms for the same size at an exact 5x. That is most of a frame budget
    spent on the periphery, and it is exactly the regime foveation puts us in.

    Halving with pyrDown until the remaining reduction is under 2x, then finishing with
    INTER_AREA, costs 1.3 ms for any source size and any target. INTER_LINEAR would be
    faster still but aliases hard at these ratios -- a shimmering periphery is the one
    artefact foveation cannot afford, because motion in the periphery is what the eye
    is most sensitive to.
    """
    img = src
    while img.shape[1] >= 2 * w and img.shape[0] >= 2 * h:
        img = cv2.pyrDown(img)
    if img.shape[1] == w and img.shape[0] == h:
        return img
    return cv2.resize(img, (w, h), interpolation=cv2.INTER_AREA)


def build_atlas(src: np.ndarray, layout: AtlasLayout, gaze: tuple[float, float] | None,
                fovea_zoom: float = 1.0):
    """
    Pack one eye's source image into the transmit canvas.

    `gaze` is a normalised (x, y) into the source image, or None for the
    non-foveated path -- in which case the whole canvas is just the downscaled
    source and the shader falls through to a plain sample. That is the entire
    foveation on/off switch, and it is why a Quest 2/3 works unchanged.

    `fovea_zoom` widens how much of the source the patch covers while keeping its
    stored size identical: 1.0 is the 1:1 crop, 2.0 covers twice the width and height
    at half the linear detail. The transmitted bytes do not change at all -- only the
    trade between coverage and sharpness -- and the shader needs no knowledge of it,
    because the header already describes the covered rect. See `SaccadeWidener`.

    Returns (canvas_bgr, fovea_norm or None, coarse_px, fovea_px). fovea_norm is
    (centre_x, centre_y, width, height) normalised into the source image; the two
    pixel pairs are each layer's stored size, which the header carries verbatim.
    """
    h, w = src.shape[:2]
    cw, ch, bh = layout.canvas_w, layout.canvas_h, layout.band_h

    if gaze is None:
        return _downscale(src, cw, ch), None, (cw, ch), (0, 0)

    # Black, not uninitialised: the unused remainder of each band must be a constant
    # the encoder can throw away, and must not show through the feather ring.
    canvas = np.zeros((ch, cw, 3), np.uint8)

    coarse_w, coarse_h = layout.coarse_px()
    canvas[0:coarse_h, 0:coarse_w] = _downscale(src, coarse_w, coarse_h)

    # At zoom 1 the crop is taken 1:1 -- no resampling at all, which is where the
    # detail advantage comes from. It is only ever shrunk to fit a small source.
    fov_w, fov_h = layout.fovea_px()
    fov_w, fov_h = min(fov_w, w), min(fov_h, h)

    # Region of the source the patch covers. Always at least the stored size, so the
    # patch is resampled down or not at all -- never up.
    zoom = max(1.0, float(fovea_zoom))
    cov_w = min(w, max(fov_w, _even(fov_w * zoom)))
    cov_h = min(h, max(fov_h, _even(fov_h * zoom)))

    x0 = max(0, min(w - cov_w, int(round(gaze[0] * w - cov_w / 2))))
    y0 = max(0, min(h - cov_h, int(round(gaze[1] * h - cov_h / 2))))
    patch = src[y0:y0 + cov_h, x0:x0 + cov_w]
    if (cov_w, cov_h) != (fov_w, fov_h):
        patch = _downscale(patch, fov_w, fov_h)
    canvas[bh:bh + fov_h, 0:fov_w] = patch

    fovea = ((x0 + cov_w / 2) / w, (y0 + cov_h / 2) / h, cov_w / w, cov_h / h)
    return canvas, fovea, (coarse_w, coarse_h), (fov_w, fov_h)


class SaccadeWidener:
    """
    Trades foveal detail for foveal coverage while the eye is moving fast.

    The problem it addresses is stated in the plan: gaze reaches the sender, the sender
    crops the *next* frame, and that frame takes an encode/network/decode trip back --
    call it 50-90 ms. A saccade completes in 30-80 ms, so the patch always arrives
    behind the eye. If the patch is a tight 1:1 crop, "behind" means the eye lands on
    blurry periphery and waits.

    Widening costs nothing. The patch keeps exactly the same stored pixel count, so the
    bitrate does not move; it simply covers more of the source at lower magnification.
    During a saccade that is the right trade -- vision is substantially suppressed for
    most of the movement anyway, so the lost sharpness is largely unseen, while the
    extra coverage means wherever the eye lands is already at better-than-periphery
    detail. As the eye settles, coverage decays back and full sharpness returns.

    Attack is instant and release is slow, on purpose: being late to widen defeats the
    point, whereas being late to narrow only costs a little sharpness for a moment.

    Untested against a real eye -- the defaults are reasoned from saccade timing, not
    measured. `max_zoom=1.0` disables it entirely.
    """

    def __init__(self, max_zoom: float = 2.5, vel_full: float = 5.0,
                 release_s: float = 0.12) -> None:
        # vel_full is in normalised image widths per second. Smooth pursuit runs around
        # 0.1-0.5 and a large saccade exceeds 30, so anywhere in between separates them.
        # It is set high rather than low deliberately: at 90 Hz a sample-to-sample
        # tracker jitter of 5% of the image reads as a velocity of 4.5, and a threshold
        # that jitter can trip would leave the patch permanently widened -- which is the
        # one failure mode that costs detail without ever buying coverage.
        # The cost of the high setting is that small saccades widen only partially, and
        # those are the ones a 30-degree patch most likely already covers.
        self.max_zoom = max(1.0, float(max_zoom))
        self.vel_full = max(1e-3, float(vel_full))
        self.release_s = max(1e-3, float(release_s))
        self._prev: tuple[float, float] | None = None
        self._t = 0.0
        self._zoom = 1.0
        self.velocity = 0.0

    @property
    def zoom(self) -> float:
        return self._zoom

    def update(self, gaze: tuple[float, float] | None, now: float) -> float:
        if gaze is None or self.max_zoom <= 1.0:
            self._prev, self._zoom, self.velocity = None, 1.0, 0.0
            return 1.0
        if self._prev is None:
            self._prev, self._t = gaze, now
            return self._zoom
        dt = now - self._t
        if dt <= 0.0:
            return self._zoom
        self.velocity = math.hypot(gaze[0] - self._prev[0], gaze[1] - self._prev[1]) / dt
        self._prev, self._t = gaze, now

        target = 1.0 + (self.max_zoom - 1.0) * min(1.0, self.velocity / self.vel_full)
        if target >= self._zoom:
            self._zoom = target                      # attack: immediately
        else:
            k = math.exp(-dt / self.release_s)       # release: settle back
            self._zoom = target + (self._zoom - target) * k
        return self._zoom


def _feather_mask(h: int, w: int, feather: float) -> np.ndarray:
    """
    Separable rectangular ramp, 0 at the patch border rising to 1 inside.

    A hard-edged patch is far more visible than a low-resolution background: the eye
    finds the seam instantly. The ramp is what makes the transition unnoticeable, and
    the shader will build the same thing from two smoothsteps.
    """
    if feather <= 0:
        return np.ones((h, w), np.float32)
    fx = max(1, min(w // 2, int(round(w * feather))))
    fy = max(1, min(h // 2, int(round(h * feather))))
    rx = np.ones(w, np.float32)
    ry = np.ones(h, np.float32)
    ramp_x = np.linspace(0.0, 1.0, fx, dtype=np.float32)
    ramp_y = np.linspace(0.0, 1.0, fy, dtype=np.float32)
    rx[:fx] = ramp_x
    rx[w - fx:] = ramp_x[::-1]
    ry[:fy] = ramp_y
    ry[h - fy:] = ramp_y[::-1]
    m = np.minimum(ry[:, None], rx[None, :])
    return m * m * (3.0 - 2.0 * m)  # smoothstep


def reconstruct(canvas: np.ndarray, header, out_w: int, out_h: int,
                feather: float = 0.15) -> np.ndarray:
    """
    CPU reference for the headset's display shader: coarse everywhere, cross-faded
    into the foveal patch where the display pixel falls inside the fovea rect.
    """
    if not header.foveated:
        return cv2.resize(canvas, (out_w, out_h), interpolation=cv2.INTER_LINEAR)

    ch, cw = canvas.shape[:2]
    bh = ch // 2
    # Each layer sits in the top-left of its band, at exactly the size the header says.
    coarse_w = header.coarse_px_w or cw
    coarse_h = header.coarse_px_h or bh
    fov_w = header.fovea_px_w or cw
    fov_h = header.fovea_px_h or bh

    coarse = canvas[0:coarse_h, 0:coarse_w]
    fovea = canvas[bh:bh + fov_h, 0:fov_w]
    out = cv2.resize(coarse, (out_w, out_h), interpolation=cv2.INTER_LINEAR)

    pw = max(1, int(round(header.fovea_w * out_w)))
    ph = max(1, int(round(header.fovea_h * out_h)))
    x0 = int(round(header.fovea_x * out_w - pw / 2))
    y0 = int(round(header.fovea_y * out_h - ph / 2))

    patch = cv2.resize(fovea, (pw, ph), interpolation=cv2.INTER_LINEAR)
    mask = _feather_mask(ph, pw, feather)

    # Clip against the output rect; the fovea can sit partly off-frame near the edges.
    sx0, sy0 = max(0, -x0), max(0, -y0)
    dx0, dy0 = max(0, x0), max(0, y0)
    cw_ = min(pw - sx0, out_w - dx0)
    ch_ = min(ph - sy0, out_h - dy0)
    if cw_ <= 0 or ch_ <= 0:
        return out

    patch = patch[sy0:sy0 + ch_, sx0:sx0 + cw_].astype(np.float32)
    m = mask[sy0:sy0 + ch_, sx0:sx0 + cw_][:, :, None]
    dst = out[dy0:dy0 + ch_, dx0:dx0 + cw_].astype(np.float32)
    out[dy0:dy0 + ch_, dx0:dx0 + cw_] = (dst * (1.0 - m) + patch * m).astype(np.uint8)
    return out


def bandwidth_note(src_w: int, src_h: int, layout: AtlasLayout) -> str:
    """What the atlas buys, stated as pixels and as a detail ratio."""
    coarse_w, coarse_h = layout.coarse_px()
    fov_w, fov_h = layout.fovea_px()
    fov_w, fov_h = min(fov_w, src_w), min(fov_h, src_h)
    # Detail ratio: source pixels per displayed pixel, coarse vs foveal. The foveal
    # layer is 1:1 by construction, so this is just how far the periphery is scaled.
    ratio = src_w / max(1, coarse_w)
    return (f"source {src_w}x{src_h} -> canvas {layout.canvas_w}x{layout.canvas_h}; "
            f"coarse {coarse_w}x{coarse_h} ({ratio:.1f}x downscaled), "
            f"fovea {fov_w}x{fov_h} at 1:1 "
            f"({100.0*fov_w/src_w:.0f}% x {100.0*fov_h/src_h:.0f}% of the frame)")
