using UnityEngine;

public sealed class CalibrationInstructionsUI : MonoBehaviour
{
    [SerializeField] private GameObject instructionText;
    [SerializeField] private GameObject localCalibrationInstructionImage;
    [SerializeField] private GameObject globalCalibrationInstructionImage;

    private void Start()
    {
        Hide();
    }

    public void ShowLocal()
    {
        if (instructionText != null)
            instructionText.SetActive(true);

        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(true);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(false);
    }

    public void ShowGlobal()
    {
        if (instructionText != null)
            instructionText.SetActive(true);

        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(false);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(true);
    }

    public void HideImages()
    {
        if (localCalibrationInstructionImage != null)
            localCalibrationInstructionImage.SetActive(false);

        if (globalCalibrationInstructionImage != null)
            globalCalibrationInstructionImage.SetActive(false);
    }

    public void Hide()
    {
        if (instructionText != null)
            instructionText.SetActive(false);

        HideImages();
    }
}