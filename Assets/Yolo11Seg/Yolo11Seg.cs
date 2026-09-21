using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime.Unity;
using Microsoft.ML.OnnxRuntime.UnityEx;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Assertions;

namespace Microsoft.ML.OnnxRuntime.Examples
{
    /*
     * ECARUII YOLO11 segmentation post-processing
     *
     * Adapted from the Ultralytics YOLO segmentation ONNX Runtime example:
     * https://github.com/ultralytics/ultralytics/blob/aecf1da32b50e144b226f54ae4e979dc3cc62145/examples/YOLOv8-Segmentation-ONNXRuntime-Python/main.py
     *
     * Modified for ECARUII by Xander Neary, 2026.
     *
     * SPDX-License-Identifier: AGPL-3.0-only
     *
     * This file is distributed under the GNU Affero General Public License
     * version 3. See the repository LICENSE file for the complete terms.
     */

    /// <summary>
    /// Runs YOLO11 instance-segmentation inference through ONNX Runtime, converts
    /// raw model outputs into filtered detections, reconstructs a weighted mask
    /// center for each detection, and produces a segmentation visualization.
    /// </summary>
    /// <remarks>
    /// The Ultralytics model and derivative implementation are subject to the
    /// AGPL-3.0 license. See the repository LICENSE and
    /// https://github.com/ultralytics/ultralytics/blob/main/LICENSE.
    /// The post-processing flow was adapted from Ultralytics' YOLO segmentation
    /// ONNX Runtime example.
    /// </remarks>
    public sealed class Yolo11Seg : ImageInference<float>
    {
        private const int MaskCoefficientCount = 32;

        /// <summary>
        /// Serializable inference and visualization settings exposed in the
        /// Unity Inspector by <c>Yolo11SegRunner</c>.
        /// </summary>
        [Serializable]
        public class Options : ImageInferenceOptions
        {
            [Header("YOLO options")]
            [Tooltip("Text file containing one model class label per line, in the same order used during training/export.")]
            public TextAsset labelFile;

            [Range(0f, 1f)]
            [Tooltip("Minimum class confidence required for an anchor to become a detection proposal.")]
            public float confidenceThreshold = 0.25f;

            [Range(0f, 1f)]
            [Tooltip("Intersection-over-union threshold used by non-maximum suppression to remove overlapping detections.")]
            public float nmsThreshold = 0.45f;

            [Range(320, 1024)]
            [Tooltip("Maximum input dimension for dynamically shaped models. The final dimensions preserve aspect ratio and are aligned to 32 pixels.")]
            public int dynamicMaxSize = 640;

            [Header("Segmentation options")]
            [Tooltip("Compute shader used to combine prototype masks and render the segmentation texture.")]
            public ComputeShader visualizeSegmentationShader;

            [Range(5, 50)]
            [Tooltip("Initial capacity reserved for final detections and segmentation visualization data.")]
            public int maxDetectionCount = 50;

            [Range(0f, 1f)]
            [Tooltip("Minimum reconstructed mask probability included in the weighted mask-center calculation.")]
            public float maskThreshold = 0.5f;

            [Tooltip("Logs the calculated mask center of every detection. Leave disabled outside focused diagnostics because this can generate substantial per-frame logging.")]
            public bool logMaskCenters;
        }

