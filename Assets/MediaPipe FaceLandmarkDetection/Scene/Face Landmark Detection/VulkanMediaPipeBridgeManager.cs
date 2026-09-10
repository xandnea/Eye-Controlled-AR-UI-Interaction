using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using Mediapipe;
using Mediapipe.Unity;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Managed owner of the Vulkan-to-MediaPipe GPU bridge.
///
/// Native code copies Unity's Vulkan camera texture into two AHardwareBuffer-backed
/// Vulkan images. Those buffers are imported as GLES textures in a context shared
/// with MediaPipe. The two slots alternate so frame transfer can overlap Face
/// Landmarker inference without GPU-to-CPU pixel readback.
/// </summary>
[DefaultExecutionOrder(-100)]
public class VulkanMediaPipeBridgeManager : MonoBehaviour
{
    private const string NativeLibrary = "VulkanBridge";
    private const int SlotCount = 2;

    // Native render events are fixed because they are also configured in
    // vulkan_mediapipe_bridge.cpp.
    private const int Slot0CopyEvent = 10;
    private const int Slot0SyncEvent = 11;
    private const int Slot1CopyEvent = 12;
    private const int Slot1SyncEvent = 13;

    #region Native API

    [DllImport(NativeLibrary)] private static extern int InitializeVulkanBridge();
    [DllImport(NativeLibrary)] private static extern void ShutdownVulkanBridge();
    [DllImport(NativeLibrary)] private static extern IntPtr GetRenderEventFunc();

    [DllImport(NativeLibrary)]
    private static extern void SetMediaPipeEGLContext(IntPtr display, IntPtr config, IntPtr context);

    [DllImport(NativeLibrary)] private static extern int InitializeMediaPipeEGLBridge();
    [DllImport(NativeLibrary)] private static extern int InitializeBridgeTextures();
    [DllImport(NativeLibrary)] private static extern void ResetBridgeSlot(int slot);
    [DllImport(NativeLibrary)] private static extern int IsBridgeSlotCopyRecorded(int slot);
    [DllImport(NativeLibrary)] private static extern int IsBridgeSlotSyncReady(int slot);
    [DllImport(NativeLibrary)] private static extern int WaitForBridgeSlotSyncOnEGL(int slot);
    [DllImport(NativeLibrary)] private static extern uint GetBridgeSlotGLESTexture(int slot);
    [DllImport(NativeLibrary)] private static extern int GetBridgeWidth();
    [DllImport(NativeLibrary)] private static extern int GetBridgeHeight();

    #endregion

    // MediaPipe's deletion callback may run off the Unity main thread, so release
    // state is kept per slot and protected independently.
    private static readonly object[] ReleaseLocks = { new object(), new object() };
    private static readonly GlSyncPoint[] ReleaseSyncPoints = new GlSyncPoint[SlotCount];
    private static readonly int[] ImageReleased = new int[SlotCount];
    private static readonly uint[] TextureNames = new uint[SlotCount];

    private readonly bool[] slotPrepared = new bool[SlotCount];
    private IntPtr renderEventFunc = IntPtr.Zero;

    public bool IsReady { get; private set; }

    #region Initialization

