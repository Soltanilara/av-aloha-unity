package com.guidedvision.gv;

import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.SocketTimeoutException;

/**
 * Owns the one video socket and hands each datagram to the right eye.
 *
 * Both eyes share a port and are told apart by the header's eye field -- which is what
 * that field is for, and what gvlink/protocol.py already does on the sending side.
 * One socket also means one place where the receive buffer is sized, and no chance of
 * the two eyes' sockets being serviced at different rates.
 *
 * Only reassembly happens on this thread; decoding drains on each stream's own thread,
 * so a slow decoder cannot stall the socket and turn backpressure into UDP loss.
 */
public final class GvVideoReceiver {

    private static final String TAG = "GvVideo";

    private final int port;
    private final GvVideoStream[] streams;

    private DatagramSocket socket;
    private Thread thread;
    private volatile boolean running;

    private long datagramsDropped;   // eye field outside the streams we were given

    public GvVideoReceiver(int port, int width, int height) {
        this.port = port;
        this.streams = new GvVideoStream[] {
            new GvVideoStream(0, width, height),
            new GvVideoStream(1, width, height),
        };
    }

    public GvVideoStream getStream(int eye) {
        return (eye >= 0 && eye < streams.length) ? streams[eye] : null;
    }

    public boolean start() {
        if (running) return true;
        for (GvVideoStream s : streams) {
            if (!s.start()) {
                stop();
                return false;
            }
        }
        try {
            socket = new DatagramSocket(null);
            socket.setReuseAddress(true);
            // Big enough that a burst of one frame's fragments cannot overflow the
            // kernel queue and look like network loss that never touched the network.
            socket.setReceiveBufferSize(8 << 20);
            socket.bind(new InetSocketAddress(port));
            socket.setSoTimeout(200);
        } catch (Exception e) {
            Log.e(TAG, "bind " + port + " failed", e);
            stop();
            return false;
        }
        running = true;
        thread = new Thread(this::loop, "gv-rx");
        thread.setPriority(Thread.MAX_PRIORITY);
        thread.start();
        Log.i(TAG, "listening on " + port);
        return true;
    }

    private void loop() {
        byte[] buf = new byte[2048];
        DatagramPacket pkt = new DatagramPacket(buf, buf.length);
        while (running) {
            try {
                pkt.setLength(buf.length);
                socket.receive(pkt);
            } catch (SocketTimeoutException e) {
                continue;
            } catch (Exception e) {
                if (running) Log.w(TAG, "receive", e);
                return;
            }
            int len = pkt.getLength();
            if (len < GvReassembler.HEADER_SIZE) continue;
            int eye = buf[8] & 0xFF;           // header layout: magic, frameId, eye
            if (eye >= streams.length) {
                datagramsDropped++;
                continue;
            }
            streams[eye].onDatagram(buf, len);
        }
    }

    public void stop() {
        running = false;
        try { if (socket != null) socket.close(); } catch (Exception ignored) { }
        if (thread != null) {
            try { thread.join(500); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            thread = null;
        }
        socket = null;
        for (GvVideoStream s : streams) s.stop();
    }

    public boolean isRunning() { return running; }
    public long getDatagramsDropped() { return datagramsDropped; }
}
