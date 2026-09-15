using System;
using System.Collections;
using System.Text;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime.Examples;
using Microsoft.ML.OnnxRuntime.Unity;
using TextureSource;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Runs YOLO11 segmentation on the latest camera texture, updates detection UI,
/// samples AR depth for valid detections, and creates corresponding AR anchors.
/// </summary>
[RequireComponent(typeof(VirtualTextureSource))]
public sealed class Yolo11SegRunner : MonoBehaviour
{
    /// <summary>
    /// UnityEvent that emits a texture.
    /// </summary>
    [Serializable]
    public class TextureEvent : UnityEvent<Texture> { }

    /// <summary>
    /// UnityEvent that emits a texture aspect ratio.
    /// </summary>
    [Serializable]
    public class AspectChangeEvent : UnityEvent<float> { }

    [Header("AR Dependencies")]
    [SerializeField] private DepthSampler depthSampler;
    [SerializeField] private DetectionAnchorManager detectionAnchorManager;
    [SerializeField] private ScanAnimationOverlayController scanAnimationOverlay;

    [Header("Scan Presentation")]
    [SerializeField, Min(0f)] private float minimumScanOverlayTime = 1.5f;

    [Header("Detection Model")]
    [SerializeField] private OrtAsset model;

    [SerializeField]
    private RemoteFile modelFile = new(
        "https://github.com/asus4/onnxruntime-unity-examples/releases/download/v0.2.7/yolo11n-seg-dynamic.onnx"
    );

    [SerializeField] private Yolo11Seg.Options options;

    [Header("Detection UI")]
    [SerializeField] private TMPro.TMP_Text detectionBoxPrefab;
    [SerializeField] private RectTransform detectionContainer;
    [SerializeField] private int maxDetections = 20;

    [Header("Depth Sampling")]
    [SerializeField]
    [Range(0f, 0.15f)]
    [Tooltip(
        "Keeps AR depth queries away from viewport edges, where ARCore may not have " +
        "enough neighboring depth samples. Edge detections are clamped inward rather " +
        "than discarded."
    )]
    private float depthViewportMargin = 0.03f;

    [Header("Output Events")]
    public TextureEvent OnSegmentationTexture = new();
    public AspectChangeEvent OnSegmentationAspectChange = new();

    private Yolo11Seg inference;
    private Texture latestTexture;
    private TMPro.TMP_Text[] detectionBoxes;
    private Image[] detectionBoxOutlines;
    private Texture previousSegmentationTexture;
    private readonly StringBuilder stringBuilder = new();
    private Coroutine scanCoroutine;
    private int scanRequestId;
    
    private struct PendingAnchor
    {
        public Vector2 viewportPoint;
        public float depthMeters;
        public Vector3 worldPosition;

        public int detectionIndex;
        public string className;
        public float confidence;
    }

    /// <summary>
    /// Registers the camera texture listener, loads the ONNX model,
    /// initializes detection UI, and starts the camera source.
    /// </summary>
    private async void Start()
    {
        RegisterTextureListener();

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Lifecycle,
            "Loading YOLO11 segmentation model.",
            this
        );

        byte[] onnxFile = model != null
            ? model.bytes
            : await modelFile.Load(destroyCancellationToken);

        inference = new Yolo11Seg(onnxFile, options);

        InitializeDetectionBoxes();

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Lifecycle,
            "YOLO11 segmentation model initialized.",
            this
        );

        StartCoroutine(StartCamera());
    }

    /// <summary>
    /// Removes event listeners and disposes the ONNX inference object.
    /// </summary>
    private void OnDestroy()
    {
        if (TryGetComponent(out VirtualTextureSource source))
            source.OnTexture.RemoveListener(OnTexture);

        inference?.Dispose();
        previousSegmentationTexture = null;
    }

    /// <summary>
    /// Registers this runner to receive textures from the attached VirtualTextureSource.
    /// </summary>
    private void RegisterTextureListener()
    {
        if (!TryGetComponent(out VirtualTextureSource source))
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Lifecycle,
                "VirtualTextureSource component is missing.",
                this
            );
            return;
        }

        source.OnTexture.AddListener(OnTexture);
    }

    /// <summary>
    /// Instantiates and caches detection-box UI elements.
    /// </summary>
    private void InitializeDetectionBoxes()
    {
        if (detectionBoxPrefab == null || detectionContainer == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Lifecycle,
                "Detection box prefab or detection container is not assigned.",
                this
            );
            return;
        }

        detectionBoxes = new TMPro.TMP_Text[maxDetections];
        detectionBoxOutlines = new Image[maxDetections];

        for (int i = 0; i < maxDetections; i++)
        {
            TMPro.TMP_Text box =
                Instantiate(detectionBoxPrefab, detectionContainer);

            box.name = $"Detection {i}";
            box.gameObject.SetActive(false);

            detectionBoxes[i] = box;

            if (box.transform.childCount > 0)
                detectionBoxOutlines[i] =
                    box.transform.GetChild(0).GetComponent<Image>();
        }
    }

    /// <summary>
    /// Requests Android camera permission when needed and enables the camera texture source.
    /// </summary>
    /// <returns>Coroutine enumerator used by Unity while camera permission is pending.</returns>
    private IEnumerator StartCamera()
    {
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);

            while (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                yield return null;
        }
