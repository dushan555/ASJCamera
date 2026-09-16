using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.Core;
using Debug = UnityEngine.Debug;
using Image = Mediapipe.Image;
using Rect = UnityEngine.Rect;
using Color = UnityEngine.Color;

namespace ASJ
{
    /// <summary>
    /// 21 real Sphere objects per hand, positioned in an RGB-aligned local space.
    /// Z is MediaPipe's wrist-relative estimate, NOT calibrated camera depth.
    /// Uses MediaPipe Unity Plugin directly — no Python subprocess required.
    /// </summary>
    public sealed class ASJHandJointTracker : MonoBehaviour
    {
        public enum InputSource { ASJ, Webcam }
        [Tooltip("Select before entering Play mode. ASJ remains the default for existing scenes.")]
        public InputSource inputSource = InputSource.ASJ;
        public ASJCamera cameraSource;
        public WebcamRgbSource webcamSource;
        public RawImage rgbView;
        public Shader sphereShader;
        [Range(1, 30)] public int inferenceFps = 15;
        [Range(.005f, .08f)] public float sphereDiameter = .025f;
        [Range(0, 30)] public float smoothing = 18;
        public float lostTimeout = .4f;
        public bool drawBones = true;
        [Header("Skeleton view (configure before Play)")]
        [Tooltip("Rotate the whole skeleton around the view origin: pose and movement reverse X/Z, while Y stays upright.")]
        public bool backOfHandView;
        [Tooltip("Estimate relative forward/back movement from image/world palm scale. RGB estimate only, not measured depth. The reference survives tracking gaps until disabled or ResetForwardReference is called.")]
        public bool estimateForwardMotion;
        [Tooltip("Reverse only whole-hand forward/back movement, independently of the back-of-hand pose and left/right motion.")]
        public bool invertForwardMotion;
        [Min(0)] public float forwardSensitivity = .6f;
        [Min(0)] public float forwardLimit = .8f;
        [Tooltip("Scales estimated forward/back displacement and its travel limit together.")]
        [Min(0)] public float forwardMotionMultiplier = 10f;
        public string Status { get; private set; } = "Starting";
        public int TrackedHandCount { get; private set; }
        public float InferenceMilliseconds { get; private set; }
        public Transform JointRoot { get; private set; }

        [Serializable] public class Point { public float x, y, z; }
        [Serializable] public class Hand { public string label; public float score; public Point[] points, world; }
        [Serializable] public class Result { public long timestamp; public float milliseconds; public Hand[] hands; }
        public Result LatestResult { get; private set; }
        public event Action<Result> OnHandsUpdated;

        static readonly string[] Names = {
            "Wrist", "ThumbCMC", "ThumbMCP", "ThumbIP", "ThumbTip",
            "IndexMCP", "IndexPIP", "IndexDIP", "IndexTip",
            "MiddleMCP", "MiddlePIP", "MiddleDIP", "MiddleTip",
            "RingMCP", "RingPIP", "RingDIP", "RingTip",
            "PinkyMCP", "PinkyPIP", "PinkyDIP", "PinkyTip"
        };
        static readonly int[] Bones = {
            0,1, 1,2, 2,3, 3,4,
            0,5, 5,6, 6,7, 7,8,
            5,9, 9,10, 10,11, 11,12,
            9,13, 13,14, 14,15, 15,16,
            13,17, 0,17, 17,18, 18,19, 19,20
        };

        readonly Transform[,]    joints    = new Transform[2, 21];
        readonly Vector3[,]      targets   = new Vector3[2, 21];
        readonly GameObject[]    handRoots = new GameObject[2];
        readonly LineRenderer[,] lines     = new LineRenderer[2, 21];
        readonly Material[]      materials = new Material[2];
        readonly Vector3[] sourcePose = new Vector3[21];
        readonly float[] referencePalmSize = new float[2];
        readonly float[] forwardOffsets = new float[2];
        readonly bool[] poseInitialized = new bool[2];

        // Worker thread state.
        readonly object gate = new object();
        volatile bool stopping;
        Thread worker;
        HandLandmarker landmarker;

        // Frame buffer: main thread writes, worker reads.
        byte[]   pendingPixels;
        int      pendingWidth, pendingHeight;
        long     pendingTimestamp;
        bool     frameReady;
        readonly AutoResetEvent frameAvailable = new AutoResetEvent(false);

