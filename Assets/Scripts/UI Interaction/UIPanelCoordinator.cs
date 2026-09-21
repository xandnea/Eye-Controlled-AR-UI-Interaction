using UnityEngine;

/// <summary>
/// Keeps the Help and About &amp; Privacy panels mutually exclusive.
/// </summary>
public sealed class UIPanelCoordinator : MonoBehaviour
{
    [Header("Panel Controllers")]
    [Tooltip("Controller for the Help panel.")]
    [SerializeField] private HelpPanelController helpController;
    [Tooltip("Controller for the About & Privacy panel.")]
    [SerializeField] private AboutPrivacyPanelController aboutPrivacyController;

    /// <summary>Toggles Help, closing About &amp; Privacy before Help opens.</summary>
    public void ToggleHelp()
    {
        if (helpController == null)
        {
            Debug.LogError("[UIPanels] HelpPanelController is not assigned.", this);
            return;
        }

        if (helpController.IsOpen)
        {
            helpController.CloseHelp();
            return;
        }

        if (aboutPrivacyController != null)
            aboutPrivacyController.ClosePanel();

        helpController.OpenHelp();
    }

    /// <summary>Toggles About &amp; Privacy, closing Help before the panel opens.</summary>
    public void ToggleAboutPrivacy()
    {
        if (aboutPrivacyController == null)
        {
            Debug.LogError("[UIPanels] AboutPrivacyPanelController is not assigned.", this);
            return;
        }

        if (aboutPrivacyController.IsOpen)
        {
            aboutPrivacyController.ClosePanel();
            return;
        }

        if (helpController != null)
            helpController.CloseHelp();

        aboutPrivacyController.OpenPanel();
    }

    /// <summary>Closes every coordinated panel that is currently assigned.</summary>
    public void CloseAllPanels()
    {
        if (helpController != null)
            helpController.CloseHelp();

        if (aboutPrivacyController != null)
            aboutPrivacyController.ClosePanel();
    }
}
