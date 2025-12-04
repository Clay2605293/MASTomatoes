using System.Collections.Generic;
using UnityEngine;

public class CameraCycler : MonoBehaviour
{
    [Header("Main / Top-down camera (assign in Inspector)")]
    public Camera mainCamera;

    [Header("Optional filter")]
    [Tooltip("If empty, all cameras are considered. If set, only cameras whose GameObject has this tag will be included (robots' cameras could be tagged, e.g. 'RobotCamera').")]
    public string includeTag = "";

    // internal list of cameras in cycle order
    private List<Camera> cycleCameras = new List<Camera>();
    private int currentIndex = 0;

    void Start()
    {
        if (mainCamera == null)
        {
            Debug.LogError("[CameraCycler] Assign the mainCamera in the inspector.");
            enabled = false;
            return;
        }

        RebuildCameraList();
        ActivateCameraAtIndex(currentIndex);
    }

    void Update()
    {
        if (Input.GetMouseButtonDown(0)) // left click
        {
            RebuildCameraList(); // rediscover cameras in case robots spawned/destroyed
            CycleNext();
        }
    }

    // Rebuild list: main first, then other cameras that match filter
    private void RebuildCameraList()
    {
        cycleCameras.Clear();

        // Add main camera first
        cycleCameras.Add(mainCamera);

        // Find all cameras in scene
        Camera[] all = FindObjectsOfType<Camera>(true); // include inactive cameras if needed

        foreach (Camera c in all)
        {
            if (c == mainCamera) continue;

            // ignore cameras that are part of editor-only objects
            if (c.gameObject.hideFlags != 0) continue;

            // if includeTag specified, only include cameras whose GameObject has that tag
            if (!string.IsNullOrEmpty(includeTag))
            {
                if (c.gameObject.CompareTag(includeTag))
                {
                    cycleCameras.Add(c);
                }
            }
            else
            {
                // otherwise include all (active in hierarchy)
                if (c.gameObject.activeInHierarchy)
                    cycleCameras.Add(c);
            }
        }

        // Ensure at least one camera exists
        if (cycleCameras.Count == 0)
            cycleCameras.Add(mainCamera);

        // clamp currentIndex
        currentIndex = Mathf.Clamp(currentIndex, 0, cycleCameras.Count - 1);
    }

    // Switch to next camera in list
    private void CycleNext()
    {
        if (cycleCameras.Count <= 1)
            return;

        currentIndex = (currentIndex + 1) % cycleCameras.Count;
        ActivateCameraAtIndex(currentIndex);
    }

    private void ActivateCameraAtIndex(int index)
    {
        Camera activeCam = cycleCameras[index];

        foreach (Camera c in cycleCameras)
        {
            if (c == null) continue;
            c.enabled = (c == activeCam);

            AudioListener al = c.GetComponent<AudioListener>();
            if (al != null) al.enabled = (c == activeCam);
        }

        AudioListener[] allAL = FindObjectsOfType<AudioListener>();
        foreach (AudioListener al in allAL)
        {
            if (al == null) continue;
            Camera parentCam = al.GetComponent<Camera>();
            if (parentCam == null || !cycleCameras.Contains(parentCam))
            {
                al.enabled = false;
            }
        }

        Debug.Log($"[CameraCycler] Activated camera: {activeCam.name} (index {index})");
    }

    public Camera GetActiveCamera()
    {
        if (cycleCameras.Count > 0 && currentIndex >= 0 && currentIndex < cycleCameras.Count)
        {
            return cycleCameras[currentIndex];
        }
        return mainCamera;
}

}