        // Result buffer: worker writes, main thread reads.
        HandLandmarkerResult latestMpResult;
        float                latestMs;
        long                 latestTimestamp;
        bool                 resultReady;
        string               workerError;

        float nextSend, lastResultTime;
        Camera        overlayCamera;
        RenderTexture overlayTexture;
        GameObject    overlayObject;
        float         aspect = 4f / 3f;
        bool          reportedTracking;

        // Reused per-frame to avoid GC alloc on every send.
        byte[] frameBuffer;
        long lastSentTimestamp;
        long lastWebcamFrame = -1;

        void Start()
        {
            if (inputSource == InputSource.ASJ && !cameraSource) cameraSource = FindObjectOfType<ASJCamera>();
            if (inputSource == InputSource.Webcam && !webcamSource) webcamSource = FindObjectOfType<WebcamRgbSource>();
            bool sourceAssigned = inputSource == InputSource.ASJ ? cameraSource != null : webcamSource != null;
            if (!sourceAssigned || !rgbView || !sphereShader)
            {
                Status = "Assign the selected camera source, rgbView and sphereShader";
                Debug.LogError("[ASJ Hand] " + Status);
                enabled = false;
                return;
            }

            string modelPath = Path.Combine(
                Application.streamingAssetsPath, "ASJ/HandTracking/hand_landmarker.task");
            if (!File.Exists(modelPath))
            {
                Status = "hand_landmarker.task not found in StreamingAssets/ASJ/HandTracking/";
                Debug.LogError("[ASJ Hand] " + Status);
                enabled = false;
                return;
            }

            CreateVisuals();
            try { InitLandmarker(modelPath); }
            catch (Exception ex)
            {
                Status = "Hand tracker initialization failed: " + ex.Message;
                Debug.LogError("[ASJ Hand] " + Status);
                enabled = false;
                return;
            }

            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "ASJ Hand Inference" };
            worker.Start();
        }

