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
using TMPro;

public class CameraStreamer : MonoBehaviour
{
    public RawImage leftImage;
    public RawImage rightImage;

    // public text mesh pro 
    public TextMeshProUGUI debugText1;
    public TextMeshProUGUI debugText2;

    private Texture2D texture;
    private Texture2D leftTexture;
    private Texture2D rightTexture;


    private byte[] imageBytes;
    
    private bool imageReady = false;

    private double receivePeriod = 0.1f;
    private readonly object receiveLock = new object();

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
            TimeSpan timeout = new TimeSpan(0, 0, 0, 0, 100);
            while (receivingImages)
            {
                
                // get csharp equiv of std::chrono::steady_clock::now()
                // do not use Time.realtimeSinceStartup as it is not monotonic
                var time = System.Diagnostics.Stopwatch.StartNew();

                byte[] bytes;
                if (!subSocket.TryReceiveFrameBytes(timeout, out bytes)) continue;
                

                lock (imageLock)
                {
                    if (bytes.Length > imageBytes.Length)
                    {
                        imageBytes = new byte[bytes.Length];
                    }
                    bytes.CopyTo(imageBytes, 0);
                    // imageBytes = bytes;
                    imageReady = true;

                    
                }

                var period = time.Elapsed.TotalSeconds;
                lock (receiveLock)
                {
                    receivePeriod = period;
                }
               

            }
            subSocket.Close();
        }
        NetMQConfig.Cleanup();
    }

    public void StartReceiving(string address)
    {
        robotAddress = address;

        
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

        imageBytes = new byte[40000];

        robotAddress = "tcp://" + PlayerPrefs.GetString("IP") + ":" + PlayerPrefs.GetString("Port");
        // robotAddress = "tcp://localhost:5555";
        Debug.Log("Connecting to " + robotAddress);

        receivingImages = true;
        imageReady = false;
        subscriberThread = new Thread(SubscriberWork);
        subscriberThread.Start();
    }

    private void Update()
    {
        if (imageReady) 
        {
            var time = Time.realtimeSinceStartup;

            lock (imageLock)
            {
                // if (imageBytes.Length > imageBytesCopy.Length)
                // {
                //     imageBytesCopy = new byte[imageBytes.Length];
                // }
                // imageBytes.CopyTo(imageBytesCopy, 0);
                texture.LoadImage(imageBytes);
                imageReady = false;
            }
            
            

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

            var period = Time.realtimeSinceStartup - time;
            debugText2.text = "Image Update Period: " + period.ToString();
        }   

        lock (receiveLock)
        {
            debugText1.text = "Receive Period: " + receivePeriod.ToString();
        }
    }

    private void OnDestroy()
    {
        receivingImages = false;
        subscriberThread.Join();
    }
}
