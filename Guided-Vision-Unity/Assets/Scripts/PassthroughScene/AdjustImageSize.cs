using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class AdjustImageSize : MonoBehaviour
{

    public Camera cam;
    public RawImage rawImage;
    public Canvas canvas;

    // define private aspect ratio
    private float cameraAspectRatio;
    private float planeDistance;
    private float imageAspectRatio;
    private float pixelsPerUnit;

    // Start is called before the first frame update
    private void Start()
    {
        cameraAspectRatio = 0;
        imageAspectRatio = 0;
        // planeDistance = canvas.planeDistance;
        
        // Debug.Log("Pixels per unit: " + pixelsPerUnit);
        // Debug.Log("[Adjust Image Size] Canvas plane distance: " + planeDistance);
    }

    // Update is called once per frame
    private void Update()
    {
        float currentCameraAspectRatio = cam.aspect;
        float currentImageAspectRatio = (float)rawImage.texture.width / (float)rawImage.texture.height;


        if (cameraAspectRatio != currentCameraAspectRatio || imageAspectRatio != currentImageAspectRatio)
        {
            cameraAspectRatio = currentCameraAspectRatio;
            imageAspectRatio = currentImageAspectRatio;
            resizeImage();
        }
    }

    void resizeImage()
    {
        // // keep image aspect ratio the same as the original image 
        // // but adjust the height to match the camera aspect ratio

        // // new height is based off of plane distance and camera aspect ratio and vertical field of view
        // float newHeightUnits = 2.0f * planeDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        // // new width is based off of new height and image aspect ratio

        // // set the size of the raw image to the new width and height
        // // however it is not in pixels
        // rawImage.rectTransform.sizeDelta = new Vector2(rawImage.texture.width, rawImage.texture.height);
        
        // pixelsPerUnit = canvas.GetComponent<CanvasScaler>().dynamicPixelsPerUnit;
        // Debug.Log("Pixels per unit: " + pixelsPerUnit);
        // // make use of pixels per unit
        // rawImage.rectTransform.localScale = new Vector3(
        //     newWidthUnits / (rawImage.texture.width / pixelsPerUnit),
        //     newHeightUnits / (rawImage.texture.height / pixelsPerUnit),
        //     1.0f
        // );

        // Debug.Log("[Adjust Image Size] old width: " +  (rawImage.texture.width / pixelsPerUnit) + " old height: " + (rawImage.texture.height / pixelsPerUnit));
        // Debug.Log("[Adjust Image Size] New width: " + newWidthUnits + " New height: " + newHeightUnits);

        float newWidth = rawImage.rectTransform.rect.height * imageAspectRatio;

        Debug.Log("New width: " + newWidth + " New height: " + rawImage.rectTransform.rect.height);

        rawImage.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, newWidth);
    }
}
