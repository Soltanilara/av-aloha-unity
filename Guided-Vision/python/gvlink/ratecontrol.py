"""
Bitrate adaptation for the video sender.

The link this runs over is not the loopback everything was measured on: over Tailscale
it may be a home uplink, a phone tether, or a DERP relay on the other side of the
country. A fixed bitrate on such a link is wrong in both directions -- too high and the
queue fills until frames arrive late and then not at all, too low and the picture is
needlessly soft on a link that could carry more.

The controller is AIMD, the same shape as TCP's, for the same reason: multiplicative
decrease is what makes competing flows converge instead of oscillating, and additive
increase is what stops a recovering flow from immediately re-congesting the path.

Two congestion signals, and the order matters:

* **Delay** is the early one. A queue builds *before* it overflows, so latency rises
  before a single packet is lost. Backing off here keeps the picture on time.
* **Loss** is the late one. By the time fragments are missing the queue has already
  overflowed and the viewer has already seen a broken frame.

Latency is measured against a rolling *minimum*, not an absolute threshold, because the
absolute number is meaningless -- it contains an unknown clock offset between the robot
and the headset. The offset cancels when comparing a sample to the best sample recently
seen, and what is left is queueing delay, which is exactly the thing worth reacting to.

Pure and clock-free: `now` is passed in, nothing is read from the network, so the whole
control law is testable against a synthetic trace.
"""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class BitrateController:
    start_kbps: int = 8000
    min_kbps: int = 800
    max_kbps: int = 20000

    # Fragment loss. 2% is already visible as broken frames; 0.5% is noise.
    loss_high: float = 0.02
    loss_low: float = 0.005

    # Queueing delay above the rolling minimum before it counts as congestion.
    delay_rise_ms: float = 40.0

    decrease: float = 0.75       # multiplicative, on congestion
    increase: float = 0.08       # additive-ish, on a clean path
    interval_s: float = 1.0      # minimum time between raises
    backoff_s: float = 0.35      # minimum time between cuts; cuts must be prompt

    # The rolling minimum has to be able to rise, or one lucky early sample pins the
    # baseline forever and every later sample reads as congestion on a path that simply
    # got slower.
    baseline_decay_ms_per_s: float = 2.0

    target_kbps: int = field(init=False)
    baseline_ms: float = field(init=False, default=float("inf"))
    reason: str = field(init=False, default="start")
    cuts: int = field(init=False, default=0)
    raises: int = field(init=False, default=0)
    reports: int = field(init=False, default=0)

    _last_change: float = field(init=False, default=-1e9)
    _last_sample: float = field(init=False, default=None)

    def __post_init__(self) -> None:
        self.target_kbps = int(min(max(self.start_kbps, self.min_kbps), self.max_kbps))

    @property
    def queue_ms(self) -> float:
        """Delay above the best recently seen, i.e. what is sitting in a queue."""
        return 0.0 if self.baseline_ms == float("inf") else max(0.0, self._latency - self.baseline_ms)

    _latency: float = field(init=False, default=0.0)

    def update(self, now: float, loss: float, latency_ms: float | None = None) -> int:
        """
        Feed one report from the viewer; returns the target bitrate in kbps.

        `loss` is the fraction of fragments lost since the last report (0..1).

        `latency_ms` is (viewer clock now - robot capture stamp). Its absolute value is
        meaningless -- the two clocks have unrelated epochs, so it can be large,
        negative, or both -- and nothing here assumes otherwise. Only its excess over
        the rolling minimum is used, and that is real queueing delay. Pass None when the
        viewer has not decoded a frame yet; loss alone still drives the controller.
        """
        self.reports += 1
        if latency_ms is not None:
            if self._last_sample is not None:
                # Let the floor drift up slowly so a permanently slower path re-baselines.
                self.baseline_ms += self.baseline_decay_ms_per_s * max(0.0, now - self._last_sample)
            self._last_sample = now
            self.baseline_ms = min(self.baseline_ms, latency_ms)
            self._latency = latency_ms

        have_delay = self.baseline_ms != float("inf")
        congested = loss > self.loss_high or (have_delay and self.queue_ms > self.delay_rise_ms)
        # Raising needs a delay measurement, not merely the absence of loss. Delay is the
        # early warning; without it the only signal left is loss, and by then the queue
        # has already overflowed. Climbing blind would mean inferring health from silence.
        clean = (have_delay and loss < self.loss_low
                 and self.queue_ms < self.delay_rise_ms * 0.5)

        if congested and now - self._last_change >= self.backoff_s:
            before = self.target_kbps
            self.target_kbps = int(max(self.min_kbps, self.target_kbps * self.decrease))
            if self.target_kbps != before:
                self.cuts += 1
                self._last_change = now
                self.reason = "loss" if loss > self.loss_high else "delay"
        elif clean and now - self._last_change >= self.interval_s:
            before = self.target_kbps
            self.target_kbps = int(min(self.max_kbps, self.target_kbps * (1.0 + self.increase)))
            if self.target_kbps != before:
                self.raises += 1
                self._last_change = now
                self.reason = "clean"

        return self.target_kbps

    def describe(self) -> str:
        """
        One line for the sender's status output.

        The queue figure reads "--" until a delay sample has arrived, because "0 ms" and
        "no viewer is reporting" are completely different situations and printing the
        same thing for both hides a broken feedback loop behind a healthy-looking number.
        """
        queue = "--" if self.baseline_ms == float("inf") else f"{self.queue_ms:.0f} ms"
        return (f"{self.target_kbps} kbps ({self.reason}, queue {queue}, "
                f"{self.reports} rpt, -{self.cuts}/+{self.raises})")
