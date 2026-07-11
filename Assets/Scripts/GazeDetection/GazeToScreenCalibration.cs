using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class GazeToScreenCalibration : MonoBehaviour
{
    [Header("References")]
    public GazeVisualizer gazeVisualizer;
    [Tooltip("Assign 4 UI Images in this order: TopLeft, TopRight, BottomRight, BottomLeft")]
    public RectTransform[] cornerTargets;
    public RectTransform cursorIndicator; // The AR cursor that moves after calibration
    public Button startCalibrationButton;

    [Header("UI Controls")]
    [Tooltip("Slider to control X Offset")]
    public Slider offsetXSlider;
    [Tooltip("Slider to control Y Offset")]
    public Slider offsetYSlider;

    [Header("Settings")]
    [Tooltip("Padding from the absolute edge of the screen to prevent target clipping")]
    public float cornerPadding = 100f;
    [Range(0.5f, 5f)]
    public float dwellTimePerTarget = 2f;
    public Color activeColor = Color.green;
    public Color inactiveColor = Color.gray;

    [Header("Manual Tuning Offsets")]
    [Tooltip("Shifts the final cursor horizontally. 0.1 = 10% to the right.")]
    [Range(-1f, 1f)] public float offsetX = 0f;

    [Tooltip("Shifts the final cursor vertically. 0.1 = 10% upwards.")]
    [Range(-1f, 1f)] public float offsetY = 0f;

    // Captured biological bounds for the screen corners
    private Vector2 gazeTL, gazeTR, gazeBR, gazeBL;
    private bool isCalibrated = false;

    private void Start()
    {
        startCalibrationButton.gameObject.SetActive(false);
        if (cursorIndicator != null) cursorIndicator.gameObject.SetActive(false);
        foreach (var t in cornerTargets) t.gameObject.SetActive(false);

        // Sync sliders with initial inspector values
        if (offsetXSlider != null) offsetXSlider.value = offsetX;
        if (offsetYSlider != null) offsetYSlider.value = offsetY;

        PositionTargetsDynamically();
    }

    private void OnEnable()
    {
        if (startCalibrationButton != null)
            startCalibrationButton.onClick.AddListener(() => StartCoroutine(CalibrationSequence()));

        // Listen for slider changes
        if (offsetXSlider != null)
            offsetXSlider.onValueChanged.AddListener(val => offsetX = val);

        if (offsetYSlider != null)
            offsetYSlider.onValueChanged.AddListener(val => offsetY = val);
    }

    private void OnDisable()
    {
        if (startCalibrationButton != null)
            startCalibrationButton.onClick.RemoveAllListeners();

        // Stop listening to prevent memory leaks
        if (offsetXSlider != null)
            offsetXSlider.onValueChanged.RemoveAllListeners();

        if (offsetYSlider != null)
            offsetYSlider.onValueChanged.RemoveAllListeners();
    }

    private void PositionTargetsDynamically()
    {
        float w = Screen.width;
        float h = Screen.height;
        float p = cornerPadding;

        // Force anchors to Bottom-Left (0,0) so pixel coordinates map cleanly
        foreach (var target in cornerTargets)
        {
            target.anchorMin = Vector2.zero;
            target.anchorMax = Vector2.zero;
            target.pivot = new Vector2(0.5f, 0.5f);
        }

        // 0: Top Left, 1: Top Right, 2: Bottom Right, 3: Bottom Left
        cornerTargets[0].anchoredPosition = new Vector2(p, h - p);
        cornerTargets[1].anchoredPosition = new Vector2(w - p, h - p);
        cornerTargets[2].anchoredPosition = new Vector2(w - p, p);
        cornerTargets[3].anchoredPosition = new Vector2(p, p);
    }

    private IEnumerator CalibrationSequence()
    {
        Debug.Log("Beginning Gaze-to-Screen Calibration Sequence...");

        // 1. Turn all targets ON and reset to inactive color
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
            startCalibrationButton.gameObject.SetActive(true);
            return;
        }

        // Move the cursor indicator in real-time
        Vector2 currentGaze = gazeVisualizer.GetCurrentGazeVector();
        cursorIndicator.anchoredPosition = GetScreenPixel(currentGaze);
    }

    // The core math: Bilinear Interpolation
    public Vector2 GetScreenPixel(Vector2 currentGaze)
    {
        if (!isCalibrated) return Vector2.zero;

        // Average the X boundaries from the left and right sides
        float leftX = (gazeTL.x + gazeBL.x) / 2f;
        float rightX = (gazeTR.x + gazeBR.x) / 2f;
        float tx = Mathf.InverseLerp(leftX, rightX, currentGaze.x);

        // Average the Y boundaries from the top and bottom sides
        float bottomY = (gazeBL.y + gazeBR.y) / 2f;
        float topY = (gazeTL.y + gazeTR.y) / 2f;
        float ty = Mathf.InverseLerp(bottomY, topY, currentGaze.y);

        // Apply the manual Global Offset (acting as a percentage shift)
        tx += offsetX;
        ty += offsetY;

        // Map the normalized 0-1 values to physical screen pixels
        return new Vector2(tx * Screen.width, ty * Screen.height);
    }
}