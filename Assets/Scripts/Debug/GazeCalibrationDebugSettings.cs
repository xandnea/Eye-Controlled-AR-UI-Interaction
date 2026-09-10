using UnityEngine;

/// <summary>
/// Central switch for optional gaze-calibration diagnostic logging.
///
/// Attach this component to the overhead "Gaze Calibration" GameObject. The gaze
/// scripts automatically search their parent hierarchy first and then the scene for
/// this component, so no additional Inspector references are normally required.
///
/// Warnings and errors remain visible even when diagnostic logging is disabled.
/// </summary>
public class GazeCalibrationDebugSettings : MonoBehaviour
{
    [Header("Debugging")]
    [Tooltip(
        "Enables detailed gaze-calibration diagnostics such as percentile ranges, " +
        "per-target samples, fit error, and UI coordinate information.")]
    [SerializeField] private bool enableDebugLogging = false;

    /// <summary>
    /// Gets whether optional gaze-calibration diagnostic messages should be written
    /// to the Unity log.
    /// </summary>
    public bool EnableDebugLogging => enableDebugLogging;
}