        /// <summary>
        /// Immutable result produced for one object after confidence filtering
        /// and non-maximum suppression.
        /// </summary>
        public readonly struct Detection : IDetection<Detection>
        {
            /// <summary>Bounding rectangle normalized to YOLO input space.</summary>
            public readonly Rect rect;

            /// <summary>Zero-based class index into <see cref="labelNames"/>.</summary>
            public readonly int label;

            /// <summary>Winning class confidence for this detection.</summary>
            public readonly float probability;

            /// <summary>Index of the source model anchor.</summary>
            public readonly int anchorId;

            /// <summary>
            /// Confidence-weighted segmentation-mask center in normalized YOLO
            /// input space, where (0, 0) is top-left and (1, 1) is bottom-right.
            /// </summary>
            public readonly Vector2 maskCenter;

            /// <summary>
            /// Whether at least one mask pixel passed the configured threshold,
            /// making <see cref="maskCenter"/> valid.
            /// </summary>
            public readonly bool hasMaskCenter;

            /// <inheritdoc/>
            public readonly int Label => label;

            /// <inheritdoc/>
            public readonly Rect Rect => rect;

            /// <summary>Creates an immutable detection result.</summary>
            /// <param name="rect">Bounding rectangle normalized to model input space.</param>
            /// <param name="label">Zero-based class index.</param>
            /// <param name="probability">Winning class confidence.</param>
            /// <param name="anchorId">Index of the model anchor that generated the result.</param>
            /// <param name="maskCenter">Optional normalized center of the reconstructed segmentation mask.</param>
            /// <param name="hasMaskCenter">Whether <paramref name="maskCenter"/> contains a valid result.</param>
            public Detection(
                Rect rect,
                int label,
                float probability,
                int anchorId,
                Vector2 maskCenter = default,
                bool hasMaskCenter = false)
            {
                this.rect = rect;
                this.label = label;
                this.probability = probability;
                this.anchorId = anchorId;
                this.maskCenter = maskCenter;
                this.hasMaskCenter = hasMaskCenter;
            }

            /// <summary>Compares detections in descending confidence order.</summary>
            /// <param name="other">Detection to compare with this instance.</param>
            /// <returns>A sort value that places higher-confidence detections first.</returns>
            public int CompareTo(Detection other)
            {
                // Descending sort
                return other.probability.CompareTo(probability);
            }

            /// <summary>Gets the repeating visualization color assigned to this class.</summary>
            /// <returns>A deterministic palette color for <see cref="label"/>.</returns>
            public Color GetColor()
            {
                return Colors[label % Colors.Length];
            }
        }

        /// <summary>
        /// Identifies a valid reconstructed mask center and the final detection
        /// that produced it. Coordinates retain YOLO's top-left origin.
        /// </summary>
        public readonly struct MaskCenter
        {
            /// <summary>Mask center normalized to the current model input.</summary>
            public readonly Vector2 normalized;

            /// <summary>Mask center expressed in current model-input pixels.</summary>
            public readonly Vector2 inputPixel;

            /// <summary>Index of the corresponding item in <see cref="Detections"/>.</summary>
            public readonly int detectionIndex;

            /// <summary>Creates mask-center metadata for one final detection.</summary>
            /// <param name="normalized">Center normalized to model input space.</param>
            /// <param name="inputPixel">Center in model-input pixels.</param>
            /// <param name="detectionIndex">Index of the corresponding final detection.</param>
            public MaskCenter(
                Vector2 normalized,
                Vector2 inputPixel,
                int detectionIndex)
            {
                this.normalized = normalized;
                this.inputPixel = inputPixel;
                this.detectionIndex = detectionIndex;
            }
        }

        /// <summary>Repeating class-color palette used by detections and segmentation visualization.</summary>
        public static readonly Color[] Colors = (new uint[]
        {
            0x042AFFFF,
            0x0BDBEBFF,
            0xF3F3F3FF,
            0x00DFB7FF,
            0x111F68FF,
            0xFF6FDDFF,
            0xFF444FFF,
            0xCCED00FF,
            0x00F344FF,
            0xBD00FFFF,
            0x00B4FFFF,
            0xDD00BAFF,
            0x00FFFFFF,
            0x26C000FF,
            0x01FFB3FF,
            0x7D24FFFF,
            0x7B0068FF,
            0xFF1B6CFF,
            0xFC6D2FFF,
            0xA2FF0BFF,
        })
        .Select(hex => new Color32(
            (byte)((hex >> 24) & 0xFF),
            (byte)((hex >> 16) & 0xFF),
            (byte)((hex >> 8) & 0xFF),
            (byte)(hex & 0xFF)))
        .Select(c => (Color)c)
        .ToArray();

        private readonly Options options;

        /// <summary>Number of class names expected in each prediction anchor.</summary>
        public readonly int classCount;

