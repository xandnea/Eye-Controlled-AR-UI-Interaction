using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;

public class GazeCalibration : MonoBehaviour
{
    public enum State { Setup, TopLeft, TopRight, BottomRight, BottomLeft, Tracking, Calibrating }

    [Header("State")]
    public State currentState = State.Setup;

    [Header("References")]
    public FaceLandmarkerRunner faceLandmarkerRunner;
    public RectTransform calibrationTarget;
    public RectTransform gazeIndicator;
    public TMPro.TextMeshProUGUI statusInstructionsText;

    [Header("Settings")]
    public float calibrationSampleDuration = 1.5f;
    [Range(0.01f, 1f)] public float alpha = 0.12f;
    [Range(0.05f, 0.2f)] public float padding = 0.1f;

    private Vector2 rawCalibTL, rawCalibTR, rawCalibBL, rawCalibBR;
    private Vector2 liveSmoothedGaze = Vector2.zero;
    private List<Vector2> collectionBuffer = new List<Vector2>();
    private bool isDataCollectionWindowOpen = false;

    private readonly object _threadLock = new object();
    private Vector2 _pendingGaze;
    private bool _hasNewData = false;

    void Start()
    {
        statusInstructionsText.gameObject.SetActive(true);
        calibrationTarget.gameObject.SetActive(true);
        gazeIndicator.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        if (faceLandmarkerRunner != null)
        {
            faceLandmarkerRunner.OnIrisCoordinatesDetected += HandleIrisCoordinates; 
        }
    }

    private void OnDisable()
    {
        if (faceLandmarkerRunner != null)
        {
            faceLandmarkerRunner.OnIrisCoordinatesDetected -= HandleIrisCoordinates;
        }
    }

    private void HandleIrisCoordinates(Vector2 leftIris, Vector2 rightIris)
    {
        // average the eyes for a single gaze point
        Vector2 centerGaze = (leftIris + rightIris) * 0.5f;
        //Debug.Log("Center Gaze: " + centerGaze);
    }
}