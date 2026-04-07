# Implements the WebRTC offer role: initializes a DepthAI stereo pipeline (CAM_B/C),
# packages frames into a VideoStreamTrack, streams them over a peer connection,
# and forwards incoming data channel messages to a UDP control interface.

#!/usr/bin/env python3

import asyncio
import json
import socket
import numpy as np
import cv2
import depthai as dai

from google.cloud import firestore
from aiortc import (
    RTCPeerConnection,
    RTCSessionDescription,
    VideoStreamTrack,
    RTCConfiguration,
    RTCIceServer,
)
from aiortc.rtcrtpsender import RTCRtpSender
from av import VideoFrame

class OakVideoStreamTrack(VideoStreamTrack):
    def __init__(self):
        super().__init__()

        self.pipeline = dai.Pipeline()

        # LEFT (CAM_B)
        cam_left = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_B)
        left_out = cam_left.requestOutput(
            size=(640, 400),
            type=dai.ImgFrame.Type.BGR888p,
            fps=20,
        )

        # RIGHT (CAM_C)
        cam_right = self.pipeline.create(dai.node.Camera).build(dai.CameraBoardSocket.CAM_C)
        right_out = cam_right.requestOutput(
            size=(640, 400),
            type=dai.ImgFrame.Type.BGR888p,
            fps=20,
        )

        # Left and right video queues
        self.q_left = left_out.createOutputQueue()
        self.q_right = right_out.createOutputQueue()

        self.pipeline.start()

        self.latest_left = None
        self.latest_right = None

    async def recv(self):
        pts, time_base = await self.next_timestamp()

        # Non-blocking updates
        if self.q_left.has():
            self.latest_left = self.q_left.get().getCvFrame()

        if self.q_right.has():
            self.latest_right = self.q_right.get().getCvFrame()

        if self.latest_left is not None and self.latest_right is not None:
            frame = np.hstack([self.latest_left, self.latest_right])
        else:
            # fallback frame (prevents freeze)
            frame = np.zeros((400, 1280, 3), dtype=np.uint8)

        video_frame = VideoFrame.from_ndarray(frame, format="bgr24")
        video_frame.pts = pts
        video_frame.time_base = time_base

        return video_frame


# Force codec
def force_codec(pc, sender, forced_codec):
    kind = forced_codec.split("/")[0]
    codecs = RTCRtpSender.getCapabilities(kind).codecs
    transceiver = next(t for t in pc.getTransceivers() if t.sender == sender)
    transceiver.setCodecPreferences(
        [codec for codec in codecs if codec.mimeType == forced_codec]
    )


# WebRTC Offer Logic
async def run_offer(pc, db, signalingSettings):
    ROBOT_ID = signalingSettings["robotID"]
    PASSWORD = signalingSettings["password"]

    # Data channel (for controls)
    channel = pc.createDataChannel("control")

    @channel.on("open")
    def on_open():
        print("Data channel open")

    @channel.on("message")
    def on_message(message):
        sock.sendto(message.encode() if isinstance(message, str) else message, dest_addr)

    # Add stereo video
    video_sender = pc.addTrack(OakVideoStreamTrack())
    force_codec(pc, video_sender, "video/VP8")

    call_doc = db.collection(PASSWORD).document(ROBOT_ID)

    # Send offer
    await pc.setLocalDescription(await pc.createOffer())
    call_doc.set({
        "sdp": pc.localDescription.sdp,
        "type": pc.localDescription.type,
    })

    print("Waiting for answer...")

    loop = asyncio.get_running_loop()
    future = loop.create_future()

    def on_snapshot(doc_snapshot, changes, read_time):
        for doc in doc_snapshot:
            data = doc.to_dict()
            if pc.remoteDescription is None and data["type"] == "answer":
                loop.call_soon_threadsafe(future.set_result, data)

    doc_watch = call_doc.on_snapshot(on_snapshot)

    data = await future
    doc_watch.unsubscribe()

    '''print("⏳ Waiting for answer...")

    # Wait for answer
    future = asyncio.Future()

    def on_snapshot(doc_snapshot, changes, read_time):
        for doc in doc_snapshot:
            data = doc.to_dict()
            if pc.remoteDescription is None and data["type"] == "answer":
                asyncio.get_event_loop().call_soon_threadsafe(
                    future.set_result, data
                )

    doc_watch = call_doc.on_snapshot(on_snapshot)
    data = await future
    doc_watch.unsubscribe()'''

    await pc.setRemoteDescription(
        RTCSessionDescription(sdp=data["sdp"], type=data["type"])
    )

    print("Connected!")

    await asyncio.Future()

    # Cleanup
    call_doc.delete()


# MAIN
if __name__ == "__main__":
    # UDP for control messages
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    dest_addr = ("172.19.248.37", 5555)

    # Load Firebase creds
    with open("serviceAccountKey.json") as f:
        serviceAccountKey = json.load(f)

    db = firestore.Client.from_service_account_info(serviceAccountKey)

    # Load signaling config
    with open("signalingSettings.json") as f:
        signalingSettings = json.load(f)

    # Peer connection
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
        asyncio.run(run_offer(pc, db, signalingSettings))
    except KeyboardInterrupt:
        pass
    finally:
        print("Closing connection...")
        asyncio.run(pc.close())