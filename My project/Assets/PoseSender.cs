using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class PoseSender : MonoBehaviour
{
    // Define the IP address and port of the server
    public string serverIPAddress = "127.0.0.1";
    public int serverPort = 5555;
    public Transform targetTransform;

    // UDP client socket
    private UdpClient client;

    // Start is called before the first frame update
    void Start()
    {
        // Initialize the UDP client
        client = new UdpClient();

        // Set the frame rate to 30 FPS (optional)
        Application.targetFrameRate = 30;
    }

    // Update is called once per frame
    void Update()
    {
        // Get the position and rotation of this GameObject
        Vector3 position = targetTransform.position;
        Quaternion rotation = targetTransform.rotation;

        // Convert position and rotation to string format
        string data = $"p:{position.x},{position.y},{position.z};o:{rotation.x},{rotation.y},{rotation.z},{rotation.w}";

        // Send the data to the server
        SendData(data);
    }

    // Send data to the server
    void SendData(string data)
    {
        try
        {
            // Convert data to bytes
            byte[] bytes = Encoding.UTF8.GetBytes(data);

            // Send data to the server
            client.Send(bytes, bytes.Length, serverIPAddress, serverPort);
        }
        catch (Exception e)
        {
            Debug.LogError($"Error sending data: {e}");
        }
    }

    // Clean up resources when the script is destroyed
    private void OnDestroy()
    {
        if (client != null)
        {
            client.Close();
        }
    }
}