#endif

        if (TryGetComponent(out VirtualTextureSource source))
        {
            if (!source.enabled)
                source.enabled = true;

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Lifecycle,
                "VirtualTextureSource enabled.",
                this
            );
        }

        yield break;
    }

    /// <summary>
    /// Stores the most recent camera texture supplied by VirtualTextureSource.
    /// </summary>
    /// <param name="texture">Latest camera texture.</param>
    public void OnTexture(Texture texture)
    {
        latestTexture = texture;
    }

    /// <summary>
    /// Runs one scan using the most recently received camera texture.
    /// Existing detection anchors are cleared before the new scan.
    /// </summary>
    public void Scan()
    {
        if (inference == null)
        {
            ObjectDetectionDebug.LogWarning(
                ObjectDetectionLogCategory.Yolo,
                "Scan skipped because YOLO inference is not initialized.",
                this
            );
            return;
        }

        if (latestTexture == null)
        {
            ObjectDetectionDebug.LogWarning(
                ObjectDetectionLogCategory.Yolo,
                "Scan skipped because no camera texture has been received.",
                this
            );
            return;
        }

        if (depthSampler == null || detectionAnchorManager == null)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Yolo,
                "DepthSampler or DetectionAnchorManager is not assigned.",
                this
            );
            return;
        }

        scanRequestId++;

        if (scanCoroutine != null)
            StopCoroutine(scanCoroutine);

        scanCoroutine = StartCoroutine(ScanRoutine(scanRequestId));
    }

    private IEnumerator ScanRoutine(int requestId)
    {
        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Yolo,
            $"Scan started | texture={latestTexture.width}x{latestTexture.height}",
            this
        );

        float scanStartTime = Time.unscaledTime;

        if (scanAnimationOverlay != null)
            scanAnimationOverlay.ShowLooping();

        // Let the overlay render before synchronous inference blocks
        // the Unity main thread.
        yield return null;

        List<PendingAnchor> pendingAnchors = RunInference(latestTexture);

        if (requestId != scanRequestId)
            yield break;

        // YOLO is usually much faster than the scan animation.
        // Keep the animation visible long enough to communicate scanning.
        float elapsed = Time.unscaledTime - scanStartTime;
        float remainingOverlayTime = minimumScanOverlayTime - elapsed;

        if (remainingOverlayTime > 0f)
            yield return new WaitForSecondsRealtime(remainingOverlayTime);

        if (requestId != scanRequestId)
            yield break;

        detectionAnchorManager.ClearAnchors();

        if (scanAnimationOverlay != null)
            scanAnimationOverlay.StopAndHide();

        // Allow the normal AR view to render before creating visuals.
        yield return null;

        if (requestId != scanRequestId)
            yield break;

        foreach (PendingAnchor result in pendingAnchors)
        {
            detectionAnchorManager.CreateAnchor(
                result.viewportPoint,
                result.depthMeters,
                result.worldPosition,
                result.detectionIndex,
                result.className,
                result.confidence
            );
        }

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Yolo,
            $"Scan complete | detections={inference.Detections.Length} " +
            $"anchors={pendingAnchors.Count}",
            this
        );

        scanCoroutine = null;
    }

    /// <summary>
    /// Executes synchronous YOLO inference and dispatches the resulting UI,
    /// segmentation, depth, and anchor updates.
    /// </summary>
    /// <param name="texture">Camera texture to process.</param>
    private List<PendingAnchor> RunInference(Texture texture)
    {
        inference.Run(texture);

        ReadOnlySpan<Yolo11Seg.Detection> detections = inference.Detections;

        UpdateDetectionBoxes(detections);
        UpdateSegmentationOutput();

        return CollectAnchorResults(detections);
    }

    /// <summary>
    /// Converts a YOLO mask-center coordinate into a Unity viewport coordinate.
    /// YOLO uses a top-left origin while Unity viewport coordinates use a bottom-left origin.
    /// </summary>
    /// <param name="maskCenter">Normalized YOLO mask-center coordinate.</param>
    /// <returns>Normalized Unity viewport coordinate.</returns>
    private Vector2 MaskCenterToViewport(Vector2 maskCenter)
    {
        Vector2 unityPoint = new(
            maskCenter.x,
            1f - maskCenter.y
        );

        return inference.InputToViewportMatrix
            .MultiplyPoint3x4(unityPoint);
    }

    /// <summary>
    /// Updates the on-screen bounding-box UI for the current detections.
    /// </summary>
    /// <param name="detections">YOLO detections produced by the current inference pass.</param>
    private void UpdateDetectionBoxes(
        ReadOnlySpan<Yolo11Seg.Detection> detections)
    {
        if (detectionBoxes == null || detectionContainer == null)
            return;

        var labels = inference.labelNames;
        Vector2 viewportSize = detectionContainer.rect.size;

        int visibleCount = Math.Min(detections.Length, maxDetections);
        int i;

        for (i = 0; i < visibleCount; i++)
        {
            Yolo11Seg.Detection detection = detections[i];
            Color color = detection.GetColor();
            TMPro.TMP_Text box = detectionBoxes[i];

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Detections,
                $"Detection[{i}] | classId={detection.label} " +
                $"class={labels[detection.label]} confidence={detection.probability:F3} " +
                $"rect={detection.rect}",
                this
            );

            box.gameObject.SetActive(true);

            stringBuilder.Clear();
            stringBuilder.Append(labels[detection.label]);
            stringBuilder.Append(": ");
            stringBuilder.Append((int)(detection.probability * 100));
            stringBuilder.Append('%');

            box.SetText(stringBuilder);
            box.color = color;

            RectTransform rectTransform = box.rectTransform;
            Rect rect = inference.ConvertToViewport(detection.rect);

            rectTransform.anchoredPosition = rect.min * viewportSize;
            rectTransform.sizeDelta = rect.size * viewportSize;

            if (detectionBoxOutlines[i] != null)
                detectionBoxOutlines[i].color = color;
        }

        for (; i < maxDetections; i++)
            detectionBoxes[i].gameObject.SetActive(false);
    }

    /// <summary>
    /// Emits the current segmentation texture and aspect ratio when the output texture changes.
    /// Optional GPU-readback diagnostics are performed only when Segmentation logging is enabled.
    /// </summary>
    private void UpdateSegmentationOutput()
    {
        Texture segmentationTexture = inference.SegmentationTexture;

        ObjectDetectionDebug.Log(
            ObjectDetectionLogCategory.Segmentation,
            $"Segmentation texture | {segmentationTexture.width}x{segmentationTexture.height} " +
            $"format={segmentationTexture.graphicsFormat} dimension={segmentationTexture.dimension}",
            this
        );

        InspectSegmentationTexture(segmentationTexture);

        if (previousSegmentationTexture == segmentationTexture)
            return;

        OnSegmentationTexture.Invoke(segmentationTexture);

        OnSegmentationAspectChange.Invoke(
            (float)segmentationTexture.width / segmentationTexture.height
        );

        previousSegmentationTexture = segmentationTexture;
    }

    /// <summary>
    /// Reads segmentation pixels back from the GPU for diagnostics.
    /// The readback is completely skipped unless Segmentation logging is enabled.
    /// </summary>
    /// <param name="texture">Segmentation texture to inspect.</param>
    private void InspectSegmentationTexture(Texture texture)
    {
        if (!ObjectDetectionDebug.IsCategoryEnabled(
                ObjectDetectionLogCategory.Segmentation))
        {
            return;
        }

        if (texture is not RenderTexture renderTexture)
        {
            ObjectDetectionDebug.LogError(
                ObjectDetectionLogCategory.Segmentation,
                $"Segmentation texture is not a RenderTexture: {texture.GetType()}",
                this
            );
            return;
        }

        AsyncGPUReadback.Request(renderTexture, 0, request =>
        {
            if (request.hasError)
            {
                ObjectDetectionDebug.LogError(
                    ObjectDetectionLogCategory.Segmentation,
                    "Segmentation texture GPU readback failed.",
                    this
                );
                return;
            }

            var data = request.GetData<Color32>();

            int nonTransparent = 0;
            int nonBlack = 0;

            byte minR = 255;
            byte minG = 255;
            byte minB = 255;
            byte minA = 255;

            byte maxR = 0;
            byte maxG = 0;
            byte maxB = 0;
            byte maxA = 0;

            foreach (Color32 pixel in data)
            {
                if (pixel.a > 5)
                    nonTransparent++;

                if (pixel.r > 5 || pixel.g > 5 || pixel.b > 5)
                    nonBlack++;

                minR = (byte)Mathf.Min(minR, pixel.r);
                minG = (byte)Mathf.Min(minG, pixel.g);
                minB = (byte)Mathf.Min(minB, pixel.b);
                minA = (byte)Mathf.Min(minA, pixel.a);

                maxR = (byte)Mathf.Max(maxR, pixel.r);
                maxG = (byte)Mathf.Max(maxG, pixel.g);
                maxB = (byte)Mathf.Max(maxB, pixel.b);
                maxA = (byte)Mathf.Max(maxA, pixel.a);
            }

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Segmentation,
                $"GPU pixels={data.Length} nonTransparent={nonTransparent} " +
                $"nonBlack={nonBlack} R={minR}-{maxR} G={minG}-{maxG} " +
                $"B={minB}-{maxB} A={minA}-{maxA}",
                this
            );
        });
    }

    /// <summary>
    /// ...
    /// </summary>
    /// <param...</param>
    private List<PendingAnchor> CollectAnchorResults(
    ReadOnlySpan<Yolo11Seg.Detection> detections)
    {
        var results = new List<PendingAnchor>();
        var labels = inference.labelNames;

        for (int i = 0; i < detections.Length; i++)
        {
            Yolo11Seg.Detection detection = detections[i];

            string className = labels[detection.label];
            float confidence = detection.probability;

            if (!detection.hasMaskCenter)
            {
                ObjectDetectionDebug.Log(
                    ObjectDetectionLogCategory.Depth,
                    $"Detection[{i}] {className} has no mask center.",
                    this
                );
                continue;
            }

            Vector2 maskCenter = detection.maskCenter;
            Vector2 detectedViewport = MaskCenterToViewport(maskCenter);
            Vector2 depthViewport = ClampViewportForDepth(detectedViewport);

            if (depthViewport != detectedViewport)
            {
                ObjectDetectionDebug.Log(
                    ObjectDetectionLogCategory.Depth,
                    $"Detection[{i}] | class={className} depth query clamped " +
                    $"from viewport={detectedViewport} to viewport={depthViewport} " +
                    $"using margin={depthViewportMargin:F3}.",
                    this
                );
            }

            bool gotDepth = depthSampler.TryGetDepth(
                depthViewport,
                out float depth,
                out Vector3 worldPosition
            );

            if (!gotDepth)
            {
                ObjectDetectionDebug.Log(
                    ObjectDetectionLogCategory.Depth,
                    $"Detection[{i}] | class={className} " +
                    $"confidence={confidence:F3} " +
                    $"maskCenter={maskCenter} detectedViewport={detectedViewport} " +
                    $"depthViewport={depthViewport} no depth hit.",
                    this
                );
                continue;
            }

            ObjectDetectionDebug.Log(
                ObjectDetectionLogCategory.Depth,
                $"Detection[{i}] | class={className} " +
                $"confidence={confidence:F3} maskCenter={maskCenter} " +
                $"detectedViewport={detectedViewport} depthViewport={depthViewport} " +
                $"depth={depth:F3}m world={worldPosition}",
                this
            );

            results.Add(new PendingAnchor
            {
                viewportPoint = detectedViewport,
                depthMeters = depth,
                worldPosition = worldPosition,

                detectionIndex = i,
                className = className,
                confidence = confidence
            });
        }

        return results;
    }

    /// <summary>
    /// Clamps a normalized viewport coordinate inward so ARCore has enough
    /// neighboring depth samples to evaluate points near the screen border.
    /// </summary>
    /// <param name="viewportPoint">Original normalized Unity viewport coordinate.</param>
    /// <returns>
    /// A viewport coordinate constrained to the configured depth-sampling margin.
    /// </returns>
    private Vector2 ClampViewportForDepth(Vector2 viewportPoint)
    {
        float margin = Mathf.Clamp(depthViewportMargin, 0f, 0.49f);

        return new Vector2(
            Mathf.Clamp(viewportPoint.x, margin, 1f - margin),
            Mathf.Clamp(viewportPoint.y, margin, 1f - margin)
        );
    }
}
