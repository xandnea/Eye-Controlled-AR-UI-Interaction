using UnityEngine;

/// <summary>
/// Controls the Help panel and automatically opens it the first time the app is run.
/// </summary>
public sealed class HelpPanelController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Root GameObject of the Help panel.")]
    [SerializeField] private GameObject helpPanel;
    [Tooltip("Button used to reopen Help after the panel is closed.")]
    [SerializeField] private GameObject helpButton;
    [Tooltip("Optional gaze-selection ring associated with the Help button.")]
    [SerializeField] private GameObject helpOuterCircle;

    /// <summary>Gets whether the Help panel is assigned and currently active.</summary>
    public bool IsOpen => helpPanel != null && helpPanel.activeSelf;

    private const string HelpSeenKey = "HelpPanelSeen_v1";

    private void Start()
    {
        if (PlayerPrefs.GetInt(HelpSeenKey, 0) == 0)
        {
            OpenHelp();
            PlayerPrefs.SetInt(HelpSeenKey, 1);
            PlayerPrefs.Save();
        }
        else
        {
            CloseHelp();
        }
    }

    /// <summary>Shows Help and hides its launcher button.</summary>
    public void OpenHelp()
    {
        if (helpPanel != null)
            helpPanel.SetActive(true);

        SetHelpButtonVisible(false);
    }

    /// <summary>Hides Help and restores its launcher button.</summary>
    public void CloseHelp()
    {
        if (helpPanel != null)
            helpPanel.SetActive(false);

        SetHelpButtonVisible(true);
    }

    /// <summary>Applies launcher visibility and clears any lingering selection ring.</summary>
    /// <param name="visible">Whether the Help launcher button should be visible.</param>
    private void SetHelpButtonVisible(bool visible)
    {
        if (helpButton != null)
            helpButton.SetActive(visible);

        if (helpOuterCircle != null)
            helpOuterCircle.SetActive(false);
    }
}
