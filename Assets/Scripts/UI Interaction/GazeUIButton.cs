using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public enum GazeUIButtonSelectionMode
{
    ExpandToOuterCircle,
    FillOverlay
}

[DisallowMultipleComponent]
[RequireComponent(typeof(Button))]
public sealed class GazeUIButton : MonoBehaviour
{
    [Header("Selection Style")]
    [SerializeField]
    private GazeUIButtonSelectionMode selectionMode =
        GazeUIButtonSelectionMode.ExpandToOuterCircle;

    [Header("Expand To Outer Circle")]
    [Tooltip(
        "Fixed-size circle shown behind/around the button while it expands. " +
        "Keep this outside the button hierarchy (usually a sibling) so it does not scale with the button.")]
    [SerializeField] private GameObject outerCircle;

    [Header("Fill Overlay")]
    [Tooltip(
        "Gray radial Image placed over the button. Set Image Type to Filled, " +
        "Fill Method to Radial 360, and Raycast Target off.")]
    [SerializeField] private Image fillOverlay;

    private Button button;
    private RectTransform buttonRect;
    private Vector3 baseScale;
    private Coroutine activationCoroutine;
    private bool initialized;

    public bool IsAvailable =>
        enabled &&
        gameObject.activeInHierarchy &&
        Button != null &&
        Button.IsInteractable();

    private Button Button
    {
        get
        {
            if (button == null)
                button = GetComponent<Button>();

            return button;
        }
    }

    private void Awake()
    {
        Initialize();
    }

    private void OnEnable()
    {
        Initialize();
        ResetSelectionVisuals();
    }

    private void OnDisable()
    {
        if (activationCoroutine != null)
        {
            StopCoroutine(activationCoroutine);
            activationCoroutine = null;
        }

        ResetSelectionVisualsImmediate();
    }

    public void BeginSelection()
    {
        Initialize();

        switch (selectionMode)
        {
            case GazeUIButtonSelectionMode.ExpandToOuterCircle:
                if (outerCircle != null)
                    outerCircle.SetActive(true);

                if (activationCoroutine == null && buttonRect != null)
                    buttonRect.localScale = baseScale;
                break;

            case GazeUIButtonSelectionMode.FillOverlay:
                if (fillOverlay != null)
                {
                    fillOverlay.fillAmount = 0f;
                    fillOverlay.gameObject.SetActive(true);
                }
                break;
        }
    }

    public void SetSelectionProgress(float progress, float selectedScale)
    {
        Initialize();
        progress = Mathf.Clamp01(progress);

        switch (selectionMode)
        {
            case GazeUIButtonSelectionMode.ExpandToOuterCircle:
                if (outerCircle != null && !outerCircle.activeSelf)
                    outerCircle.SetActive(true);

                if (buttonRect != null)
                {
                    Vector3 targetScale = baseScale * selectedScale;
                    buttonRect.localScale = Vector3.Lerp(baseScale, targetScale, progress);
                }
                break;

            case GazeUIButtonSelectionMode.FillOverlay:
                if (fillOverlay != null)
                {
                    if (!fillOverlay.gameObject.activeSelf)
                        fillOverlay.gameObject.SetActive(true);

                    fillOverlay.fillAmount = progress;
                }
                break;
        }
    }

    public void ResetSelectionVisuals()
    {
        Initialize();

        switch (selectionMode)
        {
            case GazeUIButtonSelectionMode.ExpandToOuterCircle:
                if (outerCircle != null)
                    outerCircle.SetActive(false);

                if (activationCoroutine == null && buttonRect != null)
                    buttonRect.localScale = baseScale;
                break;

            case GazeUIButtonSelectionMode.FillOverlay:
                if (fillOverlay != null)
                {
                    fillOverlay.fillAmount = 0f;
                    fillOverlay.gameObject.SetActive(false);
                }
                break;
        }
    }

    public void Activate(float releaseDuration)
    {
        Initialize();

        if (!IsAvailable)
        {
            ResetSelectionVisuals();
            return;
        }

        if (selectionMode == GazeUIButtonSelectionMode.FillOverlay)
        {
            ResetSelectionVisuals();
            Button.onClick.Invoke();
            return;
        }

        if (activationCoroutine != null)
            StopCoroutine(activationCoroutine);

        activationCoroutine = StartCoroutine(ReleaseAndInvoke(releaseDuration));
    }

    private IEnumerator ReleaseAndInvoke(float releaseDuration)
    {
        if (buttonRect == null)
        {
            activationCoroutine = null;
            Button.onClick.Invoke();
            yield break;
        }

        Vector3 startScale = buttonRect.localScale;
        float duration = Mathf.Max(0f, releaseDuration);

        if (duration > 0f)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;

                float t = Mathf.Clamp01(elapsed / duration);
                t = Mathf.SmoothStep(0f, 1f, t);

                buttonRect.localScale = Vector3.Lerp(startScale, baseScale, t);
                yield return null;
            }
        }

        buttonRect.localScale = baseScale;

        if (outerCircle != null)
            outerCircle.SetActive(false);

        activationCoroutine = null;
        Button.onClick.Invoke();
    }

    private void Initialize()
    {
        if (initialized)
            return;

        button = GetComponent<Button>();
        buttonRect = GetComponent<RectTransform>();

        if (buttonRect != null)
            baseScale = buttonRect.localScale;

        initialized = true;
        ResetSelectionVisualsImmediate();

        if (selectionMode == GazeUIButtonSelectionMode.ExpandToOuterCircle &&
            outerCircle == null)
        {
            Debug.LogWarning(
                $"[GazeUIButton] {name} uses Expand To Outer Circle but has no Outer Circle assigned.",
                this
            );
        }

        if (selectionMode == GazeUIButtonSelectionMode.FillOverlay &&
            fillOverlay == null)
        {
            Debug.LogWarning(
                $"[GazeUIButton] {name} uses Fill Overlay but has no Fill Overlay assigned.",
                this
            );
        }
    }

    private void ResetSelectionVisualsImmediate()
    {
        if (outerCircle != null)
            outerCircle.SetActive(false);

        if (fillOverlay != null)
        {
            fillOverlay.fillAmount = 0f;
            fillOverlay.gameObject.SetActive(false);
        }

        if (buttonRect != null)
            buttonRect.localScale = baseScale;
    }
}
