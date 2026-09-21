using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Samples ARCore depth at a normalized viewport coordinate and returns the
/// corresponding world-space hit position.
/// </summary>
public sealed class DepthSampler : MonoBehaviour
{
    [Header("AR Dependencies")]
    [Tooltip("AR Foundation raycast manager used to query the current depth map.")]
    [SerializeField] private ARRaycastManager raycastManager;
    [Tooltip("AR camera used to measure camera-to-hit distance.")]
    [SerializeField] private Camera arCamera;

    private readonly List<ARRaycastHit> hits = new();

    /// <summary>
    /// Attempts to obtain a depth hit at a normalized Unity viewport coordinate.
    /// </summary>
    /// <param name="viewportPoint">
    /// Normalized viewport coordinate where (0,0) is bottom-left and (1,1) is top-right.
    /// </param>
    /// <param name="depth">
    /// Receives the Euclidean distance in meters from the AR camera to the hit position.
    /// </param>
    /// <param name="worldPosition">
    /// Receives the world-space position returned by the AR depth raycast.
    /// </param>
    /// <returns>True when a valid AR depth hit is found; otherwise false.</returns>
    public bool TryGetDepth(
        Vector2 viewportPoint,
        out float depth,
        out Vector3 worldPosition)
    {
        depth = 0f;
        worldPosition = default;

        if (raycastManager == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Depth,
                "ARRaycastManager is not assigned.",
                this
            );
            return false;
        }

        if (arCamera == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Depth,
                "AR Camera is not assigned.",
                this
            );
            return false;
        }

        Vector2 screenPoint = ViewportToScreenPoint(viewportPoint);

        hits.Clear();

        bool success = raycastManager.Raycast(
            screenPoint,
            hits,
            TrackableType.Depth
        );

        if (!success || hits.Count == 0)
        {
            ObjectDetectionDebug.LogWarning(
                ObjectDetectionLogCategory.Depth,
                $"No depth hit at viewport={viewportPoint}, screen={screenPoint}.",
                this
            );
            return false;
        }

        ARRaycastHit hit = hits[0];
        worldPosition = hit.pose.position;
        depth = Vector3.Distance(arCamera.transform.position, worldPosition);

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Depth,
            $"Depth hit | type={hit.hitType} viewport={viewportPoint} " +
            $"screen={screenPoint} depth={depth:F3}m world={worldPosition}",
            this
        );

        return true;
    }

    /// <summary>
    /// Converts a normalized viewport coordinate into a screen-space pixel coordinate.
    /// </summary>
    /// <param name="viewportPoint">
    /// Normalized viewport coordinate where (0,0) is bottom-left and (1,1) is top-right.
    /// </param>
    /// <returns>The corresponding screen-space pixel coordinate.</returns>
    private static Vector2 ViewportToScreenPoint(Vector2 viewportPoint)
    {
        return new Vector2(
            viewportPoint.x * Screen.width,
            viewportPoint.y * Screen.height
        );
    }
}
