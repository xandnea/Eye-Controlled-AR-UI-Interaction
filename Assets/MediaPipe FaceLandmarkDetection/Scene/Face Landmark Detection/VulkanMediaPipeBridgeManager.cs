using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Managed owner of the Vulkan-to-MediaPipe GPU bridge.
///
/// Native code copies Unity's Vulkan camera texture into two AHardwareBuffer-backed
/// Vulkan images. Those buffers are imported as GLES textures in a context shared
/// with MediaPipe.
///
/// The optional bridge-only diagnostic mode starts the configured MediaPipe
/// ImageSource without running FaceLandmarker inference. This allows ARCore +
/// selfie camera + Vulkan/AHardwareBuffer/GLES bridge behavior to be isolated.
/// </summary>
[DefaultExecutionOrder(-100)]
public class VulkanMediaPipeBridgeManager : MonoBehaviour
{
    private const int SlotCount = 2;
    private const int Slot0CopyEvent = 10;
    private const int Slot0SyncEvent = 11;
    private const int Slot1CopyEvent = 12;
    private const int Slot1SyncEvent = 13;

    [Header("Diagnostics")]
    [SerializeField]
    [Tooltip(
        "TEST ONLY. Starts ImageSourceProvider.ImageSource from this manager so the " +
        "selfie camera and Vulkan bridge can run while FaceLandmarkerRunner is disabled. " +
        "Leave OFF during normal operation because FaceLandmarkerRunner normally owns " +
        "ImageSource.Play()."
    )]
    private bool startImageSourceForBridgeOnlyTest = false;

    [DllImport("VulkanBridge")]
    private static extern int InitializeVulkanBridge();

    [DllImport("VulkanBridge")]
    private static extern void ShutdownVulkanBridge();

