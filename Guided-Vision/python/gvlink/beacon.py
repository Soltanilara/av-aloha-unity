"""
LAN discovery beacon.

This is what replaces Firestore. The robot shouts a small JSON blob on a broadcast
address once a second; the headset listens for a couple of seconds and lists whatever
it heard. No cloud, no account, no signalling document, and strictly better than the
dropdown it replaces because it cannot list a robot that is not actually reachable.

Remote robots are not discovered -- they are typed in or picked from the saved list.
On a Tailscale network that is just an address like any other (docs/PLAN.md 3.2).
"""

from __future__ import annotations

import json
import socket
import threading
import time

from .protocol import DEFAULT_PORTS

BEACON_VERSION = 1
BEACON_INTERVAL_S = 1.0


def _local_ip(probe: str = "8.8.8.8") -> str:
    """
    A hint at our address, for the beacon payload and for logging only.

    Connecting a UDP socket sends nothing; it asks the routing table which interface
    would be used. That answers "how do I reach the internet", which is NOT the same
    as "how does the headset reach me" -- with a VPN up, the default route is the
    tunnel and this returns a tunnel address no one on the LAN can use.

    Receivers therefore treat the address a beacon actually arrived from as
    authoritative and ignore this. It stays in the payload because it is useful when
    reading logs.
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect((probe, 53))
        return s.getsockname()[0]
    except OSError:
        return "127.0.0.1"
    finally:
        s.close()


def broadcast_targets() -> list[str]:
    """
    Every address worth shouting at.

    Two lessons are baked in here. First, 255.255.255.255 is not enough: it follows the
    default route, so with a VPN up it goes down the tunnel and never reaches the LAN.
    Per-interface subnet broadcasts (192.168.71.255 and friends) reach each network
    directly. Second, macOS does not loop 255.255.255.255 back to local sockets, so
    without 127.0.0.1 in the list you cannot test sender and receiver on one machine --
    which is most of development.
    """
    targets = ["127.0.0.1"]
    try:
        import psutil
        for name, addrs in psutil.net_if_addrs().items():
            for a in addrs:
                if a.family != socket.AF_INET or not a.broadcast:
                    continue
                if a.broadcast not in targets:
                    targets.append(a.broadcast)
    except Exception:
        # No psutil, or an OS that will not enumerate: fall back to the blunt one.
        targets.append("255.255.255.255")
    if len(targets) == 1:
        targets.append("255.255.255.255")
    return targets


def build_payload(name: str, cameras: list[dict], *, host: str | None = None,
                  ports: dict | None = None, foveation: bool = True,
                  extra: dict | None = None) -> dict:
    payload = {
        "gv": BEACON_VERSION,
        "name": name,
        "host": host or _local_ip(),
        "ports": ports or {k: DEFAULT_PORTS[k] for k in ("control", "video", "input")},
        "cams": cameras,
        "fovea": bool(foveation),
    }
    if extra:
        payload.update(extra)
    return payload


class Beacon:
    """Broadcasts `payload` until stopped. Cheap enough to just leave running."""

    def __init__(self, payload: dict, port: int = DEFAULT_PORTS["beacon"],
                 address: str | list[str] | None = None,
                 interval_s: float = BEACON_INTERVAL_S) -> None:
        self.payload = payload
        self.port = port
        if address is None:
            self.addresses = broadcast_targets()
        elif isinstance(address, str):
            self.addresses = [address]
        else:
            self.addresses = list(address)
        self.interval_s = interval_s
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self.sent = 0

    def start(self) -> "Beacon":
        if self._thread is not None:
            return self
        self._thread = threading.Thread(target=self._run, daemon=True, name="gv-beacon")
        self._thread.start()
        return self

    def _run(self) -> None:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        except OSError:
            pass
        blob = json.dumps(self.payload, separators=(",", ":")).encode()
        while not self._stop.is_set():
            for addr in self.addresses:
                try:
                    sock.sendto(blob, (addr, self.port))
                    self.sent += 1
                except OSError:
                    pass      # that interface is down right now; the others still go
            self._stop.wait(self.interval_s)
        sock.close()

    def stop(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
            self._thread = None


def discover(timeout_s: float = 2.5, port: int = DEFAULT_PORTS["beacon"]) -> list[dict]:
    """Listen for beacons and return one entry per distinct robot. For tooling/tests."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind(("", port))
    except OSError:
        sock.close()
        return []
    sock.settimeout(0.25)

    found: dict[str, dict] = {}
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            data, addr = sock.recvfrom(4096)
        except socket.timeout:
            continue
        except OSError:
            break
        try:
            msg = json.loads(data.decode())
        except (ValueError, UnicodeDecodeError):
            continue
        if msg.get("gv") != BEACON_VERSION:
            continue
        # The address the datagram actually arrived from is authoritative: we know
        # packets from it reach us. A robot with several interfaces can easily
        # advertise one that we cannot route to, so keep its claim only for reference.
        msg["advertised_host"] = msg.get("host")
        msg["host"] = addr[0]
        # Key on the robot's identity, not on the address we happened to hear. One
        # machine reachable over both loopback and the LAN is one robot, not two --
        # and it shows up that way whenever sender and headset share a host.
        key = f"{msg.get('name', '?')}@{msg.get('advertised_host', '?')}"
        prev = found.get(key)
        if prev is None or (prev["host"].startswith("127.") and not msg["host"].startswith("127.")):
            found[key] = msg
    sock.close()
    return list(found.values())
