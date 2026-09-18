using UnityEngine;

public class AboutPrivacyPanelController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GameObject aboutPrivacyPanel;

    private const string PrivacyPolicyUrl =
        "https://xandnea.github.io/privacy/ecaruii/";

    private const string GooglePrivacyUrl =
        "https://policies.google.com/privacy";

    private const string GoogleTermsUrl =
        "https://policies.google.com/terms";

    public bool IsOpen =>
        aboutPrivacyPanel != null && aboutPrivacyPanel.activeSelf;

    public void OpenPanel()
    {
        if (aboutPrivacyPanel != null)
            aboutPrivacyPanel.SetActive(true);
    }

    public void ClosePanel()
    {
        if (aboutPrivacyPanel != null)
            aboutPrivacyPanel.SetActive(false);
    }

    public void TogglePanel()
    {
        if (IsOpen)
            ClosePanel();
        else
            OpenPanel();
    }

    public void OpenPrivacyPolicy()
    {
        Application.OpenURL(PrivacyPolicyUrl);
    }

    public void OpenGooglePrivacyPolicy()
    {
        Application.OpenURL(GooglePrivacyUrl);
    }

    public void OpenGoogleTerms()
    {
        Application.OpenURL(GoogleTermsUrl);
    }
}