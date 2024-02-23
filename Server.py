# Run this python script before running the unity application
import socket

# the server IP address and port
SERVER_IP = '127.0.0.1'
SERVER_PORT = 5555

# variables to store previous values
prev_position = None
prev_rotation = None

# create a UDP socket
server_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# binding the socket to the server address and port
server_socket.bind((SERVER_IP, SERVER_PORT))

print("Waiting for data...")

# receiving data from the client
while True:
    data, address = server_socket.recvfrom(1024)  # Buffer size is 1024 bytes
    data_str = data.decode()
    
    # parsing the received data
    parts = data_str.split(';')
    position_str = parts[0].split(':')[1]
    rotation_str = parts[1].split(':')[1]
    
    # converting position and rotation strings to lists of floats
    position = [float(x) for x in position_str.split(',')]
    rotation = [float(x) for x in rotation_str.split(',')]

    # checkng if values have changed
    if position != prev_position or rotation != prev_rotation:
        print("New position:", position)
        print("New rotation:", rotation)
        
        prev_position = position
        prev_rotation = rotation