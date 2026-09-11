using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Central development/debug controller for the gaze system.
///
/// Attach this component to the overhead "Gaze Calibration" GameObject. It owns the
/// optional diagnostic-log switch and the temporary runtime tuning UI used to adjust
/// cursor filter speed and deadzone on-device.
///
/// The actual gaze settings remain owned by GlobalGazeCalibrator. This controller is
/// only a development interface to those settings and can be removed later without
/// changing gaze extraction, calibration, or cursor behavior.
/// </summary>
public class GazeDebugController : MonoBehaviour
{
    [Header("Debugging")]
    [Tooltip("Enables detailed gaze extraction and calibration diagnostics in Development Builds and the Unity Editor.")]
    [SerializeField] private bool enableDebugLogging = false;

    [Header("Runtime Debug UI")]
    [Tooltip("Root GameObject containing all temporary debug UI. Disabled automatically in non-development builds.")]
    [SerializeField] private GameObject debugUiRoot;

    [SerializeField] private GameObject debugPanel;
    [SerializeField] private Button togglePanelButton;

    [Header("Cursor Tuning")]
    [SerializeField] private GlobalGazeCalibrator globalGazeCalibrator;
    [SerializeField] private Slider filterSpeedSlider;
    [SerializeField] private Slider deadzoneSlider;
    [SerializeField] private TextMeshProUGUI filterSpeedValueText;
    [SerializeField] private TextMeshProUGUI deadzoneValueText;

    [Header("Optional Buttons")]
    [SerializeField] private Button resetFilterButton;
    [SerializeField] private Button restoreDefaultsButton;
    [SerializeField] private Button logSettingsButton;

    [Header("Panel")]
    [SerializeField] private bool startPanelOpen = false;

    private int _startupFilterSpeed;
    private int _startupDeadzonePixels;
    private bool _callbacksRegistered;

