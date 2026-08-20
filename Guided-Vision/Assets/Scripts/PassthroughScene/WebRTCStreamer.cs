using System.Collections;
using UnityEngine;
using Unity.WebRTC;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
using Unity.Collections;
using OVRSimpleJSON;
using System;
using System.Linq;
using System.Collections.Generic;

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
    public uint LeftTimestamp;
    public uint RightTimestamp;
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
    public GameObject leftEyeMarker;
    public GameObject rightEyeMarker;
    public GameObject leftArmVisual;
    public GameObject rightArmVisual;
    public TextMeshProUGUI headWarningText;
    public TextMeshProUGUI infoText;
    public TextMeshProUGUI debugText;
    public float dataFrequency = 20f;
    public float videoFrequency = 30f;
    public float videoPlaneDistance = 1.0f;
    public float videoVFOV = 105f;
    public int metadataLength = 4;
    public float offerPollIntervalSeconds = 1f;
    public float offerPollTimeoutSeconds = 20f;
    public float iceGatheringTimeoutSeconds = 5f;
    public float postIceCandidateQuietPeriodSeconds = 0.5f;

    private Texture latestLeftSourceTexture = null;
    private Texture latestRightSourceTexture = null;
    private RenderTexture leftDisplayTexture = null;
    private RenderTexture rightDisplayTexture = null;
    private int videoTrackCount = 0;
    private int receiveStreamCount = 0;
    private int leftReceivedFrameId = 0;
    private int rightReceivedFrameId = 0;
    private int leftRenderedFrameId = 0;
    private int rightRenderedFrameId = 0;
    private float leftLastReceiveRealtime = 0f;
    private float rightLastReceiveRealtime = 0f;
    private float leftLastRenderRealtime = 0f;
    private float rightLastRenderRealtime = 0f;
    private int leftReceivedFramesThisSecond = 0;
    private int rightReceivedFramesThisSecond = 0;
    private int leftRenderedFramesThisSecond = 0;
    private int rightRenderedFramesThisSecond = 0;
    private float videoStatsTimer = 0f;
    private float videoRenderTimer = 0f;
    private string videoStatsText = string.Empty;

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

    private RTCRtpReceiver leftReceiver = null;
    private RTCRtpReceiver rightReceiver = null;
    private readonly object leftMetadataOutputLock = new object();
    private readonly object rightMetadataOutputLock = new object();
    private uint leftTimestamp = 0;
    private uint rightTimestamp = 0;
    private RTCIceGatheringState currentIceGatheringState = RTCIceGatheringState.New;
    private int localIceCandidateCount = 0;
    private float lastLocalIceCandidateRealtime = -1f;

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
        videoVFOV = PlayerPrefs.GetFloat("VideoVFOV", 90f);

        // set canvas local position
        leftCanvas.transform.localPosition = Vector3.forward * videoPlaneDistance;
        rightCanvas.transform.localPosition = Vector3.forward * videoPlaneDistance; 

        // TODO remove this
        // robotID = "robot1";
        // projectID = "webrtc-7cd49";
        // password = "pokemonnaruto";

        // create a new peer connection
        var configuration = GetSelectedSdpSemantics();
        pc = new RTCPeerConnection(ref configuration);
        pc.OnConnectionStateChange = state =>
        {
            Debug.Log("Peer connection state: " + state);
            debugText.text = "Peer connection state: " + state;
        };
        pc.OnIceConnectionChange = state =>
        {
            Debug.Log("ICE connection state: " + state);
            debugText.text = "ICE connection state: " + state;
        };
        pc.OnIceGatheringStateChange = state =>
        {
            currentIceGatheringState = state;
            Debug.Log("ICE gathering state: " + state);
        };

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
                        if (texture == null)
                        {
                            return;
                        }

                        latestLeftSourceTexture = texture;
                        leftReceivedFrameId++;
                        leftReceivedFramesThisSecond++;
                        leftLastReceiveRealtime = Time.realtimeSinceStartup;

                        EnsureDisplayTexture(ref leftDisplayTexture, texture, leftImage, leftCanvas);
                    };
                }
                else
                {
                    // You can access received texture using `track.Texture` property.
                    track.OnVideoReceived += (texture) =>
                    {
                        if (texture == null)
                        {
                            return;
                        }

                        latestRightSourceTexture = texture;
                        rightReceivedFrameId++;
                        rightReceivedFramesThisSecond++;
                        rightLastReceiveRealtime = Time.realtimeSinceStartup;

                        EnsureDisplayTexture(ref rightDisplayTexture, texture, rightImage, rightCanvas);
                    };
                }

                videoTrackCount++;

            }
        };

        pc.OnTrack = (RTCTrackEvent e) =>
        {
            if (e.Track.Kind == TrackKind.Video)
            {
                Debug.Log($"Received video track {receiveStreamCount}");
                // Add track to MediaStream for receiver.
                // This process triggers `OnAddTrack` event of `MediaStream`.
                receiveStream.AddTrack(e.Track);


                if (receiveStreamCount == 0)
                {
                    leftReceiver = e.Receiver;
                    SetUpLeftReceiverTransform(leftReceiver);
                }
                else
                {
                    rightReceiver = e.Receiver;
                    SetUpRightReceiverTransform(rightReceiver);
                }

                receiveStreamCount++;
            }
        };

        pc.OnIceCandidate = candidate =>
        {
            localIceCandidateCount++;
            lastLocalIceCandidateRealtime = Time.realtimeSinceStartup;
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

    private void SetUpLeftReceiverTransform(RTCRtpReceiver receiver)
    {
        receiver.Transform = new RTCRtpScriptTransform(TrackKind.Video, e => OnLeftReceiverTransform(receiver.Transform, e));
    }

    private void SetUpRightReceiverTransform(RTCRtpReceiver receiver)
    {
        receiver.Transform = new RTCRtpScriptTransform(TrackKind.Video, e => OnRightReceiverTransform(receiver.Transform, e));
    }

    void OnLeftReceiverTransform(RTCRtpTransform transform, RTCTransformEvent e)
    {
        var data = e.Frame.GetData();

        var length = data.Length - metadataLength;
        e.Frame.SetData(data, 0, length);
        transform.Write(e.Frame);

        lock (leftMetadataOutputLock)
        {
            leftTimestamp = ReadMetadataTimestamp(data, length);
        }
    }

    void OnRightReceiverTransform(RTCRtpTransform transform, RTCTransformEvent e)
    {
        var data = e.Frame.GetData();

        var length = data.Length - metadataLength;
        e.Frame.SetData(data, 0, length);
        transform.Write(e.Frame);

        lock (rightMetadataOutputLock)
        {
            rightTimestamp = ReadMetadataTimestamp(data, length);
        }
    }

    RTCConfiguration GetSelectedSdpSemantics()
    {
        // open the json file
        RTCConfiguration config = default;
        var iceServers = new List<RTCIceServer>
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
            iceServers.Add(turnServer);
        }

        config.iceServers = iceServers.ToArray();

        return config;
    }


    IEnumerator Answer()
    {
        // get the offer from the firestore
        string url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents/{password}/{robotID}";
        string sdp = null;
        float deadline = Time.realtimeSinceStartup + offerPollTimeoutSeconds;

        while (Time.realtimeSinceStartup < deadline)
        {
            UnityWebRequest www = UnityWebRequest.Get(url);
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Failed to get the offer from the firestore: " + www.error);
                debugText.text = "Failed to get the offer from the firestore: " + www.error;
                yield break;
            }

            Debug.Log("Offer received from Firestore: " + www.downloadHandler.text);
            JSONNode json = JSON.Parse(www.downloadHandler.text);
            string type = json["fields"]["type"]["stringValue"];

            if (type == "offer")
            {
                sdp = json["fields"]["sdp"]["stringValue"];
                break;
            }

            debugText.text = $"Waiting for fresh offer, current type: {type}";
            yield return new WaitForSecondsRealtime(offerPollIntervalSeconds);
        }

        if (string.IsNullOrEmpty(sdp))
        {
            Debug.LogError("Timed out waiting for a fresh offer from Firestore.");
            debugText.text = "Timed out waiting for a fresh offer from Firestore.";
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

        float iceGatheringDeadline = Time.realtimeSinceStartup + iceGatheringTimeoutSeconds;
        int observedCandidateCount = localIceCandidateCount;
        float quietDeadline = -1f;
        while (Time.realtimeSinceStartup < iceGatheringDeadline)
        {
            if (localIceCandidateCount != observedCandidateCount)
            {
                observedCandidateCount = localIceCandidateCount;
                quietDeadline = Time.realtimeSinceStartup + postIceCandidateQuietPeriodSeconds;
            }

            if (observedCandidateCount > 0 && Time.realtimeSinceStartup >= quietDeadline)
            {
                break;
            }

            yield return null;
        }

        if (observedCandidateCount == 0)
        {
            Debug.LogWarning("Sending answer without any local ICE candidates after timeout.");
        }
        else if (Time.realtimeSinceStartup < iceGatheringDeadline)
        {
            Debug.Log($"Sending answer after {observedCandidateCount} local ICE candidates.");
        }
        else
        {
            Debug.LogWarning($"Sending answer after timeout with {observedCandidateCount} local ICE candidates.");
        }

        // send the answer to the firestore
        // for sdp make sure to escape the new line characters
        RTCSessionDescription localDescription = pc.LocalDescription;
        string answerSdp = localDescription.sdp.Replace("\n", "\\n");
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

        UnityWebRequest answerRequest = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
        answerRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
        answerRequest.downloadHandler = new DownloadHandlerBuffer();
        answerRequest.SetRequestHeader("Content-Type", "application/json");
        answerRequest.SendWebRequest();
        while (!answerRequest.isDone) { }
        if (answerRequest.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Failed to send the answer to the firestore: " + answerRequest.error);
            debugText.text = "Failed to send the answer to the firestore: " + answerRequest.error;
            yield break;
        }

        Debug.Log("Answer sent to Firestore successfully!");
    }

    void OnDestroy()
    {
        // close all coroutine
        StopAllCoroutines();

        ReleaseDisplayTexture(ref leftDisplayTexture);
        ReleaseDisplayTexture(ref rightDisplayTexture);

        receiveStream?.Dispose();
        dataChannel?.Dispose();
        pc?.Close();

        receiveStream = null;
        dataChannel = null;
        pc = null;
    }

    private uint ReadMetadataTimestamp(NativeArray<byte>.ReadOnly data, int metadataOffset)
    {
        if (metadataOffset < 0 || metadataOffset + metadataLength > data.Length)
        {
            return 0;
        }

        return ((uint)data[metadataOffset] << 24) |
               ((uint)data[metadataOffset + 1] << 16) |
               ((uint)data[metadataOffset + 2] << 8) |
               data[metadataOffset + 3];
    }

    private void EnsureDisplayTexture(ref RenderTexture displayTexture, Texture sourceTexture, RawImage image, Canvas canvas)
    {
        if (displayTexture != null &&
            displayTexture.width == sourceTexture.width &&
            displayTexture.height == sourceTexture.height)
        {
            return;
        }

        if (displayTexture != null)
        {
            displayTexture.Release();
            Destroy(displayTexture);
        }

        displayTexture = new RenderTexture(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32)
        {
            name = $"{image.name}_Display",
            useMipMap = false,
            autoGenerateMips = false
        };
        displayTexture.Create();
        image.texture = displayTexture;

        Vector2 canvasSize = CalculateCanvasSize(videoVFOV, (float)sourceTexture.width / sourceTexture.height, videoPlaneDistance);
        canvas.GetComponent<RectTransform>().sizeDelta = canvasSize;
    }

    private void RenderLatestFrame(
        Texture sourceTexture,
        RenderTexture displayTexture,
        ref int renderedFrameId,
        int receivedFrameId,
        ref int renderedFramesThisSecond,
        ref float lastRenderRealtime)
    {
        if (sourceTexture == null || displayTexture == null || renderedFrameId == receivedFrameId)
        {
            return;
        }

        Graphics.Blit(sourceTexture, displayTexture);
        renderedFrameId = receivedFrameId;
        renderedFramesThisSecond++;
        lastRenderRealtime = Time.realtimeSinceStartup;
    }

    private void UpdateVideoStats()
    {
        videoStatsTimer += Time.unscaledDeltaTime;
        if (videoStatsTimer < 1f)
        {
            return;
        }

        videoStatsTimer = 0f;

        int leftDroppedFrames = Mathf.Max(0, leftReceivedFramesThisSecond - leftRenderedFramesThisSecond);
        int rightDroppedFrames = Mathf.Max(0, rightReceivedFramesThisSecond - rightRenderedFramesThisSecond);
        float now = Time.realtimeSinceStartup;
        float leftReceiveAgeMs = leftLastReceiveRealtime > 0f ? (now - leftLastReceiveRealtime) * 1000f : -1f;
        float rightReceiveAgeMs = rightLastReceiveRealtime > 0f ? (now - rightLastReceiveRealtime) * 1000f : -1f;
        float leftDisplayAgeMs = leftLastRenderRealtime > 0f ? (now - leftLastRenderRealtime) * 1000f : -1f;
        float rightDisplayAgeMs = rightLastRenderRealtime > 0f ? (now - rightLastRenderRealtime) * 1000f : -1f;

        videoStatsText =
            $"L rx {leftReceivedFramesThisSecond}/s tx {leftRenderedFramesThisSecond}/s drop {leftDroppedFrames} age {leftReceiveAgeMs:F0}/{leftDisplayAgeMs:F0}ms\n" +
            $"R rx {rightReceivedFramesThisSecond}/s tx {rightRenderedFramesThisSecond}/s drop {rightDroppedFrames} age {rightReceiveAgeMs:F0}/{rightDisplayAgeMs:F0}ms";

        leftReceivedFramesThisSecond = 0;
        rightReceivedFramesThisSecond = 0;
        leftRenderedFramesThisSecond = 0;
        rightRenderedFramesThisSecond = 0;
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


        return new Vector2(x + width/2, -y + height/2);
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

        // draw 10x10 square on right image
        // if (rightHitSuccess)
        // {
        //     int x = (int)rightPixel.x;
        //     int y = (int)rightPixel.y;
        //     for (int i = -5; i <= 5; i++)
        //     {
        //         for (int j = -5; j <= 5; j++)
        //         {
        //             rightTexture.SetPixel(x + i, rightImage.texture.height-y + j, Color.red);
        //         }
        //     }
        //     rightTexture.Apply();
        // }

        // // draw 10x10 square on left image Green
        // if (leftHitSuccess)
        // {
        //     int x = (int)leftPixel.x;
        //     int y = (int)leftPixel.y;
        //     for (int i = -5; i <= 5; i++)
        //     {
        //         for (int j = -5; j <= 5; j++)
        //         {
        //             leftTexture.SetPixel(x + i, leftImage.texture.height-y + j, Color.green);
        //         }
        //     }
        //     leftTexture.Apply();
        // }

        if (leftHitSuccess && rightHitSuccess)
        {
            // Set the marker positions based on the calculated 
            leftEyeMarker.transform.localPosition = new Vector3(leftHit.x, leftHit.y, 0);
            rightEyeMarker.transform.localPosition = new Vector3(rightHit.x, rightHit.y, 0);
        }

        
        uint leftTimestampCopy;
        uint rightTimestampCopy;
        lock (leftMetadataOutputLock)
        {
            leftTimestampCopy = leftTimestamp;
        }
        lock (rightMetadataOutputLock)
        {
            rightTimestampCopy = rightTimestamp;
        }        

        // send data to the robot
        dataTimer += Time.deltaTime;
        if (dataChannel != null && dataTimer >= 1f / dataFrequency)
        {
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
            headsetData.LeftTimestamp = leftTimestampCopy;
            headsetData.RightTimestamp = rightTimestampCopy;
            string message = JsonUtility.ToJson(headsetData);
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));
        }    


        lock (dataChannelReceiveLock)
        {
            if (headOutOfSync)
            {
                headWarningText.text = "Head out of sync!";
            }
            else
            {
                headWarningText.text = "";
            }

            if (leftOutOfSync)
            {
                leftArmVisual.SetActive(true);
                leftArmVisual.transform.position = new Vector3(leftArmPosition.x, leftArmPosition.y, leftArmPosition.z);
                leftArmVisual.transform.rotation = new Quaternion(leftArmRotation.x, leftArmRotation.y, leftArmRotation.z, leftArmRotation.w);
            }
            else
            {
                leftArmVisual.SetActive(false);
            }

            if (rightOutOfSync)
            {
                rightArmVisual.SetActive(true);
                rightArmVisual.transform.position = new Vector3(rightArmPosition.x, rightArmPosition.y, rightArmPosition.z);
                rightArmVisual.transform.rotation = new Quaternion(rightArmRotation.x, rightArmRotation.y, rightArmRotation.z, rightArmRotation.w);
            }
            else
            {
                rightArmVisual.SetActive(false);
            }
        }

        if (!string.IsNullOrEmpty(videoStatsText))
        {
            debugText.text = videoStatsText;
        }
    }

    void LateUpdate()
    {
        bool shouldRenderThisFrame = true;
        if (videoFrequency > 0f)
        {
            videoRenderTimer += Time.unscaledDeltaTime;
            shouldRenderThisFrame = videoRenderTimer >= 1f / videoFrequency;
            if (shouldRenderThisFrame)
            {
                videoRenderTimer = 0f;
            }
        }

        if (shouldRenderThisFrame)
        {
            RenderLatestFrame(
                latestLeftSourceTexture,
                leftDisplayTexture,
                ref leftRenderedFrameId,
                leftReceivedFrameId,
                ref leftRenderedFramesThisSecond,
                ref leftLastRenderRealtime);

            RenderLatestFrame(
                latestRightSourceTexture,
                rightDisplayTexture,
                ref rightRenderedFrameId,
                rightReceivedFrameId,
                ref rightRenderedFramesThisSecond,
                ref rightLastRenderRealtime);
        }

        UpdateVideoStats();
    }

    private void ReleaseDisplayTexture(ref RenderTexture displayTexture)
    {
        if (displayTexture == null)
        {
            return;
        }

        displayTexture.Release();
        Destroy(displayTexture);
        displayTexture = null;
    }

    void OnDisable()
    {
        ReleaseDisplayTexture(ref leftDisplayTexture);
        ReleaseDisplayTexture(ref rightDisplayTexture);
    }
}
