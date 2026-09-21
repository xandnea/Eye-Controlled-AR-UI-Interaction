using UnityEngine;

/// <summary>
/// Opens and closes the calibration workspace and controls optional face-landmark drawing.
/// </summary>
public sealed class CalibrationUIController : MonoBehaviour
{
    [Header("UI")]
    [Tooltip("Background shown behind the calibration controls to illuminate the user's face.")]
    [SerializeField] private GameObject calibrationBackground;
    [Tooltip("Root containing the calibration controls and status UI.")]
    [SerializeField] private GameObject calibrationModeRoot;

    [Header("Canvas Reference")]
    [Tooltip("Normal application UI canvas hidden while calibration mode is open.")]
    [SerializeField] private GameObject uiCanvas;

    [Header("Calibration Panels")]
    [Tooltip("Interaction-settings panel that should close when calibration mode closes.")]
    [SerializeField] private GameObject interactionSettingsPanel;

    [Header("Face Landmarks")]
    [Tooltip("MediaPipe runner whose landmark overlay can be toggled during calibration.")]
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

    /// <summary>Toggles the complete calibration workspace.</summary>
    public void ToggleCalibrationMode()
    {
        SetCalibrationMode(!calibrationModeEnabled);
    }

    /// <summary>
    /// Toggles MediaPipe landmark drawing while calibration mode is open.
    /// </summary>
    public void ToggleLandmarks()
    {
        if (!calibrationModeEnabled || faceLandmarkerRunner == null)
            return;

        landmarksEnabled = !landmarksEnabled;
        faceLandmarkerRunner.SetLandmarkDrawing(landmarksEnabled);
    }

    /// <summary>Applies the requested calibration-mode state.</summary>
    /// <param name="enabled">True to show calibration UI; false to restore normal UI.</param>
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

    /// <summary>Shows calibration UI and hides the normal application canvas.</summary>
    public void ShowCalibrationCanvas()
    {
        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(true);

        if (calibrationBackground != null)
            calibrationBackground.SetActive(true);
        if (uiCanvas != null)
            uiCanvas.SetActive(false);
    }

    /// <summary>Hides calibration UI and restores the normal application canvas.</summary>
    public void HideCalibrationCanvas()
    {
        if (interactionSettingsPanel != null)
            interactionSettingsPanel.SetActive(false);

        if (calibrationModeRoot != null)
            calibrationModeRoot.SetActive(false);

        if (calibrationBackground != null)
            calibrationBackground.SetActive(false);
        if (uiCanvas != null)
            uiCanvas.SetActive(true);
    }
}