        /// <summary>Read-only class names loaded from <see cref="Options.labelFile"/>.</summary>
        public readonly ReadOnlyCollection<string> labelNames;

        // [0: predictions] shape: 1,116,8400 (Batch_size=1, XyWh_conf_cls_nm, Num_anchors)
        // [1: proto] shape: 1,32,160,160
        private int3 output0Shape;
        private NativeArray<float> output0Transposed; // 1, 8400, 116
        private NativeList<Detection> proposalList;
        private NativeList<Detection> detectionList;
        private NativeList<MaskCenter> maskCenters;

        private Yolo11SegVisualize segmentation;

        /// <summary>Gets the detections produced by the most recently completed inference.</summary>
        public NativeArray<Detection>.ReadOnly Detections => detectionList.AsReadOnly();

        /// <summary>
        /// Gets valid mask centers produced by the most recent inference. Detections
        /// without any mask pixel above threshold are omitted.
        /// </summary>
        public NativeArray<MaskCenter>.ReadOnly MaskCenters => maskCenters.AsReadOnly();

        /// <summary>
        /// Gets the latest visualization texture, or <see langword="null"/> before
        /// post-processing has initialized the segmentation helper.
        /// </summary>
        public Texture SegmentationTexture => segmentation != null ? segmentation.Texture : null;

        // Profilers
        static readonly ProfilerMarker generateProposalsMarker = new($"{typeof(Yolo11Seg).Name}.GenerateProposals");
        static readonly ProfilerMarker segmentationMarker = new($"{typeof(Yolo11Seg).Name}.Segmentation");

        /// <summary>Creates a YOLO11 segmentation inference session.</summary>
        /// <param name="model">Serialized ONNX model bytes.</param>
        /// <param name="options">Inference, filtering, and visualization settings.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> or its label file is null.
        /// </exception>
        /// <exception cref="InvalidOperationException">Thrown when the label file contains no class names.</exception>
        public Yolo11Seg(byte[] model, Options options)
            : base(model, ValidateOptions(options))
        {
            this.options = options;

            var labels = options.labelFile.text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(label => label.Trim())
                .Where(label => label.Length > 0)
                .ToArray();

            if (labels.Length == 0)
            {
                throw new InvalidOperationException("The YOLO label file does not contain any class names.");
            }

            labelNames = Array.AsReadOnly(labels);
            classCount = labelNames.Count;

            if (!isDynamicOutputShape)
            {
                EnsurePostProcessResources(outputs);
            }
        }

        /// <summary>Releases persistent native buffers and segmentation resources.</summary>
        /// <param name="disposing">Whether the method was called by managed disposal.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (proposalList.IsCreated)
                {
                    proposalList.Dispose();
                }

                if (detectionList.IsCreated)
                {
                    detectionList.Dispose();
                }

                if (maskCenters.IsCreated)
                {
                    maskCenters.Dispose();
                }

                segmentation?.Dispose();

