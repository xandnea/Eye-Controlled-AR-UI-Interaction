using System.Collections.Generic;
using Unity.Sentis;
using UnityEngine;

public class YOLOv8SentisDetector : MonoBehaviour
{
    [Header("Model Settings")]
    public ModelAsset yoloModelAsset;
    private Model runtimeModel;
    private Worker worker;

    const int targetWidth = 640;
    const int targetHeight = 640;

    private TextureTransform textureTransform;
    private Tensor<float> inputTensor;

    [System.Serializable]
    public struct DetectedObject
    {
        public string className;
        public Rect boundingBox;
        public float confidence;
    }

    void Start()
    {
        if (yoloModelAsset == null)
        {
            Debug.LogError("[YOLOv8SentisDetector] FATAL: yoloModelAsset is not assigned in the Inspector!");
            return;
        }

        Debug.Log("[YOLOv8SentisDetector] Loading YOLO model asset...");
        runtimeModel = ModelLoader.Load(yoloModelAsset);

        Debug.Log("[YOLOv8SentisDetector] Initializing Sentis Worker with GPUCompute backend...");
        worker = new Worker(runtimeModel, BackendType.GPUCompute);

        textureTransform = new TextureTransform();
        textureTransform.SetDimensions(targetWidth, targetHeight, 3);
        inputTensor = new Tensor<float>(new TensorShape(1, 3, targetHeight, targetWidth));

        Debug.Log("[YOLOv8SentisDetector] Initialization complete.");
    }

    public List<DetectedObject> RunScan(Texture camTexture)
    {
        if (worker == null || camTexture == null)
        {
            Debug.LogWarning("[YOLOv8SentisDetector] RunScan aborted: Worker or camTexture is null.");
            return new List<DetectedObject>();
        }

        Debug.Log($"[YOLOv8SentisDetector] Converting texture ({camTexture.width}x{camTexture.height}) to Tensor and scheduling worker...");
        TextureConverter.ToTensor(camTexture, inputTensor, textureTransform);
        worker.Schedule(inputTensor);

        Debug.Log("[YOLOv8SentisDetector] Peeking and downloading output tensor from GPU...");
        using Tensor<float> outputTensor = worker.PeekOutput() as Tensor<float>;
        outputTensor.CompleteAllPendingOperations();
        float[] outputData = outputTensor.DownloadToArray();

        Debug.Log($"[YOLOv8SentisDetector] Tensor downloaded successfully. Length: {outputData.Length}. Parsing YOLO output...");
        var rawDetections = ParseYOLOOutput(outputData, camTexture.width, camTexture.height);

        Debug.Log($"[YOLOv8SentisDetector] Passing {rawDetections.Count} valid detections to Non-Max Suppression (NMS)...");
        var finalDetections = ApplyNMS(rawDetections, 0.45f); // 45% Overlap limit

        Debug.Log($"[YOLOv8SentisDetector] Scan complete. Final objects after NMS: {finalDetections.Count}");
        return finalDetections;
    }

