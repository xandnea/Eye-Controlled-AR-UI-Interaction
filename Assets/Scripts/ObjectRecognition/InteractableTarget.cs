using UnityEngine;

[RequireComponent(typeof(Renderer))]
public class InteractableTarget : MonoBehaviour
{
    private Renderer _renderer;
    private Color _originalColor;

    [Tooltip("The color the target becomes when looked at.")]
    public Color hoverColor = Color.green;

    void Start()
    {
        _renderer = GetComponent<Renderer>();
        _originalColor = _renderer.material.color;
    }

    // Called by the Raycaster when the ray hits this object
    public void OnHoverEnter()
    {
        _renderer.material.color = hoverColor;
    }

    // Called by the Raycaster when the ray leaves this object
    public void OnHoverExit()
    {
        _renderer.material.color = _originalColor;
    }
}