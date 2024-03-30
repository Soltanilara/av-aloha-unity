using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI; // Import the Unity UI namespace

public class TextIPDisplayer : MonoBehaviour
{
    public TMP_Text ipAddressText; // Reference to the Text component

    // Start is called before the first frame update
    void Start()
    {
        // Check if the Text component reference is not null
        if (ipAddressText != null)
        {
            // Get the device's local IP address
            string ipAddress = GetLocalIPAddress();

            // Update the Text component with the IP address
            ipAddressText.text = "Local IP Address: " + ipAddress;
        }
        else
        {
            Debug.LogError("Text component reference is null. Assign the Text component in the Inspector.");
        }
    }

    // Function to get the local IP address of the device
    private string GetLocalIPAddress()
    {
        // Get the device's network interfaces
        System.Net.NetworkInformation.NetworkInterface[] interfaces =
            System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();

        // Iterate through the interfaces to find the local IP address
        foreach (var netInterface in interfaces)
        {
            // Check if the interface is active and not a loopback or tunnel interface
            if (netInterface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                netInterface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                netInterface.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
            {
                // Get the IP properties of the interface
                System.Net.NetworkInformation.IPInterfaceProperties ipProps = netInterface.GetIPProperties();

                // Iterate through the unicast addresses of the interface to find the IPv4 address
                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        // Return the IPv4 address as a string
                        return ip.Address.ToString();
                    }
                }
            }
        }

        // Return a default message if the local IP address is not found
        return "IP address not found";
    }
}
