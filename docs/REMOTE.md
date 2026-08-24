# Driving the robot from somewhere else

Everything measured so far was on a LAN. This is what changes when the robot is not in
the building, and what the app needs from you to make that work.

The short version: **a remote robot is just an address.** There is no "remote mode" in
the client, no signalling server, and no account. The video path, the control channel and
the input uplink are ordinary UDP and TCP; give them a route and they work. What follows
is about supplying that route.

---

## Why Tailscale and not a port forward

A port forward is simpler and, for a robot you own on a network you control, entirely
legitimate. It stops being reasonable the moment the robot is behind carrier-grade NAT (a
mobile connection, most student housing, a lot of Europe), and it means exposing a UDP
port that accepts video packets to the whole internet.

Tailscale is a WireGuard overlay: both ends join a private network and get a stable
address, whichever physical network they are on. Concretely it buys three things here.

**It punches through NAT.** Two peers exchange endpoints through the coordination server
and then talk directly, peer to peer. No forwarding, no static IP.

**It falls back rather than failing.** Where hole-punching does not work, traffic goes
over a DERP relay. Slower, but the session stays up instead of dying.

**Local stays local.** When the headset and the robot are on the same LAN, Tailscale
routes directly across it -- so this is not a choice between local and remote. The same
build handles both, and the LAN case keeps its LAN latency.

**It is the only encryption in this system.** Worth stating plainly rather than leaving
implied: the video stream is raw UDP and the control channel is raw TCP, both in the
clear, with no authentication beyond "you reached the port". On a LAN that is a
reasonable trade for latency. Over the internet it is not, and a port forward would put
an unauthenticated control channel -- one that drives a physical robot -- on the public
internet. WireGuard underneath is what makes the remote case defensible, which is why
this document recommends an overlay network rather than treating it as one option among
several. **Do not port-forward the control port.** If the overlay is genuinely
unavailable, an SSH tunnel is the fallback; adding a handshake to the sockets themselves
is real work and has not been done.

The free tier covers this comfortably: it is priced per user and per device, with no
bandwidth cap and no metering of how much you use a link. One person with a robot, a
laptop and a headset is three devices against a limit of a hundred.

Encryption is not a bonus feature here. Without it, running a teleoperation link over the
internet means anyone on the path can watch the robot's cameras.

---

## Setting it up

**On the robot**

```bash
curl -fsSL https://tailscale.com/install.sh | sh
sudo tailscale up
tailscale ip -4          # the address to type into the headset
```

Worth also running `tailscale status` once from the headset side later: it tells you
whether you got a direct connection or a relay, which is the single most useful number
for explaining latency.

**On the Quest**

Quest runs Android, and Tailscale ships an Android app, so the mechanism exists. It is
not on the Horizon store, so it is sideloaded:

```bash
adb install tailscale.apk
```

then launch it from the Unknown Sources section of the library and sign in.

> **This is the one step that is not yet verified.** Tailscale needs Android's
> `VpnService`, and whether Meta's runtime permits it on a headset has not been tested
> here. It is a half-hour experiment with a headset in hand and it gates the whole remote
> route, which is why it has sat at the top of the plan's open questions since the start.
> If it fails, see the fallbacks below.

**Connecting**

The LAN beacon does **not** cross a tailnet -- it is a UDP broadcast, and broadcasts are
link-local by design. So a remote robot will not appear by itself in the menu's robot
list. Use **Address** and type the robot's Tailscale IP. It is saved to the profile list
after the first time, so this is once per robot, not once per session.

**Set the MTU**

```bash
uv run mock_robot.py --tunnel        # payload 1180 instead of 1400
```

This matters more than it looks. WireGuard tunnels typically carry a 1280-byte MTU and
spend about 60 bytes of it on their own header. Our 1400-byte payload plus a 40-byte
video header is 1440, so **every datagram** would be IP-fragmented -- and a datagram
whose IP fragment is lost is lost entirely. On a link with 1% packet loss that turns into
far more than 1% of frames broken. `--tunnel` keeps a whole datagram inside the tunnel.

---

## What to expect

| path | added latency | notes |
|---|---|---|
| Same LAN | ~0 | Tailscale routes directly; no relay involved |
| Direct peer-to-peer | one internet RTT/2, typically 10–40 ms | the normal remote case |
| DERP relay | 40–150 ms | via the nearest relay; check with `tailscale status` |

Against the LAN measurement of 3.4 ms capture-to-decode, a direct remote link should land
somewhere around 40–80 ms end to end and remain usable. A relayed link may not be, for
anything requiring fast hands.

Two things already built specifically for this case:

* **Adaptive bitrate.** A home uplink is not the 20 Mbit/s the LAN test assumed. The
  viewer reports loss and queueing delay on the control channel twice a second and the
  sender follows, backing off multiplicatively and recovering gradually. See
  `gvlink/ratecontrol.py`.
* **Foveation.** Cutting bandwidth 3x for a sharper centre is worth much more over a
  6 Mbit/s uplink than it ever was on a LAN.

---

## If Tailscale will not run on the headset

In rough order of preference.

1. **A router-level tailnet.** Run Tailscale on the router or a small always-on box at
   the headset's end and advertise a subnet route. The headset then needs nothing
   installed -- it is talking to an ordinary LAN address, and the tunnel happens
   upstream. This sidesteps `VpnService` entirely and is the fallback to try first.
2. **WireGuard directly.** The Android WireGuard app has the same `VpnService`
   requirement, so this only helps if Tailscale's app specifically is the problem rather
   than the permission.
3. **Port forward plus a firewall rule** restricting the source to the headset's public
   address, where the robot's network allows it. Workable, not private, and defeated by
   carrier-grade NAT.
4. **Roll the hole-punching into the app.** The design allows it -- the control channel
   is already a TCP session that could carry ICE candidates. It is real work and should
   only be considered once options 1–3 are genuinely exhausted.
