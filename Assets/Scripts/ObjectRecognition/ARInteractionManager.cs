using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using TMPro;

public class ARInteractionManager : MonoBehaviour
{
    [Header("YOLO Setup")]
    public YOLOv8SentisDetector detector;

    [Header("AR & UI Setup")]
    public ARRaycastManager arRaycastManager;
    public ARAnchorManager arAnchorManager;
    public RectTransform uiCanvas;
    public GameObject hitBoxPrefab;

    private List<PersistentDetectedObject> persistentObjects = new List<PersistentDetectedObject>();

    [System.Serializable]
    public class PersistentDetectedObject
    {
        public string className;
        public float confidence;
        public ARAnchor anchor;
        public GameObject uiBoxObject;
        public RectTransform rectTransform;
        public UnityEngine.UI.Image boxImage;
        public TextMeshProUGUI labelText;
    }

    public void OnScanButtonPressed()
    {
        Debug.Log("[ARInteractionManager] UI Scan Button Clicked! Initiating scan pipeline...");
        if (detector == null)
        {
            Debug.LogError("[ARInteractionManager] FATAL: YOLOv8SentisDetector reference is NULL on ARInteractionManager!");
            return;
        }
        StartCoroutine(CaptureFrameAndScan());
    }

    private IEnumerator CaptureFrameAndScan()
    {
        Debug.Log("[ARInteractionManager] Yielding until end of frame to capture screen pixels...");
        yield return new WaitForEndOfFrame();

        Debug.Log($"[ARInteractionManager] Creating Texture2D with screen dimensions: {Screen.width}x{Screen.height}");
        Texture2D screenTexture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        screenTexture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        screenTexture.Apply();

        Debug.Log("[ARInteractionManager] Screen pixels successfully captured and applied. Handing over to YOLO detector...");

        if (detector != null)
        {
            var rawDetections = detector.RunScan(screenTexture);
            Debug.Log($"[ARInteractionManager] YOLO Scan returned. Processing {rawDetections.Count} detections into spatial space.");
            ProcessDetectionsIntoSpatialSpace(rawDetections);
        }

        Debug.Log("[ARInteractionManager] Cleaning up temporary screenTexture from memory.");
        Destroy(screenTexture);
    }

    void ProcessDetectionsIntoSpatialSpace(List<YOLOv8SentisDetector.DetectedObject> rawDetections)
    {
        Debug.Log($"[ARInteractionManager] Clearing out {persistentObjects.Count} old spatial objects from previous scans...");
        foreach (var obj in persistentObjects)
        {
            if (obj.uiBoxObject != null) Destroy(obj.uiBoxObject);
            if (obj.anchor != null) Destroy(obj.anchor.gameObject);
        }
        persistentObjects.Clear();

        if (hitBoxPrefab == null || uiCanvas == null || arRaycastManager == null || arAnchorManager == null)
        {
            Debug.LogError($"[ARInteractionManager] Missing essential component references! PrefabNull: {hitBoxPrefab == null}, CanvasNull: {uiCanvas == null}, RaycastMgrNull: {arRaycastManager == null}, AnchorMgrNull: {arAnchorManager == null}");
            return;
        }

        int index = 0;
        foreach (var detected in rawDetections)
        {
            index++;
            Rect fixedRect = FixBoxAspectRatio(detected.boundingBox);
            Debug.Log($"[ARInteractionManager] Processing Object #{index}: [{detected.className}] with confidence {detected.confidence * 100f:F1}% at screen rect {fixedRect}");

            GameObject boxObj = Instantiate(hitBoxPrefab, uiCanvas);
            RectTransform rectTransform = boxObj.GetComponent<RectTransform>();
            UnityEngine.UI.Image boxImage = boxObj.GetComponent<UnityEngine.UI.Image>();
            TextMeshProUGUI labelText = boxObj.GetComponentInChildren<TextMeshProUGUI>();

            boxImage.color = new Color(1f, 0f, 0f, 0.35f);
            if (labelText != null) labelText.text = "";

            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(0, 0);
            rectTransform.pivot = new Vector2(0, 0);
            rectTransform.sizeDelta = new Vector2(fixedRect.width, fixedRect.height);

            Vector2 screenCenter = new Vector2(fixedRect.center.x, fixedRect.center.y);
            List<ARRaycastHit> hits = new List<ARRaycastHit>();
            ARAnchor assignedAnchor = null;

            Debug.Log($"[ARInteractionManager] Performing AR Raycast for object #{index} at screen position {screenCenter}...");
            if (arRaycastManager.Raycast(screenCenter, hits, TrackableType.PlaneEstimated | TrackableType.FeaturePoint))
            {
                Pose hitPose = hits[0].pose;
                assignedAnchor = arAnchorManager.AddAnchor(hitPose);
                Debug.Log($"[ARInteractionManager] AR Raycast SUCCESS for #{index}. Anchor created at world position: {hitPose.position}");
            }
            else
            {
                Debug.LogWarning($"[ARInteractionManager] AR Raycast FAILED for object #{index} ({detected.className}). No plane or feature point found under center coordinates {screenCenter}. Object will lack a spatial anchor.");
            }

            persistentObjects.Add(new PersistentDetectedObject
            {
                className = detected.className,
                confidence = detected.confidence,
                anchor = assignedAnchor,
                uiBoxObject = boxObj,
                rectTransform = rectTransform,
                boxImage = boxImage,
                labelText = labelText
            });
        }
        Debug.Log($"[ARInteractionManager] Spatial tracking synchronization complete. Total active persistent objects mapped: {persistentObjects.Count}");
    }

