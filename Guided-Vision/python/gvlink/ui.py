"""
Drawing in the operator's headset, from the robot.

    ui = HeadsetUi(link)
    ui.toast("recording", secs=2)
    ui.guide("l", pos, quat, tol=0.02, label="home")
    ui.marker("bounds", "box", pos=(0, 1, -0.4), scale=(0.6, 0.4, 0.3),
              colour=(1, 0.5, 0, 0.15))
    ui.buzz("r", 0.6, 0.05)

The wire format is the interesting part, not this class -- this is a thin, typo-proof
front for `link.publish`, and anything it can send can be sent by hand. See
`docs/HEADSET_API.md` for the protocol and the reasoning behind the split between the
semantic layer (`guide`) and the general one (`marker`).

**Poses use the headset's own frame and handedness**: the tracking origin, Y up,
left-handed, exactly as they arrive in `HeadsetInput`. A pose received from the headset
can be sent straight back as a guide and it lands where it came from. Converting to a
right-handed or Z-up convention stays at whichever end actually needs it, as it does for
the viser visualiser.
"""

from __future__ import annotations

from typing import Any, Iterable, Sequence

Vec3 = Sequence[float]
Quat = Sequence[float]
Colour = Sequence[float]


def _xyz(v: Vec3) -> list[float]:
    return [float(v[0]), float(v[1]), float(v[2])]


def _quat(q: Quat | None) -> list[float]:
    return [0.0, 0.0, 0.0, 1.0] if q is None else [float(x) for x in q[:4]]


class HeadsetUi:
    """Everything the robot can draw or trigger in the headset."""

    def __init__(self, link) -> None:
        self._link = link

    # ---------------------------------------------------------------- messages

    def toast(self, text: str, sev: str = "info", secs: float = 3.0) -> bool:
        """A transient line in the operator's view. `sev` is info | warn | error."""
        return self._link.publish("ui/toast", {"txt": str(text), "sev": sev,
                                               "secs": float(secs)})

    def buzz(self, side: str = "r", amp: float = 0.5, secs: float = 0.05) -> bool:
        """
        A haptic pulse. Ignored for a hand, which has no motor.

        The cheapest realism there is: a pulse on contact tells the operator something
        that no amount of video resolution does.
        """
        return self._link.publish("hx", {"side": side, "amp": float(amp),
                                         "secs": float(secs)})

    # ---------------------------------------------------------------- guidance

    def guide(self, side: str, pos: Vec3, rot: Quat | None = None, *,
              tol: float = 0.03, ang: float | None = None, hold: float = 0.4,
              label: str = "", ttl: float | None = None) -> bool:
        """
        "Put your left hand here."

        The headset decides what the target *looks* like -- a hand target or a controller
        target -- from whatever is being tracked at that instant, and reports
        `ui/guide/reached` once the operator has been inside `tol` for `hold` seconds. So
        this call does not care which the operator picked up, and stays correct on the
        frame they swap.

        `tol` is metres and doubles as the drawn size of the target: the sphere *is* the
        tolerance, so touching it is arriving. `ang` is an optional orientation tolerance
        in degrees; omit it when only position matters.
        """
        msg: dict[str, Any] = {"side": side, "p": _xyz(pos), "q": _quat(rot),
                               "tol": float(tol), "hold": float(hold), "label": label}
        if ang is not None:
            msg["ang"] = float(ang)
        if ttl is not None:
            msg["ttl"] = float(ttl)
        return self._link.publish("ui/guide", msg)

    def guide_clear(self, side: str) -> bool:
        return self._link.publish("ui/guide", {"side": side, "clear": True})

    def on_guide_reached(self, fn) -> None:
        """`fn({"side": "l", "label": "home", "src": "hand"})`."""
        self._link.subscribe("ui/guide/reached", fn)

    # ---------------------------------------------------------------- markers

    def marker(self, mid: str, kind: str, *, pos: Vec3 = (0, 0, 0),
               rot: Quat | None = None, frame: str = "origin",
               scale: Vec3 | float = 0.05, colour: Colour = (0.35, 0.75, 1.0, 0.8),
               points: Iterable[Vec3] | None = None, text: str = "",
               width: float = 0.004, size: float = 0.03,
               ttl: float | None = None) -> bool:
        """
        One marker, upserted by id. `kind` is pose | sphere | box | line | arrow | text.

        `frame` is origin | head | view | hand_l | hand_r.

        **Give anything advisory a `ttl`.** A robot that dies leaves its markers on
        screen, still telling the operator to do something that stopped being true when
        the process did -- and stale guidance looks exactly like working guidance. Re-send
        rather than relying on being alive to take it back.
        """
        spec: dict[str, Any] = {"id": str(mid), "t": kind, "f": frame,
                                "p": _xyz(pos), "q": _quat(rot),
                                "c": [float(c) for c in colour]}
        if isinstance(scale, (int, float)):
            spec["s"] = float(scale)
        else:
            spec["s"] = _xyz(scale)
        if points is not None:
            spec["pts"] = [_xyz(p) for p in points]
            spec["w"] = float(width)
        if text:
            spec["txt"] = str(text)
            spec["size"] = float(size)
        if ttl is not None:
            spec["ttl"] = float(ttl)
        return self._link.publish("ui/marker", {"m": [spec]})

    def markers(self, specs: Iterable[dict]) -> bool:
        """Several at once, already in wire form. One message, one frame of latency."""
        return self._link.publish("ui/marker", {"m": list(specs)})

    def marker_clear(self, prefix: str = "*") -> bool:
        """Remove by id prefix; `*` removes everything."""
        return self._link.publish("ui/marker", {"clear": prefix})
