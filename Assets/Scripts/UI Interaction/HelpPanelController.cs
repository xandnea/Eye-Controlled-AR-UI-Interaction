using UnityEngine;

public sealed class HelpPanelController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject helpPanel;
    [SerializeField] private GameObject helpButton;
    [SerializeField] private GameObject helpOuterCircle;

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

    public void OpenHelp()
    {
        if (helpPanel != null)
            helpPanel.SetActive(true);

        SetHelpButtonVisible(false);
    }

    public void CloseHelp()
    {
        if (helpPanel != null)
            helpPanel.SetActive(false);

        SetHelpButtonVisible(true);
    }

    private void SetHelpButtonVisible(bool visible)
    {
        if (helpButton != null)
            helpButton.SetActive(visible);

        if (helpOuterCircle != null)
            helpOuterCircle.SetActive(false);
    }
}