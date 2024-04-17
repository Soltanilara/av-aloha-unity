import cv2
import zmq
import time
import numpy as np
import base64
import pickle

def main():
    # Initialize ZeroMQ context and socket for publishing
    context = zmq.Context()
    socket = context.socket(zmq.PUB)
    socket.setsockopt(zmq.SNDHWM, 1)
    socket.bind("tcp://*:5555")  # Change the address/port as needed

    # Open the webcam
    cap = cv2.VideoCapture(0)
    if not cap.isOpened():
        print("Error: Failed to open webcam.")
        return

    key = ' '
    while key != 113:  # 'q' key to quit
        start_time = time.time()

        ret, frame = cap.read()
        if ret:

            # concat 2 frames into 1
            combined_frame = np.concatenate((frame, frame), axis=1)

            _, compressed_image = cv2.imencode('.jpg', combined_frame, [int(cv2.IMWRITE_WEBP_QUALITY), 10])

            socket.send(compressed_image.tobytes())

            if cv2.waitKey(1) & 0xFF == ord('q'):
                break

        elapsed_time = time.time() - start_time
        print("Elapsed time:", elapsed_time)

    cap.release()
    cv2.destroyAllWindows()

    print("\nFINISH")

if __name__ == "__main__":
    main()
