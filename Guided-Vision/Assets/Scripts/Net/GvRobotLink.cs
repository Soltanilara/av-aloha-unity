using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/// <summary>
/// The headset side of the robot control channel. Mirror of gvlink/robotlink.py.
///
///     link.Subscribe("arm/state", d => armVisual.Apply(d));
///     link.Publish("gripper/cmd", new Dictionary&lt;string, object&gt; { { "width", 0.04f } });
///     link.Call("arm/home", req, reply =&gt; hud.Show(reply), err =&gt; hud.Error(err));
///
/// Every callback runs on the Unity main thread, from <see cref="Pump"/>. The socket
/// has its own thread, and nothing it touches goes near a Unity API -- a lesson this
/// codebase learned the expensive way in GvDiscovery.
///
/// The connection is also the session: connecting tells the robot where to send video,
/// and dropping it tells the robot to stop. There is no separate keepalive to tune.
/// </summary>
public sealed class GvRobotLink : IDisposable
{
    public const int KindPublish = 0;
    public const int KindCall = 1;
    public const int KindReply = 2;
    public const int KindError = 3;

    public const string TopicSession = "_session";

    private const int MaxMessage = 4 << 20;

    private readonly object sendLock = new object();
    private readonly object queueLock = new object();
    private readonly List<Dictionary<string, object>> inbox = new List<Dictionary<string, object>>();
    private readonly Dictionary<string, List<Action<object>>> subs =
        new Dictionary<string, List<Action<object>>>();
    private readonly Dictionary<long, Action<object>> replies = new Dictionary<long, Action<object>>();
    private readonly Dictionary<long, Action<string>> errors = new Dictionary<long, Action<string>>();
    private readonly GvMsgPack.Writer writer = new GvMsgPack.Writer(1024);

    private Socket socket;
    private Thread thread;
    private volatile bool running;
    private volatile bool connected;
    private long nextCallId = 1;

    private string host;
    private int port;
    private Dictionary<string, object> sessionInfo;

    public bool Connected => connected;
    public long MessagesIn { get; private set; }
    public long MessagesOut { get; private set; }
    public string LastError { get; private set; }

    /// <summary>
    /// The robot handed the operator slot to another headset.
    ///
    /// Set instead of retrying. Being replaced is a decision somebody made, not a
    /// dropped link, and redialling would take the slot straight back from whoever now
    /// has it -- two headsets kicking each other off forever, neither getting a stable
    /// stream. Reconnecting deliberately from the menu clears this.
    /// </summary>
    public bool Displaced { get; private set; }

    /// <summary>Raised on the main thread from Pump().</summary>
    public event Action<bool> ConnectionChanged;

    private bool lastReportedConnected;

