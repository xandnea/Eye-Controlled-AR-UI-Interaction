# ECARUII — Eye-Controlled AR UI Interaction

ECARUII is a Unity Android accessibility prototype combining eye-gaze interaction, on-device YOLO11 instance segmentation, AR depth sampling, and anchored 3D annotations. The current Android build is in Google Play Console closed testing.

The project intentionally uses two independent camera paths: the rear camera handles AR/world sensing and scan-triggered detection; the front camera provides face landmarks and gaze. They meet only when the user looks at a UI control or AR annotation. This separation makes the app easier to test and debug.

> **Detailed reference:** [ECARUII Outline | Script Reference and Development Guide](https://docs.google.com/document/d/12NwAYiVth-TJw5IjjsZ63s_6z_8N0sTc/edit?usp=sharing&ouid=107575258905610502392&rtpof=true&sd=true) is Google Docs-importable and contains the development history, prefab contracts, challenges, image placeholders, and detailed script/method explanations.

## Contents

- [Status](#status)
- [Architecture and complete runtime flow](#architecture-and-complete-runtime-flow)
- [Technology stack](#technology-stack)
- [User flow](#user-flow)
- [Run the project](#run-the-project)
- [Scene setup](#scene-setup)
- [Prefab contracts](#prefab-contracts)
- [Implementation notes](#implementation-notes)
- [Vulkan–MediaPipe bridge](#vulkanmediapipe-bridge)
- [Build, diagnostics, and limitations](#build-diagnostics-and-limitations)
- [Documentation and license](#documentation-and-license)

## Status

- **Platform:** Android; a physical device is required for meaningful AR, depth, front-camera gaze, and Vulkan testing.
- **Unity target:** Unity 6000.3.11f1 (Unity 6).
- **Distribution:** Google Play closed testing.
- **Gaze:** MediaPipe face/iris landmarks with local eye-range calibration and global nine-point screen calibration.
- **Detection:** YOLO11 segmentation through ONNX Runtime for Unity.
- **Placement:** segmentation-mask centroid → AR depth raycast → AR anchor → inspectable prefab.
- **Interaction:** gaze dwell, configurable cursor feedback, standard Unity UI, and optional touch inspection.
- **Review scope:** 17 C# scripts. Several production-critical package-side classes are external to this source set, including FaceLandmarkerRunner, ONNX Runtime base helpers, and the native Vulkan bridge.

## Architecture and complete runtime flow

~~~mermaid
flowchart TD
    User["User"] --> UI["Unity UI + TextMeshPro: Calibration, Scan, Settings"]

    subgraph Front["Front-camera gaze path"]
        Cam2["Android Camera2 front camera"] --> FrontTex["Unity camera texture"]
        FrontTex --> Vk["Unity Vulkan VkImage"]
        Vk --> Native["Native bridge: Vulkan, AHardwareBuffer, EGL/GLES, two slots"]
        Native --> MP["MediaPipe Tasks Vision: Face Landmarker LIVE_STREAM"]
        MP --> Raw["GazeVisualizer: face-aligned iris ratios"]
        Raw --> Local["LocalGazeCalibrator: percentile eye ranges"]
        Local --> Global["GlobalGazeCalibrator: 9 points, quadratic fit, deadzone, smoothing"]
        Global --> Cursor["Canvas gaze cursor"]
    end

    subgraph Rear["Rear-camera AR and scan path"]
        AR["AR Foundation + ARCore: rear camera, tracking, depth"] --> Source["TextureSource / VirtualTextureSource"]
        Source --> Scan["Yolo11SegRunner: explicit Scan request"]
        Scan --> ORT["ASUS4 ONNX Runtime for Unity: YOLO11 segmentation ONNX"]
        ORT --> Decode["Yolo11Seg: Burst/Jobs proposal decode, NMS, mask reconstruction"]
        Decode --> Center["Confidence-weighted mask centroid"]
        Center --> Depth["DepthSampler: ARRaycastManager depth raycast"]
        Depth --> Anchor["DetectionAnchorManager: ARAnchorManager"]
        Anchor --> Visual["Anchor visual: collider, model, inspection panel"]
    end

    UI --> Calibration["GazeCalibrationManager + CalibrationUIController"]
    Calibration --> Local
    Calibration --> Global
    UI --> Scan
    Cursor --> Interaction["GazeInteractionManager: UI first, world second, dwell/touch"]
    Visual --> Interaction
    Interaction --> UI
~~~

### Scan path, step by step

1. The user presses **Scan**. Yolo11SegRunner reads the newest rear-camera texture; inference is scan-oriented rather than continuous to avoid unnecessary thermal and battery cost.
2. Yolo11Seg prepares the dynamic YOLO input, confidence-filters proposals, performs non-maximum suppression, and reconstructs masks for the remaining detections.
3. The runner finds a confidence-weighted centroid for pixels inside each accepted mask. This better represents the visible object than a bounding-box center.
4. It converts YOLO’s top-left normalized point to Unity’s bottom-left viewport coordinate. The required Y-axis flip is a common source of placement errors.
5. DepthSampler depth-raycasts at that viewport point. A valid ARCore depth hit supplies world position and camera-to-hit distance.
6. DetectionAnchorManager removes the previous scan’s anchors, creates new AR anchors, spawns the visual prefab, and registers it for inspection.
7. GazeInteractionManager can now open the prefab’s inspection panel by dwell or optional touch.

### Gaze path, step by step

1. The front camera provides frames to MediaPipe Face Landmarker. GazeVisualizer turns iris movement into face-aligned, eye-width-normalized measurements.
2. **Local calibration** records the user looking through a comfortable range. LocalGazeCalibrator uses percentiles rather than raw extrema to resist blinks and landmark outliers.
3. **Global calibration** collects trimmed samples at center plus eight surrounding targets. GlobalGazeCalibrator fits independent six-term quadratic mappings for screen X and Y.
4. The fit is rejected when samples are insufficient or neighboring targets collapse in gaze space. A plausible-looking but unreliable cursor should request recalibration.
5. At runtime, smoothing and a continuous radial deadzone are applied before the cursor moves. The interaction manager tests UI before AR-world colliders, so open UI prevents accidental world selections.

## Technology stack

| Layer | Framework / integration | Purpose |
|---|---|---|
| Engine/UI | Unity 6, Unity UI, TextMeshPro | Scene lifecycle, Canvas UI, controls, cursor, calibration targets, labels, and panels. |
| Input | Unity Input System | Normal UI input and optional touch inspection. |
| Rear AR | AR Foundation + Google ARCore Android provider | Tracking, rear camera, depth raycasts, and AR anchors. |
| Front camera | Android Camera2/project camera integration | Concurrent front-facing stream for face tracking. |
| Face tracking | MediaPipe Tasks Vision Face Landmarker Unity integration | Face/iris landmarks for raw gaze. |
| Graphics | Vulkan on Android | Active Unity graphics path and source VkImage for the bridge. |
| GPU interop | Custom Vulkan–MediaPipe bridge | Vulkan → AHardwareBuffer → EGL/GLES → MediaPipe GPU image. |
| Segmentation | YOLO11 segmentation ONNX model | Object boxes, labels, masks, and coefficients. |
| Inference | ASUS4 ONNX Runtime for Unity | Model loading/execution and image-inference helpers. |
| Camera texture source | TextureSource.ITextureSource / VirtualTextureSource | Latest texture used by the scan runner. |
| Performance | Burst, Jobs, Collections, Mathematics, Profiling | Efficient proposal decoding and segmentation work. |
| Scan presentation | Gilzoide Lottie Player | Optional scan animation overlay. |

Pin exact package versions in Packages/manifest.json and packages-lock.json. AR Foundation/ARCore, MediaPipe, ONNX Runtime, TextureSource, and the bridge are a compatibility set—upgrade and test them together.

## User flow

1. Open calibration mode.
2. Run local eye-range calibration while moving the eyes comfortably through their range.
3. Run global calibration while looking at the nine targets.
4. Return to the rear AR camera view.
5. Press Scan. The app runs one segmentation pass, produces overlays, samples depth at mask centers, and replaces prior anchors.
6. Dwell on a registered AR visual or gaze-aware UI target; enable touch inspection if tapping AR visuals is preferred.
7. Use settings to adjust cursor visibility, scale, opacity, filtering/deadzone, dwell feedback, and touch behavior as exposed by the scene.

## Run the project

### Prerequisites

- Unity Hub plus **Unity 6000.3.11f1** with Android Build Support, Android SDK/NDK tools, and OpenJDK.
- A supported Android device with Google Play Services for AR/ARCore. USB debugging is needed when deploying from Unity.
- The project’s model, label file, segmentation shader, MediaPipe task/assets, native Android plugin, and prefabs. If the repository uses Git LFS, fetch LFS objects too.
- A well-lit environment and textured nearby objects/surfaces for AR depth validation.

### Open and configure

1. Clone the repository and open the Unity project directory in Unity Hub using Unity 6000.3.11f1.
2. Allow package restoration to finish. Resolve package errors before opening the main scene; the project relies on matching package APIs.
3. In **Project Settings → Player → Other Settings**, retain the Android graphics API expected by the project. ECARUII’s live MediaPipe GPU path requires **Vulkan**; do not casually change it to GLES.
4. In **Project Settings → XR Plug-in Management**, enable the configured Android AR provider and confirm camera permissions are declared.
5. Open the main configured scene and assign anything Unity reports as missing using [Scene setup](#scene-setup).
6. Connect the device, choose **Android** as build target, then use **Build And Run**.

### First device run

1. Grant camera permission and wait for AR tracking to settle.
2. Open calibration and complete local, then global calibration. The runtime cursor is not expected to work well before both succeed.
3. Return to the AR view, aim at recognizable objects, and press Scan.
4. Verify detection overlays; then gaze at or tap a spawned visual to verify its inspection panel.

## Scene setup

| Area | Required configuration |
|---|---|
| AR root | ARSession, AR camera rig, ARRaycastManager, ARAnchorManager. Assign the actual AR camera to DepthSampler, DetectionAnchorManager, and GazeInteractionManager. |
| Scan root | Yolo11SegRunner with VirtualTextureSource, ONNX model (bundled OrtAsset or configured remote file), Yolo11Seg.Options, label file, segmentation shader, box prefab/container, and optional Lottie overlay. |
| Placement | DepthSampler referencing raycast manager/camera; DetectionAnchorManager referencing anchor manager/camera, visual prefab, and gaze manager. |
| Gaze root | Front-camera source, FaceLandmarkerRunner, GazeVisualizer, local/global calibrators, and status/instruction UI. Landmark drawing is useful during calibration but optional in normal use. |
| Calibration UI | CalibrationUIController, GazeCalibrationManager, calibration-area RectTransform, target dot, cursor, status text, and instruction artwork. Normal UI hides while this workspace is open. |
| Interaction UI | GazeInteractionManager, cursor transform/image, EventSystem, appropriate world-interactable layer mask, and GazeUIButton controls. |
| Panels | Help, About/Privacy, interaction settings, and scan overlay. UIPanelCoordinator keeps Help and About/Privacy mutually exclusive. |
| Vulkan bridge | Vulkan graphics API, native plugin, managed bridge manager, and bridge-compatible MediaPipe live-stream runner; treat them as one integration. |

The reviewed serialized fields have Inspector tooltips. Correct missing references directly instead of relying on unrelated fallbacks.

## Prefab contracts

### Detection anchor visual

DetectionAnchorManager and GazeInteractionManager expect a stable visual hierarchy:

- Root object with a collider on a layer included by worldInteractableLayers.
- Model transform that can idle-rotate and move to inspection orientation.
- Inspection panel with class-label and confidence text.
- Selection indicator with a fill image driven by dwell progress.
- Renderer/materials compatible with the target Android graphics API.

Keep prefab hierarchy names and lookup code synchronized. The [Development Guide](https://docs.google.com/document/d/12NwAYiVth-TJw5IjjsZ63s_6z_8N0sTc/edit?usp=sharing&ouid=107575258905610502392&rtpof=true&sd=true) includes the reviewed hierarchy and a reserved Inspector image location.

### Detection-box overlay

Yolo11SegRunner pools a TMP_Text detection-box prefab. Author its transform, text style, and outline/background for detectionContainer’s coordinate space. Avoid per-frame layout components that trigger Canvas rebuilds for every detection.

### Gaze-aware button

Add GazeUIButton to a normal Unity UI raycast target. It supports either an expanding outer-circle feedback object or an Image.fillAmount overlay. The manager owns dwell timing; the button completes its release animation and invokes the standard Button.onClick.

## Implementation notes

### Calibration

- Local calibration learns a user-specific raw-eye range; percentile bounds resist bad frames.
- Global calibration maps that range to screen position with separate quadratic X/Y fits.
- Neighbor-separation and sample-count checks prevent a broken mapping from being accepted.
- More smoothing reduces noise but adds lag; a larger deadzone reduces drift but makes small deliberate movements harder.

### Detection and placement

- Dynamic YOLO dimensions preserve aspect ratio and align to 32 pixels.
- Proposals are filtered before NMS; segmentation uses 32 mask coefficients and 32 prototype channels.
- Placement uses mask centroid, not box center.
- YOLO uses a top-left origin while Unity viewport uses bottom-left; preserve the Y flip.
- AR depth legitimately fails on reflective, low-texture, distant, moving, or poorly lit geometry.
- Every accepted scan replaces previous anchors/visuals so stale results do not accumulate.

## Vulkan–MediaPipe bridge

The Android player runs Vulkan, while MediaPipe Unity’s ordinary GPU image path expects a GLES/GL texture in a compatible context. A Unity VkImage cannot be passed directly. CPUAsync is useful as a development fallback, but its GPU-to-CPU copy and synchronization cost makes responsive gaze interaction difficult.

The custom native interop path is:

1. Access the Unity camera image through IUnityGraphicsVulkan / VkImage.
2. Copy it into one of two Vulkan images backed by Android AHardwareBuffer memory.
3. Synchronize and import the same buffer via EGL as a GLES texture in a MediaPipe-compatible context.
4. Wrap the ready slot as a MediaPipe GPU Image and call DetectAsync in LIVE_STREAM mode while Vulkan prepares the other slot.

The two slots let a consumer use one frame while the next is prepared. Startup should fail clearly—not silently fall back—when Vulkan is inactive, the graph is not live-stream, the bridge/shared GL context is unavailable, neither slot is ready, GPU-image wrapping fails, or the camera source cannot start. Test the native plugin, managed bridge, and modified FaceLandmarkerRunner together.

## Build, diagnostics, and limitations

### Before a closed-test upload

1. Confirmed model, labels, shader, MediaPipe assets, native plugin, animations, and prefabs are in the build.
2. Checked Android camera permissions and AR required/optional declarations.
3. Tested first launch, denied permission/recovery, pause/resume, camera restart, and external Privacy Policy return.
4. Calibrated across lighting, glasses, face positions, and supported orientations.
5. Scaned near/far, overlapping, occluded, and empty scenes; no stale overlays or anchors should remain.
6. Tested dwell/touch with all panels open and closed so UI always blocks accidental world hits.

### Diagnostics

- Keep mask-center logging disabled in normal use; it can emit a line per detection.
- Profile markers Yolo11Seg.GenerateProposals and Yolo11Seg.Segmentation separate proposal and mask costs.
- Verify label count matches model layout: four box values, one score per label, and 32 mask coefficients.
- For bridge startup, log graphics API, bridge init, both slots, GL context, GPU wrapping, and DetectAsync submission independently.
- For missing anchors, distinguish bad normalized center, failed depth raycast, and failed async anchor creation.
- For poor gaze, inspect landmark availability, local ranges, global geometry, fit RMSE, and runtime filtering separately.

### Known limitations

- This is not a medical or safety-grade eye tracker; calibration is user-, pose-, lighting-, and device-dependent.
- Anchor stability and depth depend on AR support and scene quality.
- Mask-centroid work grows with detection area × 32 prototype channels; a compute/batched reduction may be useful for continuous scans.
- The bridge requires compatible Vulkan, AHardwareBuffer, EGL, and GLES interop support.
- Automated tests are still recommended for coordinate conversion, percentile trimming, quadratic fitting, dwell transitions, scan cancellation, and bridge lifecycle boundaries.

## Documentation and license

### Documentation

- [ECARUII Outline | Script Reference and Development Guide](https://docs.google.com/document/d/12NwAYiVth-TJw5IjjsZ63s_6z_8N0sTc/edit?usp=sharing&ouid=107575258905610502392&rtpof=true&sd=true)
- Reviewed source files: the 17 C# files included in this documentation pass

### License

Unless a file or directory says otherwise, ECARUII source code is distributed under the [GNU Affero General Public License v3.0](./LICENSE).

Yolo11Seg.cs contains or adapts AGPL-licensed Ultralytics material and must preserve its upstream attribution and license notice. Changes to that code are also distributed under AGPL-3.0.

Third-party packages, model weights, fonts, artwork, animations, Unity assets, and other external materials remain under their own licenses; they are not relicensed by this repository. Before making the repository public, add a THIRD_PARTY_NOTICES.md inventory and remove any material you cannot redistribute.
