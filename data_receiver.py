import socket
import json

# Create a UDP socket
udp_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# Bind the socket to a specific IP address and port
udp_socket.bind(('10.0.0.101', 5555))  # Use the same port as in the Unity code

# Set a timeout for the socket (in seconds)
udp_socket.settimeout(0.2)  # Adjust the timeout value as needed

while True:
    try:
        # Receive data from the socket with timeout
        data, addr = udp_socket.recvfrom(1024)

        # Decode the received bytes into a string
        received_json = data.decode('utf-8')

        # Parse the JSON string into a Python dictionary
        received_data = json.loads(received_json)

        # Print the received data
        print("Received JSON data:")
        print(received_data)
    except socket.timeout:
        # Handle timeout by continuing the loop
        print("Socket timeout. Retrying...")
        continue

# Close the socket (this part won't be reached in this example because of the infinite loop)
udp_socket.close()
