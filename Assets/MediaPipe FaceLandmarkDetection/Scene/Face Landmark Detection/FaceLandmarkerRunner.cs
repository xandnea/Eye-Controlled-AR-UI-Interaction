// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using Mediapipe;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Debug = UnityEngine.Debug;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    /// <summary>
    /// Runs MediaPipe Face Landmarker using either:
    /// 1. the custom Vulkan-to-MediaPipe GPU bridge when ImageReadMode.GPU is selected, or
    /// 2. MediaPipe Unity's standard CPU / CPUAsync texture-read path otherwise.
    ///
    /// The Vulkan bridge path remains double-buffered so MediaPipe can process one
    /// shared GLES texture while Vulkan prepares the next camera frame.
    ///
    /// CPU and CPUAsync modes intentionally bypass VulkanMediaPipeBridgeManager so
    /// they can be used as an independent fallback and as an A/B diagnostic path.
    /// </summary>
    public class FaceLandmarkerRunner : VisionTaskApiRunner<FaceLandmarker>
    {
        [SerializeField]
        private FaceLandmarkerResultAnnotationController _faceLandmarkerResultAnnotationController;

        [SerializeField, FormerlySerializedAs("vulkanBridgeTest")]
        private VulkanMediaPipeBridgeManager vulkanBridge;

        [SerializeField, Min(1)]
        [Tooltip("Number of samples between performance log messages on the Vulkan bridge path.")]
        private int performanceLogInterval = 30;

        public readonly FaceLandmarkDetectionConfig config = new FaceLandmarkDetectionConfig();

        /// <summary>
        /// Raised whenever MediaPipe returns a LIVE_STREAM Face Landmarker result.
        /// </summary>
        public event Action<FaceLandmarkerResult> OnFaceLandmarksDetected;

        private Experimental.TextureFramePool _textureFramePool;

        private int inferenceCallbackReceived;
        private int lastFaceCount;

        // Temporary benchmark accumulators for the custom Vulkan bridge path.
        private double cycleTotalMs;
        private double inferenceTotalMs;
        private double bridgeReadyTotalMs;
        private double bridgeTailTotalMs;
        private double releaseTotalMs;
        private int performanceSamples;

        /// <summary>
        /// Stops Face Landmarker and releases the standard TextureFrame pool, if one
        /// was allocated for CPU / CPUAsync image reads.
        /// </summary>
        public override void Stop()
        {
            base.Stop();

            _textureFramePool?.Dispose();
            _textureFramePool = null;
        }

        /// <summary>
        /// Initializes Face Landmarker, starts the configured image source, and routes
        /// execution to either the custom Vulkan bridge path or the standard MediaPipe
        /// Unity texture-read path based on <see cref="FaceLandmarkDetectionConfig.ImageReadMode"/>.
        /// </summary>
        protected override IEnumerator Run()
        {
            Debug.Log(
                $"[FaceLandmarker] Starting | " +
                $"delegate={config.Delegate} " +
                $"readMode={config.ImageReadMode} " +
                $"runningMode={config.RunningMode}"
            );

            yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

            var options = config.GetFaceLandmarkerOptions(
                config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM
                    ? OnFaceLandmarkDetectionOutput
                    : null
            );

            taskApi = FaceLandmarker.CreateFromOptions(options, GpuManager.GpuResources);

            var imageSource = ImageSourceProvider.ImageSource;

            yield return imageSource.Play();

            if (!imageSource.isPrepared)
            {
                Debug.LogError("[FaceLandmarker] Failed to start the image source.");
                yield break;
            }

            screen.Initialize(imageSource);
            SetupAnnotationController(_faceLandmarkerResultAnnotationController, imageSource);

            var transform = imageSource.GetTransformationOptions();

            var imageProcessingOptions =
                new Tasks.Vision.Core.ImageProcessingOptions(
                    rotationDegrees: (int)transform.rotationAngle
                );

            if (config.ImageReadMode == ImageReadMode.GPU)
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
                {
                    Debug.LogError(
                        $"[FaceLandmarker] Custom GPU bridge requires Vulkan, but current API is " +
                        $"{SystemInfo.graphicsDeviceType}."
                    );
                    yield break;
                }

                if (taskApi.runningMode != Tasks.Vision.Core.RunningMode.LIVE_STREAM)
                {
                    Debug.LogError(
                        "[FaceLandmarker] Custom GPU bridge currently requires LIVE_STREAM mode."
                    );
                    yield break;
                }

                if (vulkanBridge == null)
                {
                    Debug.LogError(
                        "[FaceLandmarker] VulkanMediaPipeBridgeManager is not assigned."
                    );
                    yield break;
                }

                Debug.Log("[FaceLandmarker] Using Vulkan bridge GPU path.");

                yield return RunVulkanBridgePath(imageProcessingOptions);
                yield break;
            }

            Debug.Log(
                $"[FaceLandmarker] Using standard {config.ImageReadMode} path; " +
                "VulkanMediaPipeBridgeManager is not used."
            );

            yield return RunStandardImageReadPath(
                imageSource,
                options,
                transform.flipHorizontally,
                transform.flipVertically,
                imageProcessingOptions
            );
        }

        /// <summary>
        /// Runs the custom double-buffered Vulkan-to-MediaPipe GPU path.
        /// </summary>
        /// <param name="imageProcessingOptions">
        /// Rotation and other image-processing metadata passed to Face Landmarker.
        /// </param>
        private IEnumerator RunVulkanBridgePath(
            Tasks.Vision.Core.ImageProcessingOptions imageProcessingOptions)
        {
            while (!vulkanBridge.IsReady)
                yield return null;

            using var glContext = GpuManager.GetGlContext();

            if (glContext == null)
            {
                Debug.LogError("[FaceLandmarker] MediaPipe GL context is unavailable.");
                yield break;
            }

            int currentSlot = 0;
            int nextSlot = 1;

            while (true)
            {
                if (isPaused)
                    yield return new WaitWhile(() => isPaused);

                if (!vulkanBridge.IsSlotPrepared(currentSlot))
                {
                    Debug.LogError(
                        $"[FaceLandmarker] Slot {currentSlot} is not ready for MediaPipe."
                    );
                    yield break;
                }

                Image image = vulkanBridge.CreateMediaPipeImage(currentSlot, glContext);

                if (image == null)
                {
                    Debug.LogError(
                        $"[FaceLandmarker] Failed to wrap slot {currentSlot} as a MediaPipe GPU image."
                    );
                    yield break;
                }

                Interlocked.Exchange(ref inferenceCallbackReceived, 0);

                long cycleStart = Stopwatch.GetTimestamp();
                long inferenceStart = Stopwatch.GetTimestamp();
                long timestamp = GetCurrentTimestampMillisec();

                // MediaPipe owns currentSlot until its texture-deletion callback fires.
                taskApi.DetectAsync(image, timestamp, imageProcessingOptions);

                // Prepare nextSlot immediately so Vulkan transfer overlaps current inference.
                long bridgeStart = Stopwatch.GetTimestamp();

                if (!vulkanBridge.BeginPrepareFrame(nextSlot))
                {
                    Debug.LogError(
                        $"[FaceLandmarker] Failed to begin preparing slot {nextSlot}."
                    );
                    yield break;
                }

                double bridgeReadyMs = -1;

                while (Volatile.Read(ref inferenceCallbackReceived) == 0)
                {
                    if (bridgeReadyMs < 0 &&
                        vulkanBridge.IsFrameSyncReady(nextSlot))
                    {
                        bridgeReadyMs = ElapsedMs(bridgeStart);
                    }

                    yield return null;
                }

                double inferenceMs = ElapsedMs(inferenceStart);

                // Do not reuse currentSlot until MediaPipe has released the GL texture.
                long releaseStart = Stopwatch.GetTimestamp();

                while (!vulkanBridge.IsMediaPipeImageReleased(currentSlot))
                    yield return null;

                vulkanBridge.WaitForMediaPipeRelease(currentSlot);

                double releaseMs = ElapsedMs(releaseStart);

                if (bridgeReadyMs < 0 &&
                    vulkanBridge.IsFrameSyncReady(nextSlot))
                {
                    bridgeReadyMs = ElapsedMs(bridgeStart);
                }

                long bridgeTailStart = Stopwatch.GetTimestamp();

                while (!vulkanBridge.IsFrameSyncReady(nextSlot))
                {
                    if (isPaused)
                        yield return new WaitWhile(() => isPaused);

                    yield return null;
                }

                if (bridgeReadyMs < 0)
                    bridgeReadyMs = ElapsedMs(bridgeStart);

                // Synchronous finalization is deliberate. A nested coroutine here adds
                // an extra Unity frame even when the Vulkan fence is already ready.
                if (!vulkanBridge.FinalizePreparedFrame(nextSlot))
                {
                    Debug.LogError(
                        $"[FaceLandmarker] Slot {nextSlot} failed Vulkan-to-EGL synchronization."
                    );
                    yield break;
                }

                double bridgeTailMs = ElapsedMs(bridgeTailStart);
                double cycleMs = ElapsedMs(cycleStart);

                RecordPerformance(
                    cycleMs,
                    inferenceMs,
                    bridgeReadyMs,
                    bridgeTailMs,
                    releaseMs
                );

                int oldCurrent = currentSlot;
                currentSlot = nextSlot;
                nextSlot = oldCurrent;
            }
        }

        /// <summary>
        /// Runs MediaPipe Unity's standard TextureFrame path for CPU and CPUAsync
        /// image reads. This path does not call VulkanMediaPipeBridgeManager.
        /// </summary>
        /// <param name="imageSource">Active MediaPipe image source.</param>
        /// <param name="options">Face Landmarker options used to allocate result storage.</param>
        /// <param name="flipHorizontally">Whether the source texture must be flipped horizontally.</param>
        /// <param name="flipVertically">Whether the source texture must be flipped vertically.</param>
        /// <param name="imageProcessingOptions">
        /// Rotation and other image-processing metadata passed to Face Landmarker.
        /// </param>
        private IEnumerator RunStandardImageReadPath(
            ImageSource imageSource,
            FaceLandmarkerOptions options,
            bool flipHorizontally,
            bool flipVertically,
            Tasks.Vision.Core.ImageProcessingOptions imageProcessingOptions)
        {
            _textureFramePool?.Dispose();

            _textureFramePool = new Experimental.TextureFramePool(
                imageSource.textureWidth,
                imageSource.textureHeight,
                TextureFormat.RGBA32,
                10
            );

            AsyncGPUReadbackRequest request = default;
            var waitUntilRequestDone = new WaitUntil(() => request.done);
            var waitForEndOfFrame = new WaitForEndOfFrame();

            var result = FaceLandmarkerResult.Alloc(options.numFaces);

            while (true)
            {
                if (isPaused)
                    yield return new WaitWhile(() => isPaused);

                if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return null;
                    continue;
                }

                Image image;

                switch (config.ImageReadMode)
                {
                    case ImageReadMode.CPU:
                        yield return waitForEndOfFrame;

                        textureFrame.ReadTextureOnCPU(
                            imageSource.GetCurrentTexture(),
                            flipHorizontally,
                            flipVertically
                        );

                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();
                        break;

                    case ImageReadMode.CPUAsync:
                    default:
                        request = textureFrame.ReadTextureAsync(
                            imageSource.GetCurrentTexture(),
                            flipHorizontally,
                            flipVertically
                        );

                        yield return waitUntilRequestDone;

                        if (request.hasError)
                        {
                            Debug.LogWarning(
                                "[FaceLandmarker] Failed to read texture asynchronously from the image source."
                            );

                            textureFrame.Release();
                            continue;
                        }

                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();
                        break;
                }

                switch (taskApi.runningMode)
                {
                    case Tasks.Vision.Core.RunningMode.IMAGE:
                        if (taskApi.TryDetect(
                                image,
                                imageProcessingOptions,
                                ref result))
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(result);
                            OnFaceLandmarksDetected?.Invoke(result);
                        }
                        else
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(default);
                        }
                        break;

                    case Tasks.Vision.Core.RunningMode.VIDEO:
                        if (taskApi.TryDetectForVideo(
                                image,
                                GetCurrentTimestampMillisec(),
                                imageProcessingOptions,
                                ref result))
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(result);
                            OnFaceLandmarksDetected?.Invoke(result);
                        }
                        else
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(default);
                        }
                        break;

                    case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
                        taskApi.DetectAsync(
                            image,
                            GetCurrentTimestampMillisec(),
                            imageProcessingOptions
                        );
                        break;
                }
            }
        }

        /// <summary>
        /// Receives LIVE_STREAM results on MediaPipe's callback thread and exposes
        /// them to the annotation and gaze-tracking code.
        /// </summary>
        /// <param name="result">Face Landmarker result returned by MediaPipe.</param>
        /// <param name="image">Input image associated with the result.</param>
        /// <param name="timestamp">Input timestamp associated with the result.</param>
        private void OnFaceLandmarkDetectionOutput(
            FaceLandmarkerResult result,
            Image image,
            long timestamp)
        {
            Interlocked.Exchange(
                ref lastFaceCount,
                result.faceLandmarks?.Count ?? 0
            );

            Interlocked.Exchange(ref inferenceCallbackReceived, 1);

            _faceLandmarkerResultAnnotationController.DrawLater(result);
            OnFaceLandmarksDetected?.Invoke(result);
        }

        /// <summary>
        /// Records temporary steady-state timing metrics for the custom Vulkan bridge path.
        /// </summary>
        /// <param name="cycleMs">Total duration of the current bridge/inference cycle.</param>
        /// <param name="inferenceMs">Time until the LIVE_STREAM result callback was received.</param>
        /// <param name="bridgeReadyMs">Time until the next Vulkan slot's synchronization became ready.</param>
        /// <param name="bridgeTailMs">Additional wait required after inference before the next slot finalized.</param>
        /// <param name="releaseMs">Time spent waiting for MediaPipe to release the current GL texture.</param>
        private void RecordPerformance(
            double cycleMs,
            double inferenceMs,
            double bridgeReadyMs,
            double bridgeTailMs,
            double releaseMs)
        {
            cycleTotalMs += cycleMs;
            inferenceTotalMs += inferenceMs;
            bridgeReadyTotalMs += bridgeReadyMs;
            bridgeTailTotalMs += bridgeTailMs;
            releaseTotalMs += releaseMs;
            performanceSamples++;

            if (performanceSamples < performanceLogInterval)
                return;

            double samples = performanceSamples;
            double averageCycle = cycleTotalMs / samples;
            double fps = averageCycle > 0 ? 1000.0 / averageCycle : 0;

            Debug.Log(
                $"[FacePerf] {fps:F1} FPS | " +
                $"cycle={averageCycle:F1} ms | " +
                $"inference={inferenceTotalMs / samples:F1} ms | " +
                $"bridgeReady={bridgeReadyTotalMs / samples:F1} ms | " +
                $"bridgeTail={bridgeTailTotalMs / samples:F1} ms | " +
                $"release={releaseTotalMs / samples:F1} ms | " +
                $"faces={Volatile.Read(ref lastFaceCount)}"
            );

            cycleTotalMs = 0;
            inferenceTotalMs = 0;
            bridgeReadyTotalMs = 0;
            bridgeTailTotalMs = 0;
            releaseTotalMs = 0;
            performanceSamples = 0;
        }

        /// <summary>
        /// Returns elapsed wall-clock time in milliseconds since a Stopwatch timestamp.
        /// </summary>
        /// <param name="startTimestamp">Timestamp returned by <see cref="Stopwatch.GetTimestamp"/>.</param>
        /// <returns>Elapsed time in milliseconds.</returns>
        private static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) *
                   1000.0 /
                   Stopwatch.Frequency;
        }
    }
}
