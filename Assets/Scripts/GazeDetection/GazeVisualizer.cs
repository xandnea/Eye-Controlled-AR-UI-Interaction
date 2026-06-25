using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Assertions.Must;


public class GazeVisualizer : MonoBehaviour
{
    [Header("References")]
    public FaceLandmarkerRunner faceLandmarkerRunner;

    [Header("Settings")]
    public float rayLength = 5f;
    public Color rayColor = Color.red;
    [Range(0f, 1f)] public float headAlpha = 0.3f;

    private Vector2 _lastGazeVector = Vector3.forward;
    private FaceLandmarkerResult _lastResult;
    private bool _hasData = false;

    // Calibrated Bounds
    private float cMinLeftX, cMaxLeftX, cMinLeftY, cMaxLeftY;
    private float cMinRightX, cMaxRightX, cMinRightY, cMaxRightY;

    public void SetCalibrationBounds(float minLX, float maxLX, float minLY, float maxLY, float minRX, float maxRX, float minRY, float maxRY)
    {
        cMinLeftX = minLX; cMaxLeftX = maxLX; cMinLeftY = minLY; cMaxLeftY = maxLY;
        cMinRightX = minRX; cMaxRightX = maxRX; cMinRightY = minRY; cMaxRightY = maxRY;
    }