    [DllImport("VulkanBridge")]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport("VulkanBridge")]
    private static extern void SetMediaPipeEGLContext(
        IntPtr display,
        IntPtr config,
        IntPtr context
    );

    [DllImport("VulkanBridge")]
    private static extern int InitializeMediaPipeEGLBridge();

    [DllImport("VulkanBridge")]
    private static extern int InitializeBridgeTextures();

    [DllImport("VulkanBridge")]
    private static extern void ResetBridgeSlot(int slot);

    [DllImport("VulkanBridge")]
    private static extern int IsBridgeSlotCopyRecorded(int slot);

    [DllImport("VulkanBridge")]
    private static extern int IsBridgeSlotSyncReady(int slot);

    [DllImport("VulkanBridge")]
    private static extern int WaitForBridgeSlotSyncOnEGL(int slot);

    [DllImport("VulkanBridge")]
    private static extern uint GetBridgeSlotGLESTexture(int slot);

    [DllImport("VulkanBridge")]
    private static extern int GetBridgeWidth();

    [DllImport("VulkanBridge")]
    private static extern int GetBridgeHeight();

    private static readonly object[] ReleaseLocks =
    {
        new object(),
        new object()
    };

    private static readonly GlSyncPoint[] ReleaseSyncPoints =
        new GlSyncPoint[SlotCount];

    private static readonly int[] ImageReleased =
        new int[SlotCount];

    private static readonly uint[] TextureNames =
        new uint[SlotCount];

    private readonly bool[] slotPrepared =
        new bool[SlotCount];

    private IntPtr renderEventFunc = IntPtr.Zero;

    // Only populated when this manager itself starts the image source for the
    // bridge-only diagnostic. In normal operation FaceLandmarkerRunner owns it.
    private ImageSource diagnosticImageSource;
    private bool ownsDiagnosticImageSource;
    private bool ownsGpuManagerInitialization;

    /// <summary>
    /// True after both bridge slots have been primed and imported as GLES textures.
    /// </summary>
    public bool IsReady { get; private set; }

    /// <summary>
    /// Initializes MediaPipe's shared EGL context, initializes the native Vulkan
    /// bridge, optionally starts the selfie ImageSource for a bridge-only diagnostic,
    /// then primes and imports both bridge slots.
    /// </summary>
    private IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Vulkan is required."
            );
            yield break;
        }

        Debug.Log(
            $"[VulkanMediaPipeBridgeManager] Starting | " +
            $"bridgeOnlyTest={startImageSourceForBridgeOnlyTest}"
        );

        // FaceLandmarkerRunner normally causes MediaPipe GPU resources to exist.
        // In bridge-only mode FLR is disabled, so initialize GpuManager explicitly.
        if (!GpuManager.IsInitialized)
        {
            Debug.Log(
                "[VulkanMediaPipeBridgeManager] Initializing MediaPipe GPU resources for bridge-only test."
            );

            yield return GpuManager.Initialize();

            if (!GpuManager.IsInitialized)
            {
                Debug.LogError(
                    "[VulkanMediaPipeBridgeManager] GpuManager.Initialize() failed."
                );
                yield break;
            }

            ownsGpuManagerInitialization = true;
        }

        Debug.Log(
            $"[VulkanMediaPipeBridgeManager] MediaPipe GPU ready | " +
            $"helper={(GpuManager.GlCalculatorHelper != null)} " +
            $"initialized={(GpuManager.GlCalculatorHelper != null && GpuManager.GlCalculatorHelper.Initialized())}"
        );

        if (GpuManager.GlCalculatorHelper == null ||
            !GpuManager.GlCalculatorHelper.Initialized())
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] MediaPipe GL helper is unavailable after GpuManager initialization."
            );
            yield break;
        }

        using (var glContext = GpuManager.GetGlContext())
        {
            if (glContext == null)
            {
                Debug.LogError(
                    "[VulkanMediaPipeBridgeManager] MediaPipe GL context is unavailable."
                );
                yield break;
            }

            SetMediaPipeEGLContext(
                glContext.eglDisplay,
                glContext.eglConfig,
                glContext.eglContext
            );

            if (InitializeMediaPipeEGLBridge() == 0)
            {
                Debug.LogError(
                    "[VulkanMediaPipeBridgeManager] Failed to initialize the shared EGL context."
                );
                yield break;
            }
        }

        if (InitializeVulkanBridge() == 0)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Failed to initialize Vulkan."
            );
            yield break;
        }

        renderEventFunc = GetRenderEventFunc();

        if (renderEventFunc == IntPtr.Zero)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Native render callback is unavailable."
            );
            yield break;
        }

        /*
         * Normally FaceLandmarkerRunner calls ImageSource.Play(), and WebCamSource
         * registers the first real WebCamTexture with the native bridge.
         *
         * With FaceLandmarkerRunner disabled, nobody starts WebCamSource, so the
         * manager would wait forever for slot 0. The diagnostic option below starts
         * ONLY the source/bridge path; no FaceLandmarker model or inference is run.
         */
        if (startImageSourceForBridgeOnlyTest)
        {
            diagnosticImageSource = ImageSourceProvider.ImageSource;

            if (diagnosticImageSource == null)
            {
                Debug.LogError(
                    "[VulkanMediaPipeBridgeManager] Diagnostic ImageSource is unavailable."
                );
                yield break;
            }

            Debug.Log(
                "[VulkanMediaPipeBridgeManager] Bridge-only test: starting ImageSource."
            );

            yield return diagnosticImageSource.Play();

            if (!diagnosticImageSource.isPrepared)
            {
                Debug.LogError(
                    "[VulkanMediaPipeBridgeManager] Bridge-only test: ImageSource failed to prepare."
                );
                yield break;
            }

            ownsDiagnosticImageSource = true;

            Debug.Log(
                $"[VulkanMediaPipeBridgeManager] Bridge-only ImageSource ready | " +
                $"type={diagnosticImageSource.GetType().Name} " +
                $"source={diagnosticImageSource.sourceName} " +
                $"frontFacing={diagnosticImageSource.isFrontFacing} " +
                $"size={diagnosticImageSource.textureWidth}x{diagnosticImageSource.textureHeight}"
            );
        }

        // WebCamSource supplies the texture pointer and submits slot 0's first
        // copy event after receiving a real camera frame.
        while (IsBridgeSlotCopyRecorded(0) == 0)
            yield return null;

        Debug.Log(
            "[VulkanMediaPipeBridgeManager] Slot 0 copy recorded."
        );

        GL.IssuePluginEvent(
            renderEventFunc,
            Slot0SyncEvent
        );

        while (IsBridgeSlotSyncReady(0) == 0)
            yield return null;

        if (WaitForBridgeSlotSyncOnEGL(0) == 0)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Initial slot 0 synchronization failed."
            );
            yield break;
        }

        // Prime slot 1 once before importing both AHBs into GLES.
        ResetBridgeSlot(1);

        GL.IssuePluginEvent(
            renderEventFunc,
            Slot1CopyEvent
        );

        GL.IssuePluginEvent(
            renderEventFunc,
            Slot1SyncEvent
        );

        while (IsBridgeSlotSyncReady(1) == 0)
            yield return null;

        if (WaitForBridgeSlotSyncOnEGL(1) == 0)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Initial slot 1 synchronization failed."
            );
            yield break;
        }

        if (InitializeBridgeTextures() == 0)
        {
            Debug.LogError(
                "[VulkanMediaPipeBridgeManager] Failed to import bridge buffers into GLES."
            );
            yield break;
        }

        for (int slot = 0; slot < SlotCount; ++slot)
        {
            TextureNames[slot] =
                GetBridgeSlotGLESTexture(slot);

            if (TextureNames[slot] == 0)
            {
                Debug.LogError(
                    $"[VulkanMediaPipeBridgeManager] Slot {slot} has no GLES texture."
                );
                yield break;
            }
        }

        slotPrepared[0] = true;
        slotPrepared[1] = false;
        IsReady = true;

        Debug.Log(
            $"[VulkanMediaPipeBridgeManager] Ready | " +
            $"{GetBridgeWidth()}x{GetBridgeHeight()} | " +
            $"textures={TextureNames[0]},{TextureNames[1]} | " +
            $"bridgeOnlyTest={startImageSourceForBridgeOnlyTest}"
        );

        /*
         * For the bridge-only diagnostic, keep exercising the Vulkan -> AHB -> EGL
         * transfer path even though FaceLandmarkerRunner is disabled.
         *
         * This is important: merely reaching IsReady only tests bridge startup.
         * Alternating the two slots tests the repeated transfer/synchronization path
         * that normally runs while FaceLandmarker consumes frames.
         */
        if (startImageSourceForBridgeOnlyTest)
        {
            yield return RunBridgeOnlyTransferLoop();
        }
