using UnityEngine;

public sealed class GazeInteractionSettingsUI : MonoBehaviour
{
    [SerializeField]
    private GazeInteractionManager gazeInteractionManager;

    [SerializeField]
    private GameObject settingsPanel;


    public void ToggleSettingsPanel()
    {
        if (settingsPanel == null)
            return;

        settingsPanel.SetActive(
            !settingsPanel.activeSelf
        );
    }


    public void CloseSettingsPanel()
    {
        if (settingsPanel != null)
        {
            settingsPanel.SetActive(false);
        }
    }


    // ============================================================
    // CURSOR COLORS
    // ============================================================

    public void SetRed()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(1f, 0f, 0f)
        );
    }

    public void SetOrange()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(1f, 0.5f, 0f)
        );
    }

    public void SetYellow()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(1f, 1f, 0f)
        );
    }

    public void SetGreen()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(0f, 1f, 0f)
        );
    }

    public void SetBlue()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(0f, 0.5f, 1f)
        );
    }

    public void SetPurple()
    {
        gazeInteractionManager.SetCursorColor(
            new Color(0.65f, 0.2f, 1f)
        );
    }

    public void SetBlack()
    {
        gazeInteractionManager.SetCursorColor(
            Color.black
        );
    }

    public void SetWhite()
    {
        gazeInteractionManager.SetCursorColor(
            Color.white
        );
    }


    // ============================================================
    // OTHER SETTINGS
    // ============================================================

    public void SetCursorHidden(bool hidden)
    {
        gazeInteractionManager.SetCursorHidden(
            hidden
        );
    }


    public void SetCursorAlpha(float alpha)
    {
        gazeInteractionManager.SetCursorAlpha(
            alpha
        );
    }

    public void SetCursorScale(float scale)
    {
        gazeInteractionManager.SetCursorScale(scale);
    }


    public void SetTouchInspection(bool enabled)
    {
        gazeInteractionManager
            .SetTouchInspectionEnabled(enabled);
    }
}