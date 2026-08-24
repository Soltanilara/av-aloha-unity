using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Reads Meta's hand skeletons into the shape the uplink sends.
///
/// Hands and controllers are alternatives, not additions -- the runtime hands you one or
/// the other -- so this reports whether hands are live and the uplink decides which of
/// the two to mark valid. A robot mapping a wrist to an end effector has to know which
/// stream to believe on the frame the operator puts a controller down, and inferring
/// that from empty poses is guesswork.
///
/// Joints come out as positions in the tracking frame, matching every other pose in the
/// packet. The bone count is read from the runtime rather than assumed: the SDK ships
/// more than one hand skeleton (24 bones for the classic, 26 for the XR one) and hard
/// coding either breaks the moment the runtime picks the other.
/// </summary>
public class GvHandTracking : MonoBehaviour
{
    private OVRHand[] hands;
    private OVRSkeleton[] skeletons;
    private Transform origin;
    private float nextScan;

    private GvHandState left, right;
    // Sent once per side, when the runtime first hands us a valid skeleton. The robot
    // cannot know which of Meta's hand rigs is in use -- there is more than one, with
    // different bone counts and different parent tables -- and a visualiser guessing
    // draws confident nonsense. So the end that knows says so.
    private readonly bool[] topologySent = new bool[2];

    public bool AnyTracked { get; private set; }
    public GvHandState Left => left;
    public GvHandState Right => right;

    /// <summary>Poses are reported relative to this. Null means world space.</summary>
    public void SetOrigin(Transform t) { origin = t; }

    private void Awake()
    {
        left.Pinch = new float[5];
        right.Pinch = new float[5];
        Scan();
    }

    private void Scan()
    {
        hands = FindObjectsByType<OVRHand>(FindObjectsInactive.Include);
        skeletons = new OVRSkeleton[hands.Length];
        for (int i = 0; i < hands.Length; i++)
            skeletons[i] = hands[i] != null ? hands[i].GetComponent<OVRSkeleton>() : null;
    }

    private void Update()
    {
        AnyTracked = false;
        left.Tracked = false;
        right.Tracked = false;

        // Hands can be spawned after load, and a hand destroyed and respawned leaves a
        // stale array. Rescanning while nothing is tracked costs nothing and is the
        // difference between hands working and hands never appearing.
        if (hands == null || hands.Length == 0)
        {
            if (Time.unscaledTime < nextScan)
                return;
            nextScan = Time.unscaledTime + 2f;
            Scan();
            if (hands.Length == 0)
                return;
        }

        for (int i = 0; i < hands.Length; i++)
        {
            var h = hands[i];
            var sk = skeletons[i];
            if (h == null || sk == null || !h.isActiveAndEnabled || !h.IsTracked)
                continue;
            if (!sk.IsDataValid)
                continue;

            // Handedness comes from the skeleton, which reports it; OVRHand keeps its
            // own copy internal to the SDK assembly and out of reach from here.
            bool isRight = sk.GetSkeletonType() == OVRSkeleton.SkeletonType.HandRight
                        || sk.GetSkeletonType() == OVRSkeleton.SkeletonType.XRHandRight;
            if (isRight)
                Fill(ref right, h, sk);
            else
                Fill(ref left, h, sk);
            PublishTopology(sk, isRight);
            AnyTracked = true;
        }
    }

    private void Fill(ref GvHandState s, OVRHand h, OVRSkeleton sk)
    {
        s.Tracked = true;
        s.Confidence = h.HandConfidence == OVRHand.TrackingConfidence.High ? 1f : 0.5f;
        s.Wrist = ToLocal(h.transform);

        s.Pinch[0] = Strength(h, OVRHand.HandFinger.Thumb);
        s.Pinch[1] = Strength(h, OVRHand.HandFinger.Index);
        s.Pinch[2] = Strength(h, OVRHand.HandFinger.Middle);
        s.Pinch[3] = Strength(h, OVRHand.HandFinger.Ring);
        s.Pinch[4] = Strength(h, OVRHand.HandFinger.Pinky);

        var bones = sk.Bones;
        int n = bones != null ? bones.Count : 0;
        if (s.Joints == null || s.Joints.Length < n)
            s.Joints = new Vector3[n];
        int written = 0;
        for (int b = 0; b < n; b++)
        {
            var t = bones[b] != null ? bones[b].Transform : null;
            if (t == null)
                continue;
            s.Joints[written++] = origin != null
                ? origin.InverseTransformPoint(t.position)
                : t.position;
        }
        s.JointCount = written;
    }

    /// <summary>
    /// Publish this hand's parent table, once, on the control channel.
    ///
    /// Joint *positions* still travel absolute in the uplink, so a robot that only wants
    /// poses needs none of this. It is here for anything that has to draw or retarget,
    /// which cannot do either without knowing what connects to what.
    /// </summary>
    private void PublishTopology(OVRSkeleton sk, bool isRight)
    {
        int idx = isRight ? 1 : 0;
        if (topologySent[idx])
            return;
        var bones = sk.Bones;
        if (bones == null || bones.Count == 0)
            return;

        var parents = new List<object>(bones.Count);
        for (int b = 0; b < bones.Count; b++)
            parents.Add((long)(bones[b] != null ? bones[b].ParentBoneIndex : -1));

        var session = GvRobotSession.Instance;
        if (session == null || session.Link == null || !session.Link.Connected)
            return;                        // retry next frame; nothing is lost by waiting
        session.Link.Publish("hand/skeleton", GvRobotSession.Map(
            "side", isRight ? "r" : "l",
            "parents", parents,
            "count", (long)bones.Count));
        topologySent[idx] = true;
        Debug.Log($"GvHandTracking: published {(isRight ? "right" : "left")} skeleton, "
                  + $"{bones.Count} bones.");
    }

    private static float Strength(OVRHand h, OVRHand.HandFinger f)
    {
        try { return Mathf.Clamp01(h.GetFingerPinchStrength(f)); }
        catch (System.Exception) { return 0f; }
    }

    private GvPose ToLocal(Transform t)
    {
        var p = new GvPose { Valid = true };
        if (origin != null)
        {
            p.Position = origin.InverseTransformPoint(t.position);
            p.Rotation = Quaternion.Inverse(origin.rotation) * t.rotation;
        }
        else
        {
            p.Position = t.position;
            p.Rotation = t.rotation;
        }
        return p;
    }
}
