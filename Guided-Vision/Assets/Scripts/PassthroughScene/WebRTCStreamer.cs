using System.Collections;
using UnityEngine;
using Unity.WebRTC;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
using OVRSimpleJSON;
using System.Linq;

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
    public TextMeshProUGUI debugText;
    public float dataFrequency = 20f;
    public float videoFrequency = 30f;

    private Texture2D receivedTexture = null;
    private Texture2D leftTexture = null;
    private Texture2D rightTexture = null;

    private HeadsetData headsetData;
    private float dataTimer = 0f;

    private RTCPeerConnection pc = null;
    private MediaStream receiveStream = null;
    private RTCDataChannel dataChannel = null;
    private string robotID = null;
    private string projectID = null;
    private string password = null;

    private Quaternion ROTATE_TO_ROS_WORLD = Quaternion.Euler(-90, 0, -90);

    // Start is called before the first frame update
    void Start()
    {
        // get robot ID from the player prefs
        robotID = PlayerPrefs.GetString("RobotID");
        projectID = PlayerPrefs.GetString("ProjectID");
        password = PlayerPrefs.GetString("Password");
        dataFrequency = PlayerPrefs.GetFloat("DataSendFrequency", 20f);
        videoFrequency = PlayerPrefs.GetFloat("VideoRenderFrequency", 30f);

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
            Debug.Log($"pc ICE candidate:\n {candidate.Candidate}"); 
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
        };

        string turnServerURL = PlayerPrefs.GetString("TurnServerURL");
        string turnServerUsername = PlayerPrefs.GetString("TurnServerUsername");
        string turnServerPassword = PlayerPrefs.GetString("TurnServerPassword");

        if (string.IsNullOrEmpty(turnServerURL) || string.IsNullOrEmpty(turnServerUsername) || string.IsNullOrEmpty(turnServerPassword))
        {
            Debug.Log("No turn server found in the player prefs, not using turn server");
        }
        else {
            RTCIceServer turnServer = new RTCIceServer
            {
                urls = new string[] { turnServerURL },
                username = turnServerUsername,
                credential = turnServerPassword
            };
            // append to iceServers
            config.iceServers.Append(turnServer);
        }

        return config;
    }
    

    IEnumerator Answer()
    {   
        // get the offer from the firestore
        string url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents/{password}/{robotID}";
        UnityWebRequest www = UnityWebRequest.Get(url);    
        www.SendWebRequest();
        while (!www.isDone) {}
        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to get the offer from the firestore: " + www.error);
            debugText.text = "Failed to get the offer from the firestore: " + www.error;
            yield break;
        }
        Debug.Log("Offer received from Firestore: " + www.downloadHandler.text);
        // parse the offer
        JSONNode json = JSON.Parse(www.downloadHandler.text);
        string sdp = json["fields"]["sdp"]["stringValue"];
        string type = json["fields"]["type"]["stringValue"];
        if (type != "offer")
        {
            Debug.LogError("When reading the offer, the type is not offer: " + type);
            debugText.text = "When reading the offer, the type is not offer: " + type;
            yield break;
        }
        // set the remote description
        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Offer;
        desc.sdp = sdp;
        var op1 = pc.SetRemoteDescription(ref desc);
        yield return op1;

        // create the answer
        var op2 = pc.CreateAnswer();
        yield return op2;

        // set the local description
        desc = op2.Desc;
        var op = pc.SetLocalDescription(ref desc);
        yield return op;

        // send the answer to the firestore
        // for sdp make sure to escape the new line characters
        string answerSdp = desc.sdp.Replace("\n", "\\n");
        string answerType = "answer";
        url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents:commit";
        string jsonData = @$"
        {{
            ""writes"": [
                {{
                ""update"": {{
                    ""name"": ""projects/{projectID}/databases/(default)/documents/{password}/{robotID}"",
                    ""fields"": {{
                    ""sdp"": {{""stringValue"": ""{answerSdp}""}},
                    ""type"": {{""stringValue"": ""{answerType}""}}
                    }}
                }}
                }}
            ]
        }}
        ";

        www = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        www.uploadHandler = new UploadHandlerRaw(bodyRaw);
        www.downloadHandler = new DownloadHandlerBuffer();
        www.SetRequestHeader("Content-Type", "application/json");
        www.SendWebRequest();
        while (!www.isDone) {}
        if (www.result!= UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to send the answer to the firestore: " + www.error);
            debugText.text = "Failed to send the answer to the firestore: " + www.error;
            yield break;
        }

        Debug.Log("Answer sent to Firestore successfully!");
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
        float time = Time.realtimeSinceStartup;
        int frameCount = 0;
        while (true)
        {
            float startTime = Time.realtimeSinceStartup;
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
            float endTime = Time.realtimeSinceStartup;
            // wait for some time to match the video frequency
            float waitTime = 1f / videoFrequency - (endTime - startTime);
            if (waitTime > 0)
            {
                yield return new WaitForSeconds(waitTime);
            }

            frameCount++;
            if (frameCount >= 100) // calculate FPS every 100 frames
            {
                float totalTime = Time.realtimeSinceStartup - time;
                float fps = frameCount / totalTime;
                debugText.text = "FPS: " + fps.ToString("F2"); // display FPS with 2 decimal points
                time = Time.realtimeSinceStartup;
                frameCount = 0;
            }
        }
    }

    Pose convertLeftToRightCoordinateFrame(Pose leftPose)
    {
        // Flip y axis and rotation from left to right
        float x = leftPose.position.x;
        float y = -leftPose.position.y;
        float z = leftPose.position.z;
        float qx = -leftPose.rotation.x;
        float qy = leftPose.rotation.y;
        float qz = -leftPose.rotation.z;
        float qw = leftPose.rotation.w;

        // Transform to world coordinates
        Vector3 position = new Vector3(x, y, z);
        Quaternion rotation = new Quaternion(qx, qy, qz, qw);
        position = ROTATE_TO_ROS_WORLD * position;
        rotation = ROTATE_TO_ROS_WORLD * rotation;

        return new Pose(position, rotation);
    }

    // Update is called once per frame
    void Update()
    {
        // send data to the robot
        dataTimer += Time.deltaTime;
        if (dataChannel != null && dataTimer >= 1f / dataFrequency)
        {
            dataTimer = 0f;

            // convert the poses to the right coordinate frame
            Pose headPose = convertLeftToRightCoordinateFrame(new Pose(headset.position, headset.rotation));
            Pose leftPose = convertLeftToRightCoordinateFrame(new Pose(leftController.position, leftController.rotation));
            Pose rightPose = convertLeftToRightCoordinateFrame(new Pose(rightController.position, rightController.rotation));

            headsetData.HPosition = headPose.position;
            headsetData.HRotation = headPose.rotation;
            headsetData.LPosition = leftPose.position;
            headsetData.LRotation = leftPose.rotation;
            headsetData.LThumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.LIndexTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            headsetData.LHandTrigger = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch);
            headsetData.LButtonOne = OVRInput.Get(OVRInput.Button.One, OVRInput.Controller.LTouch);
            headsetData.LButtonTwo = OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch);
            headsetData.LButtonThumbstick = OVRInput.Get(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
            headsetData.RPosition = rightPose.position;
            headsetData.RRotation = rightPose.rotation;
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
