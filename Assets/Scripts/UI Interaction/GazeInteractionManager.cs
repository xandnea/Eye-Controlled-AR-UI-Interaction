using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public sealed class GazeInteractionManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Camera arCamera;
    [SerializeField] private RectTransform gazeCursor;

    [Header("World Interaction")]
    [SerializeField] private LayerMask worldInteractableLayers;
    [SerializeField] private float maxRayDistance = 10f;

    [Header("UI Interaction")]
    [SerializeField] private LayerMask uiInteractableLayers;

    [Header("Cursor Settings")]
    [SerializeField] private Image cursorImage;
    [SerializeField] private bool cursorHidden = true;
    [SerializeField, Range(0f, 1f)] private float cursorAlpha = 1f;

    [Header("Touch Inspection")]
    [SerializeField] private bool touchInspectionEnabled = false;

    [Header("Dwell")]
    [SerializeField] private float dwellDuration = 1f;
    [SerializeField] private float inspectionExitGraceDuration = 0.2f;

    [Header("Scan Button")]
    [SerializeField] private Button scanButton;
    [SerializeField] private GameObject scanOuterCircle;
    [SerializeField] private float scanSelectedScale = 1.4f;
    [SerializeField] private float scanReleaseDuration = 0.12f;

    [Header("Anchor Animation")]
    [SerializeField] private float idleSpinSpeed = 25f;
    [SerializeField] private float turnDuration = 0.25f;
    [SerializeField] private float panelExpandDuration = 0.2f;

    private readonly Dictionary<GameObject, AnchorData> anchors = new();
    private readonly List<RaycastResult> uiRaycastResults = new();

    private GameObject currentTarget;
    private float dwellTimer;
    private bool dwellTriggered;

    private AnchorData inspectedAnchor;
    private Coroutine inspectionCoroutine;
    private Coroutine inspectionCloseDelayCoroutine;

    private RectTransform scanButtonRect;
    private Vector3 scanButtonBaseScale;
    private Coroutine scanButtonActivationCoroutine;

    private sealed class AnchorData
    {
        public GameObject root;
        public Transform model;
        public Transform inspectionCanvas;

        public GameObject panelObject;
        public RectTransform panel;
        public Vector3 panelExpandedScale;

        public TMP_Text nameText;
        public TMP_Text confidenceText;

        public GameObject selectionIndicator;
        public Image selectionFill;

        public string className;
        public float confidence;

        public bool isInspected;
        public Quaternion baseLocalRotation;
        public float spinAngle;
    }

    private void Start()
    {
        SetCursorHidden(cursorHidden);
        SetCursorAlpha(cursorAlpha);

        if (scanButton != null)
        {
            scanButtonRect = scanButton.GetComponent<RectTransform>();
            scanButtonBaseScale = scanButtonRect.localScale;

            // OuterCircle is a sibling of Scan Button under the Scan object.
            if (scanOuterCircle == null)
            {
                Transform outerCircle = scanButton.transform.parent.Find("OuterCircle");

                if (outerCircle != null)
                    scanOuterCircle = outerCircle.gameObject;
            }
        }

        if (scanOuterCircle != null)
        {
            scanOuterCircle.SetActive(false);
        }
        else
        {
            Debug.LogError("[GazeInteraction] Scan OuterCircle could not be found.", this);
        }
    }

    private void Update()
    {
        UpdateIdleRotation();
        HandleTouchInspection();
        UpdateGazeInteraction();
    }

    // ============================================================
    // GAZE INTERACTION
    // ============================================================

    private void UpdateGazeInteraction()
    {
        if (gazeCursor == null || arCamera == null)
            return;

        Vector2 gazeScreenPosition = GetGazeCursorScreenPosition();

        // UI gets priority over AR objects.
        GameObject newTarget = FindScanButtonTarget(gazeScreenPosition);

        if (newTarget == null)
            newTarget = FindWorldTarget(gazeScreenPosition);

        // Treat the open inspection panel as part of its AR anchor.
        if (newTarget == null &&
            inspectedAnchor != null &&
            IsGazeOverInspectedPanel(gazeScreenPosition))
        {
            newTarget = inspectedAnchor.root;
        }

        if (newTarget != currentTarget)
            OnGazeTargetChanged(newTarget);

        if (currentTarget == null || dwellTriggered)
            return;

        dwellTimer += Time.unscaledDeltaTime;
        float progress = dwellDuration > 0f ? Mathf.Clamp01(dwellTimer / dwellDuration) : 1f;

        if (IsScanButtonTarget(currentTarget))
            SetScanButtonProgress(progress);
        else
            SetSelectionProgress(currentTarget, progress);

        if (dwellTimer < dwellDuration)
            return;

        dwellTriggered = true;

        if (IsScanButtonTarget(currentTarget))
        {
            BeginScanButtonActivation();
        }
        else
        {
            HideSelectionIndicator(currentTarget);
            Inspect(currentTarget);
        }
    }

    private Vector2 GetGazeCursorScreenPosition()
    {
        Canvas canvas = gazeCursor.GetComponentInParent<Canvas>();
        Camera canvasCamera = null;

        if (canvas != null)
        {
            Canvas rootCanvas = canvas.rootCanvas;

            if (rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                canvasCamera = rootCanvas.worldCamera;
        }

        return RectTransformUtility.WorldToScreenPoint(canvasCamera, gazeCursor.position);
    }

    private GameObject FindScanButtonTarget(Vector2 screenPosition)
    {
        if (scanButton == null ||
            EventSystem.current == null ||
            !scanButton.gameObject.activeInHierarchy ||
            !scanButton.IsInteractable())
        {
            return null;
        }

        var eventData = new PointerEventData(EventSystem.current)
        {
            position = screenPosition
        };

        uiRaycastResults.Clear();
        EventSystem.current.RaycastAll(eventData, uiRaycastResults);

        foreach (RaycastResult result in uiRaycastResults)
        {
            Button button = result.gameObject.GetComponentInParent<Button>();

            if (button == scanButton)
                return scanButton.gameObject;
        }

        return null;
    }

    private bool IsScanButtonTarget(GameObject target)
    {
        return scanButton != null && target == scanButton.gameObject;
    }

    private void OnGazeTargetChanged(GameObject newTarget)
    {
        if (currentTarget != null)
        {
            if (IsScanButtonTarget(currentTarget))
                ResetScanButtonVisuals();
            else
                HideSelectionIndicator(currentTarget);
        }

        if (inspectedAnchor != null)
        {
            if (newTarget == inspectedAnchor.root)
                CancelInspectionClose();
            else
                ScheduleInspectionClose();
        }

        currentTarget = newTarget;
        dwellTimer = 0f;
        dwellTriggered = false;

        if (currentTarget == null)
            return;

        if (IsScanButtonTarget(currentTarget))
        {
            BeginScanButtonHover();
        }
        else if (anchors.TryGetValue(currentTarget, out AnchorData anchor) &&
                 !anchor.isInspected)
        {
            ShowSelectionIndicator(currentTarget);
        }
    }

    // ============================================================
    // SELECTION INDICATOR
    // ============================================================

    private void ShowSelectionIndicator(GameObject target)
    {
        if (!anchors.TryGetValue(target, out AnchorData anchor))
            return;

        if (anchor.selectionFill != null)
            anchor.selectionFill.fillAmount = 0f;

        if (anchor.selectionIndicator != null)
            anchor.selectionIndicator.SetActive(true);
    }

    private void SetSelectionProgress(GameObject target, float progress)
    {
        if (!anchors.TryGetValue(target, out AnchorData anchor))
            return;

        if (anchor.selectionFill != null)
            anchor.selectionFill.fillAmount = Mathf.Clamp01(progress);
    }

    private void HideSelectionIndicator(GameObject target)
    {
        if (!anchors.TryGetValue(target, out AnchorData anchor))
            return;

        if (anchor.selectionFill != null)
            anchor.selectionFill.fillAmount = 0f;

        if (anchor.selectionIndicator != null)
            anchor.selectionIndicator.SetActive(false);
    }

    private void BeginScanButtonHover()
    {
        if (scanOuterCircle != null)
            scanOuterCircle.SetActive(true);

        if (scanButtonRect != null)
            scanButtonRect.localScale = scanButtonBaseScale;
    }

    private void SetScanButtonProgress(float progress)
    {
        if (scanButtonRect == null)
            return;

        if (scanOuterCircle != null && !scanOuterCircle.activeSelf)
            scanOuterCircle.SetActive(true);

        Vector3 targetScale = scanButtonBaseScale * scanSelectedScale;
        scanButtonRect.localScale = Vector3.Lerp(
            scanButtonBaseScale,
            targetScale,
            Mathf.Clamp01(progress)
        );
    }

    private void ResetScanButtonVisuals()
    {
        if (scanOuterCircle != null)
            scanOuterCircle.SetActive(false);

        if (scanButtonRect != null && scanButtonActivationCoroutine == null)
            scanButtonRect.localScale = scanButtonBaseScale;
    }

    private void BeginScanButtonActivation()
    {
        if (scanButtonActivationCoroutine != null)
            StopCoroutine(scanButtonActivationCoroutine);

        scanButtonActivationCoroutine = StartCoroutine(CompleteScanButtonActivation());
    }

    private IEnumerator CompleteScanButtonActivation()
    {
        if (scanButtonRect == null || scanButton == null)
            yield break;

        Vector3 startScale = scanButtonRect.localScale;
        float elapsed = 0f;

        while (elapsed < scanReleaseDuration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / scanReleaseDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            scanButtonRect.localScale = Vector3.Lerp(startScale, scanButtonBaseScale, t);
            yield return null;
        }

        scanButtonRect.localScale = scanButtonBaseScale;

        if (scanOuterCircle != null)
            scanOuterCircle.SetActive(false);

        scanButton.onClick.Invoke();
        scanButtonActivationCoroutine = null;
    }

    // ============================================================
    // CURSOR SETTINGS
    // ============================================================

    public void SetCursorHidden(bool hidden)
    {
        cursorHidden = hidden;

        if (cursorImage != null)
            cursorImage.enabled = !hidden;
    }

    public void SetCursorColor(Color color)
    {
        if (cursorImage == null)
        {
            Debug.LogWarning("[GazeInteraction] Cursor image is not assigned.");
            return;
        }

        Color currentColor = cursorImage.color;
        cursorImage.color = new Color(color.r, color.g, color.b, currentColor.a);
    }

    public void SetCursorAlpha(float alpha)
    {
        cursorAlpha = Mathf.Clamp01(alpha);

        if (cursorImage == null)
        {
            Debug.LogWarning("[GazeInteraction] Cursor image is not assigned.");
            return;
        }

        Color color = cursorImage.color;
        color.a = cursorAlpha;
        cursorImage.color = color;
    }

    // ============================================================
    // TOUCH INSPECTION
    // ============================================================

    public void SetTouchInspectionEnabled(bool enabled)
    {
        touchInspectionEnabled = enabled;
        Debug.Log($"[GazeInteraction] Touch inspection = {enabled}");

        if (!enabled && inspectedAnchor != null)
            CloseInspection();
    }

    private void HandleTouchInspection()
    {
        if (!touchInspectionEnabled || arCamera == null)
            return;

        Vector2 screenPosition;

#if UNITY_EDITOR
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
            return;

        screenPosition = Mouse.current.position.ReadValue();
#else
        if (Touchscreen.current == null ||
            !Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            return;
        }

        screenPosition = Touchscreen.current.primaryTouch.position.ReadValue();
#endif

        if (IsPointerOverInteractiveUI(screenPosition))
            return;

        TryInspectFromScreenPosition(screenPosition);
    }

    private bool IsPointerOverInteractiveUI(Vector2 screenPosition)
    {
        if (EventSystem.current == null)
            return false;

        var eventData = new PointerEventData(EventSystem.current)
        {
            position = screenPosition
        };

        uiRaycastResults.Clear();
        EventSystem.current.RaycastAll(eventData, uiRaycastResults);

        foreach (RaycastResult result in uiRaycastResults)
        {
            if (result.gameObject.GetComponentInParent<Selectable>() != null)
                return true;
        }

        return false;
    }

    private void TryInspectFromScreenPosition(Vector2 screenPosition)
    {
        GameObject target = FindWorldTarget(screenPosition);

        if (target == null)
        {
            if (inspectedAnchor != null)
                CloseInspection();

            return;
        }

        if (inspectedAnchor != null && inspectedAnchor.root == target)
        {
            CloseInspection();
            return;
        }

        HideSelectionIndicator(target);
        Debug.Log($"[GazeInteraction] Touch inspected {target.name}");
        Inspect(target);
    }

    // ============================================================
    // WORLD RAYCAST
    // ============================================================

    private GameObject FindWorldTarget(Vector2 screenPosition)
    {
        Ray ray = arCamera.ScreenPointToRay(screenPosition);

        if (!Physics.Raycast(
                ray,
                out RaycastHit hit,
                maxRayDistance,
                worldInteractableLayers,
                QueryTriggerInteraction.Collide))
        {
            return null;
        }

        Transform current = hit.collider.transform;

        while (current != null)
        {
            if (anchors.ContainsKey(current.gameObject))
                return current.gameObject;

            current = current.parent;
        }

        return null;
    }

    private bool IsGazeOverInspectedPanel(Vector2 screenPosition)
    {
        if (inspectedAnchor == null ||
            inspectedAnchor.panel == null ||
            inspectedAnchor.panelObject == null ||
            !inspectedAnchor.panelObject.activeInHierarchy)
        {
            return false;
        }

        return RectTransformUtility.RectangleContainsScreenPoint(
            inspectedAnchor.panel,
            screenPosition,
            arCamera
        );
    }

    // ============================================================
    // IDLE ANCHOR ROTATION
    // ============================================================

    private void UpdateIdleRotation()
    {
        foreach (AnchorData anchor in anchors.Values)
        {
            if (anchor == null || anchor.model == null || anchor.isInspected)
                continue;

            anchor.spinAngle = Mathf.Repeat(
                anchor.spinAngle + idleSpinSpeed * Time.deltaTime,
                360f
            );

            anchor.model.localRotation = anchor.baseLocalRotation *
                                         Quaternion.AngleAxis(anchor.spinAngle, Vector3.forward);
        }
    }

    // ============================================================
    // INSPECTION
    // ============================================================

    private void Inspect(GameObject target)
    {
        if (!anchors.TryGetValue(target, out AnchorData anchor))
            return;

        HideSelectionIndicator(target);

        if (inspectedAnchor != null && inspectedAnchor != anchor)
            ForceCloseInspection(inspectedAnchor);

        if (inspectionCoroutine != null)
        {
            StopCoroutine(inspectionCoroutine);
            inspectionCoroutine = null;
        }

        inspectionCoroutine = StartCoroutine(OpenInspection(anchor));
    }

    private IEnumerator OpenInspection(AnchorData anchor)
    {
        anchor.isInspected = true;
        inspectedAnchor = anchor;

        Quaternion startRotation = anchor.model.localRotation;

        float deltaTo90 = Mathf.Abs(Mathf.DeltaAngle(anchor.spinAngle, 90f));
        float deltaTo270 = Mathf.Abs(Mathf.DeltaAngle(anchor.spinAngle, 270f));
        float targetSpinAngle = deltaTo90 <= deltaTo270 ? 90f : 270f;

        Quaternion targetRotation = anchor.baseLocalRotation *
                                    Quaternion.AngleAxis(targetSpinAngle, Vector3.forward);

        float elapsed = 0f;

        while (elapsed < turnDuration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / turnDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.model.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);
            yield return null;
        }

        anchor.model.localRotation = targetRotation;
        anchor.spinAngle = targetSpinAngle;

        if (anchor.nameText != null)
            anchor.nameText.text = anchor.className;

        if (anchor.confidenceText != null)
            anchor.confidenceText.text = $"Conf: {anchor.confidence * 100f:F1}%";

        if (anchor.panelObject != null)
            anchor.panelObject.SetActive(true);

        Vector3 expandedScale = anchor.panelExpandedScale;
        Vector3 collapsedScale = expandedScale;
        collapsedScale.x = 0f;

        anchor.panel.localScale = collapsedScale;
        elapsed = 0f;

        while (elapsed < panelExpandDuration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / panelExpandDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.panel.localScale = Vector3.Lerp(collapsedScale, expandedScale, t);
            yield return null;
        }

        anchor.panel.localScale = expandedScale;
        inspectionCoroutine = null;
    }

    private void CloseInspection()
    {
        if (inspectedAnchor == null)
            return;

        if (inspectionCoroutine != null)
        {
            StopCoroutine(inspectionCoroutine);
            inspectionCoroutine = null;
        }

        inspectionCoroutine = StartCoroutine(CloseInspectionRoutine(inspectedAnchor));
    }

    private void ScheduleInspectionClose()
    {
        if (inspectionCloseDelayCoroutine != null)
            return;

        inspectionCloseDelayCoroutine = StartCoroutine(CloseInspectionAfterDelay());
    }

    private void CancelInspectionClose()
    {
        if (inspectionCloseDelayCoroutine == null)
            return;

        StopCoroutine(inspectionCloseDelayCoroutine);
        inspectionCloseDelayCoroutine = null;
    }

    private IEnumerator CloseInspectionAfterDelay()
    {
        yield return new WaitForSecondsRealtime(inspectionExitGraceDuration);

        inspectionCloseDelayCoroutine = null;

        if (inspectedAnchor != null &&
            currentTarget != inspectedAnchor.root)
        {
            CloseInspection();
        }
    }

    private IEnumerator CloseInspectionRoutine(AnchorData anchor)
    {
        Vector3 expandedScale = anchor.panelExpandedScale;
        Vector3 startScale = anchor.panel.localScale;
        Vector3 collapsedScale = expandedScale;
        collapsedScale.x = 0f;

        float elapsed = 0f;

        while (elapsed < panelExpandDuration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / panelExpandDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.panel.localScale = Vector3.Lerp(startScale, collapsedScale, t);
            yield return null;
        }

        anchor.panel.localScale = collapsedScale;

        if (anchor.panelObject != null)
            anchor.panelObject.SetActive(false);

        anchor.isInspected = false;

        if (inspectedAnchor == anchor)
            inspectedAnchor = null;

        inspectionCoroutine = null;
    }

    private void ForceCloseInspection(AnchorData anchor)
    {
        if (anchor == null)
            return;

        if (anchor.panel != null)
        {
            Vector3 collapsed = anchor.panelExpandedScale;
            collapsed.x = 0f;
            anchor.panel.localScale = collapsed;
        }

        if (anchor.panelObject != null)
            anchor.panelObject.SetActive(false);

        anchor.isInspected = false;

        if (inspectedAnchor == anchor)
            inspectedAnchor = null;
    }

    // ============================================================
    // REGISTRATION
    // ============================================================

    public void RegisterAnchor(GameObject root, string className, float confidence)
    {
        if (root == null)
            return;

        Transform model = root.transform.Find("Model");
        Transform inspectionCanvas = root.transform.Find("InspectionCanvas");
        Transform panelTransform = root.transform.Find("InspectionCanvas/Panel");
        Transform nameTransform = root.transform.Find("InspectionCanvas/Panel/LabelText");
        Transform confidenceTransform = root.transform.Find("InspectionCanvas/Panel/ConfText");
        Transform selectionTransform = root.transform.Find("InspectionCanvas/SelectionIndicator");
        Transform selectionFillTransform = root.transform.Find("InspectionCanvas/SelectionIndicator/Fill");

        if (model == null || inspectionCanvas == null || panelTransform == null)
        {
            Debug.LogError(
                $"[GazeInteraction] Invalid AR prefab hierarchy on {root.name}. " +
                "Expected Model, InspectionCanvas, and InspectionCanvas/Panel."
            );
            return;
        }

        RectTransform panelRect = panelTransform.GetComponent<RectTransform>();

        if (panelRect == null)
        {
            Debug.LogError($"[GazeInteraction] Panel on {root.name} does not have a RectTransform.");
            return;
        }

        AnchorData data = new AnchorData
        {
            root = root,
            model = model,
            inspectionCanvas = inspectionCanvas,

            panelObject = panelTransform.gameObject,
            panel = panelRect,
            panelExpandedScale = panelRect.localScale,

            nameText = nameTransform != null ? nameTransform.GetComponent<TMP_Text>() : null,
            confidenceText = confidenceTransform != null ? confidenceTransform.GetComponent<TMP_Text>() : null,

            selectionIndicator = selectionTransform != null ? selectionTransform.gameObject : null,
            selectionFill = selectionFillTransform != null ? selectionFillTransform.GetComponent<Image>() : null,

            className = className,
            confidence = confidence,

            baseLocalRotation = model.localRotation,
            spinAngle = 0f,
            isInspected = false
        };

        data.inspectionCanvas.gameObject.SetActive(true);

        Vector3 collapsedPanelScale = data.panelExpandedScale;
        collapsedPanelScale.x = 0f;
        data.panel.localScale = collapsedPanelScale;
        data.panelObject.SetActive(false);

        if (data.selectionFill != null)
            data.selectionFill.fillAmount = 0f;

        if (data.selectionIndicator != null)
            data.selectionIndicator.SetActive(false);

        anchors[root] = data;

        Debug.Log($"[GazeInteraction] Registered {root.name} | {className} | {confidence:F3}");
    }

    // ============================================================
    // CLEAR
    // ============================================================

    public void ClearAnchors()
    {
        if (inspectionCoroutine != null)
        {
            StopCoroutine(inspectionCoroutine);
            inspectionCoroutine = null;
        }

        inspectedAnchor = null;
        currentTarget = null;
        dwellTimer = 0f;
        dwellTriggered = false;

        anchors.Clear();
    }
}
