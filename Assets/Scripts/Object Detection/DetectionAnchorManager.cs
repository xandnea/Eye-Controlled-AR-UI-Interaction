using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Creates and manages AR anchors for object detections that have valid depth hits.
/// Each successfully created AR anchor receives an optional visualization prefab.
/// </summary>
public sealed class DetectionAnchorManager : MonoBehaviour
{
    [Header("AR Dependencies")]
    [SerializeField] private ARAnchorManager anchorManager;
    [SerializeField] private Camera arCamera;

    [Header("Visualization")]
    [SerializeField] private GameObject anchorPrefab;

    [SerializeField]
    [Range(1f, 5f)]
    [Tooltip("Uniform scale multiplier applied to the spawned anchor visual.")]
    private float anchorScale = 1f;

    [SerializeField]
    [Tooltip("World placement offset expressed along the AR camera's local right/up/forward axes.")]
    private Vector3 anchorOffset;

    private readonly List<ARAnchor> activeAnchors = new();

    /// <summary>
    /// Validates serialized AR dependencies before the component begins processing detections.
    /// </summary>
    private void Awake()
    {
        if (anchorManager == null)
            anchorManager = FindFirstObjectByType<ARAnchorManager>();

        if (anchorManager == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Lifecycle,
                "ARAnchorManager is not assigned and could not be found in the scene.",
                this
            );
        }

