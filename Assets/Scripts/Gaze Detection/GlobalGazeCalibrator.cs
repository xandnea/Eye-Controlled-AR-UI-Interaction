using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Performs the 9-point gaze-to-screen calibration and drives the runtime gaze cursor.
///
/// Calibration collects one robust gaze sample at each point in a 3x3 screen grid.
/// The nine gaze/screen pairs overdetermine a six-term quadratic mapping:
///
///     output = c0 + c1*x + c2*y + c3*x*y + c4*x^2 + c5*y^2
///
/// Screen X and Y are fitted independently with least squares. Before accepting the
/// fit, neighboring calibration targets are checked for sufficient gaze-space
/// separation so a collapsed/noisy region cannot silently produce an unusable map.
///
/// Runtime gaze is then mapped into the calibrated UI area, converted safely into the
/// cursor's actual RectTransform anchor space, passed through a continuous radial
/// deadzone, and smoothed using a frame-rate-independent exponential filter.
///
/// Optional diagnostics are controlled by GazeDebugController on the
/// overhead "Gaze Calibration" GameObject. Warnings and errors remain unconditional.
/// </summary>
public class GlobalGazeCalibrator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GazeVisualizer gazeVisualizer;
    [SerializeField] private RectTransform calibrationArea;
    [SerializeField] private RectTransform calibrationTarget;
    [SerializeField] private RectTransform cursorIndicator;
    [SerializeField] private TextMeshProUGUI statusInstructionsText;
    [SerializeField] private CalibrationInstructionsUI calibrationInstructionsUI;
    [SerializeField] private GazeInteractionManager gazeInteractionManager;

    [Header("9-Point Calibration")]
    [SerializeField, Min(0f)] private float horizontalPadding = 60f;
    [SerializeField, Min(0f)] private float verticalPadding = 80f;

    [Tooltip("Seconds shown before the first calibration target appears.")]
    [SerializeField, Range(1, 5)] private int preCalibrationCountdownSeconds = 3;

    [Tooltip("Time to let the eyes settle after each target moves before samples are recorded.")]
    [SerializeField, Range(0.1f, 2f)] private float settleTimePerTarget = 0.75f;

    [Tooltip("Time spent collecting gaze samples at each target.")]
    [SerializeField, Range(0.25f, 3f)] private float sampleTimePerTarget = 1.0f;

    [SerializeField, Min(5)] private int minimumSamplesPerTarget = 10;

    [Tooltip("How long to show the retry message when a calibration point does not collect enough samples.")]
    [SerializeField, Range(0.25f, 2f)] private float retryMessageDuration = 0.75f;

    [SerializeField, Range(0f, 0.4f)] private float sampleTrimFraction = 0.2f;

    [Tooltip(
        "Minimum gaze-space distance required between neighboring screen targets. " +
        "If neighboring targets collapse to nearly the same measured gaze point, " +
        "the calibration is rejected instead of producing a broken cursor mapping.")]
    [SerializeField, Range(0.01f, 0.15f)] private float minimumAdjacentGazeDistance = 0.035f;

    [SerializeField] private Color activeColor = Color.green;

    public const int MinGazeFilterSpeed = 1;
    public const int MaxGazeFilterSpeed = 60;
    public const int MinGazeDeadzonePixels = 0;
    public const int MaxGazeDeadzonePixels = 30;

    [Header("Cursor Filter")]
    [Tooltip("Higher values follow gaze faster. Uses frame-rate-independent exponential smoothing.")]
    [SerializeField, Range(MinGazeFilterSpeed, MaxGazeFilterSpeed)] private int gazeFilterSpeed = 30;

    [Tooltip("Continuous radial deadzone radius in canvas pixels. Only motion outside this radius is followed.")]
    [SerializeField, Range(MinGazeDeadzonePixels, MaxGazeDeadzonePixels)] private int gazeDeadzonePixels = 15;

    private const int BasisSize = 6;
    private const double Regularization = 1e-6;

    private static readonly Vector2[] CalibrationGrid =
    {
        new Vector2(0.5f, 0.5f),
        new Vector2(0f,   1f),
        new Vector2(0.5f, 1f),
        new Vector2(1f,   1f),
        new Vector2(1f,   0.5f),
        new Vector2(1f,   0f),
        new Vector2(0.5f, 0f),
        new Vector2(0f,   0f),
        new Vector2(0f,   0.5f),
    };

    private static readonly string[] CalibrationLabels =
    {
        "C",
        "TL",
        "TC",
        "TR",
        "MR",
        "BR",
        "BC",
        "BL",
        "ML",
    };

    private readonly List<CalibrationPoint> _calibrationPoints =
        new List<CalibrationPoint>(CalibrationGrid.Length);

    private readonly double[] _xCoefficients = new double[BasisSize];
    private readonly double[] _yCoefficients = new double[BasisSize];

    private GazeDebugController _debugController;
    private Coroutine _calibrationRoutine;
    private Image _targetImage;
    public bool IsRunning => _calibrationRoutine != null;

    private bool _isCalibrated;
    private bool _filterInitialized;
    private Vector2 _filteredCursorPosition;

    private readonly struct CalibrationPoint
    {
        public readonly string label;
        public readonly Vector2 gaze;
        public readonly Vector2 normalizedScreen;
        public readonly int sampleCount;
        public readonly Vector2 standardDeviation;

        public CalibrationPoint(
            string label,
            Vector2 gaze,
            Vector2 normalizedScreen,
            int sampleCount,
            Vector2 standardDeviation)
        {
            this.label = label;
            this.gaze = gaze;
            this.normalizedScreen = normalizedScreen;
            this.sampleCount = sampleCount;
            this.standardDeviation = standardDeviation;
        }
    }

    private void Awake()
    {
        ResolveDebugSettings();
    }

    private void Start()
    {
        if (calibrationArea == null && calibrationTarget != null)
            calibrationArea = calibrationTarget.parent as RectTransform;

        if (calibrationTarget != null)
        {
            _targetImage = calibrationTarget.GetComponent<Image>();
            calibrationTarget.gameObject.SetActive(false);
        }

        if (cursorIndicator != null)
            cursorIndicator.gameObject.SetActive(false);

        if (statusInstructionsText != null)
            statusInstructionsText.gameObject.SetActive(false);
    }

    private void OnDisable()
    {
        if (_calibrationRoutine != null)
        {
            StopCoroutine(_calibrationRoutine);
            _calibrationRoutine = null;
        }

        RestoreGazeInteraction();

        if (calibrationInstructionsUI != null)
            calibrationInstructionsUI.Hide();
    }

    /// <summary>
    /// Starts a fresh 9-point screen calibration.
    ///
    /// Any active calibration coroutine is cancelled first. The cursor stays hidden
    /// until all nine targets have produced valid samples and the quadratic mapping
    /// has been solved successfully.
    /// </summary>
    public void RunGlobalGazeCalibration()
    {
        if (!ValidateReferences())
            return;

        if (_calibrationRoutine != null)
            StopCoroutine(_calibrationRoutine);

        if (gazeInteractionManager != null)
            gazeInteractionManager.SetGazeInteractionEnabled(false);

        if (calibrationInstructionsUI != null)
            calibrationInstructionsUI.ShowGlobal();

        _calibrationRoutine = StartCoroutine(CalibrationSequence());
    }

    /// <summary>
    /// Getter and setter for gaze filter speed.
    /// </summary>
    public int GazeFilterSpeed
    {
        get => gazeFilterSpeed;
        set => gazeFilterSpeed = Mathf.Clamp(value, MinGazeFilterSpeed, MaxGazeFilterSpeed);
    }

    /// <summary>
    /// Getter and setter for gaze deadzone pixels.
    /// </summary>
    public int GazeDeadzonePixels
    {
        get => gazeDeadzonePixels;
        set => gazeDeadzonePixels = Mathf.Clamp(value, MinGazeDeadzonePixels, MaxGazeDeadzonePixels);
    }

    private void RestoreGazeInteraction()
    {
        if (gazeInteractionManager != null)
            gazeInteractionManager.SetGazeInteractionEnabled(true);
    }

    public void ResetCursorFilter()
    {
        _filterInitialized = false;
    }

    /// <summary>
    /// Moves one calibration target through the 3x3 grid, waits for the user's gaze
    /// to settle at each point, records a robust unique-frame sample, fits the final
    /// 2D quadratic mapping, and enables the cursor.
    /// </summary>
    private IEnumerator CalibrationSequence()
    {
        _isCalibrated = false;
        _filterInitialized = false;
        _calibrationPoints.Clear();

        cursorIndicator.gameObject.SetActive(false);
        calibrationTarget.gameObject.SetActive(false);

        if (_targetImage != null)
            _targetImage.color = activeColor;

        yield return RunPreCalibrationCountdown();

        calibrationTarget.gameObject.SetActive(true);

        for (int i = 0; i < CalibrationGrid.Length; ++i)
        {
            Vector2 targetLocalPosition =
                GetCalibrationTargetLocalPosition(CalibrationGrid[i]);

            calibrationTarget.anchoredPosition =
                ParentLocalToAnchoredPosition(
                    calibrationTarget,
                    calibrationArea,
                    targetLocalPosition);

            Vector2 normalizedScreen =
                LocalToNormalized(calibrationArea.rect, targetLocalPosition);

            Vector2 gazeSample = Vector2.zero;
            Vector2 sampleStdDev = Vector2.zero;
            int sampleCount = 0;
            bool collected = false;

            while (!collected)
            {
                // Every attempt gets a fresh settle period before sampling.
                yield return new WaitForSeconds(settleTimePerTarget);

                gazeSample = Vector2.zero;
                sampleStdDev = Vector2.zero;
                sampleCount = 0;

                yield return CollectGazeSample((mean, count, stdDev) =>
                {
                    gazeSample = mean;
                    sampleCount = count;
                    sampleStdDev = stdDev;
                    collected = true;
                });

                if (!collected)
                {
                    Debug.LogWarning(
                        $"[GazeCal] {CalibrationLabels[i]} failed: insufficient valid samples. " +
                        "Retrying the same calibration target.");

                    if (statusInstructionsText != null)
                    {
                        statusInstructionsText.color = Color.red;
                        statusInstructionsText.text =
                            "Not enough gaze samples.\nKeep looking at this dot — retrying...";

                        statusInstructionsText.gameObject.SetActive(true);

                        yield return new WaitForSecondsRealtime(retryMessageDuration);

                        statusInstructionsText.gameObject.SetActive(false);
                    }
                }
            }

            DebugLog(
                $"[GazeCal] {CalibrationLabels[i]} | " +
                $"samples={sampleCount} | " +
                $"mean=({gazeSample.x:F4},{gazeSample.y:F4}) | " +
                $"std=({sampleStdDev.x:F4},{sampleStdDev.y:F4})");

            _calibrationPoints.Add(
                new CalibrationPoint(
                    CalibrationLabels[i],
                    gazeSample,
                    normalizedScreen,
                    sampleCount,
                    sampleStdDev));
        }

        calibrationTarget.gameObject.SetActive(false);

        if (!ValidateCalibrationGeometry())
        {
            Debug.LogWarning(
                "[GazeCal] Global calibration rejected because neighboring " +
                "screen targets were not sufficiently separated in gaze space.");

            if (statusInstructionsText != null)
            {
                statusInstructionsText.color = Color.red;
                statusInstructionsText.text =
                    "Global gaze calibration failed.\nPlease try again.";
                statusInstructionsText.gameObject.SetActive(true);
                yield return new WaitForSeconds(2f);
            }

            if (calibrationInstructionsUI != null)
                calibrationInstructionsUI.Hide();

            RestoreGazeInteraction();
            _calibrationRoutine = null;
            yield break;
        }

        if (!FitQuadraticMapping())
        {
            Debug.LogError("[Gaze] Could not solve 9-point quadratic calibration.");

            if (statusInstructionsText != null)
            {
                statusInstructionsText.color = Color.red;
                statusInstructionsText.text =
                    "Global gaze calibration failed.\nPlease try again.";
                statusInstructionsText.gameObject.SetActive(true);
                yield return new WaitForSeconds(2f);
            }

            if (calibrationInstructionsUI != null)
                calibrationInstructionsUI.Hide();

            RestoreGazeInteraction();
            _calibrationRoutine = null;
            yield break;
        }

        _isCalibrated = true;
        _filterInitialized = false;
        cursorIndicator.gameObject.SetActive(true);

        Vector2 axisRmse = CalculateCalibrationAxisRmsePixels();
        float combinedRmse = Mathf.Sqrt(
            axisRmse.x * axisRmse.x + axisRmse.y * axisRmse.y);

        DebugLog(
            $"[GazeCal] 9-point calibration complete | " +
            $"X RMSE={axisRmse.x:F1} px | " +
            $"Y RMSE={axisRmse.y:F1} px | " +
            $"combined={combinedRmse:F1} px");

        RectTransform cursorParent =
            cursorIndicator.parent as RectTransform;

        if (cursorParent != null)
        {
            DebugLog(
                $"[GazeUI] calibrationArea={calibrationArea.rect.width:F0}x" +
                $"{calibrationArea.rect.height:F0} | " +
                $"cursorParent={cursorParent.rect.width:F0}x" +
                $"{cursorParent.rect.height:F0} | " +
                $"cursorAnchors=({cursorIndicator.anchorMin.x:F2}," +
                $"{cursorIndicator.anchorMin.y:F2})-(" +
                $"{cursorIndicator.anchorMax.x:F2}," +
                $"{cursorIndicator.anchorMax.y:F2}) | " +
                $"cursorPivot=({cursorIndicator.pivot.x:F2}," +
                $"{cursorIndicator.pivot.y:F2})");
        }

        if (statusInstructionsText != null)
        {
            statusInstructionsText.color = Color.green;
            statusInstructionsText.text = "Global gaze calibration complete.";
            statusInstructionsText.gameObject.SetActive(true);
            yield return new WaitForSeconds(1.5f);
        }

        if (calibrationInstructionsUI != null)
            calibrationInstructionsUI.Hide();

        RestoreGazeInteraction();
        _calibrationRoutine = null;
    }

    /// <summary>
    /// Shows a short preparation countdown before the first global calibration target
    /// appears. The text is black because the calibration canvas uses a white
    /// illumination background.
    /// </summary>
    private IEnumerator RunPreCalibrationCountdown()
    {
        if (statusInstructionsText == null)
        {
            if (calibrationInstructionsUI != null)
                calibrationInstructionsUI.HideImages();

            yield break;
        }

        statusInstructionsText.color = Color.black;
        statusInstructionsText.gameObject.SetActive(true);

        int seconds = Mathf.Max(1, preCalibrationCountdownSeconds);

        for (int remaining = seconds; remaining >= 1; --remaining)
        {
            statusInstructionsText.text =
                $"Keep your eyes on the green dot in: {remaining}";

            yield return new WaitForSeconds(1f);
        }

        // Countdown is over. Actual calibration begins now.
        statusInstructionsText.gameObject.SetActive(false);

        if (calibrationInstructionsUI != null)
            calibrationInstructionsUI.HideImages();
    }

    /// <summary>
    /// Collects unique MediaPipe gaze samples for one calibration target, removes
    /// symmetric outliers with a trimmed mean, and reports the retained sample spread.
    ///
    /// Version checking prevents Unity from recording the same inference result
    /// multiple times when the render loop is faster than FaceLandmarker inference.
    /// </summary>
    /// <param name="onComplete">
    /// Callback receiving the robust mean gaze vector, number of unique samples, and
    /// trimmed per-axis standard deviation. It is not invoked if too few samples exist.
    /// </param>
    private IEnumerator CollectGazeSample(Action<Vector2, int, Vector2> onComplete)
    {
        List<float> xSamples = new List<float>();
        List<float> ySamples = new List<float>();

        int lastSampleVersion = -1;
        float timer = 0f;

        while (timer < sampleTimePerTarget)
        {
            timer += Time.deltaTime;

            if (gazeVisualizer.TryGetCurrentGazeVector(
                    out Vector2 gaze,
                    out int sampleVersion) &&
                sampleVersion != lastSampleVersion &&
                IsFinite(gaze))
            {
                lastSampleVersion = sampleVersion;
                xSamples.Add(gaze.x);
                ySamples.Add(gaze.y);
            }

            yield return null;
        }

        if (xSamples.Count < minimumSamplesPerTarget)
        {
            Debug.LogWarning(
                $"[Gaze] Only {xSamples.Count}/{minimumSamplesPerTarget} " +
                "unique samples collected for calibration target.");
            yield break;
        }

        float meanX = TrimmedMean(xSamples, sampleTrimFraction);
        float meanY = TrimmedMean(ySamples, sampleTrimFraction);

        Vector2 stdDev = new Vector2(
            StandardDeviation(xSamples, meanX, sampleTrimFraction),
            StandardDeviation(ySamples, meanY, sampleTrimFraction));

        onComplete(
            new Vector2(meanX, meanY),
            xSamples.Count,
            stdDev);
    }

    /// <summary>
    /// Updates the visible cursor from the latest calibrated gaze sample.
    ///
    /// Deadzone behavior is continuous and radial. Motion inside the deadzone is
    /// ignored; once the target moves beyond the deadzone, only the distance outside
    /// that radius is presented to the exponential smoother. For example, a target
    /// 10 px from the current cursor with a 4 px deadzone contributes 6 px of motion.
    /// This avoids the discontinuous jump produced by a simple threshold.
    /// </summary>
    private void Update()
    {
        if (!_isCalibrated || cursorIndicator == null)
            return;

        Vector2 gaze = gazeVisualizer.GetCurrentGazeVector();
        if (!IsFinite(gaze))
            return;

        Vector2 targetPosition = GetScreenPixel(gaze);

        if (!_filterInitialized)
        {
            _filteredCursorPosition = targetPosition;
            _filterInitialized = true;
        }
        else
        {
            Vector2 delta = targetPosition - _filteredCursorPosition;
            float distance = delta.magnitude;
            float deadzone = Mathf.Max(0f, gazeDeadzonePixels);

            if (distance > deadzone)
            {
                // Remove the radial deadzone from the requested movement. The
                // effective target begins exactly at zero movement at the boundary
                // and grows continuously as gaze moves farther away.
                float outsideDistance = distance - deadzone;
                Vector2 outsideDelta =
                    delta * (outsideDistance / distance);

                Vector2 effectiveTarget =
                    _filteredCursorPosition + outsideDelta;

                float blend =
                    1f - Mathf.Exp(
                        -gazeFilterSpeed * Time.unscaledDeltaTime);

                _filteredCursorPosition = Vector2.LerpUnclamped(
                    _filteredCursorPosition,
                    effectiveTarget,
                    blend);
            }
        }

        cursorIndicator.anchoredPosition =
            ClampCursorToParent(_filteredCursorPosition);
    }

    /// <summary>
    /// Maps gaze into the calibrated UI area, then converts that physical UI point
    /// into the cursor's anchoredPosition. This is anchor-safe: a bottom-left
    /// anchored cursor is handled correctly instead of being given center-relative
    /// RectTransform coordinates.
    /// </summary>
    /// <param name="currentGaze">
    /// Locally normalized gaze vector produced by GazeVisualizer.
    /// </param>
    /// <returns>
    /// Cursor anchoredPosition corresponding to the calibrated screen location, or
    /// Vector2.zero when required UI references are unavailable.
    /// </returns>
    public Vector2 GetScreenPixel(Vector2 currentGaze)
    {
        if (!_isCalibrated ||
            cursorIndicator == null ||
            calibrationArea == null)
            return Vector2.zero;

        double[] basis = BuildBasis(currentGaze.x, currentGaze.y);

        float normalizedX =
            Mathf.Clamp01((float)Evaluate(_xCoefficients, basis));
        float normalizedY =
            Mathf.Clamp01((float)Evaluate(_yCoefficients, basis));

        RectTransform cursorParent =
            cursorIndicator.parent as RectTransform;

        if (cursorParent == null)
            return Vector2.zero;

        Rect calibrationRect = calibrationArea.rect;

        // This is the actual desired point inside the same area used during calibration.
        Vector2 calibrationLocalPosition = new Vector2(
            Mathf.Lerp(
                calibrationRect.xMin,
                calibrationRect.xMax,
                normalizedX),
            Mathf.Lerp(
                calibrationRect.yMin,
                calibrationRect.yMax,
                normalizedY));

        // Convert CalibrationArea local space -> world -> cursor-parent local space.
        Vector3 worldPosition =
            calibrationArea.TransformPoint(calibrationLocalPosition);

        Vector2 cursorParentLocalPosition =
            cursorParent.InverseTransformPoint(worldPosition);

        // Finally convert parent-local pivot position -> anchoredPosition.
        return ParentLocalToAnchoredPosition(
            cursorIndicator,
            cursorParent,
            cursorParentLocalPosition);
    }

    /// <summary>
    /// Rejects a calibration when adjacent screen targets collapse to nearly the same
    /// gaze-space point.
    ///
    /// A mapping algorithm cannot reliably distinguish two different screen regions
    /// when their measured gaze vectors are effectively identical. In that situation
    /// accepting the calibration produces extreme local distortion, especially near
    /// corners. Rejecting the run is preferable to presenting a cursor that appears
    /// calibrated but cannot reach part of the display.
    /// </summary>
    /// <returns>True when all horizontal and vertical neighboring targets are distinct.</returns>
    private bool ValidateCalibrationGeometry()
    {
        // Index layout:
        //     TL(1) -- TC(2) -- TR(3)
        //       |        |        |
        //     ML(8) --  C(0) -- MR(4)
        //       |        |        |
        //     BL(7) -- BC(6) -- BR(5)
        int[,] neighborPairs =
        {
            { 1, 2 }, { 2, 3 },
            { 8, 0 }, { 0, 4 },
            { 7, 6 }, { 6, 5 },

            { 1, 8 }, { 8, 7 },
            { 2, 0 }, { 0, 6 },
            { 3, 4 }, { 4, 5 },
        };

        bool valid = true;

        for (int i = 0; i < neighborPairs.GetLength(0); ++i)
        {
            CalibrationPoint first =
                _calibrationPoints[neighborPairs[i, 0]];
            CalibrationPoint second =
                _calibrationPoints[neighborPairs[i, 1]];

            float distance =
                Vector2.Distance(first.gaze, second.gaze);

            DebugLog(
                $"[GazeCal] separation {first.label}-{second.label} = " +
                $"{distance:F4}");

            if (distance < minimumAdjacentGazeDistance)
            {
                Debug.LogWarning(
                    $"[GazeCal] Calibration geometry too compressed between " +
                    $"{first.label} and {second.label}: distance={distance:F4}, " +
                    $"required>={minimumAdjacentGazeDistance:F4}.");

                valid = false;
            }
        }

        return valid;
    }

    /// <summary>
    /// Builds and solves the normal equations for the six-term quadratic gaze model.
    ///
    /// All nine calibration points contribute to both screen axes. A small diagonal
    /// regularization term improves numerical stability without materially changing
    /// the fitted mapping.
    /// </summary>
    /// <returns>True when both the X and Y coefficient systems are solved.</returns>
    private bool FitQuadraticMapping()
    {
        if (_calibrationPoints.Count < BasisSize)
            return false;

        double[,] normal = new double[BasisSize, BasisSize];
        double[] rhsX = new double[BasisSize];
        double[] rhsY = new double[BasisSize];

        foreach (CalibrationPoint point in _calibrationPoints)
        {
            double[] basis = BuildBasis(point.gaze.x, point.gaze.y);

            for (int row = 0; row < BasisSize; ++row)
            {
                rhsX[row] += basis[row] * point.normalizedScreen.x;
                rhsY[row] += basis[row] * point.normalizedScreen.y;

                for (int col = 0; col < BasisSize; ++col)
                    normal[row, col] += basis[row] * basis[col];
            }
        }

        for (int i = 0; i < BasisSize; ++i)
            normal[i, i] += Regularization;

        if (!SolveLinearSystem(
                (double[,])normal.Clone(),
                (double[])rhsX.Clone(),
                out double[] solvedX) ||
            !SolveLinearSystem(
                (double[,])normal.Clone(),
                (double[])rhsY.Clone(),
                out double[] solvedY))
            return false;

        Array.Copy(solvedX, _xCoefficients, BasisSize);
        Array.Copy(solvedY, _yCoefficients, BasisSize);
        return true;
    }

    /// <summary>
    /// Solves a small dense linear system using Gaussian elimination with partial
    /// pivoting. Partial pivoting reduces sensitivity to poorly scaled calibration
    /// equations and allows singular/degenerate systems to be rejected safely.
    /// </summary>
    /// <param name="matrix">Square coefficient matrix. Modified in place.</param>
    /// <param name="vector">Right-hand-side vector. Modified in place.</param>
    /// <param name="solution">Receives the solved coefficient vector.</param>
    /// <returns>False if a usable pivot cannot be found; otherwise true.</returns>
    private static bool SolveLinearSystem(
        double[,] matrix,
        double[] vector,
        out double[] solution)
    {
        int n = vector.Length;
        solution = new double[n];

        for (int pivot = 0; pivot < n; ++pivot)
        {
            int bestRow = pivot;
            double bestValue = Math.Abs(matrix[pivot, pivot]);

            for (int row = pivot + 1; row < n; ++row)
            {
                double value = Math.Abs(matrix[row, pivot]);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestRow = row;
                }
            }

            if (bestValue < 1e-10)
                return false;

            if (bestRow != pivot)
            {
                for (int col = pivot; col < n; ++col)
                {
                    double temp = matrix[pivot, col];
                    matrix[pivot, col] = matrix[bestRow, col];
                    matrix[bestRow, col] = temp;
                }

                double vectorTemp = vector[pivot];
                vector[pivot] = vector[bestRow];
                vector[bestRow] = vectorTemp;
            }

            double pivotValue = matrix[pivot, pivot];

            for (int col = pivot; col < n; ++col)
                matrix[pivot, col] /= pivotValue;

            vector[pivot] /= pivotValue;

            for (int row = 0; row < n; ++row)
            {
                if (row == pivot)
                    continue;

                double factor = matrix[row, pivot];
                if (Math.Abs(factor) < 1e-12)
                    continue;

                for (int col = pivot; col < n; ++col)
                    matrix[row, col] -= factor * matrix[pivot, col];

                vector[row] -= factor * vector[pivot];
            }
        }

        Array.Copy(vector, solution, n);
        return true;
    }

    /// <summary>
    /// Returns the desired calibration-target pivot in CalibrationArea local space.
    /// The target's own pivot is respected when applying edge padding.
    /// </summary>
    /// <param name="gridPosition">
    /// Normalized 3x3 grid location where (0,0) is bottom-left and (1,1) is top-right.
    /// </param>
    /// <returns>Target pivot position in CalibrationArea local coordinates.</returns>
    private Vector2 GetCalibrationTargetLocalPosition(Vector2 gridPosition)
    {
        Rect rect = calibrationArea.rect;
        Vector2 size = calibrationTarget.rect.size;
        Vector2 pivot = calibrationTarget.pivot;

        float minX =
            rect.xMin + horizontalPadding + size.x * pivot.x;
        float maxX =
            rect.xMax - horizontalPadding - size.x * (1f - pivot.x);

        float minY =
            rect.yMin + verticalPadding + size.y * pivot.y;
        float maxY =
            rect.yMax - verticalPadding - size.y * (1f - pivot.y);

        return new Vector2(
            Mathf.Lerp(minX, maxX, gridPosition.x),
            Mathf.Lerp(minY, maxY, gridPosition.y));
    }

    /// <summary>
    /// Keeps the complete cursor rectangle inside its parent while respecting the
    /// cursor's anchors and pivot.
    /// </summary>
    /// <param name="anchoredPosition">Candidate anchoredPosition to clamp.</param>
    /// <returns>Anchor-space position that keeps the complete cursor visible.</returns>
    private Vector2 ClampCursorToParent(Vector2 anchoredPosition)
    {
        RectTransform parent =
            cursorIndicator.parent as RectTransform;

        if (parent == null)
            return anchoredPosition;

        Vector2 anchorReference =
            GetAnchorReferenceLocal(cursorIndicator, parent);

        Vector2 localPivotPosition =
            anchorReference + anchoredPosition;

        Rect parentRect = parent.rect;
        Vector2 cursorSize = cursorIndicator.rect.size;
        Vector2 cursorPivot = cursorIndicator.pivot;

        float minX =
            parentRect.xMin + cursorSize.x * cursorPivot.x;
        float maxX =
            parentRect.xMax - cursorSize.x * (1f - cursorPivot.x);

        float minY =
            parentRect.yMin + cursorSize.y * cursorPivot.y;
        float maxY =
            parentRect.yMax - cursorSize.y * (1f - cursorPivot.y);

        localPivotPosition.x =
            Mathf.Clamp(localPivotPosition.x, minX, maxX);
        localPivotPosition.y =
            Mathf.Clamp(localPivotPosition.y, minY, maxY);

        return localPivotPosition - anchorReference;
    }

    /// <summary>
    /// Converts a desired child-pivot point in parent-local coordinates into the
    /// anchoredPosition Unity expects for that child's current anchors.
    /// </summary>
    /// <param name="child">Child RectTransform whose anchors define the reference.</param>
    /// <param name="parent">Parent RectTransform defining the local coordinate space.</param>
    /// <param name="desiredParentLocalPosition">Desired child pivot in parent-local space.</param>
    /// <returns>The equivalent RectTransform.anchoredPosition.</returns>
    private static Vector2 ParentLocalToAnchoredPosition(
        RectTransform child,
        RectTransform parent,
        Vector2 desiredParentLocalPosition)
    {
        return desiredParentLocalPosition -
               GetAnchorReferenceLocal(child, parent);
    }

    /// <summary>
    /// Computes the parent-local reference point used by anchoredPosition.
    /// This supports both fixed and stretched anchors.
    /// </summary>
    /// <param name="child">Child whose anchor configuration is being evaluated.</param>
    /// <param name="parent">Parent RectTransform containing the anchor rectangle.</param>
    /// <returns>Anchor reference point expressed in parent-local coordinates.</returns>
    private static Vector2 GetAnchorReferenceLocal(
        RectTransform child,
        RectTransform parent)
    {
        Rect parentRect = parent.rect;

        float anchorX =
            Mathf.Lerp(
                child.anchorMin.x,
                child.anchorMax.x,
                child.pivot.x);

        float anchorY =
            Mathf.Lerp(
                child.anchorMin.y,
                child.anchorMax.y,
                child.pivot.y);

        return new Vector2(
            Mathf.Lerp(parentRect.xMin, parentRect.xMax, anchorX),
            Mathf.Lerp(parentRect.yMin, parentRect.yMax, anchorY));
    }

    /// <summary>
    /// Reports calibration residual error separately for horizontal and vertical
    /// screen axes so we can see which gaze component is limiting accuracy.
    /// </summary>
    /// <returns>Vector2(X RMSE pixels, Y RMSE pixels).</returns>
    private Vector2 CalculateCalibrationAxisRmsePixels()
    {
        if (_calibrationPoints.Count == 0 || calibrationArea == null)
            return Vector2.zero;

        double squaredErrorX = 0.0;
        double squaredErrorY = 0.0;
        Rect rect = calibrationArea.rect;

        foreach (CalibrationPoint point in _calibrationPoints)
        {
            double[] basis = BuildBasis(point.gaze.x, point.gaze.y);

            float predictedX = (float)Evaluate(_xCoefficients, basis);
            float predictedY = (float)Evaluate(_yCoefficients, basis);

            float errorPixelsX =
                (predictedX - point.normalizedScreen.x) * rect.width;
            float errorPixelsY =
                (predictedY - point.normalizedScreen.y) * rect.height;

            squaredErrorX += errorPixelsX * errorPixelsX;
            squaredErrorY += errorPixelsY * errorPixelsY;
        }

        return new Vector2(
            Mathf.Sqrt((float)(squaredErrorX / _calibrationPoints.Count)),
            Mathf.Sqrt((float)(squaredErrorY / _calibrationPoints.Count)));
    }

    /// <summary>
    /// Verifies the required calibration and cursor references before starting.
    /// </summary>
    /// <returns>True when calibration can safely proceed.</returns>
    private bool ValidateReferences()
    {
        if (gazeVisualizer == null ||
            calibrationArea == null ||
            calibrationTarget == null ||
            cursorIndicator == null)
        {
            Debug.LogError(
                "[Gaze] Assign GazeVisualizer, Calibration Area, " +
                "Calibration Target, and Cursor Indicator.");
            return false;
        }

        if (calibrationTarget.parent != calibrationArea)
        {
            Debug.LogError(
                "[Gaze] Calibration Target must be a direct child of Calibration Area.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds [1, x, y, xy, x^2, y^2] for the quadratic calibration model.
    /// </summary>
    /// <param name="x">Normalized horizontal gaze coordinate.</param>
    /// <param name="y">Normalized vertical gaze coordinate.</param>
    /// <returns>Six-term quadratic basis vector.</returns>
    private static double[] BuildBasis(float x, float y)
    {
        return new[]
        {
            1.0,
            (double)x,
            (double)y,
            (double)x * y,
            (double)x * x,
            (double)y * y,
        };
    }

    /// <summary>
    /// Evaluates one fitted quadratic output axis.
    /// </summary>
    /// <param name="coefficients">Six fitted coefficients for the output axis.</param>
    /// <param name="basis">Six-term quadratic basis for the current gaze.</param>
    /// <returns>Predicted normalized screen coordinate.</returns>
    private static double Evaluate(double[] coefficients, double[] basis)
    {
        double value = 0.0;

        for (int i = 0; i < BasisSize; ++i)
            value += coefficients[i] * basis[i];

        return value;
    }

    /// <summary>
    /// Converts a point from Rect local coordinates into normalized [0,1] coordinates.
    /// </summary>
    /// <param name="rect">Rect defining the local coordinate bounds.</param>
    /// <param name="localPosition">Point in the Rect's local coordinate space.</param>
    /// <returns>Normalized position relative to the Rect.</returns>
    private static Vector2 LocalToNormalized(
        Rect rect,
        Vector2 localPosition)
    {
        return new Vector2(
            Mathf.InverseLerp(rect.xMin, rect.xMax, localPosition.x),
            Mathf.InverseLerp(rect.yMin, rect.yMax, localPosition.y));
    }

    /// <summary>
    /// Computes a symmetric trimmed mean to reduce transient calibration outliers.
    /// </summary>
    /// <param name="values">Samples; sorted in place.</param>
    /// <param name="trimFraction">Fraction removed from each end of the distribution.</param>
    /// <returns>Mean of the retained center samples.</returns>
    private static float TrimmedMean(
        List<float> values,
        float trimFraction)
    {
        values.Sort();

        int trim = Mathf.FloorToInt(
            values.Count * Mathf.Clamp(trimFraction, 0f, 0.4f));

        int start = trim;
        int end = values.Count - trim;

        if (start >= end)
            return values[values.Count / 2];

        double sum = 0.0;

        for (int i = start; i < end; ++i)
            sum += values[i];

        return (float)(sum / (end - start));
    }

    /// <summary>
    /// Standard deviation over the same trimmed sample range used for the robust mean.
    /// </summary>
    /// <param name="values">Samples; sorted in place.</param>
    /// <param name="mean">Trimmed mean used as the center of the calculation.</param>
    /// <param name="trimFraction">Fraction excluded from each distribution tail.</param>
    /// <returns>Population standard deviation of the retained samples.</returns>
    private static float StandardDeviation(
        List<float> values,
        float mean,
        float trimFraction)
    {
        values.Sort();

        int trim = Mathf.FloorToInt(
            values.Count * Mathf.Clamp(trimFraction, 0f, 0.4f));

        int start = trim;
        int end = values.Count - trim;

        if (start >= end)
            return 0f;

        double sumSquared = 0.0;

        for (int i = start; i < end; ++i)
        {
            double delta = values[i] - mean;
            sumSquared += delta * delta;
        }

        return Mathf.Sqrt((float)(sumSquared / (end - start)));
    }

    /// <summary>
    /// Checks that both vector components are finite numeric values.
    /// </summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when neither component is NaN or infinity.</returns>
    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.x) &&
               !float.IsInfinity(value.y);
    }

    /// <summary>
    /// Resolves the shared gaze debug controller from the "Gaze Calibration"
    /// parent hierarchy. No scene-wide lookup is required because the gaze system
    /// deliberately lives beneath that object.
    /// </summary>
    private void ResolveDebugSettings()
    {
        _debugController = GetComponentInParent<GazeDebugController>();
    }

    /// <summary>
    /// Writes a calibration diagnostic only while centralized debugging is enabled.
    /// </summary>
    /// <param name="message">Message to send to Unity's log.</param>
    private void DebugLog(string message)
    {
        if (_debugController != null && _debugController.EnableDebugLogging)
            Debug.Log(message);
    }
}

