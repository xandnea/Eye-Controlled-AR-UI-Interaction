using UnityEngine;
using UnityEngine.InputSystem;

public class SimulatedGazeRaycaster : MonoBehaviour
{
    [Header("Settings")]
    public float rayDistance = 10f;
    // Set this to "Default" in the Inspector so it doesn't ignore the cube
    public LayerMask interactableLayer = -1;

    [Header("Debug")]
    public Color rayColor = Color.green;

    private InteractableTarget _currentTarget;

    void Update()
    {
        Vector2 screenPos = Vector2.zero;
        bool hasInput = false;

        // Check for Mobile Touch first
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            screenPos = Touchscreen.current.primaryTouch.position.ReadValue();
            hasInput = true;
        }
        // Fallback to PC Mouse (Hovering)
        else if (Mouse.current != null)
        {
            screenPos = Mouse.current.position.ReadValue();
            hasInput = true;
        }

        // If neither are being used, clear the hover state and abort
        if (!hasInput)
        {
            if (_currentTarget != null)
            {
                _currentTarget.OnHoverExit();
                _currentTarget = null;
            }
            return;
        }

        // Convert the input position to a Ray
        Ray gazeRay = Camera.main.ScreenPointToRay(screenPos);
        RaycastHit hit;

        Debug.DrawRay(gazeRay.origin, gazeRay.direction * rayDistance, rayColor);

        // Fire the Raycast
        if (Physics.Raycast(gazeRay, out hit, rayDistance, interactableLayer))
        {
            InteractableTarget target = hit.collider.GetComponent<InteractableTarget>();

            if (target != null && target != _currentTarget)
            {
                if (_currentTarget != null) _currentTarget.OnHoverExit();
                target.OnHoverEnter();
                _currentTarget = target;
            }
        }
        else
        {
            if (_currentTarget != null)
            {
                _currentTarget.OnHoverExit();
                _currentTarget = null;
            }
        }
    }
}