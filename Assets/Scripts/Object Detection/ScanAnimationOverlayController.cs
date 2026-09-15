using System.Collections;
using Gilzoide.LottiePlayer;
using UnityEngine;

public sealed class ScanAnimationOverlayController : MonoBehaviour
{
    [SerializeField] private ImageLottiePlayer lottiePlayer;
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

    public void StopAndHide()
    {
        if (lottiePlayer != null)
            lottiePlayer.Pause();

        SetVisible(false);
    }

    private void SetVisible(bool visible)
    {
        if (canvasGroup == null)
            return;

        canvasGroup.alpha = visible ? 1f : 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
    }
}