using System.Collections;
using System.Collections.Generic;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using TMPro;
using UnityEngine;

/// <summary>
/// Measures each eye's useful biological movement range before screen calibration.
///
/// During calibration, raw face-aligned iris ratios are collected while the user
/// looks comfortably toward the extremes of their gaze. The final bounds use
/// configurable lower/upper percentiles rather than absolute extrema, preventing
/// blinks or isolated landmark errors from stretching the entire normalization range.
///
/// The calibration instruction text is black because the calibration canvas uses a
/// white background to illuminate the user's face. Successful completion is shown
/// in green.
/// </summary>
public class LocalGazeCalibrator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private FaceLandmarkerRunner faceLandmarkerRunner;
    [SerializeField] private GazeVisualizer gazeVisualizer;
    [SerializeField] private TextMeshProUGUI statusInstructionsText;

    [Header("Calibration")]
    [SerializeField, Min(1f)] private float calibrationDuration = 8f;
    [SerializeField, Range(0f, 0.25f)] private float lowerPercentile = 0.05f;
    [SerializeField, Range(0.75f, 1f)] private float upperPercentile = 0.95f;
    [SerializeField, Min(10)] private int minimumSamples = 60;

    private readonly object _sampleLock = new object();
    private readonly List<float> _leftX = new List<float>();
    private readonly List<float> _leftY = new List<float>();
    private readonly List<float> _rightX = new List<float>();
    private readonly List<float> _rightY = new List<float>();

    private GazeCalibrationDebugSettings _debugSettings;
    private Coroutine _calibrationRoutine;
    private bool _isCalibrating;

    private void Awake()
    {
        ResolveDebugSettings();
    }

    private void Start()
    {
        if (gazeVisualizer != null)
            gazeVisualizer.enabled = false;

        if (statusInstructionsText != null)
            statusInstructionsText.gameObject.SetActive(false);
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

        if (_calibrationRoutine != null)
        {
            StopCoroutine(_calibrationRoutine);
            _calibrationRoutine = null;
        }

        _isCalibrating = false;
    }

    /// <summary>
    /// Starts a fresh local eye-range calibration.
    ///
    /// Any currently running local calibration is cancelled first. While calibration
    /// is active, GazeVisualizer is disabled so partially calibrated values cannot
    /// propagate into the subsequent screen-calibration stage.
    /// </summary>
    public void RunLocalGazeCalibration()
    {
        if (_calibrationRoutine != null)
            StopCoroutine(_calibrationRoutine);

        _calibrationRoutine = StartCoroutine(CalibrationRoutine());
    }

    /// <summary>
    /// Drives the timed local-calibration sequence, validates sample count, computes
    /// robust percentile bounds, and passes those bounds to GazeVisualizer.
    ///
    /// Lists are copied under a lock before percentile analysis because MediaPipe
    /// detection callbacks may add samples independently of Unity's main coroutine.
    /// </summary>
    private IEnumerator CalibrationRoutine()
    {
        ResetSamples();

        if (gazeVisualizer != null)
            gazeVisualizer.enabled = false;

        if (statusInstructionsText != null)
        {
            // White canvas is intentionally used as illumination, so instructions
            // must remain dark enough to read against it.
            statusInstructionsText.color = Color.black;
            statusInstructionsText.gameObject.SetActive(true);
            statusInstructionsText.text =
                "Gaze Calibration:\n" +
                "Keep your head still and look as far up, down, left, and right as is comfortable.";
        }

        _isCalibrating = true;
        yield return new WaitForSeconds(calibrationDuration);
        _isCalibrating = false;

        List<float> leftX;
        List<float> leftY;
        List<float> rightX;
        List<float> rightY;

        lock (_sampleLock)
        {
            leftX = new List<float>(_leftX);
            leftY = new List<float>(_leftY);
            rightX = new List<float>(_rightX);
            rightY = new List<float>(_rightY);
        }

        int sampleCount = Mathf.Min(
            Mathf.Min(leftX.Count, leftY.Count),
            Mathf.Min(rightX.Count, rightY.Count));

        if (sampleCount < minimumSamples)
        {
            if (statusInstructionsText != null)
            {
                statusInstructionsText.color = Color.red;
                statusInstructionsText.text =
                    $"Calibration failed: only {sampleCount} valid samples collected.\n" +
                    "Keep your face visible and try again.";
            }

            // Calibration failures should always be visible even with optional
            // diagnostic logging disabled.
            Debug.LogWarning(
                $"[Gaze] Local calibration failed: " +
                $"{sampleCount}/{minimumSamples} valid samples.");

            _calibrationRoutine = null;
            yield break;
        }

        float minLeftX = Percentile(leftX, lowerPercentile);
        float maxLeftX = Percentile(leftX, upperPercentile);
        float minLeftY = Percentile(leftY, lowerPercentile);
        float maxLeftY = Percentile(leftY, upperPercentile);

        float minRightX = Percentile(rightX, lowerPercentile);
        float maxRightX = Percentile(rightX, upperPercentile);
        float minRightY = Percentile(rightY, lowerPercentile);
        float maxRightY = Percentile(rightY, upperPercentile);

        LogCalibrationDistribution("Left X", leftX, minLeftX, maxLeftX);
        LogCalibrationDistribution("Left Y", leftY, minLeftY, maxLeftY);
        LogCalibrationDistribution("Right X", rightX, minRightX, maxRightX);
        LogCalibrationDistribution("Right Y", rightY, minRightY, maxRightY);

        gazeVisualizer.SetCalibrationBounds(
            minLeftX, maxLeftX, minLeftY, maxLeftY,
            minRightX, maxRightX, minRightY, maxRightY);

        gazeVisualizer.enabled = true;

        if (statusInstructionsText != null)
        {
            statusInstructionsText.color = Color.green;
            statusInstructionsText.text = "Local gaze calibration complete.";
            yield return new WaitForSeconds(1.5f);
            statusInstructionsText.gameObject.SetActive(false);
        }

        DebugLog(
            $"[GazeLocal] Local calibration complete | " +
            $"samples={sampleCount} | " +
            $"percentiles={lowerPercentile * 100f:F0}-" +
            $"{upperPercentile * 100f:F0}");

        _calibrationRoutine = null;
    }

    /// <summary>
    /// Receives FaceLandmarker results while local calibration is active and stores
    /// the raw per-eye gaze measurements used to build percentile distributions.
    /// </summary>
    /// <param name="result">Latest MediaPipe FaceLandmarker result.</param>
    private void ProcessCalibrationData(FaceLandmarkerResult result)
    {
        if (!_isCalibrating ||
            !GazeVisualizer.TryCalculateRawEyeGaze(
                result,
                out GazeVisualizer.RawEyeGaze raw))
            return;

        lock (_sampleLock)
        {
            _leftX.Add(raw.leftX);
            _leftY.Add(raw.leftY);
            _rightX.Add(raw.rightX);
            _rightY.Add(raw.rightY);
        }
    }

    /// <summary>
    /// Clears all four per-eye sample distributions before a new calibration pass.
    /// </summary>
    private void ResetSamples()
    {
        lock (_sampleLock)
        {
            _leftX.Clear();
            _leftY.Clear();
            _rightX.Clear();
            _rightY.Clear();
        }
    }

    /// <summary>
    /// Logs descriptive statistics for one raw gaze distribution when centralized
    /// gaze debugging is enabled.
    ///
    /// This diagnostic makes it possible to distinguish a healthy percentile trim
    /// from a distribution dominated by isolated landmark outliers.
    /// </summary>
    /// <param name="label">Human-readable eye/axis label such as "Left X".</param>
    /// <param name="values">Raw samples collected for the eye/axis.</param>
    /// <param name="lowerBound">Computed lower percentile bound used for calibration.</param>
    /// <param name="upperBound">Computed upper percentile bound used for calibration.</param>
    private void LogCalibrationDistribution(
        string label,
        List<float> values,
        float lowerBound,
        float upperBound)
    {
        if (!DebuggingEnabled || values == null || values.Count == 0)
            return;

        values.Sort();

        float rawMin = values[0];
        float median = Percentile(values, 0.5f);
        float rawMax = values[values.Count - 1];

        float rawSpan = rawMax - rawMin;
        float retainedSpan = upperBound - lowerBound;

        float retainedPercent =
            Mathf.Abs(rawSpan) > 0.000001f
                ? Mathf.Abs(retainedSpan / rawSpan) * 100f
                : 0f;

        Debug.Log(
            $"[GazeLocal] {label} | " +
            $"samples={values.Count} | " +
            $"rawMin={rawMin:F4} | " +
            $"P{lowerPercentile * 100f:F0}={lowerBound:F4} | " +
            $"median={median:F4} | " +
            $"P{upperPercentile * 100f:F0}={upperBound:F4} | " +
            $"rawMax={rawMax:F4} | " +
            $"retainedSpan={retainedPercent:F1}%");
    }

    /// <summary>
    /// Computes a linearly interpolated percentile from the supplied samples.
    /// </summary>
    /// <param name="values">
    /// Sample list. The list is sorted in-place as part of percentile evaluation.
    /// </param>
    /// <param name="percentile">Requested percentile expressed in [0, 1].</param>
    /// <returns>The interpolated sample value at the requested percentile.</returns>
    private static float Percentile(List<float> values, float percentile)
    {
        values.Sort();

        if (values.Count == 0)
            return 0f;

        if (values.Count == 1)
            return values[0];

        float index = Mathf.Clamp01(percentile) * (values.Count - 1);
        int lower = Mathf.FloorToInt(index);
        int upper = Mathf.CeilToInt(index);

        if (lower == upper)
            return values[lower];

        return Mathf.Lerp(values[lower], values[upper], index - lower);
    }

    /// <summary>
    /// Locates the shared GazeCalibrationDebugSettings instance.
    /// </summary>
    private void ResolveDebugSettings()
    {
        _debugSettings = GetComponentInParent<GazeCalibrationDebugSettings>();

        if (_debugSettings == null)
            _debugSettings = FindFirstObjectByType<GazeCalibrationDebugSettings>();
    }

    /// <summary>
    /// Gets whether optional calibration diagnostics are currently enabled.
    /// </summary>
    private bool DebuggingEnabled =>
        _debugSettings != null && _debugSettings.EnableDebugLogging;

    /// <summary>
    /// Writes an optional diagnostic message through the centralized debug switch.
    /// </summary>
    /// <param name="message">Message to send to Unity's log.</param>
    private void DebugLog(string message)
    {
        if (DebuggingEnabled)
            Debug.Log(message);
    }
}
