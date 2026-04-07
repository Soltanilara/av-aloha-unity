# Receives a WebRTC video stream (answer role) and exposes it as an MJPEG
# HTTP stream for real-time viewing in a browser.

import asyncio
import json
import cv2
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

from google.cloud import firestore
from aiortc import (
    RTCPeerConnection,
    RTCSessionDescription,
    RTCConfiguration,
    RTCIceServer,
)

ROBOT_ID = "robot1"

latest_frame = None  # shared frame buffer

# ---------------------------
# MJPEG HTTP STREAM SERVER
# ---------------------------
class StreamHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/':
            self.send_response(200)
            self.send_header(
                'Content-type',
                'multipart/x-mixed-replace; boundary=frame'
            )
            self.end_headers()

            while True:
                global latest_frame

                if latest_frame is None:
                    continue

                _, jpeg = cv2.imencode('.jpg', latest_frame)

                self.wfile.write(b'--frame\r\n')
                self.send_header('Content-Type', 'image/jpeg')
                self.end_headers()
                self.wfile.write(jpeg.tobytes())
                self.wfile.write(b'\r\n')


def start_server():
    server = HTTPServer(('0.0.0.0', 8000), StreamHandler)
    print("HTTP stream running on port 8000")
    server.serve_forever()


# ---------------------------
# WEBRTC ANSWER
# ---------------------------
async def run_answer(pc, db, signalingSettings):

    PASSWORD = signalingSettings["password"]

    @pc.on("track")
    def on_track(track):
        print("Receiving video")

        async def receive_frames():
            global latest_frame

            while True:
                frame = await track.recv()
                img = frame.to_ndarray(format="bgr24")

                latest_frame = img  # send to HTTP stream

                await asyncio.sleep(0)

        asyncio.create_task(receive_frames())

    call_doc = db.collection(PASSWORD).document(ROBOT_ID)

    print("Waiting for offer...")

    while True:
        data = call_doc.get().to_dict()
        if data and data.get("type") == "offer":
            break
        await asyncio.sleep(1)

    print("✓ Offer received")

    await pc.setRemoteDescription(
        RTCSessionDescription(sdp=data["sdp"], type=data["type"])
    )

    await pc.setLocalDescription(await pc.createAnswer())

    call_doc.set({
        "sdp": pc.localDescription.sdp,
        "type": pc.localDescription.type,
    })

    print("✓ Answer sent")
    print("🟢 Streaming to browser...")

    await asyncio.Future()  # keep alive


# ---------------------------
# MAIN
# ---------------------------
if __name__ == "__main__":
    # Start HTTP stream server
    threading.Thread(target=start_server, daemon=True).start()

    # Firebase
    with open("serviceAccountKey.json") as f:
        serviceAccountKey = json.load(f)

    db = firestore.Client.from_service_account_info(serviceAccountKey)

    # TURN / signaling config
    with open("signalingSettings.json") as f:
        signalingSettings = json.load(f)

    pc = RTCPeerConnection(
        configuration=RTCConfiguration([
            RTCIceServer("stun:stun1.l.google.com:19302"),
            RTCIceServer("stun:stun2.l.google.com:19302"),
            RTCIceServer(
                signalingSettings["turn_server_url"],
                signalingSettings["turn_server_username"],
                signalingSettings["turn_server_password"],
            ),
        ])
    )

    try:
        asyncio.run(run_answer(pc, db, signalingSettings))
    except KeyboardInterrupt:
        pass
    finally:
        print("Closing...")