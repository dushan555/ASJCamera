using System;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Process = System.Diagnostics.Process;

namespace ASJ
{
    /// <summary>21 real Sphere objects per hand, positioned in an RGB-aligned local space.
    /// Z is MediaPipe's wrist-relative estimate, NOT calibrated camera depth.</summary>
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

        static readonly string[] Names = { "Wrist", "ThumbCMC", "ThumbMCP", "ThumbIP", "ThumbTip",
            "IndexMCP", "IndexPIP", "IndexDIP", "IndexTip", "MiddleMCP", "MiddlePIP", "MiddleDIP", "MiddleTip",
            "RingMCP", "RingPIP", "RingDIP", "RingTip", "PinkyMCP", "PinkyPIP", "PinkyDIP", "PinkyTip" };
        static readonly int[] Bones = {0,1,1,2,2,3,3,4,0,5,5,6,6,7,7,8,5,9,9,10,10,11,11,12,9,13,13,14,14,15,15,16,13,17,0,17,17,18,18,19,19,20};
        readonly Transform[,] joints = new Transform[2,21];
        readonly Vector3[,] targets = new Vector3[2,21];
        readonly GameObject[] handRoots = new GameObject[2];
        readonly LineRenderer[,] lines = new LineRenderer[2,21];
        readonly Material[] materials = new Material[2];
        readonly object gate = new object();
        readonly AutoResetEvent available = new AutoResetEvent(false);
        volatile bool stopping;
        Thread worker;
        Process process;
        byte[] pending;
        int pendingWidth,pendingHeight;
        long pendingTimestamp;
        string resultJson, workerError;
        volatile bool ready;
        float nextSend,lastResultTime;
        long lastTimestamp;
        Camera overlayCamera;
        RenderTexture overlayTexture;
        GameObject overlayObject;
        float aspect = 4f/3f;
        bool reportedTracking;

        void Start()
        {
            if (!cameraSource) cameraSource = FindObjectOfType<ASJCamera>();
            if (!cameraSource || !rgbView || !sphereShader)
            { Status="Assign cameraSource, rgbView and sphereShader"; Debug.LogError("[ASJ Hand] "+Status); enabled=false; return; }
            CreateVisuals();
            string project = Path.GetFullPath(Path.Combine(Application.dataPath,".."));
            string python = string.IsNullOrWhiteSpace(pythonExecutable)
                ? Path.Combine(project,"Tools/ASJHandTracking/.venv/Scripts/python.exe") : pythonExecutable;
            string script = Path.Combine(Application.streamingAssetsPath,"ASJ/HandTracking/hand_worker.py");
            if (!File.Exists(python) || !File.Exists(script))
            { Status="Missing Python runtime or hand_worker.py; see Tools/ASJHandTracking/README_CN.md"; Debug.LogError("[ASJ Hand] "+Status); enabled=false; return; }
            worker = new Thread(()=>RunWorker(python,script)) { IsBackground=true, Name="ASJ Hand Inference" };
            worker.Start();
        }