    /// <summary>
    /// Connects the native bridge to MediaPipe's EGL share group, initializes Vulkan,
    /// primes both bridge slots, and imports both AHardwareBuffers as GLES textures.
    /// </summary>
    private IEnumerator Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Vulkan is required.");
            yield break;
        }

        while (GpuManager.GlCalculatorHelper == null || !GpuManager.GlCalculatorHelper.Initialized())
            yield return null;

        using (var glContext = GpuManager.GetGlContext())
        {
            if (glContext == null)
            {
                Debug.LogError("[VulkanMediaPipeBridgeManager] MediaPipe GL context is unavailable.");
                yield break;
            }

            SetMediaPipeEGLContext(glContext.eglDisplay, glContext.eglConfig, glContext.eglContext);

            if (InitializeMediaPipeEGLBridge() == 0)
            {
                Debug.LogError("[VulkanMediaPipeBridgeManager] Failed to initialize the shared EGL context.");
                yield break;
            }
        }

        if (InitializeVulkanBridge() == 0)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Failed to initialize Vulkan.");
            yield break;
        }

        renderEventFunc = GetRenderEventFunc();
        if (renderEventFunc == IntPtr.Zero)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Native render callback is unavailable.");
            yield break;
        }

        // WebCamSource submits slot 0's first copy after the first real camera frame.
        while (IsBridgeSlotCopyRecorded(0) == 0)
            yield return null;

        GL.IssuePluginEvent(renderEventFunc, Slot0SyncEvent);
        while (IsBridgeSlotSyncReady(0) == 0)
            yield return null;

        if (WaitForBridgeSlotSyncOnEGL(0) == 0)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Initial slot 0 synchronization failed.");
            yield break;
        }

        // Slot 1 must also complete one Vulkan write/release before its AHB is
        // imported into the shared GLES context.
        ResetBridgeSlot(1);
        GL.IssuePluginEvent(renderEventFunc, Slot1CopyEvent);
        GL.IssuePluginEvent(renderEventFunc, Slot1SyncEvent);

        while (IsBridgeSlotSyncReady(1) == 0)
            yield return null;

        if (WaitForBridgeSlotSyncOnEGL(1) == 0)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Initial slot 1 synchronization failed.");
            yield break;
        }

        if (InitializeBridgeTextures() == 0)
        {
            Debug.LogError("[VulkanMediaPipeBridgeManager] Failed to import bridge buffers into GLES.");
            yield break;
        }

        for (int slot = 0; slot < SlotCount; ++slot)
        {
            TextureNames[slot] = GetBridgeSlotGLESTexture(slot);
            if (TextureNames[slot] == 0)
            {
                Debug.LogError($"[VulkanMediaPipeBridgeManager] Slot {slot} has no GLES texture.");
                yield break;
            }
        }

        slotPrepared[0] = true;
        slotPrepared[1] = false;
        IsReady = true;

        Debug.Log($"[VulkanMediaPipeBridgeManager] Ready at {GetBridgeWidth()}x{GetBridgeHeight()}.");
#else
        Debug.LogError("[VulkanMediaPipeBridgeManager] Android player build required.");
        yield break;
#endif
    }

    #endregion

    #region Frame Pipeline

    /// <summary>
    /// Queues Vulkan copy and sync events for the requested slot.
    /// Returns immediately so the copy can overlap MediaPipe inference.
    /// </summary>
    public bool BeginPrepareFrame(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady || !ValidSlot(slot) || renderEventFunc == IntPtr.Zero)
            return false;

        slotPrepared[slot] = false;
        ResetBridgeSlot(slot);

        GL.IssuePluginEvent(renderEventFunc, CopyEvent(slot));
        GL.IssuePluginEvent(renderEventFunc, SyncEvent(slot));
        return true;
#else
        return false;
#endif
    }

    /// <summary>
    /// Reports whether native Vulkan work has produced a sync FD for this slot.
    /// </summary>
    public bool IsFrameSyncReady(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return ValidSlot(slot) && IsBridgeSlotSyncReady(slot) != 0;
#else
        return false;
#endif
    }

    /// <summary>
    /// Imports and waits on the slot's Vulkan sync FD from the shared EGL context.
    /// Call only after IsFrameSyncReady returns true.
    ///
    /// This method must remain synchronous. Making it a nested coroutine introduces
    /// an additional Unity-frame delay even when the fence is already ready.
    /// </summary>
    public bool FinalizePreparedFrame(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady || !ValidSlot(slot) || IsBridgeSlotSyncReady(slot) == 0)
            return false;

        if (WaitForBridgeSlotSyncOnEGL(slot) == 0)
        {
            Debug.LogError($"[VulkanMediaPipeBridgeManager] Slot {slot} Vulkan-to-EGL synchronization failed.");
            return false;
        }

        slotPrepared[slot] = true;
        return true;
