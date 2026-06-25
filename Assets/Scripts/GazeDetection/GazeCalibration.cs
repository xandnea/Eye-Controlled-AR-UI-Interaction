using System.Collections;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;

public class GazeCalibration : MonoBehaviour
{
    [Header("References")]
    public FaceLandmarkerRunner faceLandmarkerRunner;
    public GazeVisualizer gazeVisualizer;
    public TMPro.TextMeshProUGUI statusInstructionsText;

    [Header("Settings")]
    public float calibrationDuration = 5f;

    private bool _isCalibrating = false;

    [Header("Calibration Bounds")]
    private float minLeftX = float.MaxValue, maxLeftX = float.MinValue;
    private float minRightX = float.MaxValue, maxRightX = float.MinValue;
    private float minLeftY = float.MaxValue, maxLeftY = float.MinValue;
    private float minRightY = float.MaxValue, maxRightY = float.MinValue;

    private void Start()
    {
        // Ensure Visualizer is off until calibration completes
        gazeVisualizer.enabled = false;
        StartCoroutine(CalibrationRoutine());
    }

    private void OnEnable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected += ProcessCalibrationData;
    }

    private void OnDisable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected -= ProcessCalibrationData;
    }

    private IEnumerator CalibrationRoutine()
    {
        statusInstructionsText.gameObject.SetActive(true);
        statusInstructionsText.text = "Gaze Calibration:\nLook as far up/down and left/right as you can without moving your head.";

        _isCalibrating = true;
        yield return new WaitForSeconds(calibrationDuration);
        _isCalibrating = false;

        statusInstructionsText.text = "Calibration Complete!";
        yield return new WaitForSeconds(1f);
        statusInstructionsText.gameObject.SetActive(false);

        // Pass the bounds to the visualizer and enable it
        gazeVisualizer.SetCalibrationBounds(
            minLeftX, maxLeftX, minLeftY, maxLeftY,
            minRightX, maxRightX, minRightY, maxRightY
        );

        gazeVisualizer.enabled = true;
        this.enabled = false; // Turn off calibration script
    }

    private void ProcessCalibrationData(FaceLandmarkerResult result)
    {
        if (!_isCalibrating || result.faceLandmarks == null || result.faceLandmarks.Count == 0) return;

        var landmarks = result.faceLandmarks[0].landmarks;
        if (landmarks.Count < 474) return;

        // Original Bounding Box
        float rEyeLookingR = landmarks[33].x;
        float rEyeLookingL = landmarks[133].x;
        float lEyeLookingR = landmarks[362].x;
        float lEyeLookingL = landmarks[263].x;
        float rEyeLookingU = landmarks[28].y;
        float rEyeLookingD = landmarks[230].y;
        float lEyeLookingU = landmarks[258].y;
        float lEyeLookingD = landmarks[450].y;

        Vector2 rightIris = new Vector2(landmarks[468].x, landmarks[468].y);
        Vector2 leftIris = new Vector2(landmarks[473].x, landmarks[473].y);

        // Calculate raw ratios (Iris position relative to the socket bounds)
        // Ratio = (Iris - MinBound) / (MaxBound - MinBound)
        float rawRightX = (rightIris.x - rEyeLookingR) / (rEyeLookingL - rEyeLookingR);
        float rawRightY = (rightIris.y - rEyeLookingU) / (rEyeLookingD - rEyeLookingU);
        float rawLeftX = (leftIris.x - lEyeLookingR) / (lEyeLookingL - lEyeLookingR);
        float rawLeftY = (leftIris.y - lEyeLookingU) / (lEyeLookingD - lEyeLookingU);

        // Record Extremities
        if (rawRightX < minRightX) minRightX = rawRightX;
        if (rawRightX > maxRightX) maxRightX = rawRightX;
        if (rawRightY < minRightY) minRightY = rawRightY;
        if (rawRightY > maxRightY) maxRightY = rawRightY;

        if (rawLeftX < minLeftX) minLeftX = rawLeftX;
        if (rawLeftX > maxLeftX) maxLeftX = rawLeftX;
        if (rawLeftY < minLeftY) minLeftY = rawLeftY;
        if (rawLeftY > maxLeftY) maxLeftY = rawLeftY;
    }
}