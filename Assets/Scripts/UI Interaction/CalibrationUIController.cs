using UnityEngine;

public class CalibrationUIController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private GameObject calibrationBackground;
    [SerializeField] private GameObject calibrationModeRoot;

    [Header("Face Landmarks")]
    [SerializeField]
    private Mediapipe.Unity.Sample.FaceLandmarkDetection.FaceLandmarkerRunner faceLandmarkerRunner;

    [Header("Normal UI")]
    [SerializeField] private GameObject scanButton;

    [Header("Calibration Background")]

    private bool calibrationModeEnabled;
    private bool landmarksEnabled;

    private void Start()
    {
        calibrationModeEnabled = false;
        landmarksEnabled = false;

        if (calibrationBackground != null)
            calibrationBackground.SetActive(false);

        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(false);

        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.SetLandmarkDrawing(false);
    }

    public void ToggleCalibrationMode()
    {
        SetCalibrationMode(!calibrationModeEnabled);
    }

    public void ToggleLandmarks()
    {
        if (!calibrationModeEnabled)
            return;

        landmarksEnabled = !landmarksEnabled;
        faceLandmarkerRunner.SetLandmarkDrawing(landmarksEnabled);
    }

    private void SetCalibrationMode(bool enabled)
    {
        calibrationModeEnabled = enabled;

        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(enabled);

        if (calibrationBackground != null)
            calibrationBackground.SetActive(enabled);

        // Normal scanning makes no sense while calibration mirror is open.
        if (scanButton != null)
            scanButton.SetActive(!enabled);

        if (!enabled)
        {
            landmarksEnabled = false;

            if (faceLandmarkerRunner != null)
                faceLandmarkerRunner.SetLandmarkDrawing(false);
        }
    }
}