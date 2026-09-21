using UnityEngine;

/// <summary>
/// Switches the shared calibration instructions between local, global, and hidden states.
/// </summary>
public sealed class CalibrationInstructionsUI : MonoBehaviour
{
    [Header("Instruction Elements")]
    [Tooltip("Shared instruction text displayed for either calibration mode.")]
    [SerializeField] private GameObject instructionText;
    [Tooltip("Instruction image displayed before local eye-range calibration.")]
    [SerializeField] private GameObject localCalibrationInstructionImage;
    [Tooltip("Instruction image displayed before global screen calibration.")]
    [SerializeField] private GameObject globalCalibrationInstructionImage;

    private void Start()
    {
        Hide();
    }

    /// <summary>Shows the shared text and local-calibration instruction image.</summary>
    public void ShowLocal()
    {
        if (instructionText != null)
            instructionText.SetActive(true);

        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(true);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(false);
    }

    /// <summary>Shows the shared text and global-calibration instruction image.</summary>
    public void ShowGlobal()
    {
        if (instructionText != null)
            instructionText.SetActive(true);

        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(false);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(true);
    }

    /// <summary>Hides both calibration images while leaving the shared text unchanged.</summary>
    public void HideImages()
    {
        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(false);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(false);
    }

    /// <summary>Hides the shared instruction text and both calibration images.</summary>
    public void Hide()
    {
        if (instructionText != null)
            instructionText.SetActive(false);

        HideImages();
    }
}
