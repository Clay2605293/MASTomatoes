using UnityEngine;

public class OrbitCamera : MonoBehaviour
{
    [Header("Target")]
    public Transform target;

    [Header("Distance")]
    public float distance = 5.0f;
    public float minDistance = 2.0f;
    public float maxDistance = 10.0f;
    public float scrollSensitivity = 2.0f;

    [Header("Rotation")]
    public float sensitivityX = 4.0f;
    public float sensitivityY = 4.0f;
    public float minY = -20.0f;
    public float maxY = 60.0f;

    private float currentX = 0.0f;
    private float currentY = 0.0f;

    void LateUpdate()
    {
        if (target == null) return;

        // Handle scroll wheel zoom
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        distance -= scroll * scrollSensitivity;
        distance = Mathf.Clamp(distance, minDistance, maxDistance);

        // Handle rotation
        currentX += Input.GetAxis("Mouse X") * sensitivityX;
        currentY -= Input.GetAxis("Mouse Y") * sensitivityY;
        currentY = Mathf.Clamp(currentY, minY, maxY);

        Quaternion rotation = Quaternion.Euler(currentY, currentX, 0f);
        Vector3 offset = rotation * new Vector3(0f, 0f, -distance);

        transform.position = target.position + offset;
        transform.LookAt(target.position);
    }
}