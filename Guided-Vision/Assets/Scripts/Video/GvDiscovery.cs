using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

// Beacon payload, mirroring gvlink/beacon.py. Field names are the wire keys --
// JsonUtility maps them literally, which is why the Python side emits camelCase.

[Serializable]
public class GvBeaconPorts
{
    public int control = 15551;
    public int video = 15552;
    public int input = 15553;
}

[Serializable]
public class GvBeaconCamera
{
    public string id = "";
    public int w, h, fps;
    public int canvasW, canvasH;
    public string codec = "h264";
}

[Serializable]
public class GvBeacon
{
    public const int Version = 1;

    public int gv;
    public string name = "";
    public string host = "";
    public GvBeaconPorts ports = new GvBeaconPorts();
    public GvBeaconCamera[] cams = Array.Empty<GvBeaconCamera>();
    public bool fovea;

    [NonSerialized] public string SourceAddress;   // where the datagram actually came from

    /// <summary>
    /// Seconds on GvDiscovery's own clock, NOT Unity's. Timestamps are taken on the
    /// socket thread, and every Unity API -- Time.realtimeSinceStartup included --
    /// throws when touched off the main thread. Doing that here killed the receive
    /// thread on its first datagram, silently, which looks exactly like a network
    /// problem and is not one.
    /// </summary>
    [NonSerialized] public double LastSeen;

    public GvBeaconCamera PrimaryCamera => (cams != null && cams.Length > 0) ? cams[0] : null;

    /// <summary>Fill a profile from what the robot advertised.</summary>
    public void ApplyTo(GvRobotProfile p)
    {
        p.name = name;
        p.host = SourceAddress ?? host;
        if (ports != null)
        {
            p.controlPort = ports.control;
            p.videoPort = ports.video;
            p.inputPort = ports.input;
        }
        p.foveation = fovea;
        var cam = PrimaryCamera;
        if (cam == null)
            return;
        if (cam.w > 0) p.sourceWidth = cam.w;
        if (cam.h > 0) p.sourceHeight = cam.h;
        if (cam.canvasW > 0) p.canvasWidth = cam.canvasW;
        if (cam.canvasH > 0) p.canvasHeight = cam.canvasH;
    }
}

/// <summary>
/// Listens for robot beacons on the LAN.
///
/// The list cannot contain a robot that is not actually reachable, because the listing
/// *is* a packet that arrived. Robots outside the LAN are typed in or picked from the
/// saved list instead -- over Tailscale that is just an address like any other
/// (docs/PLAN.md 3.2).
/// </summary>
public sealed class GvDiscovery : IDisposable
{
    /// <summary>A robot not heard from for this long is dropped from the list.</summary>
    public float StaleAfterSeconds = 5f;

    /// <summary>
    /// Monotonic and thread-safe, unlike anything in UnityEngine.Time. Shared by the
    /// socket thread that stamps beacons and the main thread that ages them out, so it
    /// has to be one clock for both.
    /// </summary>
    private static readonly System.Diagnostics.Stopwatch Clock =
        System.Diagnostics.Stopwatch.StartNew();

    private static double Now => Clock.Elapsed.TotalSeconds;

    private Socket socket;
    private Thread thread;
    private volatile bool running;

    private readonly object gate = new object();
    private readonly Dictionary<string, GvBeacon> seen = new Dictionary<string, GvBeacon>();

    public long DatagramsReceived { get; private set; }
    public long ParseFailures { get; private set; }
    public bool Running => running;

    public bool Start(int port = 15550)
    {
        if (running)
            return true;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.ReceiveTimeout = 250;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (Exception e)
        {
            Debug.LogError($"GvDiscovery: bind :{port} failed: {e.Message}");
            Dispose();
            return false;
        }
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "gv-discovery" };
        thread.Start();
        return true;
    }

    private void Loop()
    {
        try
        {
            LoopInner();
        }
        catch (Exception e)
        {
            // A silent thread death here is indistinguishable from "no robots on this
            // network", which is the single most misleading way this could fail.
            Debug.LogError("GvDiscovery: receive loop threw: " + e);
        }
    }

    private void LoopInner()
    {
        var buf = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            int len;
            try
            {
                len = socket.ReceiveFrom(buf, ref any);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (Exception e)
            {
                if (running)
                    Debug.LogError("GvDiscovery: receive stopped: " + e.Message);
                return;
            }

            GvBeacon b;
            try
            {
                b = JsonUtility.FromJson<GvBeacon>(System.Text.Encoding.UTF8.GetString(buf, 0, len));
            }
            catch (Exception)
            {
                lock (gate) ParseFailures++;
                continue;
            }
            if (b == null || b.gv != GvBeacon.Version)
                continue;

            // The address it arrived from is authoritative: we know packets from there
            // reach us. A robot with several interfaces can advertise one we cannot
            // route to. Same rule as gvlink/beacon.py discover().
            b.SourceAddress = (any as IPEndPoint)?.Address.ToString() ?? b.host;
            b.LastSeen = Now;

            lock (gate)
            {
                DatagramsReceived++;
                // Key on the robot's identity, not the address we happened to hear it
                // on. One machine reachable over both loopback and the LAN is one
                // robot -- which is exactly what happens when the sender runs on the
                // same machine as the Editor. Prefer a routable address over loopback.
                string key = b.name + "@" + b.host;
                if (seen.TryGetValue(key, out var prev)
                    && !prev.SourceAddress.StartsWith("127.")
                    && b.SourceAddress.StartsWith("127."))
                {
                    prev.LastSeen = b.LastSeen;
                }
                else
                {
                    seen[key] = b;
                }
            }
        }
    }

    /// <summary>Robots heard from recently, newest first. Main thread.</summary>
    public List<GvBeacon> Snapshot()
    {
        double now = Now;
        var list = new List<GvBeacon>();
        lock (gate)
        {
            foreach (var kv in seen)
                if (now - kv.Value.LastSeen <= StaleAfterSeconds)
                    list.Add(kv.Value);
        }
        list.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
        return list;
    }

    public void Dispose()
    {
        running = false;
        try { socket?.Close(); } catch { /* already closed */ }
        if (thread != null && thread.IsAlive)
            thread.Join(500);
        thread = null;
        socket = null;
    }
}
