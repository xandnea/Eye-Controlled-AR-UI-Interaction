using UnityEngine;

public class UIPanelCoordinator : MonoBehaviour
{
    [Header("Panel Controllers")]
    [SerializeField] private HelpPanelController helpController;
    [SerializeField] private AboutPrivacyPanelController aboutPrivacyController;

    public void ToggleHelp()
    {
        if (helpController.IsOpen)
        {
            helpController.CloseHelp();
            return;
        }

        aboutPrivacyController.ClosePanel();
        helpController.OpenHelp();
    }

    public void ToggleAboutPrivacy()
    {
        if (aboutPrivacyController.IsOpen)
        {
            aboutPrivacyController.ClosePanel();
            return;
        }

        helpController.CloseHelp();
        aboutPrivacyController.OpenPanel();
    }

    public void CloseAllPanels()
    {
        helpController.CloseHelp();
        aboutPrivacyController.ClosePanel();
    }
}