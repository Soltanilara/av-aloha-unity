using UnityEngine;

[CreateAssetMenu(fileName = "Data", menuName = "ScriptableObjects/CameraConfig", order = 1)]
public class CameraConfig : ScriptableObject
{
    public float planeDistance = 0.15f;
    public Vector4 opticalCenters = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

    public Matrix4x4 projectionMatrix = new Matrix4x4(
        new Vector4(1.565212f, 0.0f, -0.0001051585f, 0.0f),
        new Vector4(0.0f, 2.782613f, -0.04929832f, 0.0f),
        new Vector4(0.0f, 0.0f, -1.0004f, -0.20004f),
        new Vector4(0.0f, 0.0f, -1.0f, 0.0f)
    );

    public int imageWidth = 640;
    public int imageHeight = 480;

    public float verticalFieldOfView = 0.6900035f;
    
}