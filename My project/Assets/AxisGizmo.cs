using UnityEngine;

public class AxisGizmo : MonoBehaviour
{
    public float axisLength = 1f; // Adjust the length of the axes as needed

    private void OnDrawGizmos()
    {
        DrawAxes(Color.red, Color.green, Color.blue);
    }

    private void OnDrawGizmosSelected()
    {
        DrawAxes(Color.red, Color.green, Color.blue);
    }

    private void DrawAxes(Color xAxisColor, Color yAxisColor, Color zAxisColor)
    {
        Gizmos.matrix = transform.localToWorldMatrix;

        // X-axis
        Gizmos.color = xAxisColor;
        Gizmos.DrawLine(Vector3.zero, Vector3.right * axisLength);

        // Y-axis
        Gizmos.color = yAxisColor;
        Gizmos.DrawLine(Vector3.zero, Vector3.up * axisLength);

        // Z-axis
        Gizmos.color = zAxisColor;
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * axisLength);

        Gizmos.matrix = Matrix4x4.identity;
    }
}
