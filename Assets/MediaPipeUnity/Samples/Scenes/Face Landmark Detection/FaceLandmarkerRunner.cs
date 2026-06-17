// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System.Collections;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
  public class FaceLandmarkerRunner : VisionTaskApiRunner<FaceLandmarker>
  {
    [SerializeField] private FaceLandmarkerResultAnnotationController _faceLandmarkerResultAnnotationController;

    private Experimental.TextureFramePool _textureFramePool;

    public readonly FaceLandmarkDetectionConfig config = new FaceLandmarkDetectionConfig();

    public event System.Action<Vector2, Vector2> OnIrisCoordinatesDetected;

    public override void Stop()
    {
      base.Stop();
      _textureFramePool?.Dispose();
      _textureFramePool = null;
    }

    protected override IEnumerator Run()
    {
      Debug.Log($"Delegate = {config.Delegate}");
      Debug.Log($"Image Read Mode = {config.ImageReadMode}");
      Debug.Log($"Running Mode = {config.RunningMode}");
      Debug.Log($"NumFaces = {config.NumFaces}");
      Debug.Log($"MinFaceDetectionConfidence = {config.MinFaceDetectionConfidence}");
      Debug.Log($"MinFacePresenceConfidence = {config.MinFacePresenceConfidence}");
      Debug.Log($"MinTrackingConfidence = {config.MinTrackingConfidence}");
      Debug.Log($"OutputFaceBlendshapes = {config.OutputFaceBlendshapes}");
      Debug.Log($"OutputFacialTransformationMatrixes = {config.OutputFacialTransformationMatrixes}");

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

      // Use RGBA32 as the input format.
      // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
      _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, 10);

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

        // Build the input Image
        Image image;
        switch (config.ImageReadMode)
        {
          case ImageReadMode.GPU:
            if (!canUseGpuImage)
            {
              throw new System.Exception("ImageReadMode.GPU is not supported");
            }
            textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildGPUImage(glContext);
            // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
            // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
            yield return waitForEndOfFrame;
            break;
          case ImageReadMode.CPU:
            yield return waitForEndOfFrame;
            textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
          default:
            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            yield return waitUntilReqDone;

            if (req.hasError)
            {
              Debug.LogWarning($"Failed to read texture from the image source");
              continue;
            }
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
        }

        switch (taskApi.runningMode)
        {
          case Tasks.Vision.Core.RunningMode.IMAGE:
            if (taskApi.TryDetect(image, imageProcessingOptions, ref result))
            {
              _faceLandmarkerResultAnnotationController.DrawNow(result);
              ExtractAndEmitIrisCoordinates(result);
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
              ExtractAndEmitIrisCoordinates(result);
            }
            else
            {
              _faceLandmarkerResultAnnotationController.DrawNow(default);
            }
            break;
          case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
            taskApi.DetectAsync(image, GetCurrentTimestampMillisec(), imageProcessingOptions);
            break;
        }
      }
    }

    private void OnFaceLandmarkDetectionOutput(FaceLandmarkerResult result, Image image, long timestamp)
    {
      _faceLandmarkerResultAnnotationController.DrawLater(result);
      ExtractAndEmitIrisCoordinates(result);
    }

    private void ExtractAndEmitIrisCoordinates(FaceLandmarkerResult result)
    {
      if (result.faceLandmarks == null || result.faceLandmarks.Count == 0) return;

      var landmarks = result.faceLandmarks[0].landmarks;
      if (landmarks.Count < 474) return;

      // 1. Define Landmarks
      Vector2 noseTip = new Vector2(landmarks[1].x, landmarks[1].y);
      Vector2 rightIris = new Vector2(landmarks[468].x, landmarks[468].y);
      Vector2 leftIris = new Vector2(landmarks[473].x, landmarks[473].y);

      // 2. Define Eye Sockets (Crucial: These move with head rotation)
      // Landmark 159 is upper eyelid, 145 is lower (approximation of eye center)
      Vector2 rightEyeCenter = (new Vector2(landmarks[159].x, landmarks[159].y) + new Vector2(landmarks[145].x, landmarks[145].y)) * 0.5f;
      Vector2 leftEyeCenter = (new Vector2(landmarks[386].x, landmarks[386].y) +  new Vector2(landmarks[374].x, landmarks[374].y)) * 0.5f;

      // 3. Calculate Relative Gaze (Iris - EyeSocket)
      // This vector represents rotation within the socket, independent of head position
      Vector2 rightGazeRelative = rightIris - rightEyeCenter;
      Vector2 leftGazeRelative = leftIris - leftEyeCenter;

      // 4. Normalize by Head Scale (Distance between eye centers)
      // This prevents gaze sensitivity from changing when the user moves closer/further from the cam
      float headScale = Vector2.Distance(rightEyeCenter, leftEyeCenter);
      Vector2 normalizedGaze = (rightGazeRelative + leftGazeRelative) * 0.5f / headScale;

      Debug.Log("Normalized Gaze: " + normalizedGaze);

      // 5. Emit normalized vector
      OnIrisCoordinatesDetected?.Invoke(normalizedGaze, noseTip);
    }
  }
}
