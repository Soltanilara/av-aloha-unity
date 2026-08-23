"""
The robot-side control channel: publish/subscribe and request/response over TCP.

This is the interface you actually program against. Adding a new input or output is one
line on each side -- a topic name and a dict -- rather than a struct declared twice and
kept in step by hand.

Design notes worth not re-litigating:

* **TCP, not UDP.** Commands must arrive, and arrive in order; a dropped gripper-close
  is not something to paper over with a retry policy invented per call site. High-rate
  telemetry that would rather be late than queued (poses, gaze) goes on the separate
  UDP uplink instead -- see gvlink/protocol.py InputPacket.
* **The connection IS the session.** The robot learns where to send video from the peer
  address of this socket, and stops when it closes. No hello, no keepalive, no
  timeout to tune.
* **msgpack with short keys.** The envelope is four one-character keys. The previous
  generation of this project sent JSON with names like ``rightArmRotation`` at 20 Hz.
* **One viewer at a time.** A second connection displaces the first. Multi-viewer is a
  different feature with its own bandwidth questions.
"""

from __future__ import annotations

import socket
import struct
import threading
import traceback
from typing import Any, Callable

import msgpack

from .protocol import CODEC_H264, CODEC_NAMES, DEFAULT_PORTS

# Message kinds.
PUB = 0
CALL = 1
REPLY = 2
ERR = 3

# The headset announces itself on this topic as soon as it connects.
TOPIC_SESSION = "_session"

_LEN = struct.Struct("!I")
MAX_MESSAGE = 4 << 20


class ControlClient:
    """
    The viewer end of the control channel -- what `GvRobotLink.cs` does, in Python.

    It exists because the bench receiver needs it. When the hello datagram was replaced
    by this TCP channel the headset gained a client and the bench did not, so
    `mock_robot.py` sat waiting for a viewer that had no way to announce itself and
    `bench_receiver.py` silently received nothing. Requiring `--host` on the sender
    papered over it by making the robot stream blind.

    It also closes the adaptive-bitrate loop on a laptop: the bench can report loss and
    delay exactly as the headset does, so the controller can be watched working without
    a headset in the room.

    Same wire format as the server, deliberately sharing the framing constants above --
    two implementations of one protocol in one file is already one too many.
    """

    def __init__(self, host: str, port: int | None = None, *,
                 video_port: int | None = None, codec: int = CODEC_H264,
                 foveation: bool = True, name: str = "bench") -> None:
        self.host = host
        self.port = port or DEFAULT_PORTS["control"]
        self.session = {
            "video": video_port or DEFAULT_PORTS["video"],
            "codec": codec,
            "fovea": foveation,
            "name": name,
        }
        self.connected = False
        self.messages_in = 0
        self.messages_out = 0
        self._sock: socket.socket | None = None
        self._subs: dict[str, list[Callable[[Any], None]]] = {}
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None

    def subscribe(self, topic: str, fn: Callable[[Any], None]) -> None:
        self._subs.setdefault(topic, []).append(fn)

    def start(self) -> "ControlClient":
        self._thread = threading.Thread(target=self._run, daemon=True, name="gv-control")
        self._thread.start()
        return self

    def publish(self, topic: str, data: Any = None) -> bool:
        return self._send({"t": topic, "k": PUB, "d": data})

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            sock, self._sock = self._sock, None
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass

    # ------------------------------------------------------------------ internals

    def _run(self) -> None:
        backoff = 0.5
        while not self._stop.is_set():
            try:
                sock = socket.create_connection((self.host, self.port), timeout=3.0)
            except OSError:
                if self._stop.wait(backoff):
                    return
                backoff = min(5.0, backoff * 1.6)
                continue
            backoff = 0.5
            sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            sock.settimeout(0.25)
            with self._lock:
                self._sock = sock
            self.connected = True
            # The session announcement is what makes the robot start streaming, so it
            # goes out before anything else and again after every reconnect.
            self._send({"t": TOPIC_SESSION, "k": PUB, "d": self.session})
            try:
                self._read(sock)
            finally:
                self.connected = False
                with self._lock:
                    if self._sock is sock:
                        self._sock = None
                try:
                    sock.close()
                except OSError:
                    pass

    def _read(self, sock: socket.socket) -> None:
        buf = bytearray()
        while not self._stop.is_set():
            try:
                chunk = sock.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                return
            if not chunk:
                return
            buf += chunk
            while True:
                if len(buf) < 4:
                    break
                (n,) = _LEN.unpack_from(buf)
                if n > MAX_MESSAGE:
                    return
                if len(buf) < 4 + n:
                    break
                payload = bytes(buf[4:4 + n])
                del buf[:4 + n]
                self.messages_in += 1
                try:
                    msg = msgpack.unpackb(payload, raw=False)
                except Exception:
                    continue
                for fn in self._subs.get(msg.get("t"), ()):
                    try:
                        fn(msg.get("d"))
                    except Exception:
                        traceback.print_exc()

    def _send(self, msg: dict) -> bool:
        blob = msgpack.packb(msg, use_bin_type=True)
        frame = _LEN.pack(len(blob)) + blob
        with self._lock:
            sock = self._sock
            if sock is None:
                return False
            try:
                sock.sendall(frame)
                self.messages_out += 1
                return True
            except OSError:
                return False


