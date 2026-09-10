using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;

/// <summary>
/// Converts MediaPipe iris landmarks into a locally calibrated 2D gaze vector.
///
/// The raw eye measurement is performed in a face-aligned coordinate system so the
/// result is robust to camera rotation and in-plane head roll. Both horizontal and
/// vertical iris displacement are normalized by eye width because eye width remains
/// substantially more stable than eyelid opening while the user looks up and down.
///
/// This component intentionally does not smooth the gaze signal. Screen-space
/// smoothing and deadzone handling are performed later by GlobalGazeCalibrator so
/// those parameters have a direct, intuitive meaning in UI pixels.
/// </summary>
public class GazeVisualizer : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private FaceLandmarkerRunner faceLandmarkerRunner;

    private readonly object _gazeLock = new object();

    private GazeCalibrationDebugSettings _debugSettings;

    private Vector2 _currentGaze;
    private int _sampleVersion;
    private bool _hasGaze;
    private bool _hasCalibrationBounds;

    private float _minLeftX;
    private float _maxLeftX;
    private float _minLeftY;
    private float _maxLeftY;
    private float _minRightX;
    private float _maxRightX;
    private float _minRightY;
    private float _maxRightY;

    /// <summary>
    /// Contains the four eye-local iris measurements before local range calibration.
    ///
    /// X and Y are centered around each eye's geometric center and normalized by
    /// that eye's width. These values are intentionally not constrained to [-1, 1];
    /// LocalGazeCalibrator determines the useful range for the current user.
    /// </summary>
    public readonly struct RawEyeGaze
    {
        public readonly float leftX;
        public readonly float leftY;
        public readonly float rightX;
        public readonly float rightY;

        /// <summary>
        /// Creates a raw per-eye gaze sample.
        /// </summary>
        /// <param name="leftX">Left-eye horizontal iris displacement / eye width.</param>
        /// <param name="leftY">Left-eye vertical iris displacement / eye width.</param>
        /// <param name="rightX">Right-eye horizontal iris displacement / eye width.</param>
        /// <param name="rightY">Right-eye vertical iris displacement / eye width.</param>
        public RawEyeGaze(float leftX, float leftY, float rightX, float rightY)
        {
            this.leftX = leftX;
            this.leftY = leftY;
            this.rightX = rightX;
            this.rightY = rightY;
        }
    }

    private void Awake()
    {
        ResolveDebugSettings();
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

    /// <summary>
    /// Stores the robust per-eye bounds measured during local calibration.
    ///
    /// Each incoming raw eye ratio is later mapped from its corresponding min/max
    /// range into [-1, 1]. Resetting the sample state here ensures global calibration
    /// cannot accidentally consume a gaze sample generated using the previous bounds.
    /// </summary>
    /// <param name="minLeftX">Lower calibrated horizontal bound for the left eye.</param>
    /// <param name="maxLeftX">Upper calibrated horizontal bound for the left eye.</param>
    /// <param name="minLeftY">Lower calibrated vertical bound for the left eye.</param>
    /// <param name="maxLeftY">Upper calibrated vertical bound for the left eye.</param>
    /// <param name="minRightX">Lower calibrated horizontal bound for the right eye.</param>
    /// <param name="maxRightX">Upper calibrated horizontal bound for the right eye.</param>
    /// <param name="minRightY">Lower calibrated vertical bound for the right eye.</param>
    /// <param name="maxRightY">Upper calibrated vertical bound for the right eye.</param>
    public void SetCalibrationBounds(
        float minLeftX,
        float maxLeftX,
        float minLeftY,
        float maxLeftY,
        float minRightX,
        float maxRightX,
        float minRightY,
        float maxRightY)
    {
        _minLeftX = minLeftX;
        _maxLeftX = maxLeftX;
        _minLeftY = minLeftY;
        _maxLeftY = maxLeftY;

        _minRightX = minRightX;
        _maxRightX = maxRightX;
        _minRightY = minRightY;
        _maxRightY = maxRightY;

        _hasCalibrationBounds = true;

        lock (_gazeLock)
        {
            _hasGaze = false;
            _sampleVersion = 0;
        }

        DebugLog(
            $"[Gaze] Local calibration ready | " +
            $"L X({_minLeftX:F3},{_maxLeftX:F3}) Y({_minLeftY:F3},{_maxLeftY:F3}) | " +
            $"R X({_minRightX:F3},{_maxRightX:F3}) Y({_minRightY:F3},{_maxRightY:F3})");
    }

    /// <summary>
    /// Returns the most recent locally normalized gaze vector.
    /// </summary>
    /// <returns>
    /// The latest gaze vector, or Vector2.negativeInfinity when no valid calibrated
    /// gaze sample has been received yet.
    /// </returns>
    public Vector2 GetCurrentGazeVector()
    {
        lock (_gazeLock)
            return _hasGaze ? _currentGaze : Vector2.negativeInfinity;
    }

    /// <summary>
    /// Reads the latest locally normalized gaze sample together with its version.
    ///
    /// Global calibration uses the version number to ensure a Unity render loop
    /// cannot record the same MediaPipe inference result more than once.
    /// </summary>
    /// <param name="gaze">Receives the latest normalized gaze vector.</param>
    /// <param name="sampleVersion">
    /// Receives a monotonically increasing version incremented once per valid
    /// MediaPipe gaze result.
    /// </param>
    /// <returns>True when a valid calibrated gaze sample is available.</returns>
    public bool TryGetCurrentGazeVector(out Vector2 gaze, out int sampleVersion)
    {
        lock (_gazeLock)
        {
            gaze = _currentGaze;
            sampleVersion = _sampleVersion;
            return _hasGaze;
        }
    }

    /// <summary>
    /// Converts one MediaPipe face result into the normalized gaze representation
    /// consumed by global screen calibration.
    ///
    /// Both eyes are independently mapped through their local percentile bounds,
    /// averaged, and sign-adjusted to preserve the existing screen-direction
    /// convention. Only simple value types are stored because the MediaPipe result
    /// itself may originate on a worker callback.
    /// </summary>
    /// <param name="result">Latest MediaPipe FaceLandmarker result.</param>
    private void ProcessGaze(FaceLandmarkerResult result)
    {
        if (!_hasCalibrationBounds ||
            !TryCalculateRawEyeGaze(result, out RawEyeGaze raw))
            return;

        float leftX = MapToSignedRange(raw.leftX, _minLeftX, _maxLeftX);
        float leftY = MapToSignedRange(raw.leftY, _minLeftY, _maxLeftY);
        float rightX = MapToSignedRange(raw.rightX, _minRightX, _maxRightX);
        float rightY = MapToSignedRange(raw.rightY, _minRightY, _maxRightY);

        Vector2 gaze = new Vector2(
            -(rightX + leftX) * 0.5f,
            -(rightY + leftY) * 0.5f);

        lock (_gazeLock)
        {
            _currentGaze = gaze;
            _hasGaze = true;
            _sampleVersion++;
        }
    }

    /// <summary>
    /// Measures iris displacement in a face-aligned coordinate system rather than
    /// directly dividing camera-image X/Y differences.
    ///
    /// The horizontal basis runs between the two eye centers. The vertical basis is
    /// perpendicular to that axis and is oriented from the upper eyelids toward the
    /// lower eyelids. Projecting onto these axes makes the measurement insensitive
    /// to camera rotation and reduces sensitivity to head roll.
    ///
    /// Both X and Y are divided by eye width. Using eyelid opening as the vertical
    /// denominator is deliberately avoided because eyelid height changes as the user
    /// looks up/down and can partially normalize away the eye movement being measured.
    /// </summary>
    /// <param name="result">MediaPipe result containing the current face landmarks.</param>
    /// <param name="rawEyeGaze">
    /// Receives the uncalibrated, face-aligned iris ratios for both eyes.
    /// </param>
    /// <returns>
    /// True when all required landmarks and non-degenerate eye geometry are available;
    /// otherwise false.
    /// </returns>
    public static bool TryCalculateRawEyeGaze(
        FaceLandmarkerResult result,
        out RawEyeGaze rawEyeGaze)
    {
        rawEyeGaze = default;

        if (result.faceLandmarks == null || result.faceLandmarks.Count == 0)
            return false;

        var landmarks = result.faceLandmarks[0].landmarks;
        if (landmarks == null || landmarks.Count < 474)
            return false;

        // MediaPipe iris centers.
        Vector2 rightIris = new Vector2(landmarks[468].x, landmarks[468].y);
        Vector2 leftIris = new Vector2(landmarks[473].x, landmarks[473].y);

        // Eye corners used to estimate eye center and stable eye width.
        Vector2 rightOuter = new Vector2(landmarks[33].x, landmarks[33].y);
        Vector2 rightInner = new Vector2(landmarks[133].x, landmarks[133].y);
        Vector2 leftInner = new Vector2(landmarks[362].x, landmarks[362].y);
        Vector2 leftOuter = new Vector2(landmarks[263].x, landmarks[263].y);

        // Eyelids are used only to determine the sign of the vertical basis.
        Vector2 rightUpper = new Vector2(landmarks[159].x, landmarks[159].y);
        Vector2 rightLower = new Vector2(landmarks[145].x, landmarks[145].y);
        Vector2 leftUpper = new Vector2(landmarks[386].x, landmarks[386].y);
        Vector2 leftLower = new Vector2(landmarks[374].x, landmarks[374].y);

        Vector2 rightCenter = (rightOuter + rightInner) * 0.5f;
        Vector2 leftCenter = (leftOuter + leftInner) * 0.5f;

        // Shared horizontal basis gives both eyes the same directional convention.
        Vector2 horizontal = leftCenter - rightCenter;

        const float epsilon = 0.000001f;
        float horizontalLength = horizontal.magnitude;
        if (horizontalLength < epsilon)
            return false;

        horizontal /= horizontalLength;

        // Construct the perpendicular basis and orient it upper -> lower eyelid.
        Vector2 vertical = new Vector2(-horizontal.y, horizontal.x);

        Vector2 averageUpper = (rightUpper + leftUpper) * 0.5f;
        Vector2 averageLower = (rightLower + leftLower) * 0.5f;

        if (Vector2.Dot(averageLower - averageUpper, vertical) < 0f)
            vertical = -vertical;

        float rightWidth = Vector2.Distance(rightOuter, rightInner);
        float leftWidth = Vector2.Distance(leftOuter, leftInner);

        if (rightWidth < epsilon || leftWidth < epsilon)
            return false;

        Vector2 rightOffset = rightIris - rightCenter;
        Vector2 leftOffset = leftIris - leftCenter;

        rawEyeGaze = new RawEyeGaze(
            Vector2.Dot(leftOffset, horizontal) / leftWidth,
            Vector2.Dot(leftOffset, vertical) / leftWidth,
            Vector2.Dot(rightOffset, horizontal) / rightWidth,
            Vector2.Dot(rightOffset, vertical) / rightWidth);

        return true;
    }

    /// <summary>
    /// Maps a locally calibrated scalar into the signed [-1, 1] gaze range.
    /// </summary>
    /// <param name="value">Raw eye-local value to normalize.</param>
    /// <param name="min">Lower local calibration bound.</param>
    /// <param name="max">Upper local calibration bound.</param>
    /// <returns>Normalized value in [-1, 1], or zero for a degenerate range.</returns>
    private static float MapToSignedRange(float value, float min, float max)
    {
        if (Mathf.Abs(max - min) < 0.000001f)
            return 0f;

        return Mathf.Lerp(-1f, 1f, Mathf.InverseLerp(min, max, value));
    }

    /// <summary>
    /// Locates the shared debug settings component. Parent lookup is preferred so
    /// the overhead "Gaze Calibration" object naturally controls its child scripts;
    /// a scene-wide fallback supports existing hierarchies without extra wiring.
    /// </summary>
    private void ResolveDebugSettings()
    {
        _debugSettings = GetComponentInParent<GazeCalibrationDebugSettings>();

        if (_debugSettings == null)
            _debugSettings = FindFirstObjectByType<GazeCalibrationDebugSettings>();
    }

    /// <summary>
    /// Writes an optional diagnostic message when gaze debugging is enabled.
    /// </summary>
    /// <param name="message">Message to send to Unity's log.</param>
    private void DebugLog(string message)
    {
        if (_debugSettings != null && _debugSettings.EnableDebugLogging)
            Debug.Log(message);
    }
}
