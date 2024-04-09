import socket

# the server IP address and port
SERVER_IP = '127.0.0.1'
SERVER_PORT = 5555

# variables to store previous values
prev_left_position = None
prev_left_rotation = None
prev_right_position = None
prev_right_rotation = None

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
    
    left_position_str = parts[0].split(':')[1]
    left_rotation_str = parts[1].split(':')[1]
    right_position_str = parts[2].split(':')[1]
    right_rotation_str = parts[3].split(':')[1]
    
    # converting position and rotation strings to lists of floats for left hand
    left_position = [float(x) for x in left_position_str.split(',')]
    left_rotation = [float(x) for x in left_rotation_str.split(',')]
    
    # converting position and rotation strings to lists of floats for right hand
    right_position = [float(x) for x in right_position_str.split(',')]
    right_rotation = [float(x) for x in right_rotation_str.split(',')]

    # check if left hand values have changed
    if left_position != prev_left_position or left_rotation != prev_left_rotation:
        print("Left Hand New Position:", left_position)
        print("Left Hand New Rotation:", left_rotation)
        
        prev_left_position = left_position
        prev_left_rotation = left_rotation

    # check if right hand values have changed
    if right_position != prev_right_position or right_rotation != prev_right_rotation:
        print("Right Hand New Position:", right_position)
        print("Right Hand New Rotation:", right_rotation)
        
        prev_right_position = right_position
        prev_right_rotation = right_rotation
