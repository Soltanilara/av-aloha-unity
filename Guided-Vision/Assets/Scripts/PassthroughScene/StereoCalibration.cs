using System.IO;
using UnityEngine;
using OVRSimpleJSON;

/// <summary>
/// Intrinsics + rectification for the sender's stereo camera pair, used by the
/// StereoEyeView shader to undistort and rectify on the GPU at display resolution.
///
/// This exists so the sender can stop resampling. Today the sender runs cv2.remap on
/// every frame before encoding, which costs CPU on its critical path and resamples the
/// image once before the encoder resamples it again. Doing the same mapping here, per
/// display pixel, means the frames go over the wire raw and get sampled exactly once,
/// at the resolution they are actually viewed at.
///
/// The file is looked for first in Application.persistentDataPath (so it can be pushed
/// with adb and iterated on without rebuilding the APK), then in Resources.
///
/// Expected JSON -- straight out of cv2.stereoRectify, see
/// tools/export_calibration_for_unity.py:
/// {
///   "model": "fisheye" | "pinhole",
///   "image_size": [1280, 800],
///   "left":  { "fx":.., "fy":.., "cx":.., "cy":..,
///              "dist":[k1,k2,k3,k4], "tangential":[p1,p2],
///              "R":[r00,r01,r02,r10,r11,r12,r20,r21,r22] },
///   "right": { ... }
/// }
///
/// "R" is the rectification rotation as OpenCV produces it (camera frame -> rectified
/// frame). The shader needs the opposite direction, so it is transposed on load.
/// </summary>
public class StereoCalibration
{
    public enum Model
    {
        None = 0,
        Pinhole = 1,
        Fisheye = 2
    }

    public struct EyeCalibration
    {
        public Vector4 intrinsics;   // fx, fy, cx, cy, in source pixels
        public Vector4 dist;         // k1, k2, k3, k4
        public Vector4 tangential;   // p1, p2, unused, unused
        public Matrix4x4 rectInv;    // rectified frame -> camera frame
    }

    public Model model = Model.None;
    public Vector2 imageSize = Vector2.one;
    public EyeCalibration left;
    public EyeCalibration right;
    public string source = "none";

    public const string DefaultFileName = "oak_stereo_calibration.json";

    /// <summary>Returns null when there is no calibration to load; that is not an error.</summary>
    public static StereoCalibration Load(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            fileName = DefaultFileName;

        string json = null;
        string source = null;

        string path = Path.Combine(Application.persistentDataPath, fileName);
        if (File.Exists(path))
        {
            try
            {
                json = File.ReadAllText(path);
                source = path;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"StereoCalibration: could not read {path}: {e.Message}");
            }
        }

        if (json == null)
        {
            string resourceName = Path.GetFileNameWithoutExtension(fileName);
            var asset = Resources.Load<TextAsset>(resourceName);
            if (asset != null)
            {
                json = asset.text;
                source = "Resources/" + resourceName;
            }
        }

        if (string.IsNullOrEmpty(json))
            return null;

        try
        {
            return Parse(json, source);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"StereoCalibration: failed to parse {source}: {e.Message}");
            return null;
        }
    }

    private static StereoCalibration Parse(string json, string source)
    {
        JSONNode root = JSON.Parse(json);
        var calibration = new StereoCalibration { source = source };

        string model = root["model"];
        calibration.model = (model != null && model.ToLowerInvariant().StartsWith("fisheye"))
            ? Model.Fisheye
            : Model.Pinhole;

        var size = root["image_size"];
        calibration.imageSize = new Vector2(
            Mathf.Max(1f, size[0].AsFloat),
            Mathf.Max(1f, size[1].AsFloat));

        calibration.left = ParseEye(root["left"]);
        calibration.right = ParseEye(root["right"]);
        return calibration;
    }

    private static EyeCalibration ParseEye(JSONNode node)
    {
        var eye = new EyeCalibration
        {
            intrinsics = new Vector4(
                node["fx"].AsFloat, node["fy"].AsFloat,
                node["cx"].AsFloat, node["cy"].AsFloat),
            dist = new Vector4(
                node["dist"][0].AsFloat, node["dist"][1].AsFloat,
                node["dist"][2].AsFloat, node["dist"][3].AsFloat),
            tangential = new Vector4(
                node["tangential"][0].AsFloat, node["tangential"][1].AsFloat, 0f, 0f),
            rectInv = Matrix4x4.identity
        };

        var r = node["R"];
        if (r != null && r.Count >= 9)
        {
            var m = Matrix4x4.identity;
            // Transpose while reading: the file stores camera -> rectified, the shader
            // walks rectified -> camera. For a rotation the inverse is the transpose.
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    m[col, row] = r[row * 3 + col].AsFloat;
            eye.rectInv = m;
        }
        return eye;
    }
}
