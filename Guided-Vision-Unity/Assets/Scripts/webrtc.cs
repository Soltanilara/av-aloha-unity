using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Firebase.Firestore;
using Firebase.Extensions;
using System;
using UnityEngine.Assertions;
using Unity.WebRTC;
using System.Threading.Tasks;
using UnityEngine.UI;


public class webrtc : MonoBehaviour
{

    [System.Serializable]
    public class TurnServerKey
    {
        public string url;
        public string username;
        public string password;
    }

    public RawImage image;

    private RTCPeerConnection pc;

    private MediaStream receiveStream = null;

    private RTCDataChannel dataChannel = null;

    private FirebaseFirestore firestore;
    // json file with turn server credentials
    public TextAsset turnServerKeyFile;

    private string ROBOT_ID = "robot1";


    // Start is called before the first frame update
    void Start()
    {
        // create firestore instance
        Debug.Log("Create Firestore instance");
        firestore = FirebaseFirestore.DefaultInstance;

        Debug.Log("GetSelectedSdpSemantics");
        var configuration = GetSelectedSdpSemantics();
        pc = new RTCPeerConnection(ref configuration);

        receiveStream = new MediaStream();

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
                    "stun:stun2.l.google.com:19302",
                    turnServerKey.url
                },
                username = turnServerKey.username,
                credential = turnServerKey.password
            },
        };

        return config;
    }

    IEnumerator Answer()
    {
        receiveStream.OnAddTrack = e => {
            if (e.Track is VideoStreamTrack track)
            {
                // You can access received texture using `track.Texture` property.
                track.OnVideoReceived += (texture) => {
                    image.texture = texture;
                    Debug.Log("received texture of size " + texture.width + "x" + texture.height);
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
                Debug.Log(System.Text.Encoding.UTF8.GetString(bytes)); 
            };

            // send a message "hello world" to the robot
            string message = "hello world";
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));

        };   

        Debug.Log("going to read data from firestore");  

        Dictionary<string, object> data = null;
        Task<DocumentSnapshot> task1 = firestore.Collection("calls").Document(ROBOT_ID).GetSnapshotAsync();
        yield return new WaitUntil(() => task1.IsCompleted);

        if (task1.IsFaulted)
        {
            Debug.LogError("Error getting snapshot: " + task1.Exception);
            yield break;
        }

        Debug.Log("got snapshot");

        DocumentSnapshot snapshot = task1.Result;
        if (snapshot.Exists)
        {
            Debug.Log(String.Format("Document data for {0} document:", snapshot.Id));
            data = snapshot.ToDictionary();
        }
        else
        {
            Debug.Log(String.Format("Document {0} does not exist!", snapshot.Id));
        }

        Debug.Log("pc setRemoteDescription start");
        RTCSessionDescription desc = new RTCSessionDescription();
        desc.type = RTCSdpType.Offer;
        desc.sdp = data["sdp"].ToString();
        var op1 = pc.SetRemoteDescription(ref desc);
        yield return op1;

        Debug.Log("pc createAnswer start");
        var op2 = pc.CreateAnswer();
        yield return op2;

        desc = op2.Desc;
        var op = pc.SetLocalDescription(ref desc);
        yield return op;

        Task task2 = firestore.Collection("calls").Document(ROBOT_ID).SetAsync(new Dictionary<string, object>
        {
            { "sdp", desc.sdp },
            { "type", "answer" }
        });
        yield return new WaitUntil(() => task2.IsCompleted);
    }

    void Hangup()
    {
        pc.Close();
        pc = null;
    }

    // Update is called once per frame
    void Update()
    {
        if (dataChannel != null)
        {
            // send a message "hello world" to the robot
            string message = "hello world";
            dataChannel.Send(System.Text.Encoding.UTF8.GetBytes(message));
        }
        
    }
}
