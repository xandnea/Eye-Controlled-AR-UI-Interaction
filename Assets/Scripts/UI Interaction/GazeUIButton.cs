using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Visual feedback style used while gaze dwell progresses on a UI button.</summary>
public enum GazeUIButtonSelectionMode
{
    /// <summary>Expands the button toward a fixed reference circle.</summary>
    ExpandToOuterCircle,
    /// <summary>Fills a radial overlay without changing the button scale.</summary>
    FillOverlay
}

/// <summary>
/// Adds dwell-selection feedback and activation behavior to a standard Unity UI Button.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Button))]
public sealed class GazeUIButton : MonoBehaviour
{
    [Header("Selection Style")]
    [Tooltip("Visual feedback used while gaze dwell progresses on this button.")]
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

    /// <summary>Gets whether this component and its Button can currently be selected.</summary>
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

    /// <summary>Initializes the selected feedback when gaze first enters this button.</summary>
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

    /// <summary>Updates the selected feedback for the current dwell progress.</summary>
    /// <param name="progress">Normalized dwell progress in [0, 1].</param>
    /// <param name="selectedScale">Scale multiplier reached at full progress in expand mode.</param>
    public void SetSelectionProgress(float progress, float selectedScale)
    {
        Initialize();
        progress = Mathf.Clamp01(progress);
        selectedScale = Mathf.Max(0f, selectedScale);

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

    /// <summary>Clears dwell feedback without interrupting an active release animation.</summary>
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

    /// <summary>
    /// Activates the Button immediately in fill mode or after the expand-mode release animation.
    /// </summary>
    /// <param name="releaseDuration">Seconds used to return an expanded button to its base scale.</param>
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

    /// <summary>Returns an expanded button to its base scale, then invokes its click event.</summary>
    /// <param name="releaseDuration">Requested unscaled animation duration in seconds.</param>
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

    /// <summary>Caches required components and validates the selected feedback configuration.</summary>
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

    /// <summary>Immediately restores all supported feedback elements to their idle state.</summary>
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