    List<DetectedObject> ParseYOLOOutput(float[] data, int originalWidth, int originalHeight)
    {
        List<DetectedObject> detectedObjects = new List<DetectedObject>();
        int predictions = 8400;

        int ignoredLowConfidenceCount = 0;
        float absoluteHighestScoreFound = 0f;
        string bestCandidateName = "None";

        for (int i = 0; i < predictions; i++)
        {
            float maxConfidence = 0f;
            int bestClassIndex = -1;

            for (int classIdx = 0; classIdx < 80; classIdx++)
            {
                float classConfidence = data[(4 + classIdx) * predictions + i];
                if (classConfidence > maxConfidence)
                {
                    maxConfidence = classConfidence;
                    bestClassIndex = classIdx;
                }
            }

            // Track global max for diagnostic insight
            if (maxConfidence > absoluteHighestScoreFound)
            {
                absoluteHighestScoreFound = maxConfidence;
                if (bestClassIndex >= 0)
                    bestCandidateName = GetCOCOClassName(bestClassIndex);
            }

            // Threshold check (0.25f)
            if (maxConfidence > 0.25f)
            {
                float x = data[0 * predictions + i];
                float y = data[1 * predictions + i];
                float w = data[2 * predictions + i];
                float h = data[3 * predictions + i];

                float scaleX = originalWidth / (float)targetWidth;
                float scaleY = originalHeight / (float)targetHeight;

                float xMin = (x - w / 2f) * scaleX;
                float yMin = (y - h / 2f) * scaleY;

                string className = GetCOCOClassName(bestClassIndex);
                detectedObjects.Add(new DetectedObject
                {
                    className = className,
                    boundingBox = new Rect(xMin, yMin, w * scaleX, h * scaleY),
                    confidence = maxConfidence
                });
            }
            else if (maxConfidence > 0.10f)
            {
                // LOG LOW-CONFIDENCE OBJECTS: Captures things the camera "sees" but aren't confident enough to render boxes for
                ignoredLowConfidenceCount++;
                string lowConfClassName = GetCOCOClassName(bestClassIndex);
                Debug.Log($"[YOLO-Sub-Threshold] Saw object '{lowConfClassName}' with low confidence: {maxConfidence * 100f:F1}% (Ignored by threshold)");
            }
        }

        Debug.Log($"[YOLOv8SentisDetector] Parsing summary: Found {detectedObjects.Count} valid objects (>25%). Ignored {ignoredLowConfidenceCount} sub-threshold candidates (10%-25%). Absolute peak confidence spotted across grid: {absoluteHighestScoreFound * 100f:F1}% ({bestCandidateName}).");
        return detectedObjects;
    }

    private List<DetectedObject> ApplyNMS(List<DetectedObject> boxes, float iouThreshold)
    {
        boxes.Sort((a, b) => b.confidence.CompareTo(a.confidence));
        List<DetectedObject> result = new List<DetectedObject>();
        bool[] suppressed = new bool[boxes.Count];

        for (int i = 0; i < boxes.Count; i++)
        {
            if (suppressed[i]) continue;
            result.Add(boxes[i]);

            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (suppressed[j]) continue;

                if (boxes[i].className == boxes[j].className)
                {
                    float iou = CalculateIoU(boxes[i].boundingBox, boxes[j].boundingBox);
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                        Debug.Log($"[NMS] Suppressed overlapping box for '{boxes[j].className}' with IoU: {iou:F2}");
                    }
                }
            }
        }
        return result;
    }

    private float CalculateIoU(Rect a, Rect b)
    {
        float x1 = Mathf.Max(a.xMin, b.xMin);
        float y1 = Mathf.Max(a.yMin, b.yMin);
        float x2 = Mathf.Min(a.xMax, b.xMax);
        float y2 = Mathf.Min(a.yMax, b.yMax);

        float intersectionArea = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
        float unionArea = (a.width * a.height) + (b.width * b.height) - intersectionArea;

        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }

    string GetCOCOClassName(int classId)
    {
        string[] cocoClasses = new string[] {
            "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat", "traffic light",
            "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat", "dog", "horse", "sheep", "cow",
            "elephant", "bear", "zebra", "giraffe", "backpack", "umbrella", "handbag", "tie", "suitcase", "frisbee",
            "skis", "snowboard", "sports ball", "kite", "baseball bat", "baseball glove", "skateboard", "surfboard",
            "tennis racket", "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
            "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair", "couch",
            "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse", "remote", "keyboard", "cell phone",
            "microwave", "oven", "toaster", "sink", "refrigerator", "book", "clock", "vase", "scissors", "teddy bear",
            "hair drier", "toothbrush"
        };
        if (classId >= 0 && classId < cocoClasses.Length) return cocoClasses[classId];
        return "Unknown";
    }

    void OnDestroy()
    {
        Debug.Log("[YOLOv8SentisDetector] Disposing inputTensor and worker resources.");
        inputTensor?.Dispose();
        worker?.Dispose();
    }
}