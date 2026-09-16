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
    /// 使用 MediaPipe Unity 插件在后台线程识别双手，为每只手生成 21 个球形关节。
    /// 关节位于与 RGB 预览对齐的局部坐标系中；Z 为相对手腕的估计值，并非标定后的相机深度。
    /// 主线程负责图像采集、结果应用和骨架显示，无需启动 Python 子进程。
    /// </summary>
    public sealed class ASJHandJointTracker : MonoBehaviour
    {
        // 输入来源：ASJ 相机或普通摄像头，进入运行模式前配置。
        public enum InputSource { ASJ, Webcam }
        [Tooltip("Select before entering Play mode. ASJ remains the default for existing scenes.")]
        public InputSource inputSource = InputSource.ASJ;
        // 两种输入源按 inputSource 选择；只需配置实际使用的一种。
        public ASJCamera cameraSource;
        public WebcamRgbSource webcamSource;
        // RGB 预览和骨架球体使用的着色器。
        public RawImage rgbView;
        //public Shader sphereShader;
        public Material handMaterial;
        
        // 向后台提交图像的最大频率；实际识别速度还取决于推理耗时。
        [Range(1, 30)] public int inferenceFps = 15;
        // 关节球体的直径，单位为骨架局部坐标单位。
        [Range(.005f, .58f)] public float sphereDiameter = .025f;
        // 指数平滑速度；为 0 时直接应用最新关节位置。
        [Range(0, 30)] public float smoothing = 18;
        // 超过此秒数未收到结果时隐藏骨架。
        public float lostTimeout = .4f;
        // 是否显示连接关节的骨骼线段。
        public bool drawBones = true;
        [Header("Skeleton view (configure before Play)")]
        [Tooltip("Rotate the whole skeleton around the view origin: pose and movement reverse X/Z, while Y stays upright.")]
        // 绕视图原点旋转骨架，使 X/Z 方向反转，Y 方向保持不变。
        public bool backOfHandView;
        [Tooltip("Estimate relative forward/back movement from image/world palm scale. RGB estimate only, not measured depth. The reference survives tracking gaps until disabled or ResetForwardReference is called.")]
        // 根据图像掌部与世界掌部的比例估算整只手的前后位移，并非实测深度。
        public bool estimateForwardMotion;
        [Tooltip("Reverse only whole-hand forward/back movement, independently of the back-of-hand pose and left/right motion.")]
        // 只反转整只手的前后位移，不影响手背视角和左右移动。
        public bool invertForwardMotion;
        // 前后移动的灵敏度和基础位移上限。
        [Min(0)] public float forwardSensitivity = .6f;
        [Min(0)] public float forwardLimit = .8f;
        [Tooltip("Scales estimated forward/back displacement and its travel limit together.")]
        // 同时放大前后位移及其上限。
        [Min(0)] public float forwardMotionMultiplier = 10f;
        // 当前状态、手部数量、最近一次推理耗时（毫秒）以及骨架根节点。
        public string Status { get; private set; } = "Starting";
        public int TrackedHandCount { get; private set; }
        public float InferenceMilliseconds { get; private set; }
        public Transform JointRoot { get; private set; }

        // 对外发布的数据：points 为图像归一化关键点，world 优先使用世界关键点，缺失时回退到 points。
        [Serializable] public class Point { public float x, y, z; }
        [Serializable] public class Hand { public string label; public float score; public Point[] points, world; }
        [Serializable] public class Result { public long timestamp; public float milliseconds; public Hand[] hands; }
        public Result LatestResult { get; private set; }
        // 在主线程应用结果后触发；订阅者可安全更新 Unity 场景对象。
        public event Action<Result> OnHandsUpdated;

        // MediaPipe 的 21 个关节名称，以及骨骼线段的起止关节索引对。
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

        // 第一维为左/右手槽位，第二维为关节或骨骼索引；复用显示对象以避免重复创建。
        readonly Transform[,]    joints    = new Transform[2, 21];
        readonly Vector3[,]      targets   = new Vector3[2, 21];
        readonly GameObject[]    handRoots = new GameObject[2];
        readonly LineRenderer[,] lines     = new LineRenderer[2, 21];
        readonly Material[]      materials = new Material[2];
        readonly Vector3[] sourcePose = new Vector3[21];
        // 每只手独立保存初始掌部比例和前后偏移；追踪短暂中断时保留参考值。
        readonly float[] referencePalmSize = new float[2];
        readonly float[] forwardOffsets = new float[2];
        readonly bool[] poseInitialized = new bool[2];

        // 后台线程状态；gate 保护输入/输出缓冲区，stopping 跨线程通知退出。
        readonly object gate = new object();
        volatile bool stopping;
        Thread worker;
        HandLandmarker landmarker;

        // 单槽输入缓冲区：主线程写入，后台读取。frameReady 清除前禁止生产者覆盖像素。
        byte[]   pendingPixels;
        int      pendingWidth, pendingHeight;
        long     pendingTimestamp;
        bool     frameReady;
        readonly AutoResetEvent frameAvailable = new AutoResetEvent(false);

        // 单槽结果缓冲区：后台写入，主线程取走；只保留最新结果以避免积压。
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

        // 复用托管像素数组，避免每次提交都产生 GC 分配；时间戳必须严格递增。
        byte[] frameBuffer;
        long lastSentTimestamp;
        long lastWebcamFrame = -1;

        // 验证输入与模型文件，创建显示对象并启动后台识别线程。
        void Start()
        {
            if (inputSource == InputSource.ASJ && !cameraSource) cameraSource = FindObjectOfType<ASJCamera>();
            if (inputSource == InputSource.Webcam && !webcamSource) webcamSource = FindObjectOfType<WebcamRgbSource>();
            bool sourceAssigned = inputSource == InputSource.ASJ ? cameraSource != null : webcamSource != null;
            if (!sourceAssigned || !rgbView || !handMaterial)
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

        // VIDEO 模式使用递增时间戳进行同步推理，最多识别两只手。
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

        // 后台仅处理像素和识别数据；场景对象统一由主线程更新。
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
                    // 推理耗时可能超过 4 个 Unity 帧，不能使用有四帧寿命限制的 TempJob。
                    // Persistent 允许跨帧存活；嵌套 using 确保先释放借用像素的 Image，再释放像素副本。
                    // 即使 Image 构造或推理抛出异常，已分配的像素副本也会被释放。
                    using (var nativePixels = new NativeArray<byte>(pixels, Allocator.Persistent))
                    using (var image = new Image(ImageFormat.Types.Format.Srgb, w, h, w * 3, nativePixels))
                    {
                        // 已复制到独立原生内存，此时主线程才可复用托管像素缓冲区提交下一帧。
                        lock (gate) frameReady = false;
                        var sw = Stopwatch.StartNew();
                        detected = landmarker.TryDetectForVideo(image, timestamp, null, ref result);
                        ms = (float)sw.Elapsed.TotalMilliseconds;
                    }

                    lock (gate)
                    {
                        // 克隆结果，避免下一次推理修改主线程尚未消费的数据。
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

        // 主线程取走结果、按频率提交新图像，再更新关节与骨骼显示。
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
                // 转移结果所有权，防止后台修改主线程正在使用的列表。
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

            // 按设定频率提交新帧；摄像头模式还需检查帧版本，避免重复推理同一帧。
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
                    // 这里借用纹理自身的数据视图，不负责释放；跨线程传递前先复制到托管数组。
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

            // 在主线程消费后台结果并刷新最后接收时间。
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

            // 使用不受 timeScale 影响的指数平滑，使不同帧率下的位置过渡更一致。
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

        // 将识别结果映射到预览局部坐标，更新可见性，并向外部订阅者发布结果。
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

                    // 根据预览 UV 修正裁剪/翻转，再映射到匹配图像宽高比的骨架坐标。
                    Rect uv = rgbView.uvRect;
                    for (int j = 0; j < 21; j++)
                    {
                        var p = landmarks.landmarks[j];
                        float u = (p.x - uv.x) / uv.width;
                        float v = (p.y - uv.y) / uv.height;
                        sourcePose[j] = new Vector3((u - .5f) * 2, (v - .5f) * 2 / aspect, p.z * 2);
                    }
                    // 使用世界关键点补偿手掌旋转对尺度的影响；数据缺失时保留上次前后偏移，
                    // 避免切换到不兼容的比例造成深度跳变。
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
                        // 首次识别直接定位，避免关节从默认原点逐渐滑入。
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

            // 构建面向事件订阅者的数据对象，保留原始关键点及世界关键点。
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

        /// <summary>按左右手和 MediaPipe 关节索引获取球体节点；索引越界返回 null。</summary>
        public Transform GetJoint(bool leftHand, int index)
        {
            return index >= 0 && index < 21 ? joints[leftHand ? 0 : 1, index] : null;
        }

        // 用独立正交相机将骨架渲染为透明纹理，再作为 RGB 预览的子级叠加显示。
        void CreateVisuals()
        {
            JointRoot          = new GameObject("ASJ_HandJointSpheres").transform;
            // 将骨架显示区域放到远处，并通过第 31 层限定叠加相机的渲染对象。
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
                materials[h] = handMaterial;//new Material(sphereShader);
                //materials[h].color = h == 0 ? new Color(.15f, 1f, .55f) : new Color(1f, .5f, .1f);
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
                    // 关节球体只用于显示，移除基础球体自带的碰撞体。
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

        // 停用时隐藏显示并清除姿态参考；后台线程和模型在销毁时统一释放。
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

        // 恢复已经创建的显示对象；首次启用时对象尚未创建，由 Start 负责初始化。
        void OnEnable()
        {
            if (JointRoot) JointRoot.gameObject.SetActive(true);
            if (overlayObject) overlayObject.SetActive(true);
        }

        /// <summary>清空双手的前后位移基准，下次有效掌部比例将成为新的参考。</summary>
        [ContextMenu("Reset Forward Reference")]
        public void ResetForwardReference()
        {
            referencePalmSize[0] = referencePalmSize[1] = 0;
            forwardOffsets[0] = forwardOffsets[1] = 0;
        }

        // 先通知并等待后台退出，再销毁模型、同步事件和运行时创建的渲染资源。
        void OnDestroy()
        {
            stopping = true;
            frameAvailable.Set();
            // 等待正在执行的推理完成，不能提前释放其仍在使用的原生资源。
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