#else
        return false;
#endif
    }

    public bool IsSlotPrepared(int slot)
    {
        return ValidSlot(slot) && slotPrepared[slot];
    }

    /// <summary>
    /// Wraps a prepared shared GLES texture as a MediaPipe GPU Image.
    /// MediaPipe owns the texture use until OnMediaPipeImageReleased is called.
    /// </summary>
    public Image CreateMediaPipeImage(int slot, GlContext glContext)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!IsReady || !ValidSlot(slot) || !slotPrepared[slot] || glContext == null)
            return null;

        uint texture = TextureNames[slot];
        int width = GetBridgeWidth();
        int height = GetBridgeHeight();

        if (texture == 0 || width <= 0 || height <= 0)
            return null;

        lock (ReleaseLocks[slot])
        {
            ReleaseSyncPoints[slot]?.Dispose();
            ReleaseSyncPoints[slot] = null;
        }

        Interlocked.Exchange(ref ImageReleased[slot], 0);
        slotPrepared[slot] = false;

        return new Image(
            texture,
            width,
            height,
            GpuBufferFormat.kBGRA32,
            OnMediaPipeImageReleased,
            glContext);
#else
        return null;
#endif
    }

    #endregion

    #region MediaPipe Release Synchronization

    public bool IsMediaPipeImageReleased(int slot)
    {
        return ValidSlot(slot) && Volatile.Read(ref ImageReleased[slot]) != 0;
    }

    /// <summary>
    /// Waits for MediaPipe's GL sync point before allowing Vulkan to reuse the slot.
    /// </summary>
    public void WaitForMediaPipeRelease(int slot)
    {
        if (!ValidSlot(slot))
            return;

        GlSyncPoint syncPoint;

        lock (ReleaseLocks[slot])
        {
            syncPoint = ReleaseSyncPoints[slot];
            ReleaseSyncPoints[slot] = null;
        }

        if (syncPoint == null)
            return;

        syncPoint.Wait();
        syncPoint.Dispose();
    }

    /// <summary>
    /// MediaPipe deletion callback. Maps the released GL texture back to its bridge
    /// slot and stores the GL sync point for the Unity thread to wait on.
    /// </summary>
    [AOT.MonoPInvokeCallback(typeof(GlTextureBuffer.DeletionCallback))]
    private static void OnMediaPipeImageReleased(uint textureName, IntPtr syncTokenPtr)
    {
        int slot = textureName == TextureNames[0] ? 0 :
                   textureName == TextureNames[1] ? 1 : -1;

        if (slot < 0)
        {
            Debug.LogError($"[VulkanMediaPipeBridgeManager] Release callback for unknown texture {textureName}.");
            return;
        }

        lock (ReleaseLocks[slot])
        {
            ReleaseSyncPoints[slot]?.Dispose();
            ReleaseSyncPoints[slot] =
                syncTokenPtr == IntPtr.Zero ? null : new GlSyncPoint(syncTokenPtr);
        }

        Interlocked.Exchange(ref ImageReleased[slot], 1);
    }

    #endregion

    #region Helpers and Lifecycle

    private static bool ValidSlot(int slot) => slot >= 0 && slot < SlotCount;
    private static int CopyEvent(int slot) => slot == 0 ? Slot0CopyEvent : Slot1CopyEvent;
    private static int SyncEvent(int slot) => slot == 0 ? Slot0SyncEvent : Slot1SyncEvent;

    private void OnDestroy()
    {
        IsReady = false;

        for (int slot = 0; slot < SlotCount; ++slot)
        {
            lock (ReleaseLocks[slot])
            {
                ReleaseSyncPoints[slot]?.Dispose();
                ReleaseSyncPoints[slot] = null;
            }

            Interlocked.Exchange(ref ImageReleased[slot], 0);
            TextureNames[slot] = 0;
        }

        ShutdownVulkanBridge();
    }

    #endregion
}
