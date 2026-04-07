# Firestore-based signaling loop for WebRTC: polls Firestore for an SDP offer,
# sets up ICE-configured peer connection, attaches incoming media tracks to a
# recorder, and returns an SDP answer to establish bidirectional streaming.

import asyncio
import json
from google.cloud import firestore
from aiortc import (
    RTCPeerConnection,
    RTCSessionDescription,
    RTCConfiguration,
    RTCIceServer,
)
from aiortc.contrib.media import MediaRecorder
import cv2

ROBOT_ID = "robot1"

async def run_answer(pc, db, signalingSettings, recorder):

    PASSWORD = signalingSettings["password"]

    @pc.on("datachannel")
    def on_datachannel(channel):
        print("✓ Data channel open")
        channel.send("hello from answer")

    @pc.on("track")
    def on_track(track):
        print("Receiving", track.kind)
        recorder.addTrack(track)

    call_doc = db.collection(PASSWORD).document(ROBOT_ID)

    print("Waiting for offer...")

    # Wait until offer appears
    while True:
        data = call_doc.get().to_dict()
        if data and data.get("type") == "offer":
            break
        await asyncio.sleep(1)

    print("Offer received")

    # Set remote description
    await pc.setRemoteDescription(
        RTCSessionDescription(sdp=data["sdp"], type=data["type"])
    )

    
    await recorder.start()

    # Create + send answer
    await pc.setLocalDescription(await pc.createAnswer())

    call_doc.set({
        "sdp": pc.localDescription.sdp,
        "type": pc.localDescription.type,
    })

    print("Answer sent")

    print("Connected — streaming...")

    # Keep alive
    await asyncio.Future()


if __name__ == "__main__":
    # Firebase
    with open("serviceAccountKey.json") as f:
        serviceAccountKey = json.load(f)

    db = firestore.Client.from_service_account_info(serviceAccountKey)

    # signaling settings (same file as offer)
    with open("signalingSettings.json") as f:
        signalingSettings = json.load(f)

    # WebRTC config
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

    recorder = MediaRecorder("output.mp4")

    try:
        asyncio.run(run_answer(pc, db, signalingSettings, recorder)) # add recorder at the end to record video
    except KeyboardInterrupt:
        pass
    finally:
        print("Closing...")
        asyncio.run(pc.close())