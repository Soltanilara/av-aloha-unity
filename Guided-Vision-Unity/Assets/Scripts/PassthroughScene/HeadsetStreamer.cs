using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using System.Net.Sockets;


[System.Serializable]
public class HeadsetData
{
    public Vector3 HPosition;
    public Quaternion HRotation;
    public Vector3 LPosition;
    public Quaternion LRotation;
    public Vector2 LThumbstick;
    public float LIndexTrigger;
    public float LHandTrigger;
    public bool LButtonOne;
    public bool LButtonTwo;
    public bool LButtonThumbstick;
    public Vector3 RPosition;
    public Quaternion RRotation;
    public Vector2 RThumbstick;
    public float RIndexTrigger;
    public float RHandTrigger;
    public bool RButtonOne;
    public bool RButtonTwo;
    public bool RButtonThumbstick;
}

public class HeadsetStreamer : MonoBehaviour
{
    // variables for headset, left controller, and right controller transforms
    public Transform headset;
    public Transform leftController;
    public Transform rightController;
    private HeadsetData headsetData;
    private bool dataReady = false;
    private bool sendingData = false;
    private Thread publisherThread;
    private readonly object dataLock = new object();
    private UdpClient udpClient;
    private string ip;
    private int port;

    private void PublisherWork()
    {
        while (sendingData)
        {
            if (dataReady)
            {
                HeadsetData data;
                lock (dataLock)
                {
                    data = headsetData;
                    dataReady = false;
                }
                string message = JsonUtility.ToJson(data);
                byte[] dataBytes = System.Text.Encoding.ASCII.GetBytes(message);
                udpClient.SendAsync(dataBytes, dataBytes.Length, ip, port);
            }
        }
    }

    // Start is called before the first frame update
    void Start()
    {
        ip = PlayerPrefs.GetString("IP");
        port = int.Parse(PlayerPrefs.GetString("Port"));

        //ip = "127.0.0.1";
        //port = 5555;

        headsetData = new HeadsetData();
        udpClient = new UdpClient();
        sendingData = true;
        publisherThread = new Thread(PublisherWork);
        publisherThread.Start();
    }

    // Update is called once per frame
    void Update()
    {
        lock (dataLock)
        {
            dataReady = true;
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
        sendingData = false;
        publisherThread.Abort();
        udpClient.Close();
    }
}
