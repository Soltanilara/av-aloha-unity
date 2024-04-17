import sys
import numpy as np
import pyzed.sl as sl
import cv2
import zmq
import time
import base64
import blosc as bl
import pickle

def main() :

    # Initialize ZeroMQ context and socket for publishing
    context = zmq.Context()
    socket = context.socket(zmq.PUB)
    socket.setsockopt(zmq.SNDHWM, 1)
    socket.bind("tcp://*:5555")  # Change the address/port as needed

    # Create a ZED camera object
    zed = sl.Camera()

    # Set configuration parameters
    input_type = sl.InputType()
    if len(sys.argv) >= 2 :
        input_type.set_from_svo_file(sys.argv[1])
    init = sl.InitParameters(input_t=input_type)
    init.camera_resolution = sl.RESOLUTION.HD720
    init.depth_mode = sl.DEPTH_MODE.PERFORMANCE
    init.coordinate_units = sl.UNIT.MILLIMETER

    # Open the camera
    err = zed.open(init)
    if err != sl.ERROR_CODE.SUCCESS :
        print(repr(err))
        zed.close()
        exit(1)

    # Set runtime parameters after opening the camera
    runtime = sl.RuntimeParameters()

    # Prepare new image size to retrieve half-resolution images
    image_size = zed.get_camera_information().camera_configuration.resolution

    # # make smaller by half
    image_size.width = image_size.width // 2
    image_size.height = image_size.height // 2

    # Declare your sl.Mat matrices
    left_image_zed = sl.Mat(image_size.width, image_size.height, sl.MAT_TYPE.U8_C4)
    right_image_zed = sl.Mat(image_size.width, image_size.height, sl.MAT_TYPE.U8_C4)
    depth_image_zed = sl.Mat(image_size.width, image_size.height, sl.MAT_TYPE.U8_C4)

    key = ' '
    while key != 113 :

        start_time = time.time()

        err = zed.grab(runtime)
        if err == sl.ERROR_CODE.SUCCESS :
            # Retrieve the left image, depth image in the half-resolution
            zed.retrieve_image(left_image_zed, sl.VIEW.LEFT, sl.MEM.CPU, image_size)
            zed.retrieve_image(right_image_zed, sl.VIEW.RIGHT, sl.MEM.CPU, image_size)
            zed.retrieve_image(depth_image_zed, sl.VIEW.DEPTH, sl.MEM.CPU, image_size)

            # To recover data from sl.Mat to use it with opencv, use the get_data() method
            # It returns a numpy array that can be used as a matrix with opencv
            left_image_ocv = left_image_zed.get_data()
            right_image_ocv = right_image_zed.get_data()
            depth_image_ocv = depth_image_zed.get_data()
            
            combined_image = np.concatenate((left_image_ocv, right_image_ocv), axis=1)
            compressed_image = cv2.imencode('.jpg', combined_image, [int(cv2.IMWRITE_JPEG_QUALITY), 70])[1]
            socket.send(compressed_image)

            cv2.imshow("ZED", combined_image)

            if cv2.waitKey(1) & 0xFF == ord('q'): 
                break

        elapsed_time = time.time() - start_time
        print("Elapsed time: ", elapsed_time)

    cv2.destroyAllWindows()
    zed.close()

    print("\nFINISH")

if __name__ == "__main__":
    main()