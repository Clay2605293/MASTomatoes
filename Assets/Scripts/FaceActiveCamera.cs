using UnityEngine;

public class FaceActiveCamera : MonoBehaviour
{
    public CameraCycler cameraCycler;
    public bool reverseDirection = false;
    public bool yAxisOnly = false;

    void LateUpdate()
    {
        if (cameraCycler == null) return;

        Camera activeCamera = cameraCycler.GetActiveCamera();
        
        if (activeCamera == null) return;

        Vector3 directionToCamera = activeCamera.transform.position - transform.position;
        
        if (reverseDirection)
        {
            directionToCamera = -directionToCamera;
        }

        if (yAxisOnly)
        {
            directionToCamera.y = 0;
        }

        if (directionToCamera != Vector3.zero)
        {
            transform.rotation = Quaternion.LookRotation(directionToCamera);
        }
    }
}