    private void OnEnable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected += ProcessGaze;
    }

    private void OnDisable()
    {
        if (faceLandmarkerRunner != null)
            faceLandmarkerRunner.OnFaceLandmarksDetected -= ProcessGaze;
    }

    private void Update()
    {
        if (!_hasData || _lastResult.faceLandmarks == null || _lastResult.faceLandmarks.Count == 0) return;

        var landmarks = _lastResult.faceLandmarks[0].landmarks;

        // 1. Get the nose landmark (168)
        var landmark = landmarks[168];

        // 2. Map to Viewport to get correct 3D origin point (Z controls distance from camera)
        Vector3 viewportPoint = new Vector3(landmark.x, 1f - landmark.y, 0.5f);
        Vector3 origin = Camera.main.ViewportToWorldPoint(viewportPoint);

        // 3. Calculate true Perpendicular Face Normal
        Vector3 faceNormal = GetFaceNormal(_lastResult);

        // 4. Construct base eye direction (Local space)
        Vector3 eyeDirectionLocal = new Vector3(_lastGazeVector.x, _lastGazeVector.y, 1f).normalized;

        // 5. Rotate eye direction so it originates perpendicularly from the face normal
        // This solves the issue of the vector naturally tilting to the right.
        Quaternion faceRotation = Quaternion.FromToRotation(Vector3.forward, faceNormal);
        Vector3 worldDirection = faceRotation * eyeDirectionLocal;

        // 6. Draw
        Debug.DrawRay(origin, worldDirection * rayLength, rayColor);
    }

    private void ProcessGaze(FaceLandmarkerResult result)
    {
        _lastResult = result;
        _lastGazeVector = CalculateGaze(result);
        _hasData = true;
    }

    private Vector2 CalculateGaze(FaceLandmarkerResult result)
    {
        // Results check
        if (result.faceLandmarks == null || result.faceLandmarks.Count == 0) return Vector2.zero;

        var landmarks = result.faceLandmarks[0].landmarks;
        if (landmarks.Count < 474) return Vector2.zero;

        Vector3 faceNormal = GetFaceNormal(_lastResult);

        Vector2 rightIris = new Vector2(landmarks[468].x, landmarks[468].y);
        Vector2 leftIris = new Vector2(landmarks[473].x, landmarks[473].y);

        // Clamp iris movement to -1 to 1 range based on eye socket size
        // Apply the iris position to the face normal to get a 3D gaze vector

        // Original Bounding Box
        float rEyeLookingR = landmarks[33].x;
        float rEyeLookingL = landmarks[133].x;
        float lEyeLookingR = landmarks[362].x;
        float lEyeLookingL = landmarks[263].x;
        float rEyeLookingU = landmarks[28].y;
        float rEyeLookingD = landmarks[230].y;
        float lEyeLookingU = landmarks[258].y;
        float lEyeLookingD = landmarks[450].y;

        Vector2 rightEyeCenter = new Vector2((rEyeLookingL + rEyeLookingR) / 2, (rEyeLookingU + rEyeLookingD) / 2);
        Vector2 leftEyeCenter = new Vector2((lEyeLookingL + lEyeLookingR) / 2, (lEyeLookingU + lEyeLookingD) / 2);

        // RIGHT EYE
        // X: -1 [33] (looking right) to 1 [133] (looking left)
        float rightX = GetIrisInEyeSocketRatio(true, true, rEyeLookingL, rEyeLookingR, rightIris.x, rightEyeCenter.x);
        // Y: -1 [230] (looking down) to 1 [28] (looking up)
        float rightY = GetIrisInEyeSocketRatio(true, false, rEyeLookingU, rEyeLookingD, rightIris.y, rightEyeCenter.y);

        // LEFT EYE
        // X: -1 [362] (looking right) to 1 [263] (looking left)
        float leftX = GetIrisInEyeSocketRatio(false, true, lEyeLookingL, rEyeLookingR, leftIris.x, leftEyeCenter.x);
        // Y: -1 [450] (looking down) to 1 [258] (looking up)
        float leftY = GetIrisInEyeSocketRatio(false, false, lEyeLookingU, lEyeLookingR, leftIris.y, leftEyeCenter.y);

        Debug.Log($"Left Eye: X={leftX}, Y={leftY} | Right Eye: X={rightX}, Y={rightY}");

        // Get Ratios averaged across both eyes
        float avgX = (rightX + leftX) / 2f;
        float avgY = (rightY + leftY) / 2f;
        Vector2 eyeGaze = new Vector2(avgX, avgY);

        // Integrate Head Orientation (The "Alpha" adjustment)
        // We blend the eye gaze with the face normal to anchor the vector.
        // 'headAlpha' is a tuning knob: 1.0 means head movement dictates gaze, 
        // 0.0 means only eyes dictate gaze.
        float headAlpha = 0.3f;
        Vector2 headOrientation = new Vector2(faceNormal.x, faceNormal.y);

        // Final gaze is a combination of head tilt and eye movement
        Vector2 finalGaze = Vector2.Lerp(eyeGaze, headOrientation, headAlpha);

        return finalGaze;
    }

    private float GetIrisInEyeSocketRatio(bool rightEye, bool xAxis, float upperRange, float lowerRange, float irisPos, float eyeCenter)
    {
        float upperMinusCenter = upperRange - eyeCenter;
        float centerMinusLower = eyeCenter - lowerRange;

        float ratio = 0f;
        if (irisPos > eyeCenter)
        {
            ratio = (irisPos - eyeCenter) / upperMinusCenter; // Positive ratio
        }
        else if (irisPos < eyeCenter)
        {
            ratio = (irisPos - eyeCenter) / centerMinusLower; // Negative ratio
        }

        float normalizedRatio = 0f;

        if (rightEye)
        {
            if (xAxis) // Right Eye X
            {
                normalizedRatio = (ratio < 0f) ?
                    (cMinRightX != 0 ? ratio / Mathf.Abs(cMinRightX) : 0f) :
                    (cMaxRightX != 0 ? ratio / Mathf.Abs(cMaxRightX) : 0f);
            }
            else // Right Eye Y
            {
                normalizedRatio = (ratio < 0f) ?
                    (cMinRightY != 0 ? ratio / Mathf.Abs(cMinRightY) : 0f) :
                    (cMaxRightY != 0 ? ratio / Mathf.Abs(cMaxRightY) : 0f);
            }
        }
        else
        {
            if (xAxis) // Left Eye X
            {
                normalizedRatio = (ratio < 0f) ?
                    (cMinLeftX != 0 ? ratio / Mathf.Abs(cMinLeftX) : 0f) :
                    (cMaxLeftX != 0 ? ratio / Mathf.Abs(cMaxLeftX) : 0f);
            }
            else // Left Eye Y
            {
                normalizedRatio = (ratio < 0f) ?
                    (cMinLeftY != 0 ? ratio / Mathf.Abs(cMinLeftY) : 0f) :
                    (cMaxLeftY != 0 ? ratio / Mathf.Abs(cMaxLeftY) : 0f);
            }
        }

        // Clamp to strictly -1 to 1 so the vector never over-extends
        return Mathf.Clamp(normalizedRatio, -1f, 1f);
    }

    private Vector3 GetFaceNormal(FaceLandmarkerResult result)
    {
        var landmarks = result.faceLandmarks[0].landmarks;

        // Use Unity's native coordinate system (flipping Y so it matches screen space)
        Vector3 noseBase = new Vector3(landmarks[168].x, 1f - landmarks[168].y, landmarks[168].z);
        Vector3 topHead = new Vector3(landmarks[10].x, 1f - landmarks[10].y, landmarks[10].z);
        Vector3 sideHead = new Vector3(landmarks[156].x, 1f - landmarks[156].y, landmarks[156].z);

        // Cross product guarantees a perfectly perpendicular vector coming OUT of the face
        return Vector3.Cross(topHead - noseBase, sideHead - noseBase).normalized;
    }
}