                if (output0Transposed.IsCreated)
                {
                    output0Transposed.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Converts a normalized computer-vision rectangle into Unity viewport space.
        /// </summary>
        /// <param name="rect">Rectangle normalized to the model input, using a top-left origin.</param>
        /// <returns>A normalized viewport rectangle using Unity's bottom-left origin.</returns>
        public Rect ConvertToViewport(in Rect rect)
        {
            Rect unityRect = rect.FlipY();
            var mtx = InputToViewportMatrix;
            Vector2 min = mtx.MultiplyPoint3x4(unityRect.min);
            Vector2 max = mtx.MultiplyPoint3x4(unityRect.max);
            return new Rect(min, max - min);
        }

        /// <summary>Resizes dynamic input resources when needed, then preprocesses the texture.</summary>
        /// <param name="texture">Source texture supplied to inference.</param>
        protected override void PreProcess(Texture texture)
        {
            if (isDynamicInputShape)
            {
                EnsureDynamicInputs(texture);
            }
            base.PreProcess(texture);
        }

        /// <summary>Asynchronously preprocesses a texture after updating dynamic input resources.</summary>
        /// <param name="texture">Source texture supplied to inference.</param>
        /// <param name="cancellationToken">Token used to cancel asynchronous preprocessing.</param>
        /// <returns>An awaitable that completes when preprocessing finishes.</returns>
        protected override Awaitable PreProcessAsync(Texture texture, CancellationToken cancellationToken)
        {
            if (isDynamicInputShape)
            {
                EnsureDynamicInputs(texture);
            }
            return base.PreProcessAsync(texture, cancellationToken);
        }

        /// <summary>
        /// Transposes model predictions, filters and suppresses proposals,
        /// calculates per-instance mask centers, and refreshes the segmentation texture.
        /// </summary>
        /// <param name="outputs">Prediction and prototype tensors returned by the ONNX session.</param>
        protected override void PostProcess(IReadOnlyList<OrtValue> outputs)
        {
            if (isDynamicOutputShape)
            {
                EnsurePostProcessResources(outputs);
            }

            var output0 = outputs[0].GetTensorDataAsSpan<float>();

            // 0: Parse predictions
            // [0: predictions] shape: 1,116,8400 (Batch_size=1, XyWh+conf_cls(80)+nm(32), Num_anchors)
            using (generateProposalsMarker.Auto())
            {
                ScheduleGenerateProposalsJob(output0, proposalList, options.confidenceThreshold)
                    .Complete();
            }

            if (proposalList.Length == 0)
            {
                detectionList.Clear();
            }
            else
            {
                // Run Non-Maximum Suppression.
                proposalList.Sort();
                DetectionUtil.NMS(proposalList, detectionList, options.nmsThreshold);
            }

            maskCenters.Clear();

            using (segmentationMarker.Auto())
            {
                // [0] 1(batch), 8400(anchor), 116(data)
                // [1: proto] shape: 1,32,160,160
                var output0Span = output0Transposed.AsReadOnlySpan();
                var output0Tensor = output0Span.AsSpan2D(output0Shape.zy);

                var output1Info = outputs[1].GetTensorTypeAndShape();
                int maskChannels = (int)output1Info.Shape[1];
                int maskHeight = (int)output1Info.Shape[2];
                int maskWidth = (int)output1Info.Shape[3];
                var output1 = outputs[1].GetTensorDataAsSpan<float>();

                for (int i = 0; i < detectionList.Length; i++)
                {
                    Detection detection = detectionList[i];
                    Vector2 center = CalculateMaskCenter(
                        detection,
                        output1,
                        maskChannels,
                        maskHeight,
                        maskWidth,
                        options.maskThreshold,
                        out bool valid);

                    detectionList[i] = new Detection(
                        detection.rect,
                        detection.label,
                        detection.probability,
                        detection.anchorId,
                        center,
                        valid);

                    if (valid)
                    {
                        maskCenters.Add(new MaskCenter(
                            center,
                            new Vector2(center.x * Width, center.y * Height),
                            i));
                    }

                    if (options.logMaskCenters)
                    {
                        Debug.Log(
                            $"MASK CENTER | " +
                            $"index={i} " +
                            $"class={labelNames[detection.label]} " +
                            $"center={center} " +
                            $"valid={valid}");
                    }
                }

                segmentation.Process(output0Tensor, output1, Detections);
            }
        }

        /// <summary>
        /// Reconstructs the portion of an instance mask inside its bounding box
        /// and returns the confidence-weighted centroid of pixels above threshold.
        /// </summary>
        /// <param name="detection">Detection whose anchor supplies the mask coefficients.</param>
        /// <param name="output1">Flattened prototype tensor in channel/y/x order.</param>
        /// <param name="maskChannels">Number of prototype channels.</param>
        /// <param name="maskHeight">Prototype-mask height in pixels.</param>
        /// <param name="maskWidth">Prototype-mask width in pixels.</param>
        /// <param name="threshold">Minimum sigmoid mask probability included in the centroid.</param>
        /// <param name="valid">Receives whether at least one qualifying mask pixel was found.</param>
        /// <returns>The normalized mask centroid, or the default vector when no valid center exists.</returns>
        private Vector2 CalculateMaskCenter(
            Detection detection,
            ReadOnlySpan<float> output1,
            int maskChannels,
            int maskHeight,
            int maskWidth,
            float threshold,
            out bool valid)
        {
            valid = false;

            if (maskChannels <= 0 || maskHeight <= 0 || maskWidth <= 0)
                return default;

            if (maskChannels != MaskCoefficientCount)
            {
                Debug.LogWarning(
                    $"Unexpected mask channel count: {maskChannels}. " +
                    $"Expected {MaskCoefficientCount}."
                );

                return default;
            }

            // ------------------------------------------------------------
            // 1. Get this detection's 32 mask coefficients.
            // ------------------------------------------------------------

            var detectionData = output0Transposed
                .AsReadOnlySpan()
                .Slice(
                    detection.anchorId * output0Shape.y,
                    output0Shape.y
                );

            var maskCoefficients = detectionData[^MaskCoefficientCount..];

            // ------------------------------------------------------------
            // 2. Convert the YOLO bounding box to mask-space coordinates.
            //
            // detection.rect is normalized to YOLO input dimensions.
            // ------------------------------------------------------------

            float boxMinX = Mathf.Clamp01(detection.rect.xMin);
            float boxMaxX = Mathf.Clamp01(detection.rect.xMax);

            float boxMinY = Mathf.Clamp01(detection.rect.yMin);
            float boxMaxY = Mathf.Clamp01(detection.rect.yMax);

            int minX = Mathf.Clamp(Mathf.FloorToInt(boxMinX * maskWidth), 0, maskWidth - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(boxMaxX * maskWidth), minX + 1, maskWidth);
            int minY = Mathf.Clamp(Mathf.FloorToInt(boxMinY * maskHeight), 0, maskHeight - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(boxMaxY * maskHeight), minY + 1, maskHeight);

            // ------------------------------------------------------------
            // 3. Reconstruct the segmentation mask.
            //
            // output1 layout:
            //
            // [channel][y][x]
            //
            // There are 32 channels, each containing 160x160 values.
            // ------------------------------------------------------------

            double sumX = 0.0;
            double sumY = 0.0;
            double totalWeight = 0.0;

            int pixels = maskHeight * maskWidth;

            for (int y = minY; y < maxY; y++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    float maskValue = 0f;

                    for (int c = 0; c < MaskCoefficientCount; c++)
                    {
                        int protoIndex =
                            c * pixels +
                            y * maskWidth +
                            x;

                        maskValue +=
                            maskCoefficients[c] *
                            output1[protoIndex];
                    }

                    // Sigmoid
                    maskValue = 1f / (1f + Mathf.Exp(-maskValue));

                    if (maskValue < threshold)
                        continue;

                    // ----------------------------------------------------
                    // Weight the centroid by mask confidence.
                    //
                    // This is better than simply averaging all pixels
                    // above the threshold.
                    // ----------------------------------------------------

                    double weight = maskValue;

                    sumX += x * weight;
                    sumY += y * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= 0.0)
            {
                return default;
            }

            // ------------------------------------------------------------
            // 4. Convert mask-space center to normalized YOLO coordinates.
            //
            // +0.5 means we're using the pixel center.
            // ------------------------------------------------------------

            float centerX =
                (float)((sumX / totalWeight + 0.5) / maskWidth);

            float centerY =
                (float)((sumY / totalWeight + 0.5) / maskHeight);

            valid = true;

            return new Vector2(centerX, centerY);
        }

        /// <summary>
        /// Recreates input conversion resources when a dynamic model encounters
        /// a texture whose aspect-preserving, 32-pixel-aligned dimensions changed.
        /// </summary>
        /// <param name="texture">Texture whose dimensions determine the tensor shape.</param>
        private void EnsureDynamicInputs(Texture texture)
        {
            if (!isDynamicInputShape)
            {
                return;
            }

            int2 texSize = new(texture.width, texture.height);
            // Choose similar aspect ratio to the texture, 
            // But needs to be multiple of 32 for YOLOv11
            const int ALIGNMENT_SIZE = 32;
            int2 dim = MathUtil.ResizeToMaxSize(texSize, options.dynamicMaxSize, ALIGNMENT_SIZE);

            bool needResize = dim.x != Width || dim.y != Height;
            if (!needResize)
            {
                return;
            }

            // Resize input tensor
            textureToTensor.Dispose();
            textureToTensor = CreateTextureToTensor(dim.x, dim.y);

            foreach (var input in inputs)
            {
                input.Dispose();
            }
            var inputMetadata = session.InputMetadata;
            var metadata = inputMetadata.Values.First();
            var ortValue = OrtValue.CreateAllocatedTensorValue(
                OrtAllocator.DefaultInstance,
                metadata.ElementDataType,
                new long[] { 1, 3, dim.y, dim.x });
            inputs = new List<OrtValue>(1) { ortValue }.AsReadOnly();
            if (options.logMaskCenters)
            {
                Debug.Log($"Resized YOLO input to {dim}.");
            }
        }

        /// <summary>
        /// Validates output tensor ranks and recreates persistent post-processing
        /// buffers whenever a dynamic output shape changes.
        /// </summary>
        /// <param name="outputs">The prediction and prototype tensors returned by inference.</param>
        private void EnsurePostProcessResources(IReadOnlyList<OrtValue> outputs)
        {
            if (outputs == null || outputs.Count != 2)
            {
                throw new InvalidOperationException("YOLO11 segmentation requires exactly two output tensors.");
            }

            Assert.AreEqual(2, outputs.Count);

            // Output 0
            var info0 = outputs[0].GetTensorTypeAndShape();
            if (info0.DimensionsCount != 3)
            {
                throw new InvalidOperationException(
                    $"Unexpected YOLO prediction rank {info0.DimensionsCount}. Expected rank 3.");
            }

            Assert.AreEqual(3, info0.DimensionsCount);
            int3 shape0 = new((int)info0.Shape[0], (int)info0.Shape[1], (int)info0.Shape[2]);

            int expectedAnchorStride = 4 + classCount + MaskCoefficientCount;
            if (shape0.x != 1 || shape0.y != expectedAnchorStride)
            {
                throw new InvalidOperationException(
                    $"Unexpected YOLO prediction shape {shape0}. Expected batch 1 and " +
                    $"anchor stride {expectedAnchorStride} (4 box values + {classCount} classes + " +
                    $"{MaskCoefficientCount} mask coefficients).");
            }

            if (!shape0.Equals(this.output0Shape))
            {
                output0Shape = shape0;
                if (options.logMaskCenters)
                {
                    Debug.Log($"New YOLO prediction shape: {shape0}.");
                }

                if (output0Transposed.IsCreated)
                {
                    output0Transposed.Dispose();
                }
                output0Transposed = new NativeArray<float>((int)info0.ElementCount, Allocator.Persistent);

                if (proposalList.IsCreated)
                {
                    proposalList.Dispose();
                }
                proposalList = new NativeList<Detection>(shape0.z, Allocator.Persistent);

                if (detectionList.IsCreated)
                {
                    detectionList.Dispose();
                }
                detectionList = new NativeList<Detection>(options.maxDetectionCount, Allocator.Persistent);

                if (maskCenters.IsCreated)
                {
                    maskCenters.Dispose();
                }
                maskCenters = new NativeList<MaskCenter>(options.maxDetectionCount, Allocator.Persistent);
            }

            // Output 1
            var info1 = outputs[1].GetTensorTypeAndShape();
            if (info1.DimensionsCount != 4)
            {
                throw new InvalidOperationException(
                    $"Unexpected YOLO prototype-mask rank {info1.DimensionsCount}. Expected rank 4.");
            }

            Assert.AreEqual(4, info1.DimensionsCount);
            int3 shape1 = new((int)info1.Shape[1], (int)info1.Shape[2], (int)info1.Shape[3]);

            if (shape1.x != MaskCoefficientCount || shape1.y <= 0 || shape1.z <= 0)
            {
                throw new InvalidOperationException(
                    $"Unexpected YOLO prototype-mask shape {shape1}. Expected " +
                    $"{MaskCoefficientCount} positive-sized prototype channels.");
            }

            if (segmentation == null || !segmentation.shape.Equals(shape1))
            {
                if (options.logMaskCenters)
                {
                    Debug.Log($"New YOLO prototype-mask shape: {shape1}.");
                }
                segmentation?.Dispose();
                segmentation = new Yolo11SegVisualize(shape1, Colors, options);
            }
        }

        /// <summary>
        /// Schedules prediction transposition followed by a parallel anchor scan
        /// that emits proposals meeting the confidence threshold.
        /// </summary>
        /// <param name="tensor">Raw prediction tensor in channel-by-anchor order.</param>
        /// <param name="proposals">Reusable native list that receives candidate detections.</param>
        /// <param name="confidenceThreshold">Minimum winning class confidence.</param>
        /// <returns>A handle representing the dependent transpose and proposal jobs.</returns>
        private JobHandle ScheduleGenerateProposalsJob(ReadOnlySpan<float> tensor, NativeList<Detection> proposals, float confidenceThreshold)
        {
            proposals.Clear();

            Assert.AreEqual(1, output0Shape.x, "Support only batch size 1");

            // shape: 116,8400 (XyWh+conf_cls(80)+nm(32), Num_anchors)
            var tensor2D = tensor.AsSpan2D(output0Shape.yz);

            // shape: 8400,116 (Num_anchors, XyWh+conf_cls(80)+nm(32))
            var tensorTransposed = new Span2D<float>(output0Transposed, output0Shape.zy);
            var transposeJobHandle = tensor2D.ScheduleTransposeJob(tensorTransposed);

            // Then generate proposals
            var proposalsWriter = proposals.AsParallelWriter();
            return new GenerateProposalsJob
            {
                output0Transposed = output0Transposed,
                classCount = classCount,
                confidenceThreshold = confidenceThreshold,
                // reciprocal width and height
                sizeScale = new float2(1f / Width, 1f / Height),
                anchorStride = output0Shape.y,
                proposals = proposalsWriter,
            }.Schedule(output0Shape.z, 64, transposeJobHandle);
        }

        /// <summary>Validates required options before the base inference session is created.</summary>
        /// <param name="options">Options supplied to the constructor.</param>
        /// <returns>The same validated options instance.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the options or label file is null.</exception>
        private static Options ValidateOptions(Options options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.labelFile == null)
            {
                throw new ArgumentNullException(nameof(options.labelFile));
            }

            return options;
        }

        [BurstCompile]
        private struct GenerateProposalsJob : IJobParallelFor
        {
            // shape: 8400,116 (Num_anchors, XyWh+conf_cls(80)+nm(32))
            [ReadOnly]
            public NativeArray<float> output0Transposed;
            public int classCount;
            public float confidenceThreshold;
            public float2 sizeScale;
            public int anchorStride;

            [WriteOnly]
            public NativeList<Detection>.ParallelWriter proposals;

            /// <summary>
            /// Selects the highest-confidence class for one anchor, rejects weak
            /// anchors, and writes the normalized bounding box to the parallel list.
            /// </summary>
            /// <param name="anchorId">Zero-based prediction-anchor index.</param>
            public void Execute(int anchorId)
            {
                var anchor = output0Transposed
                    .AsReadOnlySpan()
                    .Slice(anchorId * anchorStride, anchorStride);

                // Find max confidence
                var confidences = anchor.Slice(4, classCount);
                int classId = confidences.ArgMax();
                float maxConfidence = confidences[classId];

                // Filter out low confidence anchors
                if (maxConfidence < confidenceThreshold)
                {
                    return;
                }

                // Normalize Rect
                float cx = anchor[0] * sizeScale.x;
                float cy = anchor[1] * sizeScale.y;
                float w = anchor[2] * sizeScale.x;
                float h = anchor[3] * sizeScale.y;
                float x = cx - w * 0.5f;
                float y = cy - h * 0.5f;

                proposals.AddNoResize(new Detection(
                    new Rect(x, y, w, h),
                    classId,
                    maxConfidence,
                    anchorId)
                );
            }
        }
    }
}

