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

class OpenCVWebcamVideoStreamTrack(VideoStreamTrack):
    def __init__(self):
        super().__init__()
        self.capture = cv2.VideoCapture(0)  # 0 is the default camera

    async def recv(self):
        pts, time_base = await self.next_timestamp()
        ret, frame = self.capture.read()
        print('frame:', frame.shape)
        if not ret:
            raise RuntimeError("Failed to read frame from webcam")
        frame = VideoFrame.from_ndarray(frame, format="bgr24")
        frame.pts = pts
        frame.time_base = time_base
        return frame

ROBOT_ID = 'robot1'

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
    data = await future
    doc_watch.unsubscribe()

    await pc.setRemoteDescription(RTCSessionDescription(
        sdp=data['sdp'],
        type=data['type']
    ))



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
        # spin
        print('spinning')
        loop.run_forever()
    except KeyboardInterrupt:
        pass
    finally:
        loop.run_until_complete(pc.close())

    



