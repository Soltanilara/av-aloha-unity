"""
A synthetic detail chart, so the foveal path can be judged without a camera.

Deliberately full of high spatial frequencies: the whole question foveation asks is
"what does the coarse layer throw away", and a smooth scene answers it dishonestly.
"""

from __future__ import annotations

import cv2
import numpy as np


def make_chart(w: int, h: int) -> np.ndarray:
    img = np.zeros((h, w, 3), np.uint8)

    # 2-pixel checkerboard floor -- the first thing any downscale destroys
    yy, xx = np.mgrid[0:h, 0:w]
    img[:] = (((xx // 2 + yy // 2) % 2) * 90 + 30)[:, :, None]

    # sinusoidal gratings, coarse to fine left to right
    band_h = h // 6
    for i in range(6):
        freq = 2.0 ** (i + 1)
        x0 = int(w * i / 6)
        x1 = int(w * (i + 1) / 6)
        gx = np.arange(x1 - x0)
        g = (127 + 120 * np.sin(2 * np.pi * gx * freq / (x1 - x0) * 8)).astype(np.uint8)
        img[band_h:band_h * 2, x0:x1] = g[None, :, None]

    # concentric rings
    cx, cy = w // 2, int(h * 0.62)
    for r in range(8, min(w, h) // 3, 7):
        cv2.circle(img, (cx, cy), r, (240, 240, 240), 1, cv2.LINE_AA)

    # text at shrinking sizes -- the readability test
    for i, scale in enumerate((1.4, 1.0, 0.7, 0.5, 0.35)):
        cv2.putText(img, f"detail {i}: the quick brown fox 0123456789",
                    (20, int(h * 0.78) + i * int(h * 0.045)),
                    cv2.FONT_HERSHEY_SIMPLEX, scale, (255, 255, 255), 1, cv2.LINE_AA)

    # colour bars along the bottom
    bar = h - h // 12
    cols = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (0, 255, 255),
            (255, 0, 255), (255, 255, 0), (255, 255, 255)]
    for i, c in enumerate(cols):
        img[bar:, int(w * i / len(cols)):int(w * (i + 1) / len(cols))] = c
    return img
