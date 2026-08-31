using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class DetectionAnchorManager : MonoBehaviour
{
    [Header("AR")]
    [SerializeField]
    private ARAnchorManager anchorManager;

    [SerializeField]
    private Camera arCamera;

    [Header("Visualization Settings")]
    [SerializeField]
    private GameObject anchorPrefab;

    [SerializeField]
    [Range(1f, 5f)]
    [Tooltip("Anchor scale multiplier [1-5]x")]
    private float anchorScale = 1f;

    [SerializeField]
    [Tooltip("Forward anchor offset multiplier")]
    private Vector3 anchorOffset;

    // All anchors created by the most recent scan.
    private readonly List<ARAnchor> activeAnchors = new();

    private void Awake()
    {
        if (anchorManager == null)
            anchorManager = FindFirstObjectByType<ARAnchorManager>();

        if (arCamera == null)
            arCamera = Camera.main;
    }

    /// <summary>
    /// Removes every anchor created by the previous scan.
    /// </summary>
    public void ClearAnchors()
    {
        if (anchorManager == null)
        {
            Debug.LogError("DetectionAnchorManager: ARAnchorManager is missing.");
            return;
        }

        Debug.Log($"ANCHOR | Clearing {activeAnchors.Count} previous anchors.");

        for (int i = activeAnchors.Count - 1; i >= 0; i--)
        {
            ARAnchor anchor = activeAnchors[i];

            if (anchor == null)
                continue;

            bool removed = anchorManager.TryRemoveAnchor(anchor);

            Debug.Log(
                $"ANCHOR | Removed {anchor.name} | success={removed}"
            );
        }

        activeAnchors.Clear();
    }

    /// <summary>
    /// Creates an AR anchor from a Unity viewport coordinate and depth.
    ///
    /// viewportCenter:
    ///     x = 0 left, 1 right
    ///     y = 0 bottom, 1 top
    ///
    /// depthMeters:
    ///     Distance from the camera in meters.
    /// </summary>
    public void CreateAnchor(Vector2 viewportCenter, float depthMeters, Vector3 worldPosition, int detectionIndex, string className)
    {
        if (anchorManager == null)
        {
            Debug.LogError("ANCHOR | Cannot create anchor: ARAnchorManager is missing.");
            return;
        }

        if (arCamera == null)
        {
            Debug.LogError("ANCHOR | Cannot create anchor: AR Camera is missing.");
            return;
        }

        if (depthMeters <= 0f || float.IsNaN(depthMeters))
        {
            Debug.LogWarning($"ANCHOR | Detection {detectionIndex} ({className}) has invalid depth={depthMeters}");
            return;
        }

        // Shift the position forward along the camera's gaze vector by the offset amount
        Vector3 shiftedPosition = worldPosition + (arCamera.transform.right * anchorOffset.x)
                                                + (arCamera.transform.up * anchorOffset.y)    
                                                + (arCamera.transform.forward * anchorOffset.z);

        Quaternion rotation = arCamera.transform.rotation;

        Pose pose = new Pose(shiftedPosition, rotation);

        // Pass the correctly positioned pose to the async creator
        CreateAnchorAsync(pose, detectionIndex, className);

        Debug.Log(
            $"ANCHOR | Initiating creation | detection={detectionIndex} " +
            $"class={className} " +
            $"origWorld={worldPosition} " +
            $"shiftedWorld={shiftedPosition} " +
            $"offset={anchorOffset}m"
        );
    }

    private async void CreateAnchorAsync(Pose pose, int detectionIndex, string className)
    {
        if (anchorManager == null) return;

        try
        {
            var result = await anchorManager.TryAddAnchorAsync(pose);
            if (!result.status.IsSuccess())
            {
                Debug.LogWarning($"ANCHOR | Failed to create anchor for detection={detectionIndex} class={className} status={result.status}");
                return;
            }

            ARAnchor anchor = result.value;
            if (anchor == null)
            {
                Debug.LogWarning($"ANCHOR | TryAddAnchorAsync succeeded but returned null for detection={detectionIndex}");
                return;
            }

            // Calculate actual relative metrics for accurate logging
            Vector3 cameraRelative = arCamera.transform.InverseTransformPoint(anchor.transform.position);
            float forwardDistance = cameraRelative.z; // True distance from camera lens plane

            Debug.Log(
                $"ANCHOR | CAMERA RELATIVE | " +
                $"cameraWorld={arCamera.transform.position} " +
                $"anchorWorld={anchor.transform.position} " +
                $"relative={cameraRelative} " +
                $"trueForwardDistance={forwardDistance:F3}m"
            );

            anchor.name = $"DetectionAnchor_{detectionIndex}_{className}";
            activeAnchors.Add(anchor);

            Debug.Log(
                $"ANCHOR | CREATED | " +
                $"detection={detectionIndex} " +
                $"class={className} " +
                $"position={anchor.transform.position} " +
                $"trackingState={anchor.trackingState} " +
                $"trackableId={anchor.trackableId}"
            );

            if (anchorPrefab == null)
            {
                Debug.LogError("ANCHOR | anchorPrefab is missing! Please assign it in the Inspector.");
                return;
            }

            // Instantiate visual element directly as a child of the native AR anchor
            GameObject visual = Instantiate(anchorPrefab, anchor.transform, false);
            visual.name = $"DetectionVisual_{detectionIndex}_{className}";
            visual.layer = arCamera.gameObject.layer;

            // Snap perfectly to the native anchor's origin
            visual.transform.localPosition = Vector3.zero;
            //visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * anchorScale;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"ANCHOR | Exception creating anchor for detection={detectionIndex}: {e}");
        }
    }

    private void OnDestroy()
    {
        ClearAnchors();
    }
}