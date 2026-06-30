using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;

public class GazeVisualizer : MonoBehaviour
{
    [Header("References")]
    public FaceLandmarkerRunner faceLandmarkerRunner;

    [Header("Settings")]
    public float rayLength = 5f;
    public Color normalColor = Color.blue; // Swapped to blue for testing the normal
    public Color gazeColor = Color.red;
    [Range(0f, 1f)] public float headAlpha = 0.3f;

    private Vector2 _lastGazeVector = Vector3.forward;
    private FaceLandmarkerResult _lastResult;
    private bool _hasData = false;

    // Calibrated Bounds
    private float cMinLeftX, cMaxLeftX, cMinLeftY, cMaxLeftY;
    private float cMinRightX, cMaxRightX, cMinRightY, cMaxRightY;

    public void SetCalibrationBounds(float minLX, float maxLX, float minLY, float maxLY, float minRX, float maxRX, float minRY, float maxRY)
    {
        cMinLeftX = minLX; cMaxLeftX = maxLX; cMinLeftY = minLY; cMaxLeftY = maxLY;
        cMinRightX = minRX; cMaxRightX = maxRX; cMinRightY = minRY; cMaxRightY = maxRY;
        Debug.Log($"Calibration Bounds Set:\nLeft Eye: X({cMinLeftX}, {cMaxLeftX}), Y({cMinLeftY}, {cMaxLeftY})\nRight Eye: X({cMinRightX}, {cMaxRightX}), Y({cMinRightY}, {cMaxRightY})");
    }

    private void OnEnable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected += ProcessGaze;
    }

    private void OnDisable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected -= ProcessGaze;
    }

    private void Update()
    {
        if (!_hasData || _lastResult.faceLandmarks == null || _lastResult.faceLandmarks.Count == 0) return;

        var landmarks = _lastResult.faceLandmarks[0].landmarks;

        // Map nose to Viewport for origin
        var landmark = landmarks[168];

        // Get Symmetrical Face Normal
        Vector3 faceNormal = GetFaceNormal(_lastResult);

        // Canvas is "Screen Space - Camera" with a Plane Distance of 10:
        float canvasPlaneDistance = 10f;

        // Map nose to Viewport. 
        Vector3 viewportPoint = new Vector3(landmark.x, 1f - landmark.y, canvasPlaneDistance*10);

        // Convert directly to World Space.
        Vector3 origin = Camera.main.ViewportToWorldPoint(viewportPoint);

        Vector3 eyeDirectionLocal = new Vector3(_lastGazeVector.x, _lastGazeVector.y, 1f).normalized;
        Quaternion faceRotation = Quaternion.FromToRotation(Vector3.forward, faceNormal);
        Vector3 finalGaze = faceRotation * eyeDirectionLocal;

        Debug.DrawRay(origin, faceNormal * rayLength, Color.blue);
        Debug.DrawRay(origin, finalGaze * rayLength, Color.red);
    }

    private void ProcessGaze(FaceLandmarkerResult result)
    {
        _lastResult = result;
        _lastGazeVector = CalculateGaze(result);
        _hasData = true;
    }

    private Vector2 CalculateGaze(FaceLandmarkerResult result)
    {
        if (result.faceLandmarks == null || result.faceLandmarks.Count == 0) return Vector2.zero;

        var landmarks = result.faceLandmarks[0].landmarks;
        if (landmarks.Count < 474) return Vector2.zero;

        Vector2 rightIris = new Vector2(landmarks[468].x, landmarks[468].y);
        Vector2 leftIris = new Vector2(landmarks[473].x, landmarks[473].y);

        // Original Bounding Box constraints
        float rEyeLookingR = landmarks[33].x;
        float rEyeLookingL = landmarks[133].x;
        float lEyeLookingR = landmarks[362].x;
        float lEyeLookingL = landmarks[263].x;

        float rEyeLookingU = landmarks[28].y;
        float rEyeLookingD = landmarks[230].y;
        float lEyeLookingU = landmarks[258].y;
        float lEyeLookingD = landmarks[450].y;

        // 1. Calculate the RAW ratio exactly as your GazeCalibration.cs does
        float rawRightX = (rightIris.x - rEyeLookingR) / (rEyeLookingL - rEyeLookingR);
        float rawRightY = (rightIris.y - rEyeLookingU) / (rEyeLookingD - rEyeLookingU);
        float rawLeftX = (leftIris.x - lEyeLookingR) / (lEyeLookingL - lEyeLookingR);
        float rawLeftY = (leftIris.y - lEyeLookingU) / (lEyeLookingD - lEyeLookingU);

        // 2. Map directly against your calibrated bounds (Completely bypasses the need for an explicit eye center)
        float rightX = MapToRange(rawRightX, cMinRightX, cMaxRightX);
        float rightY = MapToRange(rawRightY, cMinRightY, cMaxRightY);
        float leftX = MapToRange(rawLeftX, cMinLeftX, cMaxLeftX);
        float leftY = MapToRange(rawLeftY, cMinLeftY, cMaxLeftY);

        // Average the processed eyes
        float avgX = (rightX + leftX) / 2f;
        float avgY = (rightY + leftY) / 2f;
        Vector2 eyeGaze = new Vector2(avgX, avgY);

        Vector3 faceNormal = GetFaceNormal(_lastResult);
        Vector2 headOrientation = new Vector2(faceNormal.x, faceNormal.y);

        // Final gaze returns the Lerped Vector2
        return Vector2.Lerp(eyeGaze, headOrientation, headAlpha);
    }

    // This single method replaces GetIrisInEyeSocketRatio
    private float MapToRange(float rawValue, float calibMin, float calibMax)
    {
        // InverseLerp turns the calibrated min/max into a clean 0 to 1 scale.
        // If rawValue is exactly halfway between your calibrated bounds, it outputs 0.5.
        float normalized01 = Mathf.InverseLerp(calibMin, calibMax, rawValue);

        // Stretch that 0 to 1 value to our -1 to 1 gaze vector plane
        return Mathf.Lerp(-1f, 1f, normalized01);
    }

    private Vector3 GetFaceNormal(FaceLandmarkerResult result)
    {
        var landmarks = result.faceLandmarks[0].landmarks;

        // Use perfectly symmetrical landmarks to define the facial plane
        Vector3 rightSide = new Vector3(landmarks[454].x, 1f - landmarks[454].y, landmarks[454].z);
        Vector3 leftSide = new Vector3(landmarks[234].x, 1f - landmarks[234].y, landmarks[234].z);
        Vector3 topHead = new Vector3(landmarks[10].x, 1f - landmarks[10].y, landmarks[10].z);
        Vector3 chin = new Vector3(landmarks[152].x, 1f - landmarks[152].y, landmarks[152].z);

        // Create horizontal and vertical vectors
        Vector3 faceHorizontal = rightSide - leftSide;
        Vector3 faceVertical = topHead - chin;

        // Cross product guarantees a vector perfectly centered and perpendicular to the face
        return Vector3.Cross(faceVertical, faceHorizontal).normalized;
    }
}