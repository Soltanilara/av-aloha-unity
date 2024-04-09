using UnityEngine;
using System;
using NetMQ;
using NetMQ.Sockets;

public class PositionAndRotationSender : MonoBehaviour
{
    // Define the IP address and port of the server
    public string serverIPAddress = "127.0.0.1";
    public int serverPort = 5555;

    // Hand controllers
    public Transform leftHandController;
    public Transform rightHandController;

    // Start is called before the first frame update
    void Start()
    {
        // Set the frame rate to 30 FPS (optional)
        Application.targetFrameRate = 30;
    }

    // Update is called once per frame
    void Update()
    {
        // Get the position and rotation of hand controllers
        Vector3 leftHandPosition = leftHandController.position;
        Quaternion leftHandRotation = leftHandController.rotation;

        Vector3 rightHandPosition = rightHandController.position;
        Quaternion rightHandRotation = rightHandController.rotation;

        // Convert position and rotation to string format
        string data = $"LeftHandPosition:{leftHandPosition.x},{leftHandPosition.y},{leftHandPosition.z};" +
                      $"LeftHandRotation:{leftHandRotation.eulerAngles.x},{leftHandRotation.eulerAngles.y},{leftHandRotation.eulerAngles.z};" +
                      $"RightHandPosition:{rightHandPosition.x},{rightHandPosition.y},{rightHandPosition.z};" +
                      $"RightHandRotation:{rightHandRotation.eulerAngles.x},{rightHandRotation.eulerAngles.y},{rightHandRotation.eulerAngles.z}";

        // Send the data to the server
        SendData(data);
    }

    // Send data to the server
    void SendData(string data)
    {
        try
        {
            using (var client = new RequestSocket())
            {
                client.Connect($"tcp://{serverIPAddress}:{serverPort}");
                client.SendFrame(data);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending data: {e}");
        }
    }
}