    /// <summary>
    /// Gets whether optional gaze diagnostic logging is currently enabled.
    ///
    /// Diagnostics are forcibly disabled in non-development player builds even if
    /// the serialized checkbox was accidentally left enabled.
    /// </summary>
    public bool EnableDebugLogging
    {
        get
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return enableDebugLogging;
#else
            return false;
#endif
        }
    }

    private void Awake()
    {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
        if (debugUiRoot != null)
            debugUiRoot.SetActive(false);

        return;
#else
        ResolveReferences();

        if (globalGazeCalibrator == null)
        {
            Debug.LogError(
                "[GazeDebug] GlobalGazeCalibrator reference is missing. " +
                "Runtime gaze tuning is unavailable.",
                this);

            if (debugUiRoot != null)
                debugUiRoot.SetActive(false);

            return;
        }

        _startupFilterSpeed = globalGazeCalibrator.GazeFilterSpeed;
        _startupDeadzonePixels = globalGazeCalibrator.GazeDeadzonePixels;

        ConfigureControls();
        RegisterCallbacks();
        SetPanelVisible(startPanelOpen);
        RefreshUi();
#endif
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        UnregisterCallbacks();
#endif
    }

    /// <summary>
    /// Shows or hides the runtime tuning panel without changing the current settings.
    /// </summary>
    public void TogglePanel()
    {
        if (debugPanel != null)
            SetPanelVisible(!debugPanel.activeSelf);
    }

    /// <summary>
    /// Explicitly sets runtime tuning-panel visibility.
    /// </summary>
    /// <param name="visible">True to show the panel; false to hide it.</param>
    public void SetPanelVisible(bool visible)
    {
        if (debugPanel != null)
            debugPanel.SetActive(visible);
    }

    /// <summary>
    /// Applies the slider's filter-speed value to GlobalGazeCalibrator immediately.
    /// Higher values follow gaze more quickly and apply less temporal smoothing.
    /// </summary>
    /// <param name="value">Slider value in the calibrator's supported range.</param>
    public void OnFilterSpeedChanged(float value)
    {
        if (globalGazeCalibrator == null)
            return;

        globalGazeCalibrator.GazeFilterSpeed = Mathf.RoundToInt(value);

        if (filterSpeedSlider != null)
            filterSpeedSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeFilterSpeed);

        RefreshFilterSpeedLabel();
    }

    /// <summary>
    /// Applies the slider's continuous radial deadzone radius in canvas pixels.
    /// </summary>
    /// <param name="value">Deadzone radius in the calibrator's supported range.</param>
    public void OnDeadzoneChanged(float value)
    {
        if (globalGazeCalibrator == null)
            return;

        globalGazeCalibrator.GazeDeadzonePixels = Mathf.RoundToInt(value);

        if (deadzoneSlider != null)
            deadzoneSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeDeadzonePixels);

        RefreshDeadzoneLabel();
    }

    /// <summary>
    /// Clears only the cursor's current smoothing history. Calibration coefficients
    /// and tuning values remain unchanged.
    /// </summary>
    public void ResetFilter()
    {
        if (globalGazeCalibrator == null)
            return;

        globalGazeCalibrator.ResetCursorFilter();

        if (EnableDebugLogging)
            Debug.Log("[GazeDebug] Cursor filter state reset.");
    }

    /// <summary>
    /// Restores the filter speed and deadzone that were active when the scene started,
    /// then clears the current smoothing history.
    /// </summary>
    public void RestoreDefaults()
    {
        if (globalGazeCalibrator == null)
            return;

        globalGazeCalibrator.GazeFilterSpeed = _startupFilterSpeed;
        globalGazeCalibrator.GazeDeadzonePixels = _startupDeadzonePixels;
        globalGazeCalibrator.ResetCursorFilter();

        if (filterSpeedSlider != null)
            filterSpeedSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeFilterSpeed);

        if (deadzoneSlider != null)
            deadzoneSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeDeadzonePixels);

        RefreshUi();

        Debug.Log(
            $"[GazeTune] Restored defaults | " +
            $"FilterSpeed={globalGazeCalibrator.GazeFilterSpeed} | " +
            $"Deadzone={globalGazeCalibrator.GazeDeadzonePixels}px");
    }

    /// <summary>
    /// Writes the active cursor-tuning values to the Unity/Android log so a preferred
    /// combination can be recovered after an on-device testing session.
    /// </summary>
    public void LogCurrentSettings()
    {
        if (globalGazeCalibrator == null)
            return;

        Debug.Log(
            $"[GazeTune] FilterSpeed={globalGazeCalibrator.GazeFilterSpeed} | " +
            $"Deadzone={globalGazeCalibrator.GazeDeadzonePixels}px");
    }

    /// <summary>
    /// Resolves the calibrator from the same GameObject/parent hierarchy when the
    /// Inspector reference was not assigned.
    /// </summary>
    private void ResolveReferences()
    {
        if (globalGazeCalibrator == null)
            globalGazeCalibrator = GetComponent<GlobalGazeCalibrator>();

        if (globalGazeCalibrator == null)
            globalGazeCalibrator = GetComponentInParent<GlobalGazeCalibrator>();
    }

    /// <summary>
    /// Makes GlobalGazeCalibrator the single source of truth for slider ranges and
    /// initializes both sliders from the currently serialized gaze settings.
    /// </summary>
    private void ConfigureControls()
    {
        if (filterSpeedSlider != null)
        {
            filterSpeedSlider.minValue = GlobalGazeCalibrator.MinGazeFilterSpeed;
            filterSpeedSlider.maxValue = GlobalGazeCalibrator.MaxGazeFilterSpeed;
            filterSpeedSlider.wholeNumbers = true;
            filterSpeedSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeFilterSpeed);
        }

        if (deadzoneSlider != null)
        {
            deadzoneSlider.minValue = GlobalGazeCalibrator.MinGazeDeadzonePixels;
            deadzoneSlider.maxValue = GlobalGazeCalibrator.MaxGazeDeadzonePixels;
            deadzoneSlider.wholeNumbers = true;
            deadzoneSlider.SetValueWithoutNotify(globalGazeCalibrator.GazeDeadzonePixels);
        }
    }

    /// <summary>
    /// Registers all temporary debug-UI callbacks in code so the Slider/Button event
    /// lists can remain empty in the Unity Inspector.
    /// </summary>
    private void RegisterCallbacks()
    {
        if (_callbacksRegistered)
            return;

        if (togglePanelButton != null)
            togglePanelButton.onClick.AddListener(TogglePanel);

        if (filterSpeedSlider != null)
            filterSpeedSlider.onValueChanged.AddListener(OnFilterSpeedChanged);

        if (deadzoneSlider != null)
            deadzoneSlider.onValueChanged.AddListener(OnDeadzoneChanged);

        if (resetFilterButton != null)
            resetFilterButton.onClick.AddListener(ResetFilter);

        if (restoreDefaultsButton != null)
            restoreDefaultsButton.onClick.AddListener(RestoreDefaults);

        if (logSettingsButton != null)
            logSettingsButton.onClick.AddListener(LogCurrentSettings);

        _callbacksRegistered = true;
    }

    /// <summary>
    /// Removes only the callbacks registered by this component.
    /// </summary>
    private void UnregisterCallbacks()
    {
        if (!_callbacksRegistered)
            return;

        if (togglePanelButton != null)
            togglePanelButton.onClick.RemoveListener(TogglePanel);

        if (filterSpeedSlider != null)
            filterSpeedSlider.onValueChanged.RemoveListener(OnFilterSpeedChanged);

        if (deadzoneSlider != null)
            deadzoneSlider.onValueChanged.RemoveListener(OnDeadzoneChanged);

        if (resetFilterButton != null)
            resetFilterButton.onClick.RemoveListener(ResetFilter);

        if (restoreDefaultsButton != null)
            restoreDefaultsButton.onClick.RemoveListener(RestoreDefaults);

        if (logSettingsButton != null)
            logSettingsButton.onClick.RemoveListener(LogCurrentSettings);

        _callbacksRegistered = false;
    }

    /// <summary>
    /// Refreshes all displayed tuning values from GlobalGazeCalibrator.
    /// </summary>
    private void RefreshUi()
    {
        RefreshFilterSpeedLabel();
        RefreshDeadzoneLabel();
    }

    private void RefreshFilterSpeedLabel()
    {
        if (filterSpeedValueText != null && globalGazeCalibrator != null)
            filterSpeedValueText.text = $"Filter Speed: {globalGazeCalibrator.GazeFilterSpeed}";
    }

    private void RefreshDeadzoneLabel()
    {
        if (deadzoneValueText != null && globalGazeCalibrator != null)
            deadzoneValueText.text = $"Deadzone: {globalGazeCalibrator.GazeDeadzonePixels} px";
    }
}
