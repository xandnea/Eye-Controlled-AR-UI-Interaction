using UnityEngine;

/// <summary>
/// Controls the About &amp; Privacy panel and opens the external policy links shown in it.
/// </summary>
public sealed class AboutPrivacyPanelController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Root GameObject of the About & Privacy panel.")]
    [SerializeField] private GameObject aboutPrivacyPanel;

    private const string PrivacyPolicyUrl =
        "https://xandnea.github.io/privacy/ecaruii/";

    private const string GooglePrivacyUrl =
        "https://policies.google.com/privacy";

    private const string GoogleTermsUrl =
        "https://policies.google.com/terms";

    /// <summary>Gets whether the panel is assigned and currently active.</summary>
    public bool IsOpen =>
        aboutPrivacyPanel != null && aboutPrivacyPanel.activeSelf;

    /// <summary>Shows the About &amp; Privacy panel when it is assigned.</summary>
    public void OpenPanel()
    {
        if (aboutPrivacyPanel != null)
            aboutPrivacyPanel.SetActive(true);
    }

    /// <summary>Hides the About &amp; Privacy panel when it is assigned.</summary>
    public void ClosePanel()
    {
        if (aboutPrivacyPanel != null)
            aboutPrivacyPanel.SetActive(false);
    }

    /// <summary>Toggles the About &amp; Privacy panel between open and closed.</summary>
    public void TogglePanel()
    {
        if (IsOpen)
            ClosePanel();
        else
            OpenPanel();
    }

    /// <summary>Opens ECARUII's privacy policy in the system browser.</summary>
    public void OpenPrivacyPolicy()
    {
        Application.OpenURL(PrivacyPolicyUrl);
    }

    /// <summary>Opens Google's privacy policy in the system browser.</summary>
    public void OpenGooglePrivacyPolicy()
    {
        Application.OpenURL(GooglePrivacyUrl);
    }

    /// <summary>Opens Google's terms of service in the system browser.</summary>
    public void OpenGoogleTerms()
    {
        Application.OpenURL(GoogleTermsUrl);
    }
}
