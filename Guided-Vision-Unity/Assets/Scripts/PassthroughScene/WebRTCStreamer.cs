using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Firebase.Firestore;
using System;
using Unity.WebRTC;
using System.Threading.Tasks;
using UnityEngine.UI;
using TMPro;

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

[System.Serializable]
public class TurnServerKey
{
    public string url;
    public string username;
    public string password;
}

public class WebRTCStreamer : MonoBehaviour
{
    public RawImage leftImage;
    public RawImage rightImage;
    public Transform headset;
    public Transform leftController;
    public Transform rightController;
    public TextAsset turnServerKeyFile;
    public TextMeshProUGUI debugText;
    public float data_frequency = 20f;

    private Texture2D receivedTexture = null;
    private Texture2D leftTexture = null;
    private Texture2D rightTexture = null;

    private HeadsetData headsetData;
    private float data_timer = 0f;

    private RTCPeerConnection pc = null;
    private MediaStream receiveStream = null;
    private RTCDataChannel dataChannel = null;
    private FirebaseFirestore firestore = null;
    private string robotID = null;

    // Start is called before the first frame update
    void Start()
    {
        // get robot ID from the player prefs
        robotID = PlayerPrefs.GetString("RobotID");
        if (robotID == "")
        {
            debugText.text = "Invalid robot ID: " + robotID;
            return;
        }

        // create firestore instance
        firestore = FirebaseFirestore.DefaultInstance;

        // create a new peer connection
        var configuration = GetSelectedSdpSemantics();
        pc = new RTCPeerConnection(ref configuration);

        receiveStream = new MediaStream();
        headsetData = new HeadsetData();

        receiveStream.OnAddTrack = e => {
            if (e.Track is VideoStreamTrack track)
            {
                // You can access received texture using `track.Texture` property.
                track.OnVideoReceived += (texture) => {
                    receivedTexture = texture as Texture2D;
                    leftTexture = new Texture2D(receivedTexture.width / 2, receivedTexture.height, receivedTexture.format, false);
                    rightTexture = new Texture2D(receivedTexture.width / 2, receivedTexture.height, receivedTexture.format, false);
                    leftImage.texture = leftTexture;
                    rightImage.texture = rightTexture;
                    StartCoroutine(ConvertFrame());
                };
            }
        };

        pc.OnTrack = (RTCTrackEvent e) => {
            if (e.Track.Kind == TrackKind.Video)
            {
                // Add track to MediaStream for receiver.
                // This process triggers `OnAddTrack` event of `MediaStream`.
                receiveStream.AddTrack(e.Track);
            }
        };

        pc.OnIceCandidate = candidate => { 
            pc.AddIceCandidate(candidate);
            Debug.Log($"pc1 ICE candidate:\n {candidate.Candidate}"); 
        };
        
        pc.OnDataChannel = channel =>
        {
            dataChannel = channel;
            dataChannel.OnMessage = bytes => { 
                Debug.Log("Data channel received: " + System.Text.Encoding.UTF8.GetString(bytes));
            };
        };   

        StartCoroutine(Answer());
        StartCoroutine(WebRTC.Update());
    }

    RTCConfiguration GetSelectedSdpSemantics()
    {
        // open the json file
        var turnServerKey = JsonUtility.FromJson<TurnServerKey>(turnServerKeyFile.text);

        RTCConfiguration config = default;
        config.iceServers = new RTCIceServer[]
        {
            new RTCIceServer { 
                urls = new string[] { 
                    "stun:stun1.l.google.com:19302", 
                },
                
            },
            new RTCIceServer { 
                urls = new string[] { 
                    "stun:stun2.l.google.com:19302",
                }
            },
            new RTCIceServer { 
                urls = new string[] { 
                    turnServerKey.url
                },
                username = turnServerKey.username,
                credential = turnServerKey.password,
            },
        };

        return config;
    }

    IEnumerator Answer()
    {
        Dictionary<string, object> data = null;
        Task<DocumentSnapshot> task1 = firestore.Collection("calls").Document(robotID).GetSnapshotAsync();
        yield return new WaitUntil(() => task1.IsCompleted);

        if (task1.IsFaulted)
        {
            Debug.LogError("Error getting snapshot: " + task1.Exception);
            debugText.text = "Error getting snapshot of ID: " + robotID;
            yield break;
        }

        DocumentSnapshot snapshot = task1.Result;
        if (snapshot.Exists)
        {
            data = snapshot.ToDictionary();
        }
        else
        {
            Debug.Log(String.Format("Document {0} does not exist!", snapshot.Id));
        }

        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Offer;
        desc.sdp = data["sdp"].ToString();
        var op1 = pc.SetRemoteDescription(ref desc);
        yield return op1;

        var op2 = pc.CreateAnswer();
        yield return op2;

        desc = op2.Desc;
        var op = pc.SetLocalDescription(ref desc);
        yield return op;

        Task task2 = firestore.Collection("calls").Document(robotID).SetAsync(new Dictionary<string, object>
        {
            { "sdp", desc.sdp },
            { "type", "answer" }
        });
        yield return new WaitUntil(() => task2.IsCompleted);
    }

    void OnDestroy()
    {
        // close all coroutine
        StopAllCoroutines();

        receiveStream?.Dispose();
        dataChannel?.Dispose();
        pc?.Close();

        receiveStream = null;
        dataChannel = null;
        pc = null;
    }

    IEnumerator ConvertFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            Graphics.CopyTexture(
                receivedTexture, 0, 0, 
                0, 0, receivedTexture.width / 2, receivedTexture.height,
                leftTexture, 0, 0, 
                0, 0
            );
            Graphics.CopyTexture(
                receivedTexture, 0, 0, 
                receivedTexture.width / 2, 0, receivedTexture.width / 2, receivedTexture.height,
                rightTexture, 0, 0, 
                0, 0
            );
        }
    }

    // Update is called once per frame
    void Update()
    {
        // send data to the robot
        data_timer += Time.deltaTime;
        if (dataChannel != null && data_timer >= 1f / data_frequency)
        {
            data_timer = 0f;
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
            string message = JsonUtility.ToJson(headsetData);
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));
        }      
    }
}
