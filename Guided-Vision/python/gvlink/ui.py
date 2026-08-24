"""
Drawing in the operator's headset, from the robot.

    ui = HeadsetUi(link)
    ui.toast("recording", secs=2)
    ui.guide("l", pos, quat, tol=0.02, label="home")
    ui.marker("bounds", "box", pos=(0, 1, -0.4), scale=(0.6, 0.4, 0.3),
              colour=(1, 0.5, 0, 0.15))
    ui.buzz("r", 0.6, 0.05)

    ui.menu([button("rec", "Record episode"),
             choice("mode", "Control mode", ["cartesian", "joint"]),
             rng("spd", "Speed", 0.1, 1.0, 0.1, 0.5)])
    ui.on_menu_event(lambda e: print(e["id"], e["value"]))

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


def button(mid: str, label: str, *, enabled: bool = True, hint: str = "") -> dict:
    """A row that does something when pressed."""
    return {"id": mid, "label": label, "type": "button",
            "enabled": bool(enabled), "hint": hint}


def toggle(mid: str, label: str, value: bool = False, *, enabled: bool = True) -> dict:
    """An on/off row."""
    return {"id": mid, "label": label, "type": "toggle",
            "value": bool(value), "enabled": bool(enabled)}


def choice(mid: str, label: str, options: Sequence[str], value: str | None = None, *,
           enabled: bool = True) -> dict:
    """A row that steps through a fixed set of options."""
    opts = [str(o) for o in options]
    return {"id": mid, "label": label, "type": "choice", "options": opts,
            "value": value if value is not None else (opts[0] if opts else ""),
            "enabled": bool(enabled)}


def rng(mid: str, label: str, lo: float, hi: float, step: float, value: float, *,
        fmt: str = "", enabled: bool = True) -> dict:
    """
    A numeric row with minus/plus.

    `fmt` is a .NET format string for the value column, e.g. "{0:0.00} m". Left empty,
    the headset picks a sensible number of decimals from `step`.
    """
    return {"id": mid, "label": label, "type": "range",
            "min": float(lo), "max": float(hi), "step": float(step),
            "value": float(value), "fmt": fmt, "enabled": bool(enabled)}


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

    # ---------------------------------------------------------------- menu

    def menu(self, rows: Iterable[dict]) -> bool:
        """
        Declare the controls the session menu should show below the display settings.

        **This is the call that keeps teleoperation policy in Python.** Episode
        recording, control modes, gripper presets, homing and calibration routines are
        each a row here plus a handler, and none of them need the Unity app rebuilt --
        which is the whole reason the headset is a peripheral rather than an
        application.

        Rows are replaced wholesale, so enabling, disabling, relabelling and reordering
        are all just "send the list you want now"; there is no partial update, and no way
        for the two ends to disagree about a row that was removed. `menu([])` clears the
        section.

        Build rows with `button`, `toggle`, `choice` and `rng` rather than by hand -- the
        headset drops any row without an `id`, silently, because a row it cannot report
        back is a control that does nothing.
        """
        return self._link.publish("ui/menu", {"rows": [dict(r) for r in rows]})

    def on_menu_event(self, fn) -> None:
        """
        `fn({"id": "spd", "value": 0.7})` when the operator touches a row.

        The headset moves the row immediately and tells you afterwards -- waiting for a
        round trip would make every control feel broken. If you reject a change, publish
        the corrected list back and the row will follow.
        """
        self._link.subscribe("ui/menu/event", fn)

    # ---------------------------------------------------------------- device state

    def on_state(self, fn) -> None:
        """
        `fn(state)` a couple of times a second, with what the device is doing.

        ``{"mounted": True, "batt": 0.62, "src": "hands", "hz": 90, "fps": 88.4,
           "missed": 2, "deadman": "auto", "deadman_held": False, "origin_epoch": 3,
           "eye_tracking": False, "hand_conf": {"l": 1.0, "r": 0.5}}``

        **`mounted: False` means the operator took the headset off.** Poses keep arriving
        from a headset lying on a desk, so without this a robot happily goes on tracking
        a wrist that nobody is wearing. Treat it exactly like a deadman release.

        `src` and `missed` are worth writing into the episode record: hands and
        controllers produce measurably different demonstrations, and a dropped-frame
        count is how you find out afterwards that one bad episode was judder rather than
        the policy.
        """
        self._link.subscribe("hs/state", fn)

    def recenter(self) -> bool:
        """
        Re-origin the operator's view, and bump `origin_epoch`.

        Anything already placed in the `origin` frame refers to the old one afterwards.
        Re-send what still matters rather than assuming it survived.
        """
        return self._link.publish("ui/recenter", {})

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
