using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class OcclusionDebug : MonoBehaviour
{
    private AROcclusionManager occlusionManager;
    private float timer;

    private void Awake()
    {
        occlusionManager = GetComponent<AROcclusionManager>();
        if (occlusionManager == null)
        {
            Debug.LogError("OCCLUSION DEBUG | No AROcclusionManager found.");
        }
    }

    private void Update()
    {
        if (occlusionManager == null) return;

        timer += Time.deltaTime;
        // Only print once per second.
        if (timer < 1f) return;
        timer = 0f;

        var subsystem = occlusionManager.subsystem;
        if (subsystem == null)
        {
            Debug.LogWarning("OCCLUSION DEBUG | Occlusion subsystem is NULL.");
            return;
        }

        var descriptor = subsystem.subsystemDescriptor;

        // --- NON-OBSOLETE DEPTH DATA ACQUISITION ---
        bool envDepthAvailable = occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage envDepthImage);
        int envWidth = envDepthAvailable ? envDepthImage.width : 0;
        int envHeight = envDepthAvailable ? envDepthImage.height : 0;
        if (envDepthAvailable) envDepthImage.Dispose(); // Memory cleanup is mandatory

        bool humanDepthAvailable = occlusionManager.TryAcquireHumanDepthCpuImage(out XRCpuImage humanDepthImage);
        if (humanDepthAvailable) humanDepthImage.Dispose();

        bool humanStencilAvailable = occlusionManager.TryAcquireHumanStencilCpuImage(out XRCpuImage humanStencilImage);
        if (humanStencilAvailable) humanStencilImage.Dispose();
        // -------------------------------------------

        Debug.Log(
            "========== OCCLUSION DEBUG ==========\n" +
            $"Environment requested: {occlusionManager.requestedEnvironmentDepthMode}\n" +
            $"Environment current: {occlusionManager.currentEnvironmentDepthMode}\n" +
            $"Environment texture: {(envDepthAvailable ? "AVAILABLE" : "NULL")}\n" +
            $"Environment size: {(envDepthAvailable ? envWidth + "x" + envHeight : "N/A")}\n" +
            $"Human depth requested: {occlusionManager.requestedHumanDepthMode}\n" +
            $"Human depth current: {occlusionManager.currentHumanDepthMode}\n" +
            $"Human depth texture: {(humanDepthAvailable ? "AVAILABLE" : "NULL")}\n" +
            $"Human stencil texture: {(humanStencilAvailable ? "AVAILABLE" : "NULL")}\n" +
            $"Environment supported: {descriptor.environmentDepthImageSupported}\n" +
            $"Environment confidence supported: {descriptor.environmentDepthConfidenceImageSupported}\n" +
            $"Human depth supported: {descriptor.humanSegmentationDepthImageSupported}\n" +
            $"Human stencil supported: {descriptor.humanSegmentationStencilImageSupported}\n" +
            "======================================"
        );
    }
}
