using UnityEngine;

/// <summary>
/// Coordinates local and global calibration requests and prevents them from overlapping.
/// </summary>
public sealed class GazeCalibrationManager : MonoBehaviour
{
    [Header("Calibrators")]
    [Tooltip("Calibrator that measures the user's per-eye movement range.")]
    [SerializeField] private LocalGazeCalibrator localGazeCalibrator;
    [Tooltip("Calibrator that maps normalized gaze onto screen coordinates.")]
    [SerializeField] private GlobalGazeCalibrator globalGazeCalibrator;

    /// <summary>Starts local eye-range calibration when no calibration is already running.</summary>
    public void RunLocalCalibration()
    {
        if (AnyCalibrationRunning())
        {
            Debug.Log(
                "[GazeCalibration] Calibration request ignored because a calibration is already running."
            );
            return;
        }

        if (localGazeCalibrator == null)
        {
            Debug.LogError(
                "[GazeCalibration] LocalGazeCalibrator is not assigned.",
                this
            );
            return;
        }

        localGazeCalibrator.RunLocalGazeCalibration();
    }

    /// <summary>Starts global screen calibration when no calibration is already running.</summary>
    public void RunGlobalCalibration()
    {
        if (AnyCalibrationRunning())
        {
            Debug.Log(
                "[GazeCalibration] Calibration request ignored because a calibration is already running."
            );
            return;
        }

        if (globalGazeCalibrator == null)
        {
            Debug.LogError(
                "[GazeCalibration] GlobalGazeCalibrator is not assigned.",
                this
            );
            return;
        }

        globalGazeCalibrator.RunGlobalGazeCalibration();
    }

    /// <summary>Checks whether either calibration workflow currently owns a coroutine.</summary>
    /// <returns>True while local or global calibration is running.</returns>
    private bool AnyCalibrationRunning()
    {
        bool localRunning =
            localGazeCalibrator != null &&
            localGazeCalibrator.IsRunning;

        bool globalRunning =
            globalGazeCalibrator != null &&
            globalGazeCalibrator.IsRunning;

        return localRunning || globalRunning;
    }
}
