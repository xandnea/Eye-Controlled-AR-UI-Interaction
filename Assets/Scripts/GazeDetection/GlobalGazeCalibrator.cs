using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class GlobalGazeCalibrator : MonoBehaviour
{
    [Header("References")]
    public GazeVisualizer gazeVisualizer;
    [Tooltip("Assign 4 UI Images in this order: TopLeft, TopRight, BottomRight, BottomLeft")]
    public RectTransform[] cornerTargets;
    public RectTransform cursorIndicator; // The AR cursor that moves after calibration

    [Header("Settings")]
    [Tooltip("Padding from the absolute edge of the screen to prevent target clipping")]
    public float cornerXPadding = 50f;
    public float cornerYPadding = 50f;
    [Range(0.5f, 5f)]
    public float dwellTimePerTarget = 2f;
    public Color activeColor = Color.green;
    public Color inactiveColor = Color.gray;

    // Captured biological bounds for the screen corners
    private Vector2 gazeTL, gazeTR, gazeBR, gazeBL;
    private bool isCalibrated = false;

    // Tracks the active sequence so it can be interrupted
    private Coroutine _activeCalibrationRoutine;

    private void Start()
    {
        if (cursorIndicator != null) cursorIndicator.gameObject.SetActive(false);
        foreach (var t in cornerTargets) t.gameObject.SetActive(false);
    }

    private void OnEnable()
    {

    }

    private void OnDisable()
    {
        // Safety cleanup if script is disabled mid-calibration
        if (_activeCalibrationRoutine != null)
        {
            StopCoroutine(_activeCalibrationRoutine);
            _activeCalibrationRoutine = null;
        }
    }

    public void RunGlobalGazeCalibration()
    {
        if (_activeCalibrationRoutine != null)
        {
            StopCoroutine(_activeCalibrationRoutine);
            _activeCalibrationRoutine = null;
            ResetTargets();
            Debug.Log("Previous calibration stopped. Restarting...");
        }

        _activeCalibrationRoutine = StartCoroutine(CalibrationSequence());
    }

    private void ResetTargets()
    {
        foreach (var t in cornerTargets)
        {
            t.GetComponent<Image>().color = inactiveColor;
            t.gameObject.SetActive(false);
        }

        // hide the cursor
        if (cursorIndicator != null) cursorIndicator.gameObject.SetActive(false);

        isCalibrated = false;
    }

    private IEnumerator CalibrationSequence()
    {
        Debug.Log("Beginning Gaze-to-Screen Calibration Sequence...");

        // Turn all targets ON and reset to inactive color
        foreach (var t in cornerTargets)
        {
            t.gameObject.SetActive(true);
            t.GetComponent<Image>().color = inactiveColor;
        }

        // Run the 4-corner collection sequence
        Debug.Log("Collecting Data...");
        yield return StartCoroutine(CollectCornerData(0, result => gazeTL = result));
        yield return StartCoroutine(CollectCornerData(1, result => gazeTR = result));
        yield return StartCoroutine(CollectCornerData(2, result => gazeBR = result));
        yield return StartCoroutine(CollectCornerData(3, result => gazeBL = result));

        isCalibrated = true;
        Debug.Log("Calibration Complete! Bi-linear mapping is now active.");

        // Hide targets and show cursor
        foreach (var t in cornerTargets) t.gameObject.SetActive(false);
        if (cursorIndicator != null) cursorIndicator.gameObject.SetActive(true);

        // Sequence is completely finished, clear the tracker
        _activeCalibrationRoutine = null;
    }

    private IEnumerator CollectCornerData(int targetIndex, System.Action<Vector2> onCornerCalibrated)
    {
        Image targetImage = cornerTargets[targetIndex].GetComponent<Image>();
        targetImage.color = activeColor;

        // Give the user 1 second to snap their eyes to the new target before recording
        yield return new WaitForSeconds(1f);

        float timer = 0f;
        Vector2 gazeSum = Vector2.zero;
        int samples = 0;

        // Collect biological tracking data over the dwell time
        while (timer < dwellTimePerTarget)
        {
            timer += Time.deltaTime;
            gazeSum += gazeVisualizer.GetCurrentGazeVector();
            samples++;
            yield return null;
        }

        targetImage.color = inactiveColor;
        onCornerCalibrated(gazeSum / samples); // Save the averaged vector
    }

    private void Update()
    {
        if (!isCalibrated || cursorIndicator == null)
        {
            return;
        }

        // Move the cursor indicator in real-time
        Vector2 currentGaze = gazeVisualizer.GetCurrentGazeVector();
        cursorIndicator.anchoredPosition = GetScreenPixel(currentGaze);
    }

    // The core math: Bilinear Interpolation
    public Vector2 GetScreenPixel(Vector2 currentGaze)
    {
        if (!isCalibrated || cursorIndicator == null) return Vector2.zero;

        // Keep the biological boundaries pure
        float leftX = (gazeTL.x + gazeBL.x) / 2f;
        float rightX = (gazeTR.x + gazeBR.x) / 2f;
        float tx = Mathf.InverseLerp(leftX, rightX, currentGaze.x);

        float bottomY = (gazeBL.y + gazeBR.y) / 2f;
        float topY = (gazeTL.y + gazeTR.y) / 2f;
        float ty = Mathf.InverseLerp(bottomY, topY, currentGaze.y);

        tx = Mathf.Clamp01(tx);
        ty = Mathf.Clamp01(ty);

        // Get the parent canvas dimensions so it respects the Canvas Scaler resolution
        RectTransform canvasRect = cursorIndicator.GetComponentInParent<Canvas>().GetComponent<RectTransform>();
        float canvasWidth = canvasRect.rect.width;
        float canvasHeight = canvasRect.rect.height;

        // Map to canvas coordinate space
        float rawPixelX = tx * canvasWidth;
        float rawPixelY = ty * canvasHeight;

        // Account for the cursor's size and center pivot (0.5, 0.5) 
        Vector2 cursorSize = cursorIndicator.rect.size;
        float halfWidth = cursorSize.x * 0.5f;
        float halfHeight = cursorSize.y * 0.5f;

        float finalPixelX = Mathf.Clamp(rawPixelX, halfWidth, canvasWidth - halfWidth);
        float finalPixelY = Mathf.Clamp(rawPixelY, halfHeight, canvasHeight - halfHeight);

        return new Vector2(finalPixelX, finalPixelY);
    }
}