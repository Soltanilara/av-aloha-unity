from google.cloud import firestore
import json
import asyncio
from aiortc import (
    RTCIceCandidate,
    RTCPeerConnection,
    RTCSessionDescription,
    VideoStreamTrack,
    RTCConfiguration,
    RTCIceServer,
)
from aiortc.contrib.media import MediaBlackhole, MediaPlayer, MediaRecorder
from aiortc.contrib.signaling import BYE, add_signaling_arguments, create_signaling
import threading
from aiortc.sdp import candidate_from_sdp
from aiortc import VideoStreamTrack
import cv2
from av import AudioFrame, VideoFrame

ROBOT_ID = 'robot1'

class OpenCVWebcamVideoStreamTrack(VideoStreamTrack):
    def __init__(self):
        super().__init__()
        self.capture = cv2.VideoCapture(0)  # 0 is the default camera

    async def recv(self):
        pts, time_base = await self.next_timestamp()
        ret, frame = self.capture.read()
        if not ret:
            raise RuntimeError("Failed to read frame from webcam")
        frame = VideoFrame.from_ndarray(frame, format="bgr24")
        frame.pts = pts
        frame.time_base = time_base
        return frame

async def run_offer(pc, db):
    channel = pc.createDataChannel("control")

    @channel.on("open")
    def on_open():
        print("channel open")

    @channel.on("message")
    def on_message(message):
        print("message:", message)

    pc.addTrack(OpenCVWebcamVideoStreamTrack())


    call_doc = db.collection('calls').document(ROBOT_ID)

    # send offer
    await pc.setLocalDescription(await pc.createOffer())
    call_doc.set(
        {
            'sdp': pc.localDescription.sdp,
            'type': pc.localDescription.type
        }
    )

    future = asyncio.Future()
    def answer_callback(doc_snapshot, changes, read_time):
        for doc in doc_snapshot:
            if pc.remoteDescription is None and doc.to_dict()['type'] == 'answer':
                data = doc.to_dict()
                loop.call_soon_threadsafe(future.set_result, data)
    doc_watch = call_doc.on_snapshot(answer_callback)
    print('waiting for answer')
    data = await future
    doc_watch.unsubscribe()

    await pc.setRemoteDescription(RTCSessionDescription(
        sdp=data['sdp'],
        type=data['type']
    ))

    # delete call document
    call_doc = db.collection('calls').document(ROBOT_ID)
    call_doc.delete()

    # add event listener for connection close
    @pc.on("iceconnectionstatechange")
    async def on_iceconnectionstatechange():
        if pc.iceConnectionState == "closed":
            print("Connection closed, restarting...")
            await restart_connection(pc, db)

async def restart_connection(pc, db):
    # close current peer connection
    await pc.close()

    # create new peer connection
    pc = RTCPeerConnection(
        configuration=RTCConfiguration([
            RTCIceServer("stun:stun1.l.google.com:19302"),
            RTCIceServer("stun:stun2.l.google.com:19302"),
            RTCIceServer(turnServerKey['url'], turnServerKey['username'], turnServerKey['password'])
        ])
    )

    # run offer again
    await run_offer(pc, db)

if __name__ == "__main__":
    # read firebase-creds.json
    with open('serviceAccountKey.json') as f:
        serviceAccountKey = json.load(f)

    db = firestore.Client.from_service_account_info(serviceAccountKey)

    with open('turnServerKey.json') as f:
        turnServerKey = json.load(f)

    pc = RTCPeerConnection(
        configuration=RTCConfiguration([
            RTCIceServer("stun:stun1.l.google.com:19302"),
            RTCIceServer("stun:stun2.l.google.com:19302"),
            RTCIceServer(turnServerKey['url'], turnServerKey['username'], turnServerKey['password'])
        ])
    )

    coro = run_offer(pc, db)

    # run event loop
    loop = asyncio.get_event_loop()
    try:
        loop.run_until_complete(coro)
        loop.run_forever()
    except KeyboardInterrupt:
        pass
    finally:
        # delete call document if it exists
        call_doc = db.collection('calls').document(ROBOT_ID)
        call_doc.delete()
        loop.run_until_complete(pc.close())


    



