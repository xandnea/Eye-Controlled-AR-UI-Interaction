using System.Collections;
using Gilzoide.LottiePlayer;
using UnityEngine;

/// <summary>
/// Warms up and controls the non-interactive Lottie overlay shown during a scan.
/// </summary>
public sealed class ScanAnimationOverlayController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Lottie player that renders the looping scan animation.")]
    [SerializeField] private ImageLottiePlayer lottiePlayer;
    [Tooltip("CanvasGroup used to reveal or hide the scan overlay without blocking input.")]
    [SerializeField] private CanvasGroup canvasGroup;

    private IEnumerator Start()
    {
        SetVisible(false);

        if (lottiePlayer == null)
            yield break;

        // Render a couple frames invisibly so the native Lottie
        // player and render texture are initialized before first use.
        lottiePlayer.Play(0f);

        yield return null;
        yield return null;

        lottiePlayer.Pause();
    }

    /// <summary>Shows the overlay and restarts the Lottie animation from the beginning.</summary>
    public void ShowLooping()
    {
        if (lottiePlayer == null)
        {
            Debug.LogError("[ScanAnimation] Missing ImageLottiePlayer.", this);
            return;
        }

        SetVisible(true);
        lottiePlayer.Play(0f);
    }

    /// <summary>Pauses the animation and hides the overlay.</summary>
    public void StopAndHide()
    {
        if (lottiePlayer != null)
            lottiePlayer.Pause();

        SetVisible(false);
    }

    /// <summary>Applies overlay visibility while ensuring it never intercepts input.</summary>
    /// <param name="visible">True to show the overlay; false to hide it.</param>
    private void SetVisible(bool visible)
    {
        if (canvasGroup == null)
            return;

        canvasGroup.alpha = visible ? 1f : 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
    }
}
