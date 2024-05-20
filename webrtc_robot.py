from google.cloud import firestore
import json
import asyncio
from aiortc import (
    RTCPeerConnection,
    RTCSessionDescription,
    VideoStreamTrack,
    RTCConfiguration,
    RTCIceServer,
)
from aiortc import VideoStreamTrack
from av import VideoFrame
import cv2
import pyzed.sl as sl
import numpy as np
from aiortc.rtcrtpsender import RTCRtpSender
import socket

class ZedVideoStreamTrack(VideoStreamTrack):
    def __init__(self):
        super().__init__()
        # Create a ZED camera object
        self.zed = sl.Camera()

        # Set configuration parameters
        input_type = sl.InputType()
        init = sl.InitParameters(input_t=input_type)
        init.camera_resolution = sl.RESOLUTION.HD720
        init.depth_mode = sl.DEPTH_MODE.PERFORMANCE
        init.coordinate_units = sl.UNIT.MILLIMETER

        # Open the camera
        err = self.zed.open(init)
        while err != sl.ERROR_CODE.SUCCESS :
            print(repr(err))
            err = self.zed.open(init)

        # Set runtime parameters after opening the camera
        self.runtime = sl.RuntimeParameters()

        # Prepare new image size to retrieve half-resolution images
        self.image_size = self.zed.get_camera_information().camera_configuration.resolution

        self.image_size.width = self.image_size.width // 2
        self.image_size.height = self.image_size.height // 2

        # Declare your sl.Mat matrices
        self.left_image_zed = sl.Mat(self.image_size.width, self.image_size.height, sl.MAT_TYPE.U8_C4)
        self.right_image_zed = sl.Mat(self.image_size.width, self.image_size.height, sl.MAT_TYPE.U8_C4)

    async def recv(self):
        pts, time_base = await self.next_timestamp()

        err = self.zed.grab(self.runtime)

        while err != sl.ERROR_CODE.SUCCESS :
            print(repr(err))
            err = self.zed.grab(self.runtime)

        # Retrieve the left image, depth image in the half-resolution
        self.zed.retrieve_image(self.left_image_zed, sl.VIEW.LEFT, sl.MEM.CPU, self.image_size)
        self.zed.retrieve_image(self.right_image_zed, sl.VIEW.RIGHT, sl.MEM.CPU, self.image_size)

        # To recover data from sl.Mat to use it with opencv, use the get_data() method
        # It returns a numpy array that can be used as a matrix with opencv
        left_image_ocv = self.left_image_zed.get_data()[:,:,:3]
        right_image_ocv = self.right_image_zed.get_data()[:,:,:3]
        frame = np.concatenate((left_image_ocv, right_image_ocv), axis=1)

        frame = VideoFrame.from_ndarray(frame, format="bgr24")
        frame.pts = pts
        frame.time_base = time_base

        return frame
    
def force_codec(pc, sender, forced_codec):
    kind = forced_codec.split("/")[0]
    codecs = RTCRtpSender.getCapabilities(kind).codecs
    transceiver = next(t for t in pc.getTransceivers() if t.sender == sender)
    transceiver.setCodecPreferences(
        [codec for codec in codecs if codec.mimeType == forced_codec]
    )

async def run_offer(pc, db):
    channel = pc.createDataChannel("control")

    @channel.on("open")
    def on_open():
        print("channel open")

    @channel.on("message")
    def on_message(message):
        sock.sendto(message, dest_addr)


    video_sender = pc.addTrack(ZedVideoStreamTrack())
    force_codec(pc, video_sender, 'video/VP8')


    call_doc = db.collection(PASSWORD).document(ROBOT_ID)

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
    call_doc = db.collection(PASSWORD).document(ROBOT_ID)
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
            RTCIceServer(signalingSettings['turn_server_url'], signalingSettings['turn_server_username'], signalingSettings['turn_server_password'])
        ])
    )

    # run offer again
    await run_offer(pc, db)

if __name__ == "__main__":

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    dest_addr = ('172.19.248.37', 5555)

    # read firebase-creds.json
    with open('serviceAccountKey.json') as f:
        serviceAccountKey = json.load(f)

    db = firestore.Client.from_service_account_info(serviceAccountKey)

    with open('signalingSettings.json') as f:
        signalingSettings = json.load(f)
    
    ROBOT_ID = signalingSettings['robotID']
    PASSWORD = signalingSettings['password']

    pc = RTCPeerConnection(
        configuration=RTCConfiguration([
            RTCIceServer("stun:stun1.l.google.com:19302"),
            RTCIceServer("stun:stun2.l.google.com:19302"),
            RTCIceServer(signalingSettings['turn_server_url'], signalingSettings['turn_server_username'], signalingSettings['turn_server_password'])
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
        call_doc = db.collection(PASSWORD).document(ROBOT_ID)
        call_doc.delete()
        loop.run_until_complete(pc.close())


    



