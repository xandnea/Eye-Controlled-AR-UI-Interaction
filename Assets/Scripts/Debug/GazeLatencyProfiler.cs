using System.Collections.Generic;
using UnityEngine;

namespace Mediapipe.Unity
{
    public class GazeLatencyProfiler : MonoBehaviour
    {
        private static GazeLatencyProfiler _instance;
        public static GazeLatencyProfiler Instance => _instance;

        private void Awake()
        {
            if (_instance == null) _instance = this;
            else Destroy(gameObject);
        }

        // Dictionary to track timestamps per frame/packet
        private readonly Dictionary<int, float> _frameCaptureTimes = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _inferenceStartTimes = new Dictionary<int, float>();

        public void LogFrameCaptured(int frameId)
        {
            _frameCaptureTimes[frameId] = Time.realtimeSinceStartup;
        }

        public void LogInferenceStart(int frameId)
        {
            _inferenceStartTimes[frameId] = Time.realtimeSinceStartup;
            if (_frameCaptureTimes.ContainsKey(frameId))
            {
                float queueWaitTime = (_inferenceStartTimes[frameId] - _frameCaptureTimes[frameId]) * 1000f;
                if (Time.frameCount % 30 == 0) // Log periodically to avoid spam
                    Debug.Log($"[Profiler] Frame {frameId} - Queue/Capture Wait Time: {queueWaitTime:F1} ms");
            }
        }

        public void LogInferenceComplete(int frameId)
        {
            if (_inferenceStartTimes.ContainsKey(frameId))
            {
                float inferenceDuration = (Time.realtimeSinceStartup - _inferenceStartTimes[frameId]) * 1000f;
                if (Time.frameCount % 30 == 0)
                    Debug.Log($"[Profiler] Frame {frameId} - MediaPipe Inference Duration: {inferenceDuration:F1} ms (~{1000f / inferenceDuration:F1} FPS)");
            }
        }

        public void LogCursorRender(int frameId)
        {
            if (_frameCaptureTimes.ContainsKey(frameId))
            {
                float totalLatency = (Time.realtimeSinceStartup - _frameCaptureTimes[frameId]) * 1000f;
                if (Time.frameCount % 30 == 0)
                    Debug.Log($"[Profiler] Frame {frameId} - **Total End-to-End Latency**: {totalLatency:F1} ms");

                // Cleanup old frames
                _frameCaptureTimes.Remove(frameId);
                _inferenceStartTimes.Remove(frameId);
            }
        }
    }
}