        void RunWorker(string python,string script)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(python,"-u \""+script+"\"") {
                    UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
                    RedirectStandardOutput=true, RedirectStandardError=true, WorkingDirectory=Path.GetDirectoryName(script) };
                info.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";
                using (var child = new Process { StartInfo=info })
                {
                    // Publish the started process under the same lock used by teardown.
                    lock(gate) { if(stopping) return; child.Start(); process=child; }
                    child.ErrorDataReceived += (_,e)=> { if(e.Data!=null) lock(gate) workerErrorDetail=e.Data; };
                    child.BeginErrorReadLine();
                    string hello=child.StandardOutput.ReadLine();
                    if(hello==null || !hello.Contains("ready")) throw new IOException("Model failed to initialize");
                    ready=true;
                    using(var writer=new BinaryWriter(child.StandardInput.BaseStream))
                    {
                        while(!stopping)
                        {
                            available.WaitOne();
                            if(stopping) break;
                            byte[] frame; int w,h; long timestamp;
                            lock(gate) { frame=pending; pending=null; w=pendingWidth; h=pendingHeight; timestamp=pendingTimestamp; }
                            if(frame==null) continue;
                            writer.Write(w); writer.Write(h); writer.Write(frame.Length); writer.Write(timestamp); writer.Write(frame); writer.Flush();
                            string line=child.StandardOutput.ReadLine();
                            if(line==null) throw new IOException("Recognition process exited");
                            lock(gate) resultJson=line;
                        }
                    }
                }
            }
            catch(Exception ex) { if(!stopping) lock(gate) workerError=ex.Message+" "+workerErrorDetail; }
            finally { ready=false; lock(gate) process=null; }
        }
        string workerErrorDetail;

        void LateUpdate()
        {
            string json,error;
            lock(gate) { json=resultJson; resultJson=null; error=workerError; workerError=null; }
            if(error!=null) { Status=error; Debug.LogError("[ASJ Hand] "+error); }
            float now=Time.realtimeSinceStartup;
            if(ready && cameraSource.RgbTexture && now>=nextSend)
            {
                var texture=cameraSource.RgbTexture;
                aspect=(float)texture.width/texture.height;
                overlayCamera.orthographicSize=1f/aspect;
                long timestamp=Math.Max(lastTimestamp+1,(long)(now*1000)); lastTimestamp=timestamp;
                byte[] frame=texture.GetRawTextureData<byte>().ToArray();
                lock(gate) { pending=frame; pendingWidth=texture.width; pendingHeight=texture.height; pendingTimestamp=timestamp; }
                available.Set();
                nextSend=now+1f/Mathf.Max(1,inferenceFps);
            }
            if(json!=null)
            {
                try
                {
                    var result=JsonUtility.FromJson<Result>(json);
                    // Ignore stale results when inference or the editor stalls.
                    if(result!=null && now-result.timestamp*.001f<lostTimeout)
                    { ApplyResult(result); lastResultTime=now; }
                }
                catch(Exception ex) { Status=ex.Message; }
            }
            if(now-lastResultTime>lostTimeout) { TrackedHandCount=0; for(int h=0;h<2;h++) handRoots[h].SetActive(false); }
            float blend=smoothing<=0?1:1-Mathf.Exp(-smoothing*Time.unscaledDeltaTime);
            for(int h=0;h<2;h++) if(handRoots[h].activeSelf)
            {
                for(int j=0;j<21;j++) { joints[h,j].localPosition=Vector3.Lerp(joints[h,j].localPosition,targets[h,j],blend); joints[h,j].localScale=Vector3.one*sphereDiameter; }
                for(int b=0;b<Bones.Length/2;b++)
                { lines[h,b].enabled=drawBones; lines[h,b].SetPosition(0,joints[h,Bones[b*2]].localPosition); lines[h,b].SetPosition(1,joints[h,Bones[b*2+1]].localPosition); }
            }
        }

        void ApplyResult(Result result)
        {
            LatestResult=result; InferenceMilliseconds=result.milliseconds;
            bool[] used=new bool[2]; TrackedHandCount=0;
            if(result.hands!=null) foreach(var hand in result.hands)
            {
                if(hand.points==null || hand.points.Length!=21) continue;
                int slot=hand.label=="Left"?0:1;
                if(used[slot]) slot=1-slot;
                if(used[slot]) continue;
                bool wasVisible=handRoots[slot].activeSelf;
                used[slot]=true; TrackedHandCount++;
                for(int j=0;j<21;j++)
                {
                    Point p=hand.points[j];
                    float x=p.x, y=p.y;
                    // Invert the RawImage UV transform to follow mirrored/flipped previews.
                    Rect uv=rgbView.uvRect;
                    float u=(x-uv.x)/uv.width, v=(y-uv.y)/uv.height;
                    targets[slot,j]=new Vector3((u-.5f)*2,(v-.5f)*2/aspect,p.z*2);
                    if(!wasVisible) joints[slot,j].localPosition=targets[slot,j];
                }
            }
            for(int h=0;h<2;h++) handRoots[h].SetActive(used[h]);
            Status=TrackedHandCount>0?$"Tracking {TrackedHandCount} hand(s), {TrackedHandCount*21} joints, {InferenceMilliseconds:F0} ms":"Ready - show a hand to the RGB camera";
            if(TrackedHandCount>0 && !reportedTracking) { reportedTracking=true; Debug.Log("[ASJ Hand] Detected hand: 21 Sphere joints are tracking live landmarks."); }
            OnHandsUpdated?.Invoke(result);
        }

        public Transform GetJoint(bool leftHand,int index) { return index>=0 && index<21 ? joints[leftHand?0:1,index] : null; }

        void CreateVisuals()
        {
            // Isolate the preview geometry away from the user's scene and cameras.
            JointRoot=new GameObject("ASJ_HandJointSpheres").transform;
            JointRoot.position=new Vector3(10000,10000,10000);
            var camObject=new GameObject("Hand Joint Overlay Camera"); camObject.transform.SetParent(JointRoot,false);
            camObject.transform.localPosition=new Vector3(0,0,-3);
            overlayCamera=camObject.AddComponent<Camera>(); overlayCamera.orthographic=true; overlayCamera.orthographicSize=.75f;
            overlayCamera.clearFlags=CameraClearFlags.SolidColor; overlayCamera.backgroundColor=Color.clear;
            overlayCamera.nearClipPlane=.1f; overlayCamera.farClipPlane=6;
            overlayCamera.cullingMask=1<<31; overlayCamera.allowHDR=false; overlayCamera.allowMSAA=false;
            overlayTexture=new RenderTexture(640,480,24,RenderTextureFormat.ARGB32); overlayTexture.Create(); overlayCamera.targetTexture=overlayTexture;
            overlayObject=new GameObject("Hand Joint Sphere Overlay",typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage));
            var rect=overlayObject.GetComponent<RectTransform>(); rect.SetParent(rgbView.transform,false);
            rect.anchorMin=Vector2.zero; rect.anchorMax=Vector2.one; rect.offsetMin=rect.offsetMax=Vector2.zero;
            var view=overlayObject.GetComponent<RawImage>(); view.texture=overlayTexture; view.raycastTarget=false;
            for(int h=0;h<2;h++)
            {
                materials[h]=new Material(sphereShader); materials[h].color=h==0?new Color(.15f,1,.55f):new Color(1,.5f,.1f);
                handRoots[h]=new GameObject(h==0?"LeftHand_21Joints":"RightHand_21Joints"); handRoots[h].transform.SetParent(JointRoot,false);
                for(int j=0;j<21;j++)
                {
                    var sphere=GameObject.CreatePrimitive(PrimitiveType.Sphere); sphere.name=$"{j:D2}_{Names[j]}";
                    sphere.layer=31; sphere.transform.SetParent(handRoots[h].transform,false); sphere.transform.localScale=Vector3.one*sphereDiameter;
                    var collider=sphere.GetComponent<Collider>(); collider.enabled=false; Destroy(collider);
                    sphere.GetComponent<Renderer>().sharedMaterial=materials[h]; joints[h,j]=sphere.transform;
                }
                for(int b=0;b<Bones.Length/2;b++)
                {
                    var bone=new GameObject("Bone_"+b); bone.layer=31; bone.transform.SetParent(handRoots[h].transform,false);
                    var line=bone.AddComponent<LineRenderer>(); line.useWorldSpace=false; line.positionCount=2; line.startWidth=line.endWidth=.006f;
                    line.sharedMaterial=materials[h]; lines[h,b]=line;
                }
                handRoots[h].SetActive(false);
            }
        }

        void OnGUI()
        {
            GUI.Label(new Rect(12,12,900,26),"ASJ Hand Tracking | "+Status);
        }

        void OnDestroy()
        {
            stopping=true; available.Set();
            // EOF lets both the venv launcher and its Python child exit cleanly.
            // Do not hold gate while waiting: the worker also needs it on exit.
            Process child;
            lock(gate) child=process;
            try { if(child!=null && !child.HasExited) child.StandardInput.Close(); }
            catch(Exception) { /* Worker may already have disposed its pipe. */ }
            if(worker!=null && !worker.Join(2500))
            {
                try { if(child!=null && !child.HasExited) child.Kill(); } catch(Exception) {}
                worker.Join(500);
            }
            if(worker==null || !worker.IsAlive) available.Dispose();
            if(overlayObject) Destroy(overlayObject);
            if(JointRoot) Destroy(JointRoot.gameObject);
            if(overlayTexture) { overlayTexture.Release(); Destroy(overlayTexture); }
            foreach(var material in materials) if(material) Destroy(material);
        }
    }
}
