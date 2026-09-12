using UnityEngine;

/// <summary>
/// Logging categories used by the object-detection pipeline.
/// </summary>
public enum ObjectDetectionLogCategory
{
    Lifecycle,
    Yolo,
    Detections,
    Segmentation,
    Depth,
    Anchors,
    AnchorVisuals
}

/// <summary>
/// Centralized logging controller for the object-detection pipeline.
/// Add one instance to the scene (for example, on a "Debug" GameObject)
/// and enable only the categories you want to see in Logcat.
/// </summary>
[DefaultExecutionOrder(-10000)]
public sealed class ObjectDetectionDebug : MonoBehaviour
{
    [Header("Master Controls")]
    [SerializeField] private bool enableLogs = true;
    [SerializeField] private bool enableWarnings = true;
    [SerializeField] private bool enableErrors = true;

    [Header("Log Categories")]
    [SerializeField] private bool lifecycle = true;
    [SerializeField] private bool yolo = true;
    [SerializeField] private bool detections = true;
    [SerializeField] private bool segmentation = false;
    [SerializeField] private bool depth = true;
    [SerializeField] private bool anchors = true;
    [SerializeField] private bool anchorVisuals = false;

    private static ObjectDetectionDebug instance;

    /// <summary>
    /// Registers this component as the active object-detection logger.
    /// </summary>
    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Debug.LogWarning(
                "[ObjectDetection][Lifecycle] Multiple ObjectDetectionDebug components found. " +
                "The newest instance will be used.",
                this
            );
        }

        instance = this;
    }

    /// <summary>
    /// Clears the static instance when this logger is destroyed.
    /// </summary>
    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    /// <summary>
    /// Returns whether normal logs for a category are currently enabled.
    /// If no controller exists, normal logs remain enabled so diagnostics are not silently lost.
    /// </summary>
    /// <param name="category">The object-detection logging category to query.</param>
    /// <returns>True when messages in the requested category should be emitted.</returns>
    public static bool IsCategoryEnabled(ObjectDetectionLogCategory category)
    {
        if (instance == null)
            return true;

        return instance.enableLogs && instance.IsCategoryToggleEnabled(category);
    }

    /// <summary>
    /// Writes a normal diagnostic message when the requested category is enabled.
    /// </summary>
    /// <param name="category">The logging category associated with the message.</param>
    /// <param name="message">The message to write.</param>
    /// <param name="context">Optional Unity object used as the log context.</param>
    public static void Log(
        ObjectDetectionLogCategory category,
        string message,
        Object context = null)
    {
        if (!IsCategoryEnabled(category))
            return;

        Debug.Log(FormatMessage(category, message), context);
    }

    /// <summary>
    /// Writes a warning message when warnings are enabled.
    /// Warnings are not filtered by the normal category toggles.
    /// </summary>
    /// <param name="category">The logging category associated with the warning.</param>
    /// <param name="message">The warning message to write.</param>
    /// <param name="context">Optional Unity object used as the log context.</param>
    public static void LogWarning(
        ObjectDetectionLogCategory category,
        string message,
        Object context = null)
    {
        if (instance != null && !instance.enableWarnings)
            return;

        Debug.LogWarning(FormatMessage(category, message), context);
    }

    /// <summary>
    /// Writes an error message when errors are enabled.
    /// Errors remain enabled by default because hiding them can obscure configuration failures.
    /// </summary>
    /// <param name="category">The logging category associated with the error.</param>
    /// <param name="message">The error message to write.</param>
    /// <param name="context">Optional Unity object used as the log context.</param>
    public static void LogError(
        ObjectDetectionLogCategory category,
        string message,
        Object context = null)
    {
        if (instance != null && !instance.enableErrors)
            return;

        Debug.LogError(FormatMessage(category, message), context);
    }

    /// <summary>
    /// Returns the serialized category toggle associated with a logging category.
    /// </summary>
    /// <param name="category">The category whose toggle should be checked.</param>
    /// <returns>True when that category is enabled in the Inspector.</returns>
    private bool IsCategoryToggleEnabled(ObjectDetectionLogCategory category)
    {
        return category switch
        {
            ObjectDetectionLogCategory.Lifecycle => lifecycle,
            ObjectDetectionLogCategory.Yolo => yolo,
            ObjectDetectionLogCategory.Detections => detections,
            ObjectDetectionLogCategory.Segmentation => segmentation,
            ObjectDetectionLogCategory.Depth => depth,
            ObjectDetectionLogCategory.Anchors => anchors,
            ObjectDetectionLogCategory.AnchorVisuals => anchorVisuals,
            _ => true
        };
    }

    /// <summary>
    /// Applies a consistent prefix to object-detection log messages.
    /// </summary>
    /// <param name="category">The category associated with the message.</param>
    /// <param name="message">The unformatted message body.</param>
    /// <returns>The formatted message that should be sent to Unity's logger.</returns>
    private static string FormatMessage(
        ObjectDetectionLogCategory category,
        string message)
    {
        return $"[ObjectDetection][{category}] {message}";
    }
}
