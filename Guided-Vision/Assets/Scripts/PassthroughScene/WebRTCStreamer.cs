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
    public Vector2 LEyePixel;
    public Vector2 REyePixel;
}

public class WebRTCStreamer : MonoBehaviour
{
    public RawImage leftImage;
    public RawImage rightImage;
    public Canvas leftCanvas;
    public Canvas rightCanvas;
    public Transform headset;
    public Transform leftController;
    public Transform rightController;
    public Transform leftEye;
    public Transform rightEye;
    public GameObject leftArmVisual;
    public GameObject rightArmVisual;
    public TextMeshProUGUI headWarningText;
    public TextMeshProUGUI infoText;
    public TextMeshProUGUI debugText;
    public float dataFrequency = 20f;
    public float videoFrequency = 30f;
    public float videoPlaneDistance = 1.0f;
    public float videoVFOV = 105f;
    public GameObject leftEyeMarker;
    public GameObject rightEyeMarker;

    private Texture2D receivedLeftTexture = null;
    private Texture2D receivedRightTexture = null;
    private Texture2D leftTexture = null;
    private Texture2D rightTexture = null;
    private int videoTrackCount = 0;

    private HeadsetData headsetData;
    private float dataTimer = 0f;

    // create mutex lock for data channel receiving
    private object dataChannelReceiveLock = new object();
    private bool headOutOfSync = false;
    private bool leftOutOfSync = false;
    private Vector3 leftArmPosition = Vector3.zero;
    private Quaternion leftArmRotation = Quaternion.identity;
    private bool rightOutOfSync = false;
    private Vector3 rightArmPosition = Vector3.zero;
    private Quaternion rightArmRotation = Quaternion.identity;

    private RTCPeerConnection pc = null;
    private MediaStream receiveStream = null;
    private RTCDataChannel dataChannel = null;
    private string robotID = null;
    private string projectID = null;
    private string password = null;

