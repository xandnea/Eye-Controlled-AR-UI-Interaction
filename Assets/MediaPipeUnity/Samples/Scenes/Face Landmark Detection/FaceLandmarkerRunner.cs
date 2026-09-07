// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using Mediapipe.Tasks.Vision.FaceLandmarker;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;



namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
    public class FaceLandmarkerRunner : VisionTaskApiRunner<FaceLandmarker>
    {
        [SerializeField] private FaceLandmarkerResultAnnotationController _faceLandmarkerResultAnnotationController;
        [SerializeField] private int framePoolSize = 2;

        // ============================================================
        // PERFORMANCE DEBUGGING
        // ============================================================

        private Stopwatch _performanceStopwatch;

        private long _lastReadbackStartMs = -1;
        private long _lastReadbackEndMs = -1;
        private long _lastDispatchMs = -1;
        private long _lastCallbackMs = -1;

        private long _lastCallbackTimestamp = -1;

        private int _frameCount;
        private int _callbackCount;

        private long _totalReadbackMs;
        private long _totalCallbackIntervalMs;
        private long _totalDispatchIntervalMs;

        private long _lastDispatchTimestamp = -1;

        // ============================================================

        private Experimental.TextureFramePool _textureFramePool;

        public readonly FaceLandmarkDetectionConfig config = new FaceLandmarkDetectionConfig();

        public override void Stop()
        {
            base.Stop();
            _textureFramePool?.Dispose();
            _textureFramePool = null;
        }

        protected override IEnumerator Run()
        {
            _performanceStopwatch = Stopwatch.StartNew();

            yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

            var options = config.GetFaceLandmarkerOptions(config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnFaceLandmarkDetectionOutput : null);
            taskApi = FaceLandmarker.CreateFromOptions(options, GpuManager.GpuResources);
            var imageSource = ImageSourceProvider.ImageSource;

            yield return imageSource.Play();

            if (!imageSource.isPrepared)
            {
                Debug.LogError("Failed to start ImageSource, exiting...");
                yield break;
            }

            Debug.Log("========== FACE CAMERA CONFIG ==========");
            Debug.Log($"ImageSource: {imageSource.GetType().Name}");
            Debug.Log($"Texture width: {imageSource.textureWidth}");
            Debug.Log($"Texture height: {imageSource.textureHeight}");
            Debug.Log($"Source name: {imageSource.sourceName}");
            Debug.Log($"Front facing: {imageSource.isFrontFacing}");
            Debug.Log($"Rotation: {imageSource.rotation}");
            Debug.Log($"Vertically flipped: {imageSource.isVerticallyFlipped}");
            Debug.Log($"Graphics API: {SystemInfo.graphicsDeviceType}");
            Debug.Log($"Device: {SystemInfo.deviceModel}");
            Debug.Log($"CPU: {SystemInfo.processorType}");
            Debug.Log($"GPU: {SystemInfo.graphicsDeviceName}");
            Debug.Log($"System memory: {SystemInfo.systemMemorySize} MB");
            Debug.Log($"Graphics memory: {SystemInfo.graphicsMemorySize} MB");
            Debug.Log("=========================================");

            // Use RGBA32 as the input format.
            // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
            _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, framePoolSize);

            // NOTE: The screen will be resized later, keeping the aspect ratio.
            screen.Initialize(imageSource);

            SetupAnnotationController(_faceLandmarkerResultAnnotationController, imageSource);

            var transformationOptions = imageSource.GetTransformationOptions();
            var flipHorizontally = transformationOptions.flipHorizontally;
            var flipVertically = transformationOptions.flipVertically;
            var imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformationOptions.rotationAngle);

            AsyncGPUReadbackRequest req = default;
            var waitUntilReqDone = new WaitUntil(() => req.done);
            var waitForEndOfFrame = new WaitForEndOfFrame();
            var result = FaceLandmarkerResult.Alloc(options.numFaces);

            // NOTE: we can share the GL context of the render thread with MediaPipe (for now, only on Android)
            var canUseGpuImage = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && GpuManager.GpuResources != null;
            using var glContext = canUseGpuImage ? GpuManager.GetGlContext() : null;

            Debug.Log("========== MEDIAPIPE INPUT CONFIG ==========");
            Debug.Log($"Graphics API: {SystemInfo.graphicsDeviceType}");
            Debug.Log($"GpuManager resources available: {GpuManager.GpuResources != null}");
            Debug.Log($"ImageReadMode requested: {config.ImageReadMode}");
            Debug.Log($"Can use GPU image directly: {canUseGpuImage}");
            Debug.Log($"FaceLandmarker delegate: {config.Delegate}");
            Debug.Log("============================================");

            _frameCount = 0;
            _callbackCount = 0;

            while (true)
            {
                if (isPaused)
                {
                    yield return new WaitWhile(() => isPaused);
                }

                if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return null;
                    continue;
                }

                _frameCount++;
                float captureStartTime = Time.realtimeSinceStartup;

                // Build the input Image
                Image image;

                switch (config.ImageReadMode)
                {
                    case ImageReadMode.CPU:
                        Debug.Log("ImageReadMode: CPU");
                        yield return waitForEndOfFrame;
                        textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();
                        break;
                    case ImageReadMode.GPU:
                        if (canUseGpuImage)
                        {
                            Debug.Log("ImageReadMode: GPU");
                            textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                            image = textureFrame.BuildGPUImage(glContext);
                            // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
                            // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
                            yield return waitForEndOfFrame;
                        } else {
                            Debug.LogWarning("Vulkan graphics API not yet supported for GPU, swapping to CPUAsync");
                            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                            yield return waitUntilReqDone;

                            if (req.hasError)
                            {
                                Debug.LogWarning($"[FacePerf] Readback ERROR");

                                textureFrame.Release();
                                continue;
                            }

                            image = textureFrame.BuildCPUImage();
                            textureFrame.Release();
                        }
                        break;
                    case ImageReadMode.CPUAsync:
                    default:
                        Debug.Log("ImageReadMode: CPUAsync");

                        long readbackStartMs = _performanceStopwatch.ElapsedMilliseconds;

                        req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);

                        yield return waitUntilReqDone;

                        long readbackEndMs = _performanceStopwatch.ElapsedMilliseconds;

                        long readbackMs = readbackEndMs - readbackStartMs;

                        _totalReadbackMs += readbackMs;

                        if (req.hasError)
                        {
                            Debug.LogWarning($"[FacePerf] Readback ERROR | " + $"frame={_frameCount} | " + $"time={readbackMs} ms");

                            textureFrame.Release();
                            continue;
                        }

                        image = textureFrame.BuildCPUImage();
                        textureFrame.Release();

                        if (_frameCount % 30 == 0)
                        {
                            Debug.Log($"[FacePerf] READBACK | " + $"frame={_frameCount} | " + $"duration={readbackMs} ms | " + $"resolution={imageSource.textureWidth}x{imageSource.textureHeight}");
                        }

                        break;
                }

                // OLD SWITCH, SYSTEM FAILS IF IMAGE READ MODE IS GPU AND GRAPHICS API IS VULKAN
                //switch (config.ImageReadMode)
                //{
                //    case ImageReadMode.GPU:
                //        if (!canUseGpuImage)
                //        {
                //            throw new System.Exception("ImageReadMode.GPU is not supported");
                //        }
                //        Debug.Log("ImageReadMode: GPU");
                //        textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                //        image = textureFrame.BuildGPUImage(glContext);
                //        // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
                //        // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
                //        yield return waitForEndOfFrame;
                //        break;
                //    case ImageReadMode.CPU:
                //        Debug.Log("ImageReadMode: CPU");
                //        yield return waitForEndOfFrame;
                //        textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                //        image = textureFrame.BuildCPUImage();
                //        textureFrame.Release();
                //        break;
                //    case ImageReadMode.CPUAsync:
                //    default:
                //        Debug.Log("ImageReadMode: CPU");

                //        long readbackStartMs = _performanceStopwatch.ElapsedMilliseconds;

                //        req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);

                //        yield return waitUntilReqDone;

                //        long readbackEndMs = _performanceStopwatch.ElapsedMilliseconds;

                //        long readbackMs = readbackEndMs - readbackStartMs;

                //        _totalReadbackMs += readbackMs;

                //        if (req.hasError)
                //        {
                //            Debug.LogWarning($"[FacePerf] Readback ERROR | " + $"frame={_frameCount} | " + $"time={readbackMs} ms");

                //            textureFrame.Release();
                //            continue;
                //        }

                //        image = textureFrame.BuildCPUImage();
                //        textureFrame.Release();

                //        if (_frameCount % 30 == 0)
                //        {
                //            Debug.Log($"[FacePerf] READBACK | " + $"frame={_frameCount} | " + $"duration={readbackMs} ms | " + $"resolution={imageSource.textureWidth}x{imageSource.textureHeight}");
                //        }

                //        break;
                //}

                // Dispatch to MediaPipe
                float inferenceStartTime = Time.realtimeSinceStartup;

                switch (taskApi.runningMode)
                {
                    case Tasks.Vision.Core.RunningMode.IMAGE:
                        if (taskApi.TryDetect(image, imageProcessingOptions, ref result))
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(result);
                        }
                        else
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(default);
                        }
                        break;
                    case Tasks.Vision.Core.RunningMode.VIDEO:
                        if (taskApi.TryDetectForVideo(image, GetCurrentTimestampMillisec(), imageProcessingOptions, ref result))
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(result);
                        }
                        else
                        {
                            _faceLandmarkerResultAnnotationController.DrawNow(default);
                        }
                        break;
                    case Tasks.Vision.Core.RunningMode.LIVE_STREAM:

                        long dispatchNowMs = _performanceStopwatch.ElapsedMilliseconds;

                        long timestamp = GetCurrentTimestampMillisec();

                        long dispatchInterval = _lastDispatchMs >= 0 ? dispatchNowMs - _lastDispatchMs : -1;

                        _lastDispatchMs = dispatchNowMs;

                        if (_frameCount % 30 == 0)
                        {
                            Debug.Log($"[FacePerf] DISPATCH | " + $"frame={_frameCount} | " + $"timestamp={timestamp} ms | " + $"interval={dispatchInterval} ms");
                        }

                        taskApi.DetectAsync(image, timestamp, imageProcessingOptions);

                        break;
                }
            }
        }

        public event System.Action<FaceLandmarkerResult> OnFaceLandmarksDetected;

        private void OnFaceLandmarkDetectionOutput(FaceLandmarkerResult result, Image image, long timestamp)
        {
            long callbackNowMs = _performanceStopwatch.ElapsedMilliseconds;

            long callbackInterval = _lastCallbackMs >= 0 ? callbackNowMs - _lastCallbackMs : -1;

            _lastCallbackMs = callbackNowMs;

            _callbackCount++;

            if (_callbackCount % 30 == 0)
            {
                Debug.Log($"[FacePerf] CALLBACK | " + $"count={_callbackCount} | " + $"timestamp={timestamp} ms | " + $"interval={callbackInterval} ms");
            }

            _faceLandmarkerResultAnnotationController.DrawLater(result);
            OnFaceLandmarksDetected?.Invoke(result);
        }
    }
}