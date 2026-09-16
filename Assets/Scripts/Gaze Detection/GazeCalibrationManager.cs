using UnityEngine;

public sealed class GazeCalibrationManager : MonoBehaviour
{
    [Header("Calibrators")]
    [SerializeField] private LocalGazeCalibrator localGazeCalibrator;
    [SerializeField] private GlobalGazeCalibrator globalGazeCalibrator;

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