    // Start is called before the first frame update
    void Start()
    {

        // get robot ID from the player prefs
        robotID = PlayerPrefs.GetString("RobotID");
        projectID = PlayerPrefs.GetString("ProjectID");
        password = PlayerPrefs.GetString("Password");
        dataFrequency = PlayerPrefs.GetFloat("DataSendFrequency", 20f);
        videoFrequency = PlayerPrefs.GetFloat("VideoRenderFrequency", 30f);
        videoPlaneDistance = PlayerPrefs.GetFloat("VideoPlaneDistance", 1.0f);
        videoVFOV = PlayerPrefs.GetFloat("VideoVFOV", 105f);

        // set canvas local position
        leftCanvas.transform.localPosition = Vector3.forward * videoPlaneDistance;
        rightCanvas.transform.localPosition = Vector3.forward * videoPlaneDistance; 

        // TODO remove this
        robotID = "robot1";
        projectID = "webrtc-7cd49";
        password = "pokemonnaruto";


        // create a new peer connection
        var configuration = GetSelectedSdpSemantics();
        pc = new RTCPeerConnection(ref configuration);

        receiveStream = new MediaStream();
        headsetData = new HeadsetData();

        receiveStream.OnAddTrack = e =>
        {
            if (e.Track is VideoStreamTrack track)
            {
                if (videoTrackCount == 0)
                {
                    // You can access received texture using `track.Texture` property.
                    track.OnVideoReceived += (texture) =>
                    {
                        receivedLeftTexture = texture as Texture2D;
                        leftTexture = new Texture2D(receivedLeftTexture.width, receivedLeftTexture.height, receivedLeftTexture.format, false);
                        leftImage.texture = leftTexture;

                        Vector2 canvasSize1 = CalculateCanvasSize(videoVFOV, (float)leftImage.texture.width / leftImage.texture.height, videoPlaneDistance);
                        rightCanvas.GetComponent<RectTransform>().sizeDelta = canvasSize1;

                        StartCoroutine(ConvertLeftFrame());
                    };
                }
                else
                {
                    // You can access received texture using `track.Texture` property.
                    track.OnVideoReceived += (texture) =>
                    {
                        receivedRightTexture = texture as Texture2D;
                        rightTexture = new Texture2D(receivedRightTexture.width, receivedRightTexture.height, receivedRightTexture.format, false);
                        rightImage.texture = rightTexture;

                        Vector2 canvasSize2 = CalculateCanvasSize(videoVFOV, (float)rightImage.texture.width / rightImage.texture.height, videoPlaneDistance);
                        leftCanvas.GetComponent<RectTransform>().sizeDelta = canvasSize2;

                        StartCoroutine(ConvertRightFrame());
                    };
                }

                videoTrackCount++;

            }
        };

        pc.OnTrack = (RTCTrackEvent e) =>
        {
            if (e.Track.Kind == TrackKind.Video)
            {
                // Add track to MediaStream for receiver.
                // This process triggers `OnAddTrack` event of `MediaStream`.
                receiveStream.AddTrack(e.Track);
            }
        };

        pc.OnIceCandidate = candidate =>
        {
            pc.AddIceCandidate(candidate);
            Debug.Log($"pc ICE candidate:\n {candidate.Candidate}");
        };

        pc.OnDataChannel = channel =>
        {
            dataChannel = channel;
            dataChannel.OnMessage = bytes =>
            {
                try
                {
                    string message = System.Text.Encoding.UTF8.GetString(bytes);
                    JSONNode json = JSON.Parse(message);

                    bool headSync = json["headOutOfSync"].AsBool;
                    bool leftSync = json["leftOutOfSync"].AsBool;
                    bool rightSync = json["rightOutOfSync"].AsBool;
                    string info = json["info"];
                    Vector3 rightPosition = new Vector3(json["rightArmPosition"][0].AsFloat, json["rightArmPosition"][1].AsFloat, json["rightArmPosition"][2].AsFloat);
                    Quaternion rightRotation = new Quaternion(json["rightArmRotation"][0].AsFloat, json["rightArmRotation"][1].AsFloat, json["rightArmRotation"][2].AsFloat, json["rightArmRotation"][3].AsFloat);
                    Vector3 leftPosition = new Vector3(json["leftArmPosition"][0].AsFloat, json["leftArmPosition"][1].AsFloat, json["leftArmPosition"][2].AsFloat);
                    Quaternion leftRotation = new Quaternion(json["leftArmRotation"][0].AsFloat, json["leftArmRotation"][1].AsFloat, json["leftArmRotation"][2].AsFloat, json["leftArmRotation"][3].AsFloat);

                    lock (dataChannelReceiveLock)
                    {
                        headOutOfSync = headSync;
                        leftOutOfSync = leftSync;
                        rightOutOfSync = rightSync;
                        infoText.text = info;
                        leftArmPosition = leftPosition;
                        leftArmRotation = leftRotation;
                        rightArmPosition = rightPosition;
                        rightArmRotation = rightRotation;
                    }


                }
                catch (System.Exception e)
                {
                    Debug.LogError("Failed to parse the message: " + e.Message);
                }
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
        else
        {
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
        while (!www.isDone) { }
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
        while (!www.isDone) { }
        if (www.result != UnityWebRequest.Result.Success)
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

    IEnumerator ConvertLeftFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            Graphics.CopyTexture(
                receivedLeftTexture, 0, 0,
                0, 0, receivedLeftTexture.width, receivedLeftTexture.height,
                leftTexture, 0, 0,
                0, 0
            );
        }
    }

    IEnumerator ConvertRightFrame()
    {
        while (true)
        {
            yield return new WaitForEndOfFrame();
            Graphics.CopyTexture(
                receivedRightTexture, 0, 0,
                0, 0, receivedRightTexture.width, receivedRightTexture.height,
                rightTexture, 0, 0,
                0, 0
            );
        }
    }

    // function to calculate canvas width and height from VFOV, distance and aspect ratio
    private Vector2 CalculateCanvasSize(float vfov, float aspectRatio, float distance)
    {
        float halfVFOV = vfov / 2;
        float halfHeight = Mathf.Tan(halfVFOV * Mathf.Deg2Rad) * distance;
        float halfWidth = halfHeight * aspectRatio;
        return new Vector2(halfWidth * 2, halfHeight * 2);
    }
    Vector2 HitPoint2Pixel(Vector3 hitPoint, float height, float width, float distance, float vfov)
    {
        float halfVFOV = vfov / 2;
        float halfHeight = Mathf.Tan(halfVFOV * Mathf.Deg2Rad) * distance;
        float halfWidth = halfHeight * (float)width / height;
        float x = hitPoint.x / halfWidth * width / 2;
        float y = hitPoint.y / halfHeight * height / 2;


        return new Vector2(x, y);
    }

    (Vector2, Vector3, Vector3, bool) GetLeftEyeInfo()
    {
        Vector3 eyeDirection = leftEye.forward;
        (Vector3 hitPoint, bool hit) = CalculateHitPoint(leftEye);

        if (!hit || leftImage.texture == null)
        {
            return (Vector2.zero, Vector3.zero, Vector3.zero, false);
        }

        Vector2 pixel = HitPoint2Pixel(hitPoint, leftImage.texture.height, leftImage.texture.width, videoPlaneDistance, videoVFOV);
        return (pixel, hitPoint, eyeDirection, true);
    }

    (Vector2, Vector3, Vector3, bool) GetRightEyeInfo()
    {
        Vector3 eyeDirection = rightEye.forward;
        (Vector3 hitPoint, bool hit) = CalculateHitPoint(rightEye);

        if (!hit || rightImage.texture == null)
        {
            return (Vector2.zero, Vector3.zero, Vector3.zero, false);
        }

        Vector2 pixel = HitPoint2Pixel(hitPoint, rightImage.texture.height, rightImage.texture.width, videoPlaneDistance, videoVFOV);
        return (pixel, hitPoint, eyeDirection, true);
    }

    (Vector3, bool) CalculateHitPoint(Transform eye)
    {
        // Get the eye's forward direction in world space
        Vector3 eyeDirection = eye.forward;

        Plane screenPlane = new Plane(-eye.parent.forward, eye.position + eye.parent.forward * videoPlaneDistance);

        // Create a ray from the eye position in the eye's forward direction
        Ray eyeRay = new Ray(eye.position, eyeDirection);

        float distanceToPlane;
        bool hitPlane = screenPlane.Raycast(eyeRay, out distanceToPlane);

        if (hitPlane)
        {
            // Calculate the hit point in world space
            Vector3 globalHit = eyeRay.GetPoint(distanceToPlane);

            Vector3 localHit = eye.parent.InverseTransformPoint(globalHit);

            return (localHit, true);
        }

        return (Vector3.zero, false);
    }

    void Update()
    {
        (Vector2 leftPixel, Vector3 leftHit, Vector3 leftDirection, bool leftHitSuccess) = GetLeftEyeInfo();
        (Vector2 rightPixel, Vector3 rightHit, Vector3 rightDirection, bool rightHitSuccess) = GetRightEyeInfo();

        if (leftHitSuccess && rightHitSuccess)
        {
            // Set the marker positions based on the calculated 
            leftEyeMarker.transform.localPosition = leftHit;
            rightEyeMarker.transform.localPosition = rightHit;

            // Update headWarningText with only the local hit points
            headWarningText.text = $"Left eye: {leftPixel}\nRight eye: {rightPixel}";
        }

        




        // send data to the robot
        dataTimer += Time.deltaTime;
        if (dataChannel != null && dataTimer >= 1f / dataFrequency)
        {
            Debug.Log("Sending data to the robot");
            dataTimer = 0f;
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
            headsetData.LEyePixel = leftPixel;
            headsetData.REyePixel = rightPixel;
            string message = JsonUtility.ToJson(headsetData);
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));
        }    

        // lock (dataChannelReceiveLock)
        // {
        //     if (headOutOfSync)
        //     {
        //         headWarningText.text = "Head out of sync!";
        //     }
        //     else
        //     {
        //         headWarningText.text = "";
        //     }

        //     if (leftOutOfSync)
        //     {
        //         leftArmVisual.SetActive(true);
        //         leftArmVisual.transform.position = new Vector3(leftArmPosition.x, leftArmPosition.y, leftArmPosition.z);
        //         leftArmVisual.transform.rotation = new Quaternion(leftArmRotation.x, leftArmRotation.y, leftArmRotation.z, leftArmRotation.w);
        //     }
        //     else
        //     {
        //         leftArmVisual.SetActive(false);
        //     }

        //     if (rightOutOfSync)
        //     {
        //         rightArmVisual.SetActive(true);
        //         rightArmVisual.transform.position = new Vector3(rightArmPosition.x, rightArmPosition.y, rightArmPosition.z);
        //         rightArmVisual.transform.rotation = new Quaternion(rightArmRotation.x, rightArmRotation.y, rightArmRotation.z, rightArmRotation.w);
        //     }
        //     else
        //     {
        //         rightArmVisual.SetActive(false);
        //     }
    }
}

