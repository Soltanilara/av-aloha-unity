using System.Collections.Concurrent;
using System.Threading;
using NetMQ;
using UnityEngine;
using NetMQ.Sockets;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Net.WebSockets;
using System;
using UnityEngine.XR;
using UnityEngine.UI;
using System.Data.SqlTypes;

public class CameraStreamer : MonoBehaviour
{
    public RawImage leftImage;
    public RawImage rightImage;
    private Texture2D texture;
    private Texture2D leftTexture;
    private Texture2D rightTexture;

    private byte[] imageBytes;

    private bool receivingImages = false;
    private int imageWidth;
    private int imageHeight;
    private Thread subscriberThread;

    private string robotAddress;

    // create mutex lock for imageBytes
    private readonly object imageLock = new object();

    private void SubscriberWork()
    {
        AsyncIO.ForceDotNet.Force();
        using (var subSocket = new SubscriberSocket())
        {
            subSocket.Options.ReceiveHighWatermark = 10;
            subSocket.Connect(robotAddress);
            subSocket.Subscribe("");
            while (receivingImages)
            {
                byte[] bytes;
                if (!subSocket.TryReceiveFrameBytes(out bytes)) continue;

                lock (imageLock)
                {
                    imageBytes = bytes;
                }
            }
            subSocket.Close();
        }
        NetMQConfig.Cleanup();
    }

    public void StartReceiving(string address)
    {
        robotAddress = address;

        StopReceiving();

        receivingImages = true;
        subscriberThread = new Thread(SubscriberWork);
        subscriberThread.Start();
    }

    public void StopReceiving()
    {
        // check if the thread is already running
        if (subscriberThread != null)
        {
            receivingImages = false;
            subscriberThread.Join();
        }
    }


    private void Start()
    {
        imageWidth = 4;
        imageHeight = 2;
        texture = new Texture2D(4, 2, TextureFormat.RGB24, false);
        leftTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        rightTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        leftImage.texture = leftTexture;
        rightImage.texture = rightTexture;

        string address = "tcp://" + PlayerPrefs.GetString("IP") + ":" + PlayerPrefs.GetString("Port");

        Debug.Log("Connecting to " + address);

        StartReceiving(address);
    }

    private void Update()
    {
        if (receivingImages && imageBytes != null) 
        {
            byte[] imageBytesCopy;
            lock (imageLock)
            {
                imageBytesCopy = new byte[imageBytes.Length];
                imageBytes.CopyTo(imageBytesCopy, 0);
                imageBytes = null;
            }
            texture.LoadImage(imageBytesCopy);

            if (imageWidth != texture.width || imageHeight != texture.height)
            {
                imageWidth = texture.width;
                imageHeight = texture.height;
                leftTexture = new Texture2D(imageWidth/2, imageHeight, TextureFormat.RGB24, false);
                rightTexture = new Texture2D(imageWidth/2, imageHeight, TextureFormat.RGB24, false);
                leftImage.texture = leftTexture;
                rightImage.texture = rightTexture;
            }

            Color[] pixels = texture.GetPixels(0, 0, imageWidth/2, imageHeight);
            // Texture2D leftTexture = new Texture2D(width/2, height, TextureFormat.RGB24, false);
            // leftImage.texture = leftTexture;
            leftTexture.SetPixels(pixels);
            leftTexture.Apply();
            

            pixels = texture.GetPixels(imageWidth/2, 0, imageWidth/2, imageHeight);
            // Texture2D rightTexture = new Texture2D(width/2, height, TextureFormat.RGB24, false);
            // rightImage.texture = rightTexture;
            rightTexture.SetPixels(pixels);
            rightTexture.Apply();
        }   
    }

    private void OnDestroy()
    {
        StopReceiving();
    }
}
