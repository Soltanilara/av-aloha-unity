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
from aiortc.sdp import candidate_from_sdp

ROBOT_ID = 'robot1'

async def run_answer(pc, db, recorder):

    @pc.on("datachannel")
    def on_datachannel(channel):
        print('datachannel open')
        channel.send("started from the bottom now we're here")

    @pc.on("track")
    def on_track(track):
        print("Receiving %s" % track.kind)
        recorder.addTrack(track)

    call_doc = db.collection('calls').document(ROBOT_ID)

    data = call_doc.get().to_dict()

    await pc.setRemoteDescription(RTCSessionDescription(
        sdp=data['sdp'],
        type=data['type']
    ))

    await recorder.start()

    await pc.setLocalDescription(await pc.createAnswer())

    call_doc.set(
        {
            'sdp': pc.localDescription.sdp,
            'type': pc.localDescription.type
        }
    )

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

    recorder = MediaRecorder('output.mp4')

    coro = run_answer(pc, db, recorder)

    # run event loop
    loop = asyncio.get_event_loop()
    try:
        loop.run_until_complete(coro)
        # spin
        print('running')
        loop.run_forever()
    except KeyboardInterrupt:
        pass
    finally:
        loop.run_until_complete(pc.close())



