using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using Mediapipe;
using Mediapipe.Unity;
using UnityEngine;
using UnityEngine.Rendering;

[DefaultExecutionOrder(-100)]
public class VulkanMediaPipeBridgeManager : MonoBehaviour
{
    private const int SlotCount = 2;
    private const int Slot0CopyEvent = 10;
    private const int Slot0SyncEvent = 11;
    private const int Slot1CopyEvent = 12;
    private const int Slot1SyncEvent = 13;

    [DllImport("VulkanBridge")] private static extern int InitializeVulkanBridge();
    [DllImport("VulkanBridge")] private static extern void ShutdownVulkanBridge();
    [DllImport("VulkanBridge")] private static extern IntPtr GetRenderEventFunc();
    [DllImport("VulkanBridge")] private static extern void SetMediaPipeEGLContext(IntPtr display, IntPtr config, IntPtr context);
    [DllImport("VulkanBridge")] private static extern int InitializeMediaPipeEGLBridge();
    [DllImport("VulkanBridge")] private static extern int InitializeBridgeTextures();
    [DllImport("VulkanBridge")] private static extern void ResetBridgeSlot(int slot);
    [DllImport("VulkanBridge")] private static extern int IsBridgeSlotCopyRecorded(int slot);
    [DllImport("VulkanBridge")] private static extern int IsBridgeSlotSyncReady(int slot);
    [DllImport("VulkanBridge")] private static extern int WaitForBridgeSlotSyncOnEGL(int slot);
    [DllImport("VulkanBridge")] private static extern uint GetBridgeSlotGLESTexture(int slot);
    [DllImport("VulkanBridge")] private static extern int GetBridgeWidth();
    [DllImport("VulkanBridge")] private static extern int GetBridgeHeight();

    private static readonly object[] ReleaseLocks = { new object(), new object() };
    private static readonly GlSyncPoint[] ReleaseSyncPoints = new GlSyncPoint[SlotCount];
    private static readonly int[] ImageReleased = new int[SlotCount];
    private static readonly uint[] TextureNames = new uint[SlotCount];

    private readonly bool[] slotPrepared = new bool[SlotCount];
    private IntPtr renderEventFunc = IntPtr.Zero;

    public bool IsReady { get; private set; }

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

        // WebCamSource provides the texture pointer and submits slot 0's first copy event.
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

        // Prime slot 1 once before importing both AHBs into GLES.
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

        Debug.Log(
            $"[VulkanMediaPipeBridgeManager] Ready | {GetBridgeWidth()}x{GetBridgeHeight()} | " +
            $"textures={TextureNames[0]},{TextureNames[1]}");
#else
        Debug.LogError("[VulkanMediaPipeBridgeManager] Android player build required.");
        yield break;
#endif
    }

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

    public bool IsFrameSyncReady(int slot)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return ValidSlot(slot) && IsBridgeSlotSyncReady(slot) != 0;
#else
        return false;
#endif
    }

    // Call only after IsFrameSyncReady(slot) is true.
    // This is intentionally NOT a coroutine: yielding a completed nested IEnumerator
    // can add a whole Unity frame before the caller resumes.
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

    public bool IsSlotPrepared(int slot) => ValidSlot(slot) && slotPrepared[slot];

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

    public bool IsMediaPipeImageReleased(int slot)
    {
        return ValidSlot(slot) && Volatile.Read(ref ImageReleased[slot]) != 0;
    }

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
}