#else
        Debug.LogError(
            "[VulkanMediaPipeBridgeManager] Android player build required."
        );
        yield break;
#endif
    }

    /// <summary>
    /// Continuously alternates bridge slots without constructing MediaPipe Images or
    /// running FaceLandmarker inference. This isolates the recurring Vulkan-to-EGL
    /// transfer/synchronization path from MediaPipe texture ownership.
    /// </summary>
    private IEnumerator RunBridgeOnlyTransferLoop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        Debug.Log(
            "[VulkanMediaPipeBridgeManager] Bridge-only transfer loop started."
        );

        int slot = 1;
        int completedFrames = 0;

        while (true)
        {
            if (!BeginPrepareFrame(slot))
            {
                Debug.LogError(
                    $"[VulkanMediaPipeBridgeManager] Bridge-only test failed to prepare slot {slot}."
                );
                yield break;
            }

            while (!IsFrameSyncReady(slot))
                yield return null;

            if (!FinalizePreparedFrame(slot))
            {
                Debug.LogError(
                    $"[VulkanMediaPipeBridgeManager] Bridge-only test failed to finalize slot {slot}."
                );
                yield break;
            }

            completedFrames++;

            if (completedFrames % 120 == 0)
            {
                Debug.Log(
                    $"[VulkanMediaPipeBridgeManager] Bridge-only transfer active | " +
                    $"frames={completedFrames}"
                );
            }

            slot = slot == 0 ? 1 : 0;

            // One transfer per Unity frame is enough for this diagnostic and avoids
            // an artificial tight loop that does not resemble normal application use.
            yield return null;
        }
#else
        yield break;
#endif
    }

    /// <summary>
    /// Queues Vulkan copy and synchronization events for a bridge slot.
    /// </summary>
    /// <param name="slot">Bridge slot index, 0 or 1.</param>
    /// <returns>True if preparation was queued successfully.</returns>
    public bool BeginPrepareFrame(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady ||
            !ValidSlot(slot) ||
            renderEventFunc == IntPtr.Zero)
        {
            return false;
        }

        slotPrepared[slot] = false;

        ResetBridgeSlot(slot);

        GL.IssuePluginEvent(
            renderEventFunc,
            CopyEvent(slot)
        );

        GL.IssuePluginEvent(
            renderEventFunc,
            SyncEvent(slot)
        );

        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Reports whether the native Vulkan synchronization FD is ready for a slot.
    /// </summary>
    /// <param name="slot">Bridge slot index, 0 or 1.</param>
    /// <returns>True once the native sync point is ready.</returns>
    public bool IsFrameSyncReady(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return ValidSlot(slot) &&
               IsBridgeSlotSyncReady(slot) != 0;
#else
        return false;
#endif
    }

    /// <summary>
    /// Completes Vulkan-to-EGL synchronization for a prepared bridge slot.
    /// </summary>
    /// <param name="slot">Bridge slot index, 0 or 1.</param>
    /// <returns>True if the slot was finalized successfully.</returns>
    public bool FinalizePreparedFrame(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady ||
            !ValidSlot(slot) ||
            IsBridgeSlotSyncReady(slot) == 0)
        {
            return false;
        }

        if (WaitForBridgeSlotSyncOnEGL(slot) == 0)
        {
            Debug.LogError(
                $"[VulkanMediaPipeBridgeManager] Slot {slot} Vulkan-to-EGL synchronization failed."
            );
            return false;
        }

        slotPrepared[slot] = true;
        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Returns whether a slot is ready to be wrapped as a MediaPipe image.
    /// </summary>
    /// <param name="slot">Bridge slot index, 0 or 1.</param>
    public bool IsSlotPrepared(int slot)
    {
        return ValidSlot(slot) &&
               slotPrepared[slot];
    }

    /// <summary>
    /// Wraps a prepared shared GLES texture as a MediaPipe GPU Image.
    /// </summary>
    /// <param name="slot">Prepared bridge slot index.</param>
    /// <param name="glContext">MediaPipe GL context that will own the image wrapper.</param>
    /// <returns>The GPU Image wrapper, or null if the slot is invalid/not prepared.</returns>
    public Image CreateMediaPipeImage(
        int slot,
        GlContext glContext)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady ||
            !ValidSlot(slot) ||
            !slotPrepared[slot] ||
            glContext == null)
        {
            return null;
        }

        uint texture =
            TextureNames[slot];

        int width =
            GetBridgeWidth();

        int height =
            GetBridgeHeight();

        if (texture == 0 ||
            width <= 0 ||
            height <= 0)
        {
            return null;
        }

        lock (ReleaseLocks[slot])
        {
            ReleaseSyncPoints[slot]?.Dispose();
            ReleaseSyncPoints[slot] = null;
        }

        Interlocked.Exchange(
            ref ImageReleased[slot],
            0
        );

        slotPrepared[slot] = false;

        return new Image(
            texture,
            width,
            height,
            GpuBufferFormat.kBGRA32,
            OnMediaPipeImageReleased,
            glContext
        );
