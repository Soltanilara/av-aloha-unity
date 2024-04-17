using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using NetMQ;
using NetMQ.Sockets;

public class HeadsetStreamer : MonoBehaviour
{
    // variables for headset, left controller, and right controller transforms
    public Transform headset;
    public Transform leftController;
    public Transform rightController;
    private HeadsetData headsetData;
    private bool headsetDataReady = false;

    private bool sendingData = false;
    private Thread publisherThread;

    private readonly object dataLock = new object();

    private void PublisherWork()
    {
        AsyncIO.ForceDotNet.Force();
        using (var pubSocket = new PublisherSocket())
        {
            pubSocket.Options.SendHighWatermark = 10;
            pubSocket.Bind("tcp://*:5555");
            while (sendingData)
            {
                if (headsetDataReady)
                {
                    HeadsetData data;
                    lock (dataLock)
                    {
                        data = headsetData;
                        headsetDataReady = false;
                    }
                    string message = JsonUtility.ToJson(data);
                    pubSocket.TrySendFrame(message);
                }

                Debug.Log("Sending data");
            }
            pubSocket.Close();
        }
        NetMQConfig.Cleanup();
    }

    // Start is called before the first frame update
    void Start()
    {
        headsetData = new HeadsetData();

        sendingData = true;
        publisherThread = new Thread(PublisherWork);
        publisherThread.Start();
    }

    // Update is called once per frame
    void Update()
    {
        

        lock (dataLock)
        {
            headsetDataReady = true;
            headsetData.HPosition = headset.position;
            headsetData.HRotation = headset.rotation;
            headsetData.LPosition = leftController.position;
            headsetData.LRotation = leftController.rotation;
            headsetData.LThumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.LIndexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            headsetData.LHandTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            headsetData.LButtonOne = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
            headsetData.LButtonTwo = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
            headsetData.LButtonThumbstick = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.RPosition = rightController.position;
            headsetData.RRotation = rightController.rotation;
            headsetData.RThumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            headsetData.RIndexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
            headsetData.RHandTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch);
            headsetData.RButtonOne = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.RTouch);
            headsetData.RButtonTwo = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.RTouch);
            headsetData.RButtonThumbstick = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch);
        }
    }

    private void OnDestroy()
    {   
        if (publisherThread != null) {     
            sendingData = false;
            publisherThread.Join();
        }
    }
}
