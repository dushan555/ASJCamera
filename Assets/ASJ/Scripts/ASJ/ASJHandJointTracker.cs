using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Process = System.Diagnostics.Process;

namespace ASJ
{
    /// <summary>21 real Sphere objects per hand, positioned in an RGB-aligned local space.
    /// Z is MediaPipe's wrist-relative estimate, NOT calibrated camera depth.
    /// Frame data is exchanged via Windows named MemoryMappedFile instead of the stdin pipe,
    /// eliminating the per-frame kernel copy and JSON serialization overhead.</summary>
    public sealed class ASJHandJointTracker : MonoBehaviour
    {
        public ASJCamera cameraSource;
        public RawImage rgbView;
        public Shader sphereShader;
        [Tooltip("Blank uses the project-local Tools/ASJHandTracking/.venv runtime.")]
        public string pythonExecutable;
        [Range(1,30)] public int inferenceFps = 15;
        [Range(.005f,.08f)] public float sphereDiameter = .025f;
        [Range(0,30)] public float smoothing = 18;
        public float lostTimeout = .4f;
        public bool drawBones = true;
        public string Status { get; private set; } = "Starting";
        public int TrackedHandCount { get; private set; }
        public float InferenceMilliseconds { get; private set; }
        public Transform JointRoot { get; private set; }

        [Serializable] public class Point { public float x,y,z; }
        [Serializable] public class Hand { public string label; public float score; public Point[] points,world; }
        [Serializable] public class Result { public long timestamp; public float milliseconds; public Hand[] hands; }
        public Result LatestResult { get; private set; }
        public event Action<Result> OnHandsUpdated;

        // Shared memory layout — must match hand_worker.py constants.
        const string FrameShmName  = "ASJHandFrame";
        const string ResultShmName = "ASJHandResult";
        const int    MaxHands      = 2;
        const int    FloatsPerHand = 65;   // 1 label + 1 score + 21*3 coords
        const long   FrameMmfSize  = 4 * 1024 * 1024;  // 4 MB — covers up to ~1080p RGB24
        const long   ResultMmfSize = MaxHands * FloatsPerHand * sizeof(float) + 64;

        MemoryMappedFile           _frameMmf,      _resultMmf;
        MemoryMappedViewAccessor   _frameAccessor, _resultAccessor;
        byte[] _frameBuffer;  // reused each frame; reallocated only on resolution change

        static readonly string[] Names = { "Wrist", "ThumbCMC", "ThumbMCP", "ThumbIP", "ThumbTip",
            "IndexMCP", "IndexPIP", "IndexDIP", "IndexTip", "MiddleMCP", "MiddlePIP", "MiddleDIP", "MiddleTip",
            "RingMCP", "RingPIP", "RingDIP", "RingTip", "PinkyMCP", "PinkyPIP", "PinkyDIP", "PinkyTip" };
        static readonly int[] Bones = {0,1,1,2,2,3,3,4,0,5,5,6,6,7,7,8,5,9,9,10,10,11,11,12,9,13,13,14,14,15,15,16,13,17,0,17,17,18,18,19,19,20};
        readonly Transform[,]    joints    = new Transform[2,21];
        readonly Vector3[,]      targets   = new Vector3[2,21];
        readonly GameObject[]    handRoots = new GameObject[2];
        readonly LineRenderer[,] lines     = new LineRenderer[2,21];
        readonly Material[]      materials = new Material[2];
        readonly object gate = new object();
        volatile bool stopping;
        Thread worker;
        Process process;
        // Pending result from worker thread: hand count + raw float data.
        volatile int   pendingHandCount = -1;
        float[]        pendingFloats;   // MaxHands * FloatsPerHand, written by worker thread
        string         workerError;
        volatile bool  ready;
        float nextSend, lastResultTime;
        Camera overlayCamera;
        RenderTexture overlayTexture;
        GameObject overlayObject;
        float aspect = 4f / 3f;
        bool reportedTracking;

