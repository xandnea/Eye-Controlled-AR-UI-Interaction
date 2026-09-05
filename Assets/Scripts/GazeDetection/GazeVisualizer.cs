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
    [Range(0f, 200f)] public float sensitivity = 100f;

    [Header("Filter Settings")]
    [Range(1f, 20f)] public float gazeFilterSpeed = 1f;

    private Vector2 _rawGazeVector = Vector2.zero;
    private Vector2 _smoothedGazeVector = Vector2.zero;

    private FaceLandmarkerResult _lastResult;
    private bool _hasData = false;

    // Calibrated Bounds
    private float cMinLeftX, cMaxLeftX, cMinLeftY, cMaxLeftY;
    private float cMinRightX, cMaxRightX, cMinRightY, cMaxRightY;

    public void SetCalibrationBounds(
        float minLX,
        float maxLX,
        float minLY,
        float maxLY,
        float minRX,
        float maxRX,
        float minRY,
        float maxRY)
    {
        cMinLeftX = minLX;
        cMaxLeftX = maxLX;
        cMinLeftY = minLY;
        cMaxLeftY = maxLY;

        cMinRightX = minRX;
        cMaxRightX = maxRX;
        cMinRightY = minRY;
        cMaxRightY = maxRY;

        Debug.Log(
            $"Calibration Bounds Set:\n" +
            $"Left Eye: X({cMinLeftX}, {cMaxLeftX}), " +
            $"Y({cMinLeftY}, {cMaxLeftY})\n" +
            $"Right Eye: X({cMinRightX}, {cMaxRightX}), " +
            $"Y({cMinRightY}, {cMaxRightY})");
    }

    public Vector2 GetCurrentGazeVector()
    {
        return _hasData ? _smoothedGazeVector : Vector2.negativeInfinity;
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
        if (!_hasData ||
            _lastResult.faceLandmarks == null ||
            _lastResult.faceLandmarks.Count == 0)
            return;

        _smoothedGazeVector = Vector2.Lerp(
            _smoothedGazeVector,
            _rawGazeVector,
            Time.deltaTime * gazeFilterSpeed);

        var landmarks = _lastResult.faceLandmarks[0].landmarks;

        // Map nose to Viewport for origin
        var landmark = landmarks[168];

        // Face normal and head rotation
        Vector3 faceNormal = GetFaceNormal(_lastResult);
        Quaternion headRotation = Quaternion.LookRotation(faceNormal);

        // Canvas is "Screen Space - Camera" with a Plane Distance of 10:
        float canvasPlaneDistance = 10f;

        // Map nose to Viewport
        Vector3 viewportPoint = new Vector3(
            landmark.x,
            1f - landmark.y,
            canvasPlaneDistance * 10);

        // Convert directly to World Space.
        Vector3 origin = Camera.main.ViewportToWorldPoint(viewportPoint);

        Quaternion eyeOffset = Quaternion.Euler(
            -_smoothedGazeVector.y * sensitivity,
            _smoothedGazeVector.x * sensitivity,
            0);

        Quaternion finalRotation =
            headRotation *
            Quaternion.Slerp(
                Quaternion.identity,
                eyeOffset,
                1f - headAlpha);

        Vector3 finalGaze = finalRotation * Vector3.forward;

        Debug.DrawRay(origin, faceNormal * rayLength, Color.blue);
        Debug.DrawRay(origin, finalGaze * rayLength, Color.red);
    }

    private void ProcessGaze(FaceLandmarkerResult result)
    {
        _lastResult = result;

        _rawGazeVector = CalculateGaze(result);
        _hasData = true;
    }

    private Vector2 CalculateGaze(FaceLandmarkerResult result)
    {
        if (result.faceLandmarks == null ||
            result.faceLandmarks.Count == 0)
            return Vector2.zero;

        var landmarks = result.faceLandmarks[0].landmarks;

        if (landmarks.Count < 474)
            return Vector2.zero;

        Vector2 rightIris =
            new Vector2(landmarks[468].x, landmarks[468].y);

        Vector2 leftIris =
            new Vector2(landmarks[473].x, landmarks[473].y);

        // Original Bounding Box constraints
        float rEyeLookingR = landmarks[33].x;
        float rEyeLookingL = landmarks[133].x;

        float lEyeLookingR = landmarks[362].x;
        float lEyeLookingL = landmarks[263].x;

        float rEyeLookingU = landmarks[28].y;
        float rEyeLookingD = landmarks[230].y;

        float lEyeLookingU = landmarks[258].y;
        float lEyeLookingD = landmarks[450].y;

        // Calculate RAW ratios
        float rawRightX =
            (rightIris.x - rEyeLookingR) /
            (rEyeLookingL - rEyeLookingR);

        float rawRightY =
            (rightIris.y - rEyeLookingU) /
            (rEyeLookingD - rEyeLookingU);

        float rawLeftX =
            (leftIris.x - lEyeLookingR) /
            (lEyeLookingL - lEyeLookingR);

        float rawLeftY =
            (leftIris.y - lEyeLookingU) /
            (lEyeLookingD - lEyeLookingU);

        // Map against calibrated bounds
        float rightX =
            MapToRange(rawRightX, cMinRightX, cMaxRightX);

        float rightY =
            MapToRange(rawRightY, cMinRightY, cMaxRightY);

        float leftX =
            MapToRange(rawLeftX, cMinLeftX, cMaxLeftX);

        float leftY =
            MapToRange(rawLeftY, cMinLeftY, cMaxLeftY);

        // Average the processed eyes
        float avgX = -(rightX + leftX) / 2f;
        float avgY = -(rightY + leftY) / 2f;

        return new Vector2(avgX, avgY);
    }

    private float MapToRange(
        float rawValue,
        float calibMin,
        float calibMax)
    {
        float normalized01 =
            Mathf.InverseLerp(calibMin, calibMax, rawValue);

        return Mathf.Lerp(-1f, 1f, normalized01);
    }

    private Vector3 GetFaceNormal(FaceLandmarkerResult result)
    {
        var landmarks = result.faceLandmarks[0].landmarks;

        Vector3 rightSide = new Vector3(
            landmarks[454].x,
            1f - landmarks[454].y,
            landmarks[454].z);

        Vector3 leftSide = new Vector3(
            landmarks[234].x,
            1f - landmarks[234].y,
            landmarks[234].z);

        Vector3 topHead = new Vector3(
            landmarks[10].x,
            1f - landmarks[10].y,
            landmarks[10].z);

        Vector3 chin = new Vector3(
            landmarks[152].x,
            1f - landmarks[152].y,
            landmarks[152].z);

        Vector3 faceHorizontal = rightSide - leftSide;
        Vector3 faceVertical = topHead - chin;

        return Vector3.Cross(
            faceVertical,
            faceHorizontal).normalized;
    }
}