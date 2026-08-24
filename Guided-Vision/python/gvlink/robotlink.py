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
* **One operator, any number of observers.** Video is unicast UDP to one address, so
  there is exactly one *operator* session and a second operator displaces the first.
  Everything else -- a bench receiver, a visualiser, a logger, a second headset watching
  -- connects as an ``observer``, sees every published topic, and never touches the video
  destination. Connections are served on their own threads, so a client that is merely
  connected can no longer stop another from being accepted at all. That used to happen
  silently: the accept loop served each connection inline, so a headset left connected
  meant the next one sat in the backlog looking like a robot that was not listening.
"""

from __future__ import annotations

import itertools
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

# What a session is for. Video goes to exactly one operator; observers are read-only as
# far as the video path is concerned, though they can publish on the control channel.
ROLE_OPERATOR = "operator"
ROLE_OBSERVER = "observer"

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
                 foveation: bool = True, name: str = "bench",
                 role: str = ROLE_OPERATOR) -> None:
        self.host = host
        self.port = port or DEFAULT_PORTS["control"]
        # role=ROLE_OBSERVER attaches without claiming the video stream: every topic
        # arrives, nothing is retargeted, and whoever is actually flying keeps flying.
        # That is what a logger, a visualiser or a second pair of eyes wants.
        self.session = {
            "video": video_port or DEFAULT_PORTS["video"],
            "codec": codec,
            "fovea": foveation,
            "name": name,
            "role": role,
        }
        self.connected = False
        # Set when the robot hands the operator slot to somebody else. The client then
        # stops instead of redialling -- see _read.
        self.displaced = False
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
                # The robot closes a displaced operator's socket. Without this the
                # client treats that as a dropped link, reconnects, displaces whoever
                # took over, and the two clients kick each other off forever -- neither
                # ever gets a stable stream. Being replaced is a decision, not a fault.
                if msg.get("t") == TOPIC_SESSION:
                    d = msg.get("d") or {}
                    if isinstance(d, dict) and d.get("displaced"):
                        self.displaced = True
                        self._stop.set()
                        return

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

    # Every session gets an identity, so "is this the same viewer as last frame" is a
    # fact rather than an inference from the parameters. Two sessions with identical
    # settings are still two sessions: the second one has a brand-new decoder that has
    # never seen a keyframe, and the encoder has to be told.
    _next_id = itertools.count(1)

    def __init__(self, addr: str, data: dict) -> None:
        self.id = next(Session._next_id)
        self.addr = addr
        self.video_port = int(data.get("video", DEFAULT_PORTS["video"]))
        self.codec = int(data.get("codec", CODEC_H264))
        self.foveation = bool(data.get("fovea", True))
        self.name = str(data.get("name", "headset"))

        # Absent means operator: every client written before roles existed is one, and
        # defaulting the other way would silently stop sending them video.
        self.role = str(data.get("role", ROLE_OPERATOR))
        if self.role not in (ROLE_OPERATOR, ROLE_OBSERVER):
            self.role = ROLE_OPERATOR

        # Stream shape, chosen by the viewer because it is the end that knows its own
        # decoder and its own link. None means "whatever the robot was started with".
        self.canvas = None
        if data.get("cw") and data.get("ch"):
            self.canvas = (int(data["cw"]), int(data["ch"]))
        self.coarse_scale = float(data["cs"]) if data.get("cs") else None
        self.fovea_scale = float(data["fs"]) if data.get("fs") else None

    def layout_key(self):
        """What a change here means a new encoder. Compared, not applied blindly."""
        return (self.canvas, self.coarse_scale, self.fovea_scale)

    @property
    def is_operator(self) -> bool:
        return self.role == ROLE_OPERATOR

    def __str__(self) -> str:
        role = "" if self.is_operator else " [observer]"
        return (f"{self.name} {self.addr}:{self.video_port} "
                f"{CODEC_NAMES.get(self.codec, '?')}"
                f"{' fovea' if self.foveation else ' plain'}{role}")


class RobotLink:
    """
    Serves one operator and any number of observers.

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

        # Guards _conns and _operator. Held only around bookkeeping and individual
        # sendall calls, never around a recv.
        self._lock = threading.Lock()
        self._conns: dict[socket.socket, Session | None] = {}
        self._operator: socket.socket | None = None
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
        self._server.listen(8)
        self._server.settimeout(0.25)
        self._thread = threading.Thread(target=self._accept_loop, daemon=True,
                                        name="gv-link")
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        with self._lock:
            conns = list(self._conns)
            self._conns.clear()
            self._operator = None
        for conn in conns:
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
        """The operator session -- where video goes. None when nobody is flying."""
        return self._session

    @property
    def sessions(self) -> list[Session]:
        """Every connected session that has announced itself, operator and observers."""
        with self._lock:
            return [s for s in self._conns.values() if s is not None]

    @property
    def connected(self) -> bool:
        """Whether an operator is connected. Observers do not count: this gates video."""
        return self._operator is not None

    @property
    def viewers(self) -> int:
        """How many clients are attached, of any role."""
        with self._lock:
            return len(self._conns)

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
                self._conns[conn] = None
            # One thread per connection. Serving inline meant a single attached client
            # blocked accept() entirely, so the second one waited in the backlog and
            # looked exactly like a robot that was not running.
            threading.Thread(target=self._serve, args=(conn, addr[0]), daemon=True,
                             name=f"gv-link-{addr[0]}").start()

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
                    self._dispatch(msgpack.unpackb(payload, raw=False), peer, conn)
        except Exception:
            traceback.print_exc()
        finally:
            try:
                conn.close()
            except OSError:
                pass
            with self._lock:
                gone = self._conns.pop(conn, None)
                was_operator = self._operator is conn
                if was_operator:
                    self._operator = None
                    self._session = None
            # Only the operator leaving is a session ending. An observer disconnecting
            # must not tell the robot to stop streaming.
            if was_operator:
                if gone is not None:
                    print(f"link: {gone.name} disconnected")
                for fn in self._on_session:
                    fn(None)

    def _dispatch(self, msg: dict, peer: str, conn: socket.socket) -> None:
        topic = msg.get("t")
        kind = msg.get("k", PUB)
        data = msg.get("d")

        if topic == TOPIC_SESSION:
            session = Session(peer, data or {})
            displaced = None
            with self._lock:
                self._conns[conn] = session
                if session.is_operator:
                    displaced = self._operator if self._operator is not conn else None
                    self._operator = conn
                    self._session = session
            if displaced is not None:
                # Newest operator wins, because a headset reconnecting must be able to
                # replace its own stale session. Say so before closing: a client that is
                # merely dropped will redial and take the slot straight back.
                self._send_to(displaced, {"t": TOPIC_SESSION, "k": PUB,
                                          "d": {"displaced": True}})
                try:
                    displaced.close()
                except OSError:
                    pass
            print(f"link: {session}")
            # Observers are not sessions as far as the video path is concerned, so they
            # must not trigger a retarget of the stream.
            if session.is_operator:
                for fn in self._on_session:
                    fn(session)
            return

        if kind == CALL:
            # A reply goes back to the caller, not to everyone. Broadcasting it would
            # hand other clients a reply to a request they never made.
            fn = self._handlers.get(topic)
            if fn is None:
                self._send_to(conn, {"t": topic, "k": ERR, "i": msg.get("i"),
                                     "d": f"no handler for '{topic}'"})
                return
            try:
                self._send_to(conn, {"t": topic, "k": REPLY, "i": msg.get("i"), "d": fn(data)})
            except Exception as e:
                self._send_to(conn, {"t": topic, "k": ERR, "i": msg.get("i"), "d": repr(e)})
            return

        for fn in self._subs.get(topic, ()):
            try:
                fn(data)
            except Exception:
                # One bad subscriber must not take the channel down with it.
                traceback.print_exc()

    # ------------------------------------------------------------------- outbound

    def publish(self, topic: str, data: Any = None) -> bool:
        """
        Send to every attached client. True if at least one received it.

        Observers get the same stream of topics the operator does, which is the point:
        a visualiser or a logger should not need its own protocol.
        """
        return self._send({"t": topic, "k": PUB, "d": data})

    def _send(self, msg: dict) -> bool:
        frame = self._frame(msg)
        with self._lock:
            conns = list(self._conns)
        ok = False
        dead = []
        for conn in conns:
            if self._write(conn, frame):
                ok = True
            else:
                dead.append(conn)
        # A broken pipe here is a client that has gone; its own thread will notice and
        # clean up. Dropping it now just stops us writing to it again in the meantime.
        if dead:
            with self._lock:
                for conn in dead:
                    self._conns.pop(conn, None)
                    if self._operator is conn:
                        self._operator = None
                        self._session = None
        return ok

    def _send_to(self, conn: socket.socket, msg: dict) -> bool:
        return self._write(conn, self._frame(msg))

    @staticmethod
    def _frame(msg: dict) -> bytes:
        blob = msgpack.packb(msg, use_bin_type=True)
        return _LEN.pack(len(blob)) + blob

    def _write(self, conn: socket.socket, frame: bytes) -> bool:
        with self._lock:
            try:
                conn.sendall(frame)
                self.messages_out += 1
                return True
            except OSError:
                return False
