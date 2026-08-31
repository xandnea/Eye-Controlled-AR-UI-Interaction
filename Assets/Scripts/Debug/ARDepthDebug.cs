using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ARDepthDebug : MonoBehaviour
{
    [SerializeField] private AROcclusionManager occlusionManager;
    [SerializeField] private ARCameraManager cameraManager;

    private bool? lastDepthAvailable;
    private EnvironmentDepthMode? lastCurrentMode;

    void Update()
    {
        if (occlusionManager == null) occlusionManager = GetComponent<AROcclusionManager>();
        if (cameraManager == null) cameraManager = GetComponent<ARCameraManager>();
        if (occlusionManager == null) return; // Guard clause to prevent errors if component is missing

        var currentMode = occlusionManager.currentEnvironmentDepthMode;
        bool depthAvailable = occlusionManager.TryGetEnvironmentDepthTexture(out Texture depthTexture);

        if (lastDepthAvailable != depthAvailable || lastCurrentMode != currentMode)
        {
            Debug.Log(
                $"DEPTH STATE CHANGED | " +
                $"requested={occlusionManager.requestedEnvironmentDepthMode} " +
                $"current={currentMode} " +
                $"smoothingRequested={occlusionManager.environmentDepthTemporalSmoothingRequested} " +
                $"smoothingEnabled={occlusionManager.environmentDepthTemporalSmoothingEnabled} " +
                $"depthTexture={depthAvailable}"
            );

            // FIX: Only log texture properties if the texture actually exists
            if (depthAvailable && depthTexture != null)
            {
                Debug.Log(
                    $"DEPTH TEXTURE | " +
                    $"size={depthTexture.width}x{depthTexture.height} " +
                    $"format={depthTexture.graphicsFormat} " +
                    $"dimension={depthTexture.dimension}"
                );
            }
            else
            {
                Debug.Log("DEPTH TEXTURE | null");
            }

            lastDepthAvailable = depthAvailable;
            lastCurrentMode = currentMode;
        }
    }

}