    /// <summary>
    /// Starts connecting in the background and keeps retrying. `session` is sent as
    /// soon as each connection comes up, so a robot that restarts is told what it
    /// needs without the operator doing anything.
    /// </summary>
    public void Connect(string host, int port, Dictionary<string, object> session)
    {
        Displaced = false;
        if (running)
            return;
        this.host = host;
        this.port = port;
        sessionInfo = session;
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "gv-link" };
        thread.Start();
    }

    // ------------------------------------------------------------------ public API

    public void Subscribe(string topic, Action<object> fn)
    {
        if (!subs.TryGetValue(topic, out var list))
            subs[topic] = list = new List<Action<object>>();
        list.Add(fn);
    }

    public bool Publish(string topic, object data) =>
        Send(new Dictionary<string, object> { { "t", topic }, { "k", KindPublish }, { "d", data } });

    public bool Call(string topic, object data, Action<object> onReply, Action<string> onError = null)
    {
        long id = Interlocked.Increment(ref nextCallId);
        lock (queueLock)
        {
            if (onReply != null) replies[id] = onReply;
            if (onError != null) errors[id] = onError;
        }
        return Send(new Dictionary<string, object> {
            { "t", topic }, { "k", KindCall }, { "i", id }, { "d", data } });
    }

    /// <summary>Call once per frame on the main thread. Nothing is delivered without it.</summary>
    public void Pump()
    {
        if (connected != lastReportedConnected)
        {
            lastReportedConnected = connected;
            ConnectionChanged?.Invoke(connected);
        }

        List<Dictionary<string, object>> batch = null;
        lock (queueLock)
        {
            if (inbox.Count > 0)
            {
                batch = new List<Dictionary<string, object>>(inbox);
                inbox.Clear();
            }
        }
        if (batch == null)
            return;

        foreach (var msg in batch)
        {
            string topic = GvMsgPack.GetString(msg, "t", "");
            long kind = GvMsgPack.GetLong(msg, "k", KindPublish);
            msg.TryGetValue("d", out object data);

            if (kind == KindReply || kind == KindError)
            {
                long id = GvMsgPack.GetLong(msg, "i", 0);
                Action<object> ok = null;
                Action<string> bad = null;
                lock (queueLock)
                {
                    replies.TryGetValue(id, out ok);
                    errors.TryGetValue(id, out bad);
                    replies.Remove(id);
                    errors.Remove(id);
                }
                try
                {
                    if (kind == KindReply) ok?.Invoke(data);
                    else bad?.Invoke(data as string ?? "error");
                }
                catch (Exception e)
                {
                    Debug.LogError($"GvRobotLink: reply handler for '{topic}' threw: {e}");
                }
                continue;
            }

            if (topic == TopicSession)
            {
                var info = data as Dictionary<string, object>;
                if (info != null && GvMsgPack.GetBool(info, "displaced", false))
                {
                    Displaced = true;
                    Debug.Log("GvRobotLink: another headset took over this robot.");
                    GvToast.Post("Another headset took over this robot", "warn", 6f);
                    Disconnect();
                    return;
                }
            }

            if (!subs.TryGetValue(topic, out var list))
                continue;
            foreach (var fn in list)
            {
                try
                {
                    fn(data);
                }
                catch (Exception e)
                {
                    // One bad subscriber must not stop the rest of the batch.
                    Debug.LogError($"GvRobotLink: subscriber for '{topic}' threw: {e}");
                }
            }
        }
    }

    // -------------------------------------------------------------------- internals

    private bool Send(Dictionary<string, object> msg)
    {
        lock (sendLock)
        {
            var s = socket;
            if (s == null || !connected)
                return false;
            try
            {
                writer.Reset();
                GvMsgPack.Encode(writer, msg);
                int n = writer.Length;
                var frame = new byte[4 + n];
                frame[0] = (byte)(n >> 24); frame[1] = (byte)(n >> 16);
                frame[2] = (byte)(n >> 8); frame[3] = (byte)n;
                Buffer.BlockCopy(writer.Buffer_, 0, frame, 4, n);
                s.Send(frame);
                MessagesOut++;
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                return false;
            }
        }
    }

    private void Loop()
    {
        float backoff = 0.5f;
        while (running)
        {
            try
            {
                Serve();
                backoff = 0.5f;          // a clean session resets the retry delay
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
            if (!running)
                break;
            // Retry, backing off to a few seconds. A robot that is simply not running
            // yet is the normal case, not an error worth spamming the log about.
            Thread.Sleep((int)(backoff * 1000));
            backoff = Mathf.Min(backoff * 2f, 4f);
        }
    }

    private void Serve()
    {
        Socket s = null;
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            IPAddress ip = null;
            foreach (var a in addresses)
                if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            if (ip == null)
                throw new Exception($"could not resolve '{host}'");

            s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // Nagle would hold a small command back for up to 40 ms waiting for company.
            s.NoDelay = true;
            s.ReceiveTimeout = 250;
            s.Connect(new IPEndPoint(ip, port));

            socket = s;
            connected = true;
            LastError = null;
            Debug.Log($"GvRobotLink: connected to {ip}:{port}");
            if (sessionInfo != null)
                Send(new Dictionary<string, object> {
                    { "t", TopicSession }, { "k", KindPublish }, { "d", sessionInfo } });

            ReadLoop(s);
        }
        finally
        {
            connected = false;
            socket = null;
            try { s?.Close(); } catch { /* already gone */ }
        }
    }

    private void ReadLoop(Socket s)
    {
        var buf = new byte[65536];
        var acc = new List<byte>(65536);
        while (running)
        {
            int n;
            try
            {
                n = s.Receive(buf);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            if (n <= 0)
                return;                    // peer closed
            for (int i = 0; i < n; i++)
                acc.Add(buf[i]);

            while (acc.Count >= 4)
            {
                int len = (acc[0] << 24) | (acc[1] << 16) | (acc[2] << 8) | acc[3];
                if (len < 0 || len > MaxMessage)
                    throw new Exception($"implausible message length {len}");
                if (acc.Count < 4 + len)
                    break;
                var payload = new byte[len];
                acc.CopyTo(4, payload, 0, len);
                acc.RemoveRange(0, 4 + len);
                try
                {
                    if (GvMsgPack.Decode(payload) is Dictionary<string, object> msg)
                    {
                        lock (queueLock)
                        {
                            inbox.Add(msg);
                            MessagesIn++;
                        }
                    }
                }
                catch (Exception e)
                {
                    // A malformed message is not a reason to drop the connection --
                    // the framing is still intact, so skip it and keep going.
                    Debug.LogWarning("GvRobotLink: undecodable message: " + e.Message);
                }
            }
        }
    }

    /// <summary>
    /// Drop the connection and stop retrying, leaving the object reusable.
    ///
    /// Distinct from Dispose, which is teardown. Ending a session is a thing the
    /// operator does on purpose and may well undo a second later, so it must not leave
    /// the link in a state that needs a new object to recover from.
    /// </summary>
    public void Disconnect()
    {
        Dispose();
        // Subscriptions deliberately survive. They are registered once, in a consumer's
        // Start, and nothing re-registers them afterwards -- clearing them here meant
        // that after one Reconnect the display never heard camera/params again, silently
        // and for the rest of the session. Pending calls are different: they can never be
        // answered now, so drop them rather than leaving callbacks that fire against a
        // session that no longer exists.
        replies.Clear();
        errors.Clear();
        lock (queueLock) inbox.Clear();
        if (lastReportedConnected)
        {
            lastReportedConnected = false;
            ConnectionChanged?.Invoke(false);
        }
    }

    public void Dispose()
    {
        running = false;
        connected = false;
        try { socket?.Close(); } catch { /* already gone */ }
        socket = null;
        if (thread != null && thread.IsAlive)
            thread.Join(600);
        thread = null;
    }
}
