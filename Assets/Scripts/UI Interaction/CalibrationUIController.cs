using UnityEngine;

public class CalibrationUIController : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private GameObject calibrationBackground;
    [SerializeField] private GameObject calibrationModeRoot;

    [Header("Canvas Reference")]
    [SerializeField] private GameObject uiCanvas;

    [Header("Calibration Panels")]
    [SerializeField] private GameObject interactionSettingsPanel;

    [Header("Face Landmarks")]
    [SerializeField]
    private Mediapipe.Unity.Sample.FaceLandmarkDetection.FaceLandmarkerRunner faceLandmarkerRunner;

    private bool calibrationModeEnabled;
    private bool landmarksEnabled;

    private void Start()
    {
        landmarksEnabled = false;
        SetCalibrationMode(false);

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

        if (enabled)
            ShowCalibrationCanvas();
        else
            HideCalibrationCanvas();

        if (!enabled)
        {
            landmarksEnabled = false;

            if (faceLandmarkerRunner != null)
                faceLandmarkerRunner.SetLandmarkDrawing(false);
        }
    }

    public void ShowCalibrationCanvas()
    {
        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(true);

        if (calibrationBackground != null)
            calibrationBackground.SetActive(true);
        uiCanvas.SetActive(false);
    }

    public void HideCalibrationCanvas()
    {
        if (interactionSettingsPanel != null)
            interactionSettingsPanel.SetActive(false);

        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(false);

        if (calibrationBackground != null)
            calibrationBackground.SetActive(false);
        uiCanvas.SetActive(true);
    }
}