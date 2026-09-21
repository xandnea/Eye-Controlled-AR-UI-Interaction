using UnityEngine;

/// <summary>
/// Adapts Inspector-wired UI controls into cursor and touch-inspection settings.
/// </summary>
public sealed class GazeInteractionSettingsUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Runtime gaze-interaction controller that receives the selected settings.")]
    [SerializeField]
    private GazeInteractionManager gazeInteractionManager;

    [Tooltip("Root of the interaction-settings panel opened by this component.")]
    [SerializeField]
    private GameObject settingsPanel;

    /// <summary>Toggles the interaction-settings panel when it is assigned.</summary>
    public void ToggleSettingsPanel()
    {
        if (settingsPanel == null)
            return;

        settingsPanel.SetActive(
            !settingsPanel.activeSelf
        );
    }

    /// <summary>Closes the interaction-settings panel when it is assigned.</summary>
    public void CloseSettingsPanel()
    {
        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }
    }


    #region Cursor Colors

    /// <summary>Sets the gaze cursor to red while preserving its current alpha.</summary>
    public void SetRed()
    {
        SetCursorColor(new Color(1f, 0f, 0f));
    }

    /// <summary>Sets the gaze cursor to orange while preserving its current alpha.</summary>
    public void SetOrange()
    {
        SetCursorColor(new Color(1f, 0.5f, 0f));
    }

    /// <summary>Sets the gaze cursor to yellow while preserving its current alpha.</summary>
    public void SetYellow()
    {
        SetCursorColor(new Color(1f, 1f, 0f));
    }

    /// <summary>Sets the gaze cursor to green while preserving its current alpha.</summary>
    public void SetGreen()
    {
        SetCursorColor(new Color(0f, 1f, 0f));
    }

    /// <summary>Sets the gaze cursor to blue while preserving its current alpha.</summary>
    public void SetBlue()
    {
        SetCursorColor(new Color(0f, 0.5f, 1f));
    }

    /// <summary>Sets the gaze cursor to purple while preserving its current alpha.</summary>
    public void SetPurple()
    {
        SetCursorColor(new Color(0.65f, 0.2f, 1f));
    }

    /// <summary>Sets the gaze cursor to black while preserving its current alpha.</summary>
    public void SetBlack()
    {
        SetCursorColor(Color.black);
    }

    /// <summary>Sets the gaze cursor to white while preserving its current alpha.</summary>
    public void SetWhite()
    {
        SetCursorColor(Color.white);
    }

    #endregion

    #region Other Settings

    /// <summary>Shows or hides the gaze cursor.</summary>
    /// <param name="hidden">True to hide the cursor image.</param>
    public void SetCursorHidden(bool hidden)
    {
        if (TryGetManager(out GazeInteractionManager manager))
            manager.SetCursorHidden(hidden);
    }

    /// <summary>Changes the gaze cursor opacity.</summary>
    /// <param name="alpha">Requested alpha; the manager clamps it to [0, 1].</param>
    public void SetCursorAlpha(float alpha)
    {
        if (TryGetManager(out GazeInteractionManager manager))
            manager.SetCursorAlpha(alpha);
    }

    /// <summary>Changes the gaze cursor scale multiplier.</summary>
    /// <param name="scale">Requested scale; the manager clamps it to its supported range.</param>
    public void SetCursorScale(float scale)
    {
        if (TryGetManager(out GazeInteractionManager manager))
            manager.SetCursorScale(scale);
    }

    /// <summary>Enables or disables touch-based inspection of detected objects.</summary>
    /// <param name="enabled">Whether touch inspection should be active.</param>
    public void SetTouchInspection(bool enabled)
    {
        if (TryGetManager(out GazeInteractionManager manager))
            manager.SetTouchInspectionEnabled(enabled);
    }

    #endregion

    /// <summary>Forwards a preset color when the runtime manager is available.</summary>
    /// <param name="color">RGB color to apply while preserving cursor alpha.</param>
    private void SetCursorColor(Color color)
    {
        if (TryGetManager(out GazeInteractionManager manager))
            manager.SetCursorColor(color);
    }

    /// <summary>Validates the settings target before a UI callback uses it.</summary>
    /// <param name="manager">Receives the assigned manager.</param>
    /// <returns>True when the manager is assigned.</returns>
    private bool TryGetManager(out GazeInteractionManager manager)
    {
        manager = gazeInteractionManager;

        if (manager != null)
            return true;

        Debug.LogError(
            "[GazeInteractionSettings] GazeInteractionManager is not assigned.",
            this);
        return false;
    }
}