    Rect FixBoxAspectRatio(Rect rawBox)
    {
        float minDim = Mathf.Min(Screen.width, Screen.height);
        float correctedWidth = Mathf.Max(rawBox.width, minDim * 0.1f);
        float correctedHeight = Mathf.Max(rawBox.height, minDim * 0.1f);
        return new Rect(rawBox.x, rawBox.y, correctedWidth, correctedHeight);
    }

    void Update()
    {
        UpdateSpatialUIProjections();

        Vector2 screenGazePoint = Vector2.zero;
        bool hasInput = false;

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            screenGazePoint = Touchscreen.current.primaryTouch.position.ReadValue();
            hasInput = true;
        }
        else if (Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            screenGazePoint = Mouse.current.position.ReadValue();
            hasInput = true;
        }

        if (hasInput) SimulateGazeHover(screenGazePoint);
        else ResetHoverStates();
    }

    void UpdateSpatialUIProjections()
    {
        foreach (var obj in persistentObjects)
        {
            if (obj.anchor != null && obj.uiBoxObject != null)
            {
                Vector3 screenPos = Camera.main.WorldToScreenPoint(obj.anchor.transform.position);

                if (screenPos.z > 0)
                {
                    obj.uiBoxObject.SetActive(true);
                    obj.rectTransform.position = screenPos;
                }
                else
                {
                    obj.uiBoxObject.SetActive(false);
                }
            }
        }
    }

    void SimulateGazeHover(Vector2 screenGazePoint)
    {
        foreach (var obj in persistentObjects)
        {
            if (obj.uiBoxObject == null || !obj.uiBoxObject.activeSelf) continue;

            if (RectTransformUtility.RectangleContainsScreenPoint(obj.rectTransform, screenGazePoint, null))
            {
                obj.boxImage.color = new Color(0f, 1f, 0f, 0.5f);
                if (obj.labelText != null)
                    obj.labelText.text = $"{obj.className} ({obj.confidence * 100f:F0}%)";
            }
            else
            {
                obj.boxImage.color = new Color(1f, 0f, 0f, 0.35f);
                if (obj.labelText != null)
                    obj.labelText.text = "";
            }
        }
    }

    void ResetHoverStates()
    {
        foreach (var obj in persistentObjects)
        {
            if (obj.boxImage != null) obj.boxImage.color = new Color(1f, 0f, 0f, 0.35f);
            if (obj.labelText != null) obj.labelText.text = "";
        }
    }
}