        void InitLandmarker(string modelPath)
        {
            var baseOptions = new BaseOptions(modelAssetPath: modelPath);
            var options = new HandLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numHands: 2,
                minHandDetectionConfidence: 0.5f,
                minHandPresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f);
            landmarker = HandLandmarker.CreateFromOptions(options);
        }

        void WorkerLoop()
        {
            var result = HandLandmarkerResult.Alloc(2);
            try
            {
                while (!stopping)
                {
                    if (!frameAvailable.WaitOne(100)) continue;
                    if (stopping) break;

                    byte[] pixels;
                    int    w, h;
                    long   timestamp;
                    lock (gate)
                    {
                        if (!frameReady) continue;
                        pixels    = pendingPixels;
                        w         = pendingWidth;
                        h         = pendingHeight;
                        timestamp = pendingTimestamp;
                    }
                    if (pixels == null) continue;

                    float ms;
                    bool detected;
                    using (var nativePixels = new NativeArray<byte>(pixels, Allocator.TempJob))
                    using (var image = new Image(ImageFormat.Types.Format.Srgb, w, h, w * 3, nativePixels))
                    {
                        // NativeArray now owns a copy; the producer may reuse its buffer.
                        lock (gate) frameReady = false;
                        var sw = Stopwatch.StartNew();
                        detected = landmarker.TryDetectForVideo(image, timestamp, null, ref result);
                        ms = (float)sw.Elapsed.TotalMilliseconds;
                    }

                    lock (gate)
                    {
                        if (detected) result.CloneTo(ref latestMpResult);
                        else          latestMpResult = default;
                        latestMs    = ms;
                        latestTimestamp = timestamp;
                        resultReady = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!stopping) lock (gate) workerError = ex.Message;
            }
        }

        void LateUpdate()
        {
            string error;
            bool   hasResult;
            HandLandmarkerResult mpResult;
            float  inferMs;
            long timestamp;
            lock (gate)
            {
                error      = workerError; workerError = null;
                hasResult  = resultReady; resultReady = false;
                mpResult   = latestMpResult;
                // Transfer ownership so the worker cannot mutate lists being consumed.
                latestMpResult = default;
                inferMs    = latestMs;
                timestamp = latestTimestamp;
            }
            if (error != null)
            {
                Status = error;
                Debug.LogError("[ASJ Hand] " + error);
                enabled = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            bool useWebcam = inputSource == InputSource.Webcam;
            var texture = useWebcam ? (webcamSource ? webcamSource.RgbTexture : null)
                : (cameraSource ? cameraSource.RgbTexture : null);
            bool streaming = useWebcam ? webcamSource && webcamSource.IsStreaming
                : cameraSource && cameraSource.IsStreaming;
            if (useWebcam)
            {
                rgbView.texture = texture;
                rgbView.uvRect = new Rect(0, 1, 1, -1);
                if (!streaming) Status = webcamSource ? webcamSource.Status : "Assign webcamSource";
            }

            // Send a new frame to the worker thread if the rate allows.
            if (streaming && texture != null && now >= nextSend
                && (!useWebcam || webcamSource.FrameVersion != lastWebcamFrame))
            {
                aspect = (float)texture.width / texture.height;
                overlayCamera.orthographicSize = 1f / aspect;
                overlayCamera.aspect = aspect;

                bool workerBusy;
                lock (gate) { workerBusy = frameReady; }
                if (!workerBusy)
                {
                    var nativeData = texture.GetRawTextureData<byte>();
                    int byteCount = nativeData.Length;
                    if (frameBuffer == null || frameBuffer.Length != byteCount)
                        frameBuffer = new byte[byteCount];
                    nativeData.CopyTo(frameBuffer);
                    if (useWebcam) lastWebcamFrame = webcamSource.FrameVersion;
                    lastSentTimestamp = Math.Max(lastSentTimestamp + 1, (long)(now * 1000));
                    lock (gate)
                    {
                        pendingPixels    = frameBuffer;
                        pendingWidth     = texture.width;
                        pendingHeight    = texture.height;
                        pendingTimestamp = lastSentTimestamp;
                        frameReady       = true;
                    }
                    frameAvailable.Set();
                }
                nextSend = now + 1f / Mathf.Max(1, inferenceFps);
            }

            // Consume result from worker thread.
            if (hasResult)
            {
                InferenceMilliseconds = inferMs;
                ApplyMpResult(mpResult, timestamp);
                lastResultTime = now;
            }

            if (now - lastResultTime > lostTimeout)
            {
                if (TrackedHandCount > 0)
                    ApplyMpResult(default, (long)(now * 1000));
                TrackedHandCount = 0;
                for (int h = 0; h < 2; h++) handRoots[h].SetActive(false);
            }

            // Smooth joint positions.
            float blend = smoothing <= 0 ? 1f : 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            for (int h = 0; h < 2; h++)
            {
                if (!handRoots[h].activeSelf) continue;
                for (int j = 0; j < 21; j++)
                {
                    joints[h, j].localPosition = Vector3.Lerp(joints[h, j].localPosition, targets[h, j], blend);
                    joints[h, j].localScale    = Vector3.one * sphereDiameter;
                }
                for (int b = 0; b < Bones.Length / 2; b++)
                {
                    lines[h, b].enabled = drawBones;
                    lines[h, b].SetPosition(0, joints[h, Bones[b * 2]].localPosition);
                    lines[h, b].SetPosition(1, joints[h, Bones[b * 2 + 1]].localPosition);
                }
            }
        }

        void ApplyMpResult(HandLandmarkerResult mpResult, long timestamp)
        {
            bool[] used = new bool[2];
            TrackedHandCount = 0;

            if (mpResult.handLandmarks != null)
            {
                for (int i = 0; i < mpResult.handLandmarks.Count; i++)
                {
                    var landmarks  = mpResult.handLandmarks[i];
                    var handedness = mpResult.handedness[i];
                    if (landmarks.landmarks == null || landmarks.landmarks.Count != 21) continue;

                    string label = handedness.categories[0].categoryName;
                    int    slot  = label == "Left" ? 0 : 1;
                    if (used[slot]) slot = 1 - slot;
                    if (used[slot]) continue;

                    used[slot] = true;
                    TrackedHandCount++;

                    Rect uv = rgbView.uvRect;
                    for (int j = 0; j < 21; j++)
                    {
                        var p = landmarks.landmarks[j];
                        float u = (p.x - uv.x) / uv.width;
                        float v = (p.y - uv.y) / uv.height;
                        sourcePose[j] = new Vector3((u - .5f) * 2, (v - .5f) * 2 / aspect, p.z * 2);
                    }
                    // World landmarks compensate for palm rotation. If unavailable,
                    // hold the last depth instead of switching to an incompatible scale.
                    if (estimateForwardMotion && mpResult.handWorldLandmarks != null
                        && i < mpResult.handWorldLandmarks.Count)
                    {
                        var world = mpResult.handWorldLandmarks[i].landmarks;
                        if (world != null && world.Count == 21)
                        {
                            var w = world[0]; var a = world[5]; var b = world[17];
                            float palmScale;
                            if (HandSkeletonViewMath.TryPalmScale(sourcePose[0], sourcePose[5], sourcePose[17],
                                new Vector3(w.x,w.y,w.z), new Vector3(a.x,a.y,a.z), new Vector3(b.x,b.y,b.z), out palmScale))
                            {
                                if (referencePalmSize[slot] <= 0) referencePalmSize[slot] = palmScale;
                                forwardOffsets[slot] = HandSkeletonViewMath.RelativeForward(
                                    referencePalmSize[slot], palmScale, forwardSensitivity, forwardLimit);
                            }
                        }
                    }
                    float forward = estimateForwardMotion ? forwardOffsets[slot] * Mathf.Max(0, forwardMotionMultiplier) : 0;
                    for (int j = 0; j < 21; j++)
                    {
                        targets[slot, j] = HandSkeletonViewMath.MapPose(sourcePose[j], forward, backOfHandView, invertForwardMotion);
                        if (!poseInitialized[slot]) joints[slot, j].localPosition = targets[slot, j];
                    }
                    poseInitialized[slot] = true;
                }
            }

            for (int h = 0; h < 2; h++)
            {
                handRoots[h].SetActive(used[h]);
            }

            Status = TrackedHandCount > 0
                ? $"Tracking {TrackedHandCount} hand(s), {TrackedHandCount * 21} joints, {InferenceMilliseconds:F0} ms"
                : "Ready - show a hand to the RGB camera";

            if (TrackedHandCount > 0 && !reportedTracking)
            {
                reportedTracking = true;
                Debug.Log("[ASJ Hand] Detected hand: 21 Sphere joints are tracking live landmarks.");
            }

            // Build Result for subscribers that use the event API.
            var result = new Result
            {
                timestamp    = timestamp,
                milliseconds = InferenceMilliseconds,
                hands        = new Hand[TrackedHandCount]
            };
            int ri = 0;
            for (int i = 0; mpResult.handLandmarks != null && i < mpResult.handLandmarks.Count && ri < TrackedHandCount; i++)
            {
                var landmarks  = mpResult.handLandmarks[i];
                var handedness = mpResult.handedness[i];
                if (landmarks.landmarks == null || landmarks.landmarks.Count != 21) continue;

                string label = handedness.categories[0].categoryName;
                int    slot  = label == "Left" ? 0 : 1;
                if (!handRoots[slot].activeSelf) continue;

                var hand = new Hand
                {
                    label  = label,
                    score  = handedness.categories[0].score,
                    points = new Point[21],
                    world  = new Point[21]
                };
                for (int j = 0; j < 21; j++)
                {
                    var p = landmarks.landmarks[j];
                    hand.points[j] = new Point { x = p.x, y = p.y, z = p.z };
                    hand.world[j]  = hand.points[j];
                }
                if (mpResult.handWorldLandmarks != null && i < mpResult.handWorldLandmarks.Count)
                {
                    var world = mpResult.handWorldLandmarks[i];
                    if (world.landmarks != null && world.landmarks.Count == 21)
                        for (int j = 0; j < 21; j++)
                        {
                            var wp = world.landmarks[j];
                            hand.world[j] = new Point { x = wp.x, y = wp.y, z = wp.z };
                        }
                }
                result.hands[ri++] = hand;
            }
            LatestResult = result;
            OnHandsUpdated?.Invoke(result);
        }

        public Transform GetJoint(bool leftHand, int index)
        {
            return index >= 0 && index < 21 ? joints[leftHand ? 0 : 1, index] : null;
        }

        void CreateVisuals()
        {
            JointRoot          = new GameObject("ASJ_HandJointSpheres").transform;
            JointRoot.position = new Vector3(10000, 10000, 10000);

            var camObject = new GameObject("Hand Joint Overlay Camera");
            camObject.transform.SetParent(JointRoot, false);
            camObject.transform.localPosition = new Vector3(0, 0, -3);
            overlayCamera                  = camObject.AddComponent<Camera>();
            overlayCamera.orthographic     = true;
            overlayCamera.orthographicSize = .75f;
            overlayCamera.clearFlags       = CameraClearFlags.SolidColor;
            overlayCamera.backgroundColor  = Color.clear;
            overlayCamera.nearClipPlane    = .1f;
            overlayCamera.farClipPlane     = 6f;
            overlayCamera.cullingMask      = 1 << 31;
            overlayCamera.allowHDR         = false;
            overlayCamera.allowMSAA        = false;
            overlayTexture = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
            overlayTexture.Create();
            overlayCamera.targetTexture = overlayTexture;

            overlayObject = new GameObject(
                "Hand Joint Sphere Overlay",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = overlayObject.GetComponent<RectTransform>();
            rect.SetParent(rgbView.transform, false);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var view = overlayObject.GetComponent<RawImage>();
            view.texture = overlayTexture; view.raycastTarget = false;

            for (int h = 0; h < 2; h++)
            {
                materials[h]       = new Material(sphereShader);
                materials[h].color = h == 0 ? new Color(.15f, 1f, .55f) : new Color(1f, .5f, .1f);
                handRoots[h]       = new GameObject(h == 0 ? "LeftHand_21Joints" : "RightHand_21Joints");
                handRoots[h].transform.SetParent(JointRoot, false);

                for (int j = 0; j < 21; j++)
                {
                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name  = $"{j:D2}_{Names[j]}";
                    sphere.layer = 31;
                    sphere.transform.SetParent(handRoots[h].transform, false);
                    sphere.transform.localScale = Vector3.one * sphereDiameter;
                    var col = sphere.GetComponent<Collider>();
                    col.enabled = false; Destroy(col);
                    sphere.GetComponent<Renderer>().sharedMaterial = materials[h];
                    joints[h, j] = sphere.transform;
                }
                for (int b = 0; b < Bones.Length / 2; b++)
                {
                    var bone = new GameObject("Bone_" + b);
                    bone.layer = 31;
                    bone.transform.SetParent(handRoots[h].transform, false);
                    var line = bone.AddComponent<LineRenderer>();
                    line.useWorldSpace = false; line.positionCount = 2;
                    line.startWidth    = line.endWidth = .006f;
                    line.sharedMaterial = materials[h];
                    lines[h, b] = line;
                }
                handRoots[h].SetActive(false);
            }
        }

        void OnGUI() { GUI.Label(new Rect(12, 12, 900, 26), "ASJ Hand Tracking | " + Status); }

        void OnDisable()
        {
            ResetForwardReference();
            poseInitialized[0] = poseInitialized[1] = false;
            foreach (var handRoot in handRoots) if (handRoot) handRoot.SetActive(false);
            if (JointRoot) JointRoot.gameObject.SetActive(false);
            if (overlayObject) overlayObject.SetActive(false);
            TrackedHandCount = 0;
            LatestResult = null;
        }

        void OnEnable()
        {
            if (JointRoot) JointRoot.gameObject.SetActive(true);
            if (overlayObject) overlayObject.SetActive(true);
        }

        [ContextMenu("Reset Forward Reference")]
        public void ResetForwardReference()
        {
            referencePalmSize[0] = referencePalmSize[1] = 0;
            forwardOffsets[0] = forwardOffsets[1] = 0;
        }

        void OnDestroy()
        {
            stopping = true;
            frameAvailable.Set();
            // Never release native resources while an inference is still using them.
            worker?.Join();
            frameAvailable.Dispose();
            ((IDisposable)landmarker)?.Dispose();
            landmarker = null;

            if (overlayObject)  Destroy(overlayObject);
            if (JointRoot)      Destroy(JointRoot.gameObject);
            if (overlayTexture) { overlayTexture.Release(); Destroy(overlayTexture); }
            foreach (var mat in materials) if (mat) Destroy(mat);
        }
    }
}
