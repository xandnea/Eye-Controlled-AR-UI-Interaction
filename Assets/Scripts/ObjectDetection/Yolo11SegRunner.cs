using System;
using System.Text;
using Microsoft.ML.OnnxRuntime.Unity;
using Microsoft.ML.OnnxRuntime.Examples;
using TextureSource;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Events;
using UnityEngine.Android;
using UnityEngine.UI;
using System.Threading;
using System.Collections;

[RequireComponent(typeof(VirtualTextureSource))]
public class Yolo11SegRunner : MonoBehaviour
{
    [Serializable]
    public class TextureEvent : UnityEvent<Texture> { }

    [Serializable]
    public class AspectChangeEvent : UnityEvent<float> { }

    [Header("AR Settings")]
    [SerializeField]
    private DepthSampler depthSampler;

    [SerializeField]
    private DetectionAnchorManager detectionAnchorManager;

    [Header("Detection Model")]
    [SerializeField]
    private OrtAsset model;

    [SerializeField]
    private RemoteFile modelFile = new("https://github.com/asus4/onnxruntime-unity-examples/releases/download/v0.2.7/yolo11n-seg-dynamic.onnx");

    [SerializeField]
    private Yolo11Seg.Options options;

    [SerializeField]
    private bool runBackground = false;

    [Header("Visualization Options")]
    [SerializeField]
    private TMPro.TMP_Text detectionBoxPrefab;

    [SerializeField]
    private RectTransform detectionContainer;

    [SerializeField]
    private int maxDetections = 20;

    public TextureEvent OnSegmentationTexture = new();
    public AspectChangeEvent OnSegmentationAspectChange = new();

    private Yolo11Seg inference;
    private Texture latestTexture;
    private TMPro.TMP_Text[] detectionBoxes;
    private Image[] detectionBoxOutline;
    private Texture prevSegmentationTexture;
    private readonly StringBuilder sb = new();

    private async void Start()
    {
        // 1. Hook up texture event listener early
        if (TryGetComponent(out VirtualTextureSource source))
        {
            source.OnTexture.AddListener(OnTexture);
        }

        // 2. Load ONNX model
        byte[] onnxFile = model != null
            ? model.bytes
            : await modelFile.Load(destroyCancellationToken);

        inference = new Yolo11Seg(onnxFile, options);

        // 3. Setup detection bounding box UI elements
        detectionBoxes = new TMPro.TMP_Text[maxDetections];
        detectionBoxOutline = new Image[maxDetections];
        for (int i = 0; i < maxDetections; i++)
        {
            var box = Instantiate(detectionBoxPrefab, detectionContainer);
            box.name = $"Detection {i}";
            box.gameObject.SetActive(false);
            detectionBoxes[i] = box;
            detectionBoxOutline[i] = box.transform.GetChild(0).GetComponent<Image>();
        }

        // 4. Start camera only after model is loaded
        StartCoroutine(StartCamera());
    }

    private void OnDestroy()
    {
        if (TryGetComponent(out VirtualTextureSource source))
        {
            source.OnTexture.RemoveListener(OnTexture);
        }

        inference?.Dispose();
        prevSegmentationTexture = null;
    }

    private IEnumerator StartCamera()
    {
#if UNITY_ANDROID
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);

            while (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                yield return null;
            }
        }
#endif

        // Enable camera ONCE when everything is ready
        if (TryGetComponent(out VirtualTextureSource source))
        {
            if (!source.enabled)
            {
                source.enabled = true;
            }
        }

        yield break;
    }

    public void OnTexture(Texture texture)
    {
        latestTexture = texture;
    }

    private Vector2 MaskCenterToViewport(Vector2 maskCenter)
    {
        // YOLO/image coordinates use top-left origin.
        // Unity viewport uses bottom-left origin.

        Vector2 unityPoint = new Vector2(
            maskCenter.x,
            1f - maskCenter.y
        );

        return inference.InputToViewportMatrix
            .MultiplyPoint3x4(unityPoint);
    }

    public void Scan()
    {
        if (inference == null)
        {
            Debug.LogWarning("Scan failed: YOLO inference is not initialized yet.");
            return;
        }

        if (latestTexture == null)
        {
            Debug.LogWarning("Scan failed: no camera texture received yet.");
            return;
        }

        Debug.Log("========== SCAN PRESSED ==========");

        detectionAnchorManager.ClearAnchors();

        Debug.Log(
            $"Running YOLO on texture: " +
            $"{latestTexture.width}x{latestTexture.height}"
        );

        Run(latestTexture);

        Debug.Log(
            $"SCAN COMPLETE - Detections: {inference.Detections.Length}"
        );
    }

    private void Run(Texture texture)
    {
        inference.Run(texture);

        UpdateDetectionBox(inference.Detections);

        var segTex = inference.SegmentationTexture;
        Debug.Log($"SEG TEXTURE: {segTex.width}x{segTex.height} format={segTex.graphicsFormat} dimension={segTex.dimension}");

        InspectSegmentationTexture(segTex);

        DetectDepth(inference.Detections);

        if (prevSegmentationTexture != segTex)
        {
            OnSegmentationTexture.Invoke(segTex);
            OnSegmentationAspectChange.Invoke((float)segTex.width / segTex.height);
            prevSegmentationTexture = segTex;
        }
    }

    private async Awaitable RunAsync(Texture texture, CancellationToken cancellationToken)
    {
        try
        {
            await inference.RunAsync(texture, cancellationToken);
        }
        catch (OperationCanceledException e)
        {
            Debug.LogWarning(e);
            return;
        }
        await Awaitable.MainThreadAsync();

        UpdateDetectionBox(inference.Detections);

        var segTex = inference.SegmentationTexture;
        if (prevSegmentationTexture != segTex)
        {
            OnSegmentationTexture.Invoke(segTex);
            OnSegmentationAspectChange.Invoke((float)segTex.width / segTex.height);
            prevSegmentationTexture = segTex;
        }
    }

    private void UpdateDetectionBox(ReadOnlySpan<Yolo11Seg.Detection> detections)
    {
        var labels = inference.labelNames;
        Vector2 viewportSize = detectionContainer.rect.size;

        int i;
        int length = Math.Min(detections.Length, maxDetections);
        for (i = 0; i < length; i++)
        {
            var detection = detections[i];
            var color = detection.GetColor();
            var box = detectionBoxes[i];

            Debug.Log($"YOLO Detection index={i} classID={detection.label} className={labels[detection.label]} confidence={detection.probability:F3} rect={detection.rect}");
            box.gameObject.SetActive(true);

            sb.Clear();
            sb.Append(labels[detection.label]);
            sb.Append(": ");
            sb.Append((int)(detection.probability * 100));
            sb.Append('%');
            box.SetText(sb);
            box.color = color;

            RectTransform rt = box.rectTransform;
            Rect rect = inference.ConvertToViewport(detection.rect);
            rt.anchoredPosition = rect.min * viewportSize;
            rt.sizeDelta = rect.size * viewportSize;

            detectionBoxOutline[i].color = color;
        }

        for (; i < maxDetections; i++)
        {
            detectionBoxes[i].gameObject.SetActive(false);
        }
    }

    private void InspectSegmentationTexture(Texture texture)
    {
        if (texture is not RenderTexture rt)
        {
            Debug.LogError($"Segmentation texture is not a RenderTexture: {texture.GetType()}");
            return;
        }

        AsyncGPUReadback.Request(rt, 0, request =>
        {
            if (request.hasError)
            {
                Debug.LogError("Segmentation texture GPU readback failed.");
                return;
            }

            var data = request.GetData<Color32>();

            int nonTransparent = 0;
            int nonBlack = 0;

            byte minR = 255, minG = 255, minB = 255, minA = 255;
            byte maxR = 0, maxG = 0, maxB = 0, maxA = 0;

            foreach (var p in data)
            {
                if (p.a > 5) nonTransparent++;
                if (p.r > 5 || p.g > 5 || p.b > 5) nonBlack++;

                minR = (byte)Mathf.Min(minR, p.r);
                minG = (byte)Mathf.Min(minG, p.g);
                minB = (byte)Mathf.Min(minB, p.b);
                minA = (byte)Mathf.Min(minA, p.a);

                maxR = (byte)Mathf.Max(maxR, p.r);
                maxG = (byte)Mathf.Max(maxG, p.g);
                maxB = (byte)Mathf.Max(maxB, p.b);
                maxA = (byte)Mathf.Max(maxA, p.a);
            }

            Debug.Log($"SEG GPU DATA: pixels={data.Length}, nonTransparent={nonTransparent}, nonBlack={nonBlack}, R={minR}-{maxR}, G={minG}-{maxG}, B={minB}-{maxB}, A={minA}-{maxA}");
        });
    }

    private void DetectDepth(
    ReadOnlySpan<Yolo11Seg.Detection> detections)
    {
        var labels = inference.labelNames;

        Debug.Log("========== DETECTION DEPTH ==========");

        for (int i = 0; i < detections.Length; i++)
        {
            var detection = detections[i];

            if (!detection.hasMaskCenter)
            {
                Debug.Log(
                    $"DEPTH [{i}] " +
                    $"{labels[detection.label]} " +
                    "NO MASK CENTER"
                );

                continue;
            }

            Vector2 maskCenter = detection.maskCenter;

            Vector2 viewport = MaskCenterToViewport(maskCenter);

            bool gotDepth = depthSampler.TryGetDepth(
                viewport,
                out float depth,
                out Vector3 worldPosition
            );

            if (!gotDepth)
            {
                Debug.Log(
                    $"DEPTH [{i}] " +
                    $"class={labels[detection.label]} " +
                    $"confidence={detection.probability:F3} " +
                    $"maskCenter={maskCenter} " +
                    $"viewport={viewport} " +
                    "NO DEPTH HIT"
                );

                continue;
            }

            Debug.Log(
                $"DEPTH [{i}] " +
                $"class={labels[detection.label]} " +
                $"confidence={detection.probability:F3} " +
                $"maskCenter={maskCenter} " +
                $"viewport={viewport} " +
                $"depth={depth:F3}m " +
                $"world={worldPosition}"
            );

            detectionAnchorManager.CreateAnchor(viewport, depth, worldPosition, i, labels[detection.label]);
        }

        Debug.Log("====================================");
    }
}