using System.Collections.Generic;
using UnityEngine;

public class ConcurrentCameraCheck : MonoBehaviour
{
    private const int LENS_FACING_FRONT = 0;
    private const int LENS_FACING_BACK = 1;
    private const int LENS_FACING_EXTERNAL = 2;

    private const int HARDWARE_LEVEL_LIMITED = 0;
    private const int HARDWARE_LEVEL_FULL = 1;
    private const int HARDWARE_LEVEL_LEGACY = 2;
    private const int HARDWARE_LEVEL_3 = 3;
    private const int HARDWARE_LEVEL_EXTERNAL = 4;

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        CheckConcurrentCameras();
#else
        Debug.Log("Concurrent camera check only runs on Android.");
#endif
    }

    private void CheckConcurrentCameras()
    {
        try
        {
            using var unityPlayer =
                new AndroidJavaClass("com.unity3d.player.UnityPlayer");

            using var activity =
                unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            using var packageManager =
                activity.Call<AndroidJavaObject>("getPackageManager");

            bool concurrentSupported =
                packageManager.Call<bool>(
                    "hasSystemFeature",
                    "android.hardware.camera.concurrent"
                );

            Debug.Log(
                "\n========================================\n" +
                $"CONCURRENT CAMERA SUPPORT: {concurrentSupported}\n" +
                "========================================"
            );

            using var version =
                new AndroidJavaClass("android.os.Build$VERSION");

            int sdkInt = version.GetStatic<int>("SDK_INT");

            Debug.Log($"Android API Level: {sdkInt}");

            if (!concurrentSupported)
            {
                Debug.LogWarning("Device does not advertise concurrent camera support.");
                return;
            }

            if (sdkInt < 30)
            {
                Debug.LogWarning(
                    "getConcurrentCameraIds() requires Android API 30+."
                );
                return;
            }

            using var cameraManager =
                activity.Call<AndroidJavaObject>(
                    "getSystemService",
                    "camera"
                );

            if (cameraManager == null)
            {
                Debug.LogError("Failed to get Android CameraManager.");
                return;
            }

            LogAllCameras(cameraManager);
            LogConcurrentCameraSets(cameraManager);
        }
        catch (System.Exception e)
        {
            Debug.LogError(
                "Concurrent camera check failed:\n" + e
            );
        }
    }

    private void LogAllCameras(AndroidJavaObject cameraManager)
    {
        Debug.Log(
            "\n========== ALL CAMERA DEVICES =========="
        );

        string[] cameraIds =
            cameraManager.Call<string[]>("getCameraIdList");

        using var characteristicsClass =
            new AndroidJavaClass(
                "android.hardware.camera2.CameraCharacteristics"
            );

        using var lensFacingKey =
            characteristicsClass.GetStatic<AndroidJavaObject>(
                "LENS_FACING"
            );

        using var hardwareLevelKey =
            characteristicsClass.GetStatic<AndroidJavaObject>(
                "INFO_SUPPORTED_HARDWARE_LEVEL"
            );

        foreach (string cameraId in cameraIds)
        {
            using var characteristics =
                cameraManager.Call<AndroidJavaObject>(
                    "getCameraCharacteristics",
                    cameraId
                );

            int? lensFacing =
                GetIntegerCharacteristic(
                    characteristics,
                    lensFacingKey
                );

            int? hardwareLevel =
                GetIntegerCharacteristic(
                    characteristics,
                    hardwareLevelKey
                );

            Debug.Log(
                $"Camera ID: {cameraId}\n" +
                $"  Facing: {LensFacingToString(lensFacing)}\n" +
                $"  Hardware Level: {HardwareLevelToString(hardwareLevel)}"
            );
        }
    }

    private void LogConcurrentCameraSets(
        AndroidJavaObject cameraManager)
    {
        Debug.Log(
            "\n========== CONCURRENT CAMERA SETS =========="
        );

        using var concurrentSets =
            cameraManager.Call<AndroidJavaObject>(
                "getConcurrentCameraIds"
            );

        if (concurrentSets == null)
        {
            Debug.LogWarning(
                "getConcurrentCameraIds() returned null."
            );
            return;
        }

        int setCount = concurrentSets.Call<int>("size");

        Debug.Log($"Concurrent set count: {setCount}");

        using var outerIterator =
            concurrentSets.Call<AndroidJavaObject>("iterator");

        int setIndex = 0;

        while (outerIterator.Call<bool>("hasNext"))
        {
            using var cameraSet =
                outerIterator.Call<AndroidJavaObject>("next");

            using var innerIterator =
                cameraSet.Call<AndroidJavaObject>("iterator");

            List<string> ids = new List<string>();

            while (innerIterator.Call<bool>("hasNext"))
            {
                using var idObject =
                    innerIterator.Call<AndroidJavaObject>("next");

                string id =
                    idObject.Call<string>("toString");

                ids.Add(id);
            }

            Debug.Log(
                $"Concurrent Set {setIndex}: [{string.Join(", ", ids)}]"
            );

            setIndex++;
        }

        Debug.Log(
            "============================================"
        );
    }

    private int? GetIntegerCharacteristic(
        AndroidJavaObject characteristics,
        AndroidJavaObject key)
    {
        try
        {
            using var value =
                characteristics.Call<AndroidJavaObject>(
                    "get",
                    key
                );

            if (value == null)
                return null;

            return value.Call<int>("intValue");
        }
        catch
        {
            return null;
        }
    }

    private string LensFacingToString(int? facing)
    {
        if (!facing.HasValue)
            return "UNKNOWN";

        return facing.Value switch
        {
            LENS_FACING_FRONT => "FRONT",
            LENS_FACING_BACK => "BACK",
            LENS_FACING_EXTERNAL => "EXTERNAL",
            _ => $"UNKNOWN ({facing.Value})"
        };
    }

    private string HardwareLevelToString(int? level)
    {
        if (!level.HasValue)
            return "UNKNOWN";

        return level.Value switch
        {
            HARDWARE_LEVEL_LIMITED => "LIMITED",
            HARDWARE_LEVEL_FULL => "FULL",
            HARDWARE_LEVEL_LEGACY => "LEGACY",
            HARDWARE_LEVEL_3 => "LEVEL_3",
            HARDWARE_LEVEL_EXTERNAL => "EXTERNAL",
            _ => $"UNKNOWN ({level.Value})"
        };
    }
}