class Session:
    """What the connected viewer asked for."""

    def __init__(self, addr: str, data: dict) -> None:
        self.addr = addr
        self.video_port = int(data.get("video", DEFAULT_PORTS["video"]))
        self.codec = int(data.get("codec", CODEC_H264))
        self.foveation = bool(data.get("fovea", True))
        self.name = str(data.get("name", "headset"))

    def __str__(self) -> str:
        return (f"{self.name} {self.addr}:{self.video_port} "
                f"{CODEC_NAMES.get(self.codec, '?')}"
                f"{' fovea' if self.foveation else ' plain'}")


class RobotLink:
    """
    Serves one headset at a time.

        link = RobotLink()

        @link.handler("arm/home")
        def home(req):
            return {"ok": True}

        link.subscribe("gripper/cmd", lambda m: gripper.set(m["width"]))
        link.publish("arm/state", {"q": q, "t": t})
        link.start()
    """

    def __init__(self, port: int = DEFAULT_PORTS["control"], name: str = "robot") -> None:
        self.port = port
        self.name = name

        self._handlers: dict[str, Callable[[Any], Any]] = {}
        self._subs: dict[str, list[Callable[[Any], None]]] = {}
        self._on_session: list[Callable[[Session | None], None]] = []

        self._lock = threading.Lock()          # guards the write side of the socket
        self._conn: socket.socket | None = None
        self._session: Session | None = None

        self._stop = threading.Event()
        self._server: socket.socket | None = None
        self._thread: threading.Thread | None = None

        self.messages_in = 0
        self.messages_out = 0

    # ------------------------------------------------------------------ registration

    def handler(self, topic: str):
        """Decorator. The return value becomes the reply."""
        def wrap(fn):
            self._handlers[topic] = fn
            return fn
        return wrap

    def subscribe(self, topic: str, fn: Callable[[Any], None]) -> None:
        self._subs.setdefault(topic, []).append(fn)

    def on_session(self, fn: Callable[["Session | None"], None]) -> None:
        """Called with a Session when a headset connects, and None when it leaves."""
        self._on_session.append(fn)

    # ---------------------------------------------------------------------- lifecycle

    def start(self) -> "RobotLink":
        if self._thread is not None:
            return self
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind(("0.0.0.0", self.port))
        self._server.listen(1)
        self._server.settimeout(0.25)
        self._thread = threading.Thread(target=self._accept_loop, daemon=True,
                                        name="gv-link")
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            conn, self._conn = self._conn, None
        if conn is not None:
            try:
                conn.close()
            except OSError:
                pass
        if self._server is not None:
            self._server.close()
            self._server = None
        if self._thread is not None:
            self._thread.join(timeout=1.0)
            self._thread = None

    @property
    def session(self) -> Session | None:
        return self._session

    @property
    def connected(self) -> bool:
        return self._conn is not None

    def _accept_loop(self) -> None:
        while not self._stop.is_set():
            try:
                conn, addr = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            # Nagle would coalesce small control messages into a 40 ms wait, which is
            # the whole latency budget for a button press.
            conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            conn.settimeout(0.25)
            with self._lock:
                old, self._conn = self._conn, conn
            if old is not None:
                try:
                    old.close()      # newest viewer wins
                except OSError:
                    pass
            self._serve(conn, addr[0])

    def _serve(self, conn: socket.socket, peer: str) -> None:
        buf = bytearray()
        try:
            while not self._stop.is_set():
                try:
                    chunk = conn.recv(65536)
                except socket.timeout:
                    continue
                except OSError:
                    break
                if not chunk:
                    break
                buf += chunk
                while True:
                    if len(buf) < 4:
                        break
                    (n,) = _LEN.unpack_from(buf)
                    if n > MAX_MESSAGE:
                        raise ValueError(f"message of {n} bytes is implausible")
                    if len(buf) < 4 + n:
                        break
                    payload = bytes(buf[4:4 + n])
                    del buf[:4 + n]
                    self.messages_in += 1
                    self._dispatch(msgpack.unpackb(payload, raw=False), peer)
        except Exception:
            traceback.print_exc()
        finally:
            try:
                conn.close()
            except OSError:
                pass
            with self._lock:
                if self._conn is conn:
                    self._conn = None
            if self._session is not None:
                self._session = None
                for fn in self._on_session:
                    fn(None)

    def _dispatch(self, msg: dict, peer: str) -> None:
        topic = msg.get("t")
        kind = msg.get("k", PUB)
        data = msg.get("d")

        if topic == TOPIC_SESSION:
            self._session = Session(peer, data or {})
            print(f"link: {self._session}")
            for fn in self._on_session:
                fn(self._session)
            return

        if kind == CALL:
            fn = self._handlers.get(topic)
            if fn is None:
                self._send({"t": topic, "k": ERR, "i": msg.get("i"),
                            "d": f"no handler for '{topic}'"})
                return
            try:
                self._send({"t": topic, "k": REPLY, "i": msg.get("i"), "d": fn(data)})
            except Exception as e:
                self._send({"t": topic, "k": ERR, "i": msg.get("i"), "d": repr(e)})
            return

        for fn in self._subs.get(topic, ()):
            try:
                fn(data)
            except Exception:
                # One bad subscriber must not take the channel down with it.
                traceback.print_exc()

    # ------------------------------------------------------------------- outbound

    def publish(self, topic: str, data: Any = None) -> bool:
        return self._send({"t": topic, "k": PUB, "d": data})

    def _send(self, msg: dict) -> bool:
        blob = msgpack.packb(msg, use_bin_type=True)
        frame = _LEN.pack(len(blob)) + blob
        with self._lock:
            conn = self._conn
            if conn is None:
                return False
            try:
                conn.sendall(frame)
                self.messages_out += 1
                return True
            except OSError:
                return False
