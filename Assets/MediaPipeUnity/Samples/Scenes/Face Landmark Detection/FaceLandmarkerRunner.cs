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
    public class FaceLandmarkerRunner : VisionTaskApiRunner<FaceLandmarker>
    {
        [SerializeField] private FaceLandmarkerResultAnnotationController _faceLandmarkerResultAnnotationController;
        [SerializeField, FormerlySerializedAs("vulkanBridgeTest")] private VulkanMediaPipeBridgeManager vulkanBridge;
        [SerializeField, Min(1)] private int performanceLogInterval = 30;

        private int inferenceCallbackReceived;
        private int lastFaceCount;

        private double cycleTotalMs;
        private double inferenceTotalMs;
        private double bridgeReadyTotalMs;
        private double bridgeTailTotalMs;
        private double releaseTotalMs;
        private int performanceSamples;

        public readonly FaceLandmarkDetectionConfig config = new FaceLandmarkDetectionConfig();
        public event Action<FaceLandmarkerResult> OnFaceLandmarksDetected;

        protected override IEnumerator Run()
        {
            yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

            var options = config.GetFaceLandmarkerOptions(
                config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM
                    ? OnFaceLandmarkDetectionOutput
                    : null);

            taskApi = FaceLandmarker.CreateFromOptions(options, GpuManager.GpuResources);
            var imageSource = ImageSourceProvider.ImageSource;

            yield return imageSource.Play();
            if (!imageSource.isPrepared)
            {
                Debug.LogError("[FaceLandmarker] Failed to start the image source.");
                yield break;
            }

            if (config.ImageReadMode != ImageReadMode.GPU ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan ||
                taskApi.runningMode != Tasks.Vision.Core.RunningMode.LIVE_STREAM)
            {
                Debug.LogError("[FaceLandmarker] Requires ImageReadMode.GPU, Vulkan, and LIVE_STREAM.");
                yield break;
            }

            if (vulkanBridge == null)
            {
                Debug.LogError("[FaceLandmarker] VulkanMediaPipeBridgeManager is not assigned.");
                yield break;
            }

            screen.Initialize(imageSource);
            SetupAnnotationController(_faceLandmarkerResultAnnotationController, imageSource);

            var transform = imageSource.GetTransformationOptions();
            var imageProcessingOptions =
                new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transform.rotationAngle);

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
                    Debug.LogError($"[FaceLandmarker] Slot {currentSlot} is not ready for MediaPipe.");
                    yield break;
                }

                Image image = vulkanBridge.CreateMediaPipeImage(currentSlot, glContext);
                if (image == null)
                {
                    Debug.LogError($"[FaceLandmarker] Failed to wrap slot {currentSlot} as a MediaPipe GPU image.");
                    yield break;
                }

                Interlocked.Exchange(ref inferenceCallbackReceived, 0);

                long cycleStart = Stopwatch.GetTimestamp();
                long inferenceStart = Stopwatch.GetTimestamp();
                long timestamp = GetCurrentTimestampMillisec();

                taskApi.DetectAsync(image, timestamp, imageProcessingOptions);

                long bridgeStart = Stopwatch.GetTimestamp();
                if (!vulkanBridge.BeginPrepareFrame(nextSlot))
                {
                    Debug.LogError($"[FaceLandmarker] Failed to begin preparing slot {nextSlot}.");
                    yield break;
                }

                double bridgeReadyMs = -1;

                while (Volatile.Read(ref inferenceCallbackReceived) == 0)
                {
                    if (bridgeReadyMs < 0 && vulkanBridge.IsFrameSyncReady(nextSlot))
                        bridgeReadyMs = ElapsedMs(bridgeStart);

                    yield return null;
                }

                double inferenceMs = ElapsedMs(inferenceStart);
                long releaseStart = Stopwatch.GetTimestamp();

                while (!vulkanBridge.IsMediaPipeImageReleased(currentSlot))
                    yield return null;

                vulkanBridge.WaitForMediaPipeRelease(currentSlot);
                double releaseMs = ElapsedMs(releaseStart);

                if (bridgeReadyMs < 0 && vulkanBridge.IsFrameSyncReady(nextSlot))
                    bridgeReadyMs = ElapsedMs(bridgeStart);

                long bridgeTailStart = Stopwatch.GetTimestamp();

                while (!vulkanBridge.IsFrameSyncReady(nextSlot))
                {
                    if (isPaused)
                        yield return new WaitWhile(() => isPaused);

                    yield return null;
                }

                if (bridgeReadyMs < 0)
                    bridgeReadyMs = ElapsedMs(bridgeStart);

                if (!vulkanBridge.FinalizePreparedFrame(nextSlot))
                {
                    Debug.LogError($"[FaceLandmarker] Slot {nextSlot} failed Vulkan-to-EGL synchronization.");
                    yield break;
                }

                double bridgeTailMs = ElapsedMs(bridgeTailStart);

                if (!vulkanBridge.IsSlotPrepared(nextSlot))
                {
                    Debug.LogError($"[FaceLandmarker] Slot {nextSlot} failed to prepare.");
                    yield break;
                }

                double cycleMs = ElapsedMs(cycleStart);
                RecordPerformance(cycleMs, inferenceMs, bridgeReadyMs, bridgeTailMs, releaseMs);

                int oldCurrent = currentSlot;
                currentSlot = nextSlot;
                nextSlot = oldCurrent;
            }
        }

        private void OnFaceLandmarkDetectionOutput(FaceLandmarkerResult result, Image image, long timestamp)
        {
            Interlocked.Exchange(ref lastFaceCount, result.faceLandmarks?.Count ?? 0);
            Interlocked.Exchange(ref inferenceCallbackReceived, 1);

            _faceLandmarkerResultAnnotationController.DrawLater(result);
            OnFaceLandmarksDetected?.Invoke(result);
        }

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
                $"[FacePerf] {fps:F1} FPS | cycle={averageCycle:F1} ms | " +
                $"inference={inferenceTotalMs / samples:F1} ms | " +
                $"bridgeReady={bridgeReadyTotalMs / samples:F1} ms | " +
                $"bridgeTail={bridgeTailTotalMs / samples:F1} ms | " +
                $"release={releaseTotalMs / samples:F1} ms | " +
                $"faces={Volatile.Read(ref lastFaceCount)}");

            cycleTotalMs = 0;
            inferenceTotalMs = 0;
            bridgeReadyTotalMs = 0;
            bridgeTailTotalMs = 0;
            releaseTotalMs = 0;
            performanceSamples = 0;
        }

        private static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
