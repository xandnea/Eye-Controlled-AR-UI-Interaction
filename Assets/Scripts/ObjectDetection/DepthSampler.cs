using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class DepthSampler : MonoBehaviour
{
    [SerializeField]
    private ARRaycastManager raycastManager;

    [SerializeField]
    private Camera arCamera;

    private readonly List<ARRaycastHit> hits = new();

    public bool TryGetDepth(Vector2 viewportPoint, out float depth, out Vector3 worldPosition)
    {
        depth = 0f;
        worldPosition = default;

        if (raycastManager == null)
        {
            Debug.LogError("DepthSampler: ARRaycastManager is not assigned.");
            return false;
        }

        if (arCamera == null)
        {
            Debug.LogError("DepthSampler: AR Camera is not assigned.");
            return false;
        }

        // Unity viewport:
        // (0,0) = bottom-left
        // (1,1) = top-right

        Vector2 screenPoint = new Vector2(
            viewportPoint.x * Screen.width,
            viewportPoint.y * Screen.height
        );

        hits.Clear();

        bool success = raycastManager.Raycast(
            screenPoint,
            hits,
            TrackableType.Depth
         );


        if (!success || hits.Count == 0)
        {
            Debug.LogWarning(
                $"DepthSampler: No depth hit at screen={screenPoint}"
            );

            return false;
        }

        Debug.Log(
            $"DEPTH HIT | type={hits[0].hitType} " +
            $"screen={screenPoint} " +
            $"world={hits[0].pose.position}"
        );

        worldPosition = hits[0].pose.position;

        depth = Vector3.Distance(
            arCamera.transform.position,
            worldPosition
        );

        return true;
    }
}