        if (arCamera == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Lifecycle,
                "AR Camera is not assigned.",
                this
            );
        }
    }

    /// <summary>
    /// Removes every AR anchor created by the most recent scan and clears the local anchor list.
    /// </summary>
    public void ClearAnchors()
    {
        if (anchorManager == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Anchors,
                "Cannot clear anchors because ARAnchorManager is missing.",
                this
            );
            return;
        }

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Anchors,
            $"Clearing {activeAnchors.Count} previous anchor(s).",
            this
        );

        for (int i = activeAnchors.Count - 1; i >= 0; i--)
        {
            ARAnchor anchor = activeAnchors[i];

            if (anchor == null)
                continue;

            bool removed = anchorManager.TryRemoveAnchor(anchor);

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Anchors,
                $"Removed {anchor.name} | success={removed}",
                this
            );
        }

        activeAnchors.Clear();
    }

    /// <summary>
    /// Creates an AR anchor for a detection using its world-space depth hit.
    /// </summary>
    /// <param name="viewportCenter">
    /// Detection center in normalized Unity viewport coordinates. Retained for call-site context.
    /// </param>
    /// <param name="depthMeters">Measured camera-to-hit distance in meters.</param>
    /// <param name="worldPosition">World-space position returned by the AR depth raycast.</param>
    /// <param name="detectionIndex">Index of the detection in the current YOLO result set.</param>
    /// <param name="className">YOLO class label associated with the detection.</param>
    public void CreateAnchor(
        Vector2 viewportCenter,
        float depthMeters,
        Vector3 worldPosition,
        int detectionIndex,
        string className)
    {
        if (anchorManager == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Anchors,
                "Cannot create anchor because ARAnchorManager is missing.",
                this
            );
            return;
        }

        if (arCamera == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Anchors,
                "Cannot create anchor because AR Camera is missing.",
                this
            );
            return;
        }

        if (depthMeters <= 0f || float.IsNaN(depthMeters))
        {
            ObjectDetectionDebug.LogWarning(
                ObjectDetectionLogCategory.Anchors,
                $"Detection {detectionIndex} ({className}) has invalid depth={depthMeters}.",
                this
            );
            return;
        }

        Vector3 shiftedPosition = ApplyCameraRelativeOffset(worldPosition);
        Quaternion rotation = arCamera.transform.rotation;
        Pose pose = new Pose(shiftedPosition, rotation);

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Anchors,
            $"Initiating anchor creation | detection={detectionIndex} class={className} " +
            $"viewport={viewportCenter} depth={depthMeters:F3}m " +
            $"origWorld={worldPosition} shiftedWorld={shiftedPosition} offset={anchorOffset}",
            this
        );

        CreateAnchorAsync(pose, detectionIndex, className);
    }

    /// <summary>
    /// Adds an AR anchor through AR Foundation and attaches the configured visual prefab.
    /// </summary>
    /// <param name="pose">World-space pose at which the AR anchor should be created.</param>
    /// <param name="detectionIndex">Index of the source detection in the current YOLO result set.</param>
    /// <param name="className">YOLO class label associated with the source detection.</param>
    private async void CreateAnchorAsync(
        Pose pose,
        int detectionIndex,
        string className)
    {
        if (anchorManager == null)
            return;

        try
        {
            var result = await anchorManager.TryAddAnchorAsync(pose);

            if (!result.status.IsSuccess())
            {
                ObjectDetectionDebug.LogWarning(
                    ObjectDetectionLogCategory.Anchors,
                    $"Failed to create anchor | detection={detectionIndex} " +
                    $"class={className} status={result.status}",
                    this
                );
                return;
            }

            ARAnchor anchor = result.value;

            if (anchor == null)
            {
                ObjectDetectionDebug.LogWarning(
                    ObjectDetectionLogCategory.Anchors,
                    $"TryAddAnchorAsync succeeded but returned null | detection={detectionIndex}.",
                    this
                );
                return;
            }

            Vector3 cameraRelative =
                arCamera.transform.InverseTransformPoint(anchor.transform.position);

            float forwardDistance = cameraRelative.z;

            anchor.name = $"DetectionAnchor_{detectionIndex}_{className}";
            activeAnchors.Add(anchor);

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Anchors,
                $"Created {anchor.name} | position={anchor.transform.position} " +
                $"relative={cameraRelative} forward={forwardDistance:F3}m " +
                $"trackingState={anchor.trackingState} trackableId={anchor.trackableId}",
                this
            );

            CreateAnchorVisual(anchor, detectionIndex, className);
        }
        catch (System.Exception exception)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Anchors,
                $"Exception creating anchor | detection={detectionIndex} class={className}\n{exception}",
                this
            );
        }
    }

    /// <summary>
    /// Applies the serialized anchor offset along the current AR camera basis vectors.
    /// </summary>
    /// <param name="worldPosition">Original world-space depth hit position.</param>
    /// <returns>The offset world-space position used for anchor creation.</returns>
    private Vector3 ApplyCameraRelativeOffset(Vector3 worldPosition)
    {
        return worldPosition
             + arCamera.transform.right * anchorOffset.x
             + arCamera.transform.up * anchorOffset.y
             + arCamera.transform.forward * anchorOffset.z;
    }

    /// <summary>
    /// Instantiates the configured visual as a child of an AR anchor.
    /// The prefab's authored local rotation is intentionally preserved.
    /// </summary>
    /// <param name="anchor">Parent AR anchor that owns the visualization.</param>
    /// <param name="detectionIndex">Index of the source detection.</param>
    /// <param name="className">YOLO class label associated with the source detection.</param>
    private void CreateAnchorVisual(
        ARAnchor anchor,
        int detectionIndex,
        string className)
    {
        if (anchorPrefab == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.AnchorVisuals,
                "Anchor prefab is missing. Assign it in the DetectionAnchorManager Inspector.",
                this
            );
            return;
        }

        GameObject visual = Instantiate(anchorPrefab, anchor.transform, false);
        visual.name = $"DetectionVisual_{detectionIndex}_{className}";

        // Preserve the prefab's authored local rotation.
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localScale = anchorPrefab.transform.localScale * anchorScale;

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.AnchorVisuals,
            $"Created {visual.name} | active={visual.activeInHierarchy} " +
            $"layer={LayerMask.LayerToName(visual.layer)} " +
            $"worldPos={visual.transform.position} localPos={visual.transform.localPosition} " +
            $"localRotation={visual.transform.localEulerAngles} " +
            $"localScale={visual.transform.localScale} lossyScale={visual.transform.lossyScale}",
            this
        );

        LogVisualRenderers(visual);
    }

    /// <summary>
    /// Logs renderer, material, mesh, and bounds information for a spawned anchor visual.
    /// This work is skipped when the AnchorVisuals logging category is disabled.
    /// </summary>
    /// <param name="visual">Root GameObject of the spawned anchor visualization.</param>
    private void LogVisualRenderers(GameObject visual)
    {
        if (!ObjectDetectionDebug.IsCategoryEnabled(
                ObjectDetectionLogCategory.AnchorVisuals))
        {
            return;
        }

        Renderer[] renderers =
            visual.GetComponentsInChildren<Renderer>(true);

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.AnchorVisuals,
            $"Renderer count={renderers.Length} for {visual.name}.",
            this
        );

        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer renderer = renderers[i];
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();

            string materialName =
                renderer.sharedMaterial != null
                    ? renderer.sharedMaterial.name
                    : "NULL";

            string meshName =
                meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.name
                    : "NULL";

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.AnchorVisuals,
                $"Renderer[{i}] | type={renderer.GetType().Name} " +
                $"name={renderer.gameObject.name} enabled={renderer.enabled} " +
                $"active={renderer.gameObject.activeInHierarchy} " +
                $"layer={LayerMask.LayerToName(renderer.gameObject.layer)} " +
                $"boundsCenter={renderer.bounds.center} boundsSize={renderer.bounds.size} " +
                $"material={materialName} mesh={meshName}",
                this
            );
        }
    }

    /// <summary>
    /// Removes tracked anchors when this manager is destroyed.
    /// </summary>
    private void OnDestroy()
    {
        if (anchorManager != null)
            ClearAnchors();
    }
}
