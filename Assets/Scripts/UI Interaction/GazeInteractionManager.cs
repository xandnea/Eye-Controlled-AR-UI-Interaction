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

    [Header("Cursor Settings")]
    [SerializeField]
    private Image cursorImage;

    [SerializeField]
    private bool cursorHidden = true;

    [SerializeField]
    [Range(0f, 1f)]
    private float cursorAlpha = 1f;

    [Header("Touch Inspection")]
    [SerializeField]
    private bool touchInspectionEnabled = false;

    [Header("Dwell")]
    [SerializeField] private float dwellDuration = 1f;

    [Header("Anchor Animation")]
    [SerializeField] private float idleSpinSpeed = 25f;
    [SerializeField] private float turnDuration = 0.25f;
    [SerializeField] private float panelExpandDuration = 0.2f;

    private readonly Dictionary<GameObject, AnchorData> anchors = new();

    private GameObject currentTarget;
    private float dwellTimer;
    private bool dwellTriggered;

    private readonly List<RaycastResult> uiRaycastResults = new();

    private AnchorData inspectedAnchor;
    private Coroutine inspectionCoroutine;


    private sealed class AnchorData
    {
        public GameObject root;
        public Transform model;

        public GameObject inspectionPanel;
        public RectTransform panel;

        public TMP_Text nameText;
        public TMP_Text confidenceText;

        public string className;
        public float confidence;

        public bool isInspected;

        public Quaternion baseLocalRotation;
        public float spinAngle;
    }


    private void Update()
    {
        UpdateIdleRotation();

        HandleTouchInspection();

        if (gazeCursor == null || arCamera == null)
            return;

        Vector2 gazeScreenPosition = RectTransformUtility.WorldToScreenPoint(null, gazeCursor.position);

        GameObject newTarget = FindWorldTarget(gazeScreenPosition);

        if (newTarget != currentTarget)
        {
            OnTargetChanged(newTarget);
        }

        if (currentTarget == null || dwellTriggered)
            return;

        dwellTimer += Time.deltaTime;

        if (dwellTimer >= dwellDuration)
        {
            dwellTriggered = true;
            Inspect(currentTarget);
        }
    }

    public void SetCursorHidden(bool hidden)
    {
        cursorHidden = hidden;

        if (cursorImage != null)
        {
            cursorImage.enabled = !hidden;
        }
    }

    public void SetCursorColor(Color color)
    {
        if (cursorImage == null)
        {
            Debug.LogWarning("[GazeInteraction] Cursor image is not assigned.");
            return;
        }

        Color currentColor = cursorImage.color;

        cursorImage.color = new Color(
            color.r,
            color.g,
            color.b,
            currentColor.a
        );
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

    public void SetTouchInspectionEnabled(bool enabled)
    {
        touchInspectionEnabled = enabled;

        Debug.Log($"[GazeInteraction] Touch inspection = {enabled}");

        if (!enabled && inspectedAnchor != null)
        {
            CloseInspection();
        }
    }


    private void UpdateIdleRotation()
    {
        foreach (AnchorData anchor in anchors.Values)
        {
            if (anchor == null ||
                anchor.model == null ||
                anchor.isInspected)
            {
                continue;
            }

            anchor.spinAngle = Mathf.Repeat(anchor.spinAngle + idleSpinSpeed * Time.deltaTime, 360f);

            anchor.model.localRotation = anchor.baseLocalRotation * Quaternion.AngleAxis(anchor.spinAngle, Vector3.forward);
        }
    }

    private void HandleTouchInspection()
    {
        if (!touchInspectionEnabled || arCamera == null)
            return;

        Vector2 screenPosition;

#if UNITY_EDITOR

        if (Mouse.current == null ||
            !Mouse.current.leftButton.wasPressedThisFrame)
        {
            return;
        }

        screenPosition =
            Mouse.current.position.ReadValue();

#else

        if (Touchscreen.current == null ||
            !Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            return;
        }

        screenPosition =
            Touchscreen.current.primaryTouch.position.ReadValue();

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
            // Selectable is the base class for Button, Toggle,
            // Slider, Dropdown, etc.
            if (result.gameObject.GetComponentInParent<Selectable>() != null)
            {
                return true;
            }
        }

        return false;
    }

    private void TryInspectFromScreenPosition(Vector2 screenPosition)
    {
        GameObject target = FindWorldTarget(screenPosition);

        // Tapped empty space:
        // close whatever is currently inspected.
        if (target == null)
        {
            if (inspectedAnchor != null)
            {
                CloseInspection();
            }

            return;
        }

        // Tapped the currently open anchor:
        // toggle it closed.
        if (inspectedAnchor != null &&
            inspectedAnchor.root == target)
        {
            CloseInspection();
            return;
        }

        Debug.Log(
            $"[GazeInteraction] Touch inspected {target.name}"
        );

        Inspect(target);
    }

    private GameObject FindWorldTarget(Vector2 screenPosition)
    {
        Ray ray = arCamera.ScreenPointToRay(screenPosition);

        if (!Physics.Raycast(ray, out RaycastHit hit, maxRayDistance, worldInteractableLayers, QueryTriggerInteraction.Collide))
        {
            return null;
        }

        Transform current = hit.collider.transform;

        while (current != null)
        {
            if (anchors.ContainsKey(current.gameObject))
            {
                return current.gameObject;
            }

            current = current.parent;
        }

        return null;
    }


    private void OnTargetChanged(GameObject newTarget)
    {
        // If gaze leaves the inspected object, close it.
        if (inspectedAnchor != null && newTarget != inspectedAnchor.root)
        {
            CloseInspection();
        }

        currentTarget = newTarget;
        dwellTimer = 0f;
        dwellTriggered = false;
    }


    private void Inspect(GameObject target)
    {
        if (!anchors.TryGetValue(target, out AnchorData anchor))
            return;

        if (inspectionCoroutine != null)
        {
            StopCoroutine(inspectionCoroutine);
        }

        inspectionCoroutine = StartCoroutine(OpenInspection(anchor));
    }


    private IEnumerator OpenInspection(AnchorData anchor)
    {
        anchor.isInspected = true;
        inspectedAnchor = anchor;

        Quaternion startRotation =
    anchor.model.localRotation;

        float deltaTo90 = Mathf.Abs(Mathf.DeltaAngle(anchor.spinAngle, 90f));

        float deltaTo270 = Mathf.Abs(Mathf.DeltaAngle(anchor.spinAngle, 270f));

        float targetSpinAngle = deltaTo90 <= deltaTo270 ? 0f : 180f;

        Quaternion targetRotation = anchor.baseLocalRotation * Quaternion.AngleAxis(targetSpinAngle, Vector3.forward);

        float elapsed = 0f;

        while (elapsed < turnDuration)
        {
            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / turnDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.model.localRotation = Quaternion.Slerp(startRotation, targetRotation, t);

            yield return null;
        }

        anchor.model.localRotation = targetRotation;
        anchor.spinAngle = targetSpinAngle;


        // -----------------------------
        // Fill inspection UI
        // -----------------------------

        if (anchor.nameText != null)
        {
            anchor.nameText.text =
                anchor.className;
        }

        if (anchor.confidenceText != null)
        {
            anchor.confidenceText.text =
                $"Conf: {anchor.confidence * 100f:F1}%";
        }


        // -----------------------------
        // Open panel
        // -----------------------------

        anchor.inspectionPanel.SetActive(true);

        Vector3 fullScale = anchor.panel.localScale;

        fullScale.x = 1f;

        Vector3 collapsedScale = fullScale;

        collapsedScale.x = 0f;

        anchor.panel.localScale = collapsedScale;

        elapsed = 0f;

        while (elapsed < panelExpandDuration)
        {
            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / panelExpandDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.panel.localScale = Vector3.Lerp(collapsedScale, fullScale, t);

            yield return null;
        }

        anchor.panel.localScale =
            fullScale;

        inspectionCoroutine = null;
    }


    private void CloseInspection()
    {
        if (inspectedAnchor == null)
            return;

        if (inspectionCoroutine != null)
        {
            StopCoroutine(inspectionCoroutine);
        }

        inspectionCoroutine = StartCoroutine(CloseInspectionRoutine(inspectedAnchor));
    }


    private IEnumerator CloseInspectionRoutine(AnchorData anchor)
    {
        Vector3 fullScale = anchor.panel.localScale;

        Vector3 collapsedScale = fullScale;

        collapsedScale.x = 0f;

        float elapsed = 0f;

        while (elapsed < panelExpandDuration)
        {
            elapsed += Time.deltaTime;

            float t = Mathf.Clamp01(elapsed / panelExpandDuration);
            t = Mathf.SmoothStep(0f, 1f, t);

            anchor.panel.localScale = Vector3.Lerp(fullScale, collapsedScale, t);

            yield return null;
        }

        anchor.inspectionPanel.SetActive(false);

        anchor.isInspected = false;

        if (inspectedAnchor == anchor)
        {
            inspectedAnchor = null;
        }

        inspectionCoroutine = null;
    }


    public void RegisterAnchor(GameObject root, string className, float confidence)
    {
        if (root == null)
            return;

        Transform model = root.transform.Find("Model");

        Transform inspection = root.transform.Find("InspectionPanel");

        Transform panelTransform = root.transform.Find("InspectionPanel/Panel");

        Transform nameTransform =root.transform.Find("InspectionPanel/Panel/NameText");

        Transform confidenceTransform = root.transform.Find("InspectionPanel/Panel/ConfidenceText");

        if (model == null ||
            inspection == null ||
            panelTransform == null)
        {
            Debug.LogError(
                $"[GazeInteraction] Invalid anchor prefab hierarchy on {root.name}."
            );

            return;
        }

        AnchorData data = new AnchorData
            {
                root = root,
                model = model,
                inspectionPanel = inspection.gameObject,
                panel = panelTransform.GetComponent<RectTransform>(),
                nameText = nameTransform != null ? nameTransform.GetComponent<TMP_Text>(): null,
                confidenceText = confidenceTransform != null ? confidenceTransform.GetComponent<TMP_Text>() : null,
                className = className,
                confidence = confidence,
                baseLocalRotation = model.localRotation,
                spinAngle = 0f
        };

        data.inspectionPanel.SetActive(false);

        anchors[root] = data;

        Debug.Log(
            $"[GazeInteraction] Registered {root.name} | " +
            $"{className} | {confidence:F3}"
        );
    }


    public void ClearAnchors()
    {
        inspectedAnchor = null;
        currentTarget = null;

        dwellTimer = 0f;
        dwellTriggered = false;

        anchors.Clear();
    }
}