        void Start()
        {
            if (!cameraSource) cameraSource = FindObjectOfType<ASJCamera>();
            if (!cameraSource || !rgbView || !sphereShader)
            {
                Status = "Assign cameraSource, rgbView and sphereShader";
                Debug.LogError("[ASJ Hand] " + Status); enabled = false; return;
            }
            CreateVisuals();
            CreateSharedMemory();

            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string python  = string.IsNullOrWhiteSpace(pythonExecutable)
                ? Path.Combine(project, "Tools/ASJHandTracking/.venv/Scripts/python.exe")
                : pythonExecutable;
            string script = Path.Combine(Application.streamingAssetsPath, "ASJ/HandTracking/hand_worker.py");
            if (!File.Exists(python) || !File.Exists(script))
            {
                Status = "Missing Python runtime or hand_worker.py; see Tools/ASJHandTracking/README_CN.md";
                Debug.LogError("[ASJ Hand] " + Status); enabled = false; return;
            }
            worker = new Thread(() => RunWorker(python, script)) { IsBackground = true, Name = "ASJ Hand Inference" };
            worker.Start();
        }

        void CreateSharedMemory()
        {
            _frameMmf      = MemoryMappedFile.CreateOrOpen(FrameShmName,  FrameMmfSize);
            _resultMmf     = MemoryMappedFile.CreateOrOpen(ResultShmName, ResultMmfSize);
            _frameAccessor = _frameMmf.CreateViewAccessor(0, FrameMmfSize);
            _resultAccessor= _resultMmf.CreateViewAccessor(0, ResultMmfSize);
        }

        void RunWorker(string python, string script)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(python, "-u \"" + script + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(script)
                };
                info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                using (var child = new Process { StartInfo = info })
                {
                    lock (gate) { if (stopping) return; child.Start(); process = child; }
                    child.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) workerError = e.Data; };
                    child.BeginErrorReadLine();

                    string hello = child.StandardOutput.ReadLine();
                    if (hello == null || !hello.Contains("ready"))
                        throw new IOException("Model failed to initialize");
                    ready = true;