#else
        return null;
#endif
    }

    /// <summary>
    /// Reports whether MediaPipe has released the GLES texture for a slot.
    /// </summary>
    /// <param name="slot">Bridge slot index.</param>
    public bool IsMediaPipeImageReleased(int slot)
    {
        return ValidSlot(slot) &&
               Volatile.Read(
                   ref ImageReleased[slot]
               ) != 0;
    }

    /// <summary>
    /// Waits for and disposes MediaPipe's GL synchronization point for a slot.
    /// </summary>
    /// <param name="slot">Bridge slot index.</param>
    public void WaitForMediaPipeRelease(int slot)
    {
        if (!ValidSlot(slot))
            return;

        GlSyncPoint syncPoint;

        lock (ReleaseLocks[slot])
        {
            syncPoint =
                ReleaseSyncPoints[slot];

            ReleaseSyncPoints[slot] =
                null;
        }

        if (syncPoint == null)
            return;

        syncPoint.Wait();
        syncPoint.Dispose();
    }

    [AOT.MonoPInvokeCallback(typeof(GlTextureBuffer.DeletionCallback))]
    private static void OnMediaPipeImageReleased(
        uint textureName,
        IntPtr syncTokenPtr)
    {
        int slot =
            textureName == TextureNames[0] ? 0 :
            textureName == TextureNames[1] ? 1 :
            -1;

        if (slot < 0)
        {
            Debug.LogError(
                $"[VulkanMediaPipeBridgeManager] Release callback for unknown texture {textureName}."
            );
            return;
        }

        lock (ReleaseLocks[slot])
        {
            ReleaseSyncPoints[slot]?.Dispose();

            ReleaseSyncPoints[slot] =
                syncTokenPtr == IntPtr.Zero
                    ? null
                    : new GlSyncPoint(syncTokenPtr);
        }

        Interlocked.Exchange(
            ref ImageReleased[slot],
            1
        );
    }

    private static bool ValidSlot(int slot)
    {
        return slot >= 0 &&
               slot < SlotCount;
    }

    private static int CopyEvent(int slot)
    {
        return slot == 0
            ? Slot0CopyEvent
            : Slot1CopyEvent;
    }

    private static int SyncEvent(int slot)
    {
        return slot == 0
            ? Slot0SyncEvent
            : Slot1SyncEvent;
    }

    /// <summary>
    /// Releases test-owned camera resources and shuts down the native bridge.
    /// </summary>
    private void OnDestroy()
    {
        IsReady = false;

        if (ownsDiagnosticImageSource &&
            diagnosticImageSource != null)
        {
            diagnosticImageSource.Stop();
            ownsDiagnosticImageSource = false;
            diagnosticImageSource = null;
        }

        for (int slot = 0;
             slot < SlotCount;
             ++slot)
        {
            lock (ReleaseLocks[slot])
            {
                ReleaseSyncPoints[slot]?.Dispose();
                ReleaseSyncPoints[slot] = null;
            }

            Interlocked.Exchange(
                ref ImageReleased[slot],
                0
            );

            TextureNames[slot] = 0;
        }

        ShutdownVulkanBridge();

        if (ownsGpuManagerInitialization)
        {
            GpuManager.Shutdown();
            ownsGpuManagerInitialization = false;
        }
    }
}