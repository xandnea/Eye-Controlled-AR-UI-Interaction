using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

public class VulkanBridgeTest : MonoBehaviour
{
    [DllImport("VulkanBridge")]
    private static extern void InitializeVulkanBridge();

    [DllImport("VulkanBridge")]
    private static extern void ShutdownVulkanBridge();

    [DllImport("VulkanBridge")]
    private static extern void InspectTexture(
        IntPtr nativeTexture,
        int width,
        int height);

    [DllImport("VulkanBridge")]
    private static extern IntPtr GetRenderEventFunc();

    private Texture2D testTexture;

    private void Start()
    {
        Debug.Log("========== VULKAN BRIDGE TEST ==========");

        Debug.Log($"Graphics API: {SystemInfo.graphicsDeviceType}");
        Debug.Log($"Graphics Device: {SystemInfo.graphicsDeviceName}");
        Debug.Log($"Graphics Device Version: {SystemInfo.graphicsDeviceVersion}");

        InitializeVulkanBridge();

        CreateKnownTexture();

        IntPtr nativeTexture =
            testTexture.GetNativeTexturePtr();

        Debug.Log(
            $"Test texture native pointer: 0x{nativeTexture.ToInt64():X}");

        Debug.Log(
            $"Test texture size: {testTexture.width} x {testTexture.height}");

        InspectTexture(
            nativeTexture,
            testTexture.width,
            testTexture.height);

        Debug.Log("Requesting render event...");

        IntPtr renderEventFunc = GetRenderEventFunc();

        Debug.Log(
            $"Render event function pointer: 0x{renderEventFunc.ToInt64():X}");

        GL.IssuePluginEvent(
            renderEventFunc,
            1);

        Debug.Log("GL.IssuePluginEvent submitted");
    }

    private void CreateKnownTexture()
    {
        testTexture = new Texture2D(
            4,
            4,
            TextureFormat.RGBA32,
            false,
            false);

        Color32[] pixels = new Color32[16];

        // Row 0
        pixels[0] = new Color32(255, 0, 0, 255);
        pixels[1] = new Color32(0, 255, 0, 255);
        pixels[2] = new Color32(0, 0, 255, 255);
        pixels[3] = new Color32(255, 255, 0, 255);

        // Row 1
        pixels[4] = new Color32(255, 0, 255, 255);
        pixels[5] = new Color32(0, 255, 255, 255);
        pixels[6] = new Color32(255, 255, 255, 255);
        pixels[7] = new Color32(0, 0, 0, 255);

        // Row 2
        pixels[8] = new Color32(128, 64, 32, 255);
        pixels[9] = new Color32(64, 128, 32, 255);
        pixels[10] = new Color32(32, 64, 128, 255);
        pixels[11] = new Color32(10, 20, 30, 40);

        // Row 3
        pixels[12] = new Color32(1, 2, 3, 4);
        pixels[13] = new Color32(10, 20, 30, 40);
        pixels[14] = new Color32(100, 150, 200, 250);
        pixels[15] = new Color32(17, 34, 51, 68);

        testTexture.SetPixels32(pixels);
        testTexture.Apply(false, false);

        Debug.Log(
            $"Created RGBA32 test texture: format={testTexture.format}");
    }

    private void OnDestroy()
    {
        ShutdownVulkanBridge();

        if (testTexture != null)
        {
            Destroy(testTexture);
        }
    }
}