                    while (!stopping)
                    {
                        // Wait for main thread to signal a new frame is ready in shared memory.
                        string cmd;
                        lock (gate)
                        {
                            cmd = _pendingCmd;
                            _pendingCmd = null;
                        }
                        if (cmd == null) { Thread.Sleep(1); continue; }

                        child.StandardInput.WriteLine(cmd);
                        child.StandardInput.Flush();

                        string line = child.StandardOutput.ReadLine();
                        if (line == null) throw new IOException("Recognition process exited");

                        // Parse "<hand_count> <ms>" and read floats from shared result memory.
                        var parts = line.Split(' ');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int hc) && float.TryParse(parts[1], out float ms))
                        {
                            float[] floats = new float[MaxHands * FloatsPerHand];
                            _resultAccessor.ReadArray(0, floats, 0, floats.Length);
                            lock (gate)
                            {
                                pendingHandCount = hc;
                                pendingFloats    = floats;
                                _pendingMs       = ms;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { if (!stopping) lock (gate) workerError = ex.Message; }
            finally { ready = false; lock (gate) process = null; }
        }
        string _pendingCmd;
        float  _pendingMs;

        void LateUpdate()
        {
            string error;
            int    handCount;
            float[] floats;
            float   inferMs;
            lock (gate)
            {
                error     = workerError; workerError = null;
                handCount = pendingHandCount; pendingHandCount = -1;
                floats    = pendingFloats;    pendingFloats    = null;
                inferMs   = _pendingMs;
            }
            if (error != null) { Status = error; Debug.LogError("[ASJ Hand] " + error); }

            float now = Time.realtimeSinceStartup;
            if (ready && cameraSource.RgbTexture && now >= nextSend)
            {
                var texture = cameraSource.RgbTexture;
                aspect = (float)texture.width / texture.height;
                overlayCamera.orthographicSize = 1f / aspect;

                // Copy RGB pixels into shared frame memory without allocating a new array each frame.
                var nativeData = texture.GetRawTextureData<byte>();
                int byteCount  = nativeData.Length;
                if (_frameBuffer == null || _frameBuffer.Length != byteCount)
                    _frameBuffer = new byte[byteCount];
                nativeData.CopyTo(_frameBuffer);
                _frameAccessor.WriteArray(0, _frameBuffer, 0, byteCount);

                long timestamp = Math.Max(1, (long)(now * 1000));
                string cmd = $"RUN {texture.width} {texture.height} {timestamp}";
                lock (gate) { _pendingCmd = cmd; }
                nextSend = now + 1f / Mathf.Max(1, inferenceFps);
            }

            if (handCount >= 0 && floats != null)
            {
                InferenceMilliseconds = inferMs;
                ApplyFloatResult(handCount, floats);
                lastResultTime = now;
            }

            if (now - lastResultTime > lostTimeout)
            {
                TrackedHandCount = 0;
                for (int h = 0; h < 2; h++) handRoots[h].SetActive(false);
            }

            float blend = smoothing <= 0 ? 1 : 1 - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            for (int h = 0; h < 2; h++) if (handRoots[h].activeSelf)
            {
                for (int j = 0; j < 21; j++)
                {
                    joints[h,j].localPosition = Vector3.Lerp(joints[h,j].localPosition, targets[h,j], blend);
                    joints[h,j].localScale    = Vector3.one * sphereDiameter;
                }
                for (int b = 0; b < Bones.Length / 2; b++)
                {
                    lines[h,b].enabled = drawBones;
                    lines[h,b].SetPosition(0, joints[h, Bones[b*2]].localPosition);
                    lines[h,b].SetPosition(1, joints[h, Bones[b*2+1]].localPosition);
                }
            }
        }

        // Decode the flat float array written by hand_worker.py.
        void ApplyFloatResult(int handCount, float[] floats)
        {
            bool[] used = new bool[2];
            TrackedHandCount = 0;
            for (int i = 0; i < handCount && i < MaxHands; i++)
            {
                int   base_   = i * FloatsPerHand;
                int   slot    = floats[base_] < 0.5f ? 0 : 1;
                float score   = floats[base_ + 1];
                if (used[slot]) slot = 1 - slot;
                if (used[slot]) continue;

                bool wasVisible = handRoots[slot].activeSelf;
                used[slot] = true;
                TrackedHandCount++;

                Rect uv = rgbView.uvRect;
                for (int j = 0; j < 21; j++)
                {
                    float px = floats[base_ + 2 + j * 3];
                    float py = floats[base_ + 2 + j * 3 + 1];
                    float pz = floats[base_ + 2 + j * 3 + 2];
                    float u  = (px - uv.x) / uv.width;
                    float v  = (py - uv.y) / uv.height;
                    targets[slot,j] = new Vector3((u - .5f) * 2, (v - .5f) * 2 / aspect, pz * 2);
                    if (!wasVisible) joints[slot,j].localPosition = targets[slot,j];
                }
            }
            for (int h = 0; h < 2; h++) handRoots[h].SetActive(used[h]);

            Status = TrackedHandCount > 0
                ? $"Tracking {TrackedHandCount} hand(s), {TrackedHandCount*21} joints, {InferenceMilliseconds:F0} ms"
                : "Ready - show a hand to the RGB camera";
            if (TrackedHandCount > 0 && !reportedTracking)
            {
                reportedTracking = true;
                Debug.Log("[ASJ Hand] Detected hand: 21 Sphere joints are tracking live landmarks.");
            }

            // Build a Result object for subscribers that still use the event API.
            var result = new Result
            {
                timestamp    = (long)(Time.realtimeSinceStartup * 1000),
                milliseconds = InferenceMilliseconds,
                hands        = new Hand[TrackedHandCount]
            };
            int ri = 0;
            for (int i = 0; i < handCount && i < MaxHands; i++)
            {
                int base_ = i * FloatsPerHand;
                int slot  = floats[base_] < 0.5f ? 0 : 1;
                if (!handRoots[slot].activeSelf) continue;
                var hand  = new Hand
                {
                    label  = slot == 0 ? "Left" : "Right",
                    score  = floats[base_ + 1],
                    points = new Point[21],
                    world  = new Point[21]
                };
                for (int j = 0; j < 21; j++)
                {
                    hand.points[j] = new Point
                    {
                        x = floats[base_ + 2 + j * 3],
                        y = floats[base_ + 2 + j * 3 + 1],
                        z = floats[base_ + 2 + j * 3 + 2]
                    };
                    hand.world[j] = hand.points[j]; // worker only sends normalized landmarks
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
            JointRoot = new GameObject("ASJ_HandJointSpheres").transform;
            JointRoot.position = new Vector3(10000, 10000, 10000);
            var camObject = new GameObject("Hand Joint Overlay Camera");
            camObject.transform.SetParent(JointRoot, false);
            camObject.transform.localPosition = new Vector3(0, 0, -3);
            overlayCamera = camObject.AddComponent<Camera>();
            overlayCamera.orthographic = true; overlayCamera.orthographicSize = .75f;
            overlayCamera.clearFlags = CameraClearFlags.SolidColor; overlayCamera.backgroundColor = Color.clear;
            overlayCamera.nearClipPlane = .1f; overlayCamera.farClipPlane = 6;
            overlayCamera.cullingMask = 1 << 31; overlayCamera.allowHDR = false; overlayCamera.allowMSAA = false;
            overlayTexture = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32);
            overlayTexture.Create(); overlayCamera.targetTexture = overlayTexture;
            overlayObject = new GameObject("Hand Joint Sphere Overlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
            var rect = overlayObject.GetComponent<RectTransform>();
            rect.SetParent(rgbView.transform, false);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var view = overlayObject.GetComponent<RawImage>(); view.texture = overlayTexture; view.raycastTarget = false;
            for (int h = 0; h < 2; h++)
            {
                materials[h] = new Material(sphereShader);
                materials[h].color = h == 0 ? new Color(.15f, 1, .55f) : new Color(1, .5f, .1f);
                handRoots[h] = new GameObject(h == 0 ? "LeftHand_21Joints" : "RightHand_21Joints");
                handRoots[h].transform.SetParent(JointRoot, false);
                for (int j = 0; j < 21; j++)
                {
                    var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name = $"{j:D2}_{Names[j]}"; sphere.layer = 31;
                    sphere.transform.SetParent(handRoots[h].transform, false);
                    sphere.transform.localScale = Vector3.one * sphereDiameter;
                    var col = sphere.GetComponent<Collider>(); col.enabled = false; Destroy(col);
                    sphere.GetComponent<Renderer>().sharedMaterial = materials[h];
                    joints[h,j] = sphere.transform;
                }
                for (int b = 0; b < Bones.Length / 2; b++)
                {
                    var bone = new GameObject("Bone_" + b); bone.layer = 31;
                    bone.transform.SetParent(handRoots[h].transform, false);
                    var line = bone.AddComponent<LineRenderer>();
                    line.useWorldSpace = false; line.positionCount = 2;
                    line.startWidth = line.endWidth = .006f; line.sharedMaterial = materials[h];
                    lines[h,b] = line;
                }
                handRoots[h].SetActive(false);
            }
        }

        void OnGUI() { GUI.Label(new Rect(12, 12, 900, 26), "ASJ Hand Tracking | " + Status); }

        void OnDestroy()
        {
            stopping = true;
            Process child;
            lock (gate) child = process;
            try { if (child != null && !child.HasExited) child.StandardInput.Close(); }
            catch (Exception) { }
            if (worker != null && !worker.Join(2500))
            {
                try { if (child != null && !child.HasExited) child.Kill(); } catch (Exception) { }
                worker.Join(500);
            }

            _frameAccessor?.Dispose();  _frameAccessor  = null;
            _resultAccessor?.Dispose(); _resultAccessor = null;
            _frameMmf?.Dispose();       _frameMmf       = null;
            _resultMmf?.Dispose();      _resultMmf      = null;

            if (overlayObject) Destroy(overlayObject);
            if (JointRoot) Destroy(JointRoot.gameObject);
            if (overlayTexture) { overlayTexture.Release(); Destroy(overlayTexture); }
            foreach (var material in materials) if (material) Destroy(material);
        }
    }
}
