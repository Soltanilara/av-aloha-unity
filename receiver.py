import sys
import numpy as np
import cv2
import zmq
import pickle
import base64

# Initialize ZeroMQ context and socket for receiving
context = zmq.Context()

socket = context.socket(zmq.SUB)
socket.setsockopt(zmq.RCVHWM, 10)
socket.setsockopt(zmq.CONFLATE, 1)  # last msg only.
socket.connect("tcp://localhost:5555")  # Replace with the correct address/port
socket.setsockopt_string(zmq.SUBSCRIBE, "")

while True:
    # Receive compressed image data
    compressed_image = socket.recv()
    
    encoded_data = np.fromstring(base64.b64decode(compressed_image), np.uint8)
    image = cv2.imdecode(encoded_data, 1)

    print(image)

    # Display the image in a window
    cv2.imshow("Received Image", image)

    # Exit loop if 'q' key is pressed
    if cv2.waitKey(1) & 0xFF == ord('q'):
        break

# Close the window and socket
cv2.destroyAllWindows()
socket.close()
context.term()
