using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace ASJ
{
    [DefaultExecutionOrder(100)]
    public sealed class ASJPinchGrab : MonoBehaviour
    {
        public ASJHandJointTracker tracker;
        public Transform target;
        [Tooltip("Mirror the RGB/hand overlay and the 3D scene together. Tracking and grabbing remain in the same world space.")]
        public bool mirrorHorizontal;
        [Tooltip("3D scene camera to mirror. Uses Main Camera when unassigned.")]
        public Camera sceneCamera;
        [Tooltip("Distance in the hand preview's Unity units.")]
        public float grabDistance = .08f;
        [Tooltip("Thumb/index distance divided by palm width.")]
        public float pinchCloseRatio = .32f;
        public float pinchOpenRatio = .55f;
        public float confirmSeconds = .1f;
        public float releaseSeconds = .08f;
        public float trackingLossSeconds = .25f;
        public UnityEvent onGrab = new UnityEvent();
        public UnityEvent onRelease = new UnityEvent();
        public bool IsHolding { get { return owner >= 0; } }
        public string Status { get; private set; } = "Move hand near target, then pinch";
        public int GrabCount { get; private set; }
        public int ReleaseCount { get; private set; }

        sealed class HandState
        {
            public bool closed, eligible, armed;
            public float closeSince, openSince = -1, lostSince = -1;
        }
        readonly HandState[] hands = { new HandState(), new HandState() };
        Collider[] colliders;
        Rigidbody body;
        bool wasKinematic, usedGravity;
        Vector3 handAtGrab, targetAtGrab;
        Quaternion handRotAtGrab, targetRotAtGrab;
        int owner = -1;
        Transform mirroredPreview;
        Vector3 previewScale;
        Camera renderingCamera;
        Matrix4x4 savedProjection;
        bool savedInvertCulling, restoreAutomaticProjection;
        bool lastBackView, lastForwardMotion;

        private GameObject tipObj;
        
        void OnEnable()
        {
            if (!tracker) tracker = FindObjectOfType<ASJHandJointTracker>();
            if (!target) target = transform;
            colliders = target.GetComponentsInChildren<Collider>();
            body = target.GetComponent<Rigidbody>();
            for(int i=0;i<2;i++) hands[i] = new HandState();
            lastBackView = tracker && tracker.backOfHandView;
            lastForwardMotion = tracker && tracker.estimateForwardMotion;
            Camera.onPreCull -= BeginSceneMirror;
            Camera.onPreCull += BeginSceneMirror;
            Camera.onPostRender -= EndSceneMirror;
            Camera.onPostRender += EndSceneMirror;
            RenderPipelineManager.beginCameraRendering -= BeginSceneMirrorSrp;
            RenderPipelineManager.beginCameraRendering += BeginSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering -= EndSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering += EndSceneMirrorSrp;
        }

        bool Sample(int slot, out Vector3 position, out float ratio)
        {
            position=Vector3.zero; ratio=1;
            if(!tracker || !tracker.isActiveAndEnabled) return false;
            var thumb=tracker.GetJoint(slot==0,4);
            var index=tracker.GetJoint(slot==0,8);
            var a=tracker.GetJoint(slot==0,5);
            var b=tracker.GetJoint(slot==0,17);
            if(!thumb || !index || !a || !b || !thumb.gameObject.activeInHierarchy) return false;
            float width=Vector3.Distance(a.localPosition,b.localPosition);
            if(width < .015f) return false;
            ratio=Vector3.Distance(thumb.localPosition,index.localPosition)/width;
            position=(thumb.position+index.position)*.5f;
            return !float.IsNaN(ratio) && !float.IsInfinity(ratio);
        }

        bool Near(Vector3 position)
        {
            if(!target || !target.gameObject.activeInHierarchy) return false;
            bool hasCollider=false;
            foreach(var c in colliders)
            {
                if(!c || !c.enabled || !c.gameObject.activeInHierarchy) continue;
                hasCollider=true;
                if(Vector3.Distance(c.ClosestPoint(position),position)<=grabDistance) return true;
            }
            return !hasCollider && Vector3.Distance(position,target.position)<=grabDistance;
        }

        private void Start()
        {
            var tip = target ? target.Find("tip") : null;
            tipObj = tip ? tip.gameObject : null;
        }

        private void Update()
        {
            if (tipObj)
            {
                tipObj.SetActive(IsHolding);
            }
        }

        void LateUpdate()
        {
            if (tracker && (lastBackView != tracker.backOfHandView || lastForwardMotion != tracker.estimateForwardMotion))
            {
                Release();
                for (int i = 0; i < 2; i++) hands[i] = new HandState();
                lastBackView = tracker.backOfHandView;
                lastForwardMotion = tracker.estimateForwardMotion;
                return;
            }
            UpdateMirrorPreview();
            if(!target || !target.gameObject.activeInHierarchy) { Release(); return; }
            float now=Time.unscaledTime;
            bool anyNear=false;
            for(int slot=0;slot<2;slot++)
            {
                HandState hand=hands[slot];
                Vector3 position; float ratio;
                if(!Sample(slot,out position,out ratio))
                {
                    if(hand.lostSince<0) hand.lostSince=now;
                    if(owner==slot && ((tracker && tracker.estimateForwardMotion) || now-hand.lostSince>=trackingLossSeconds)) Release();
                    hand.armed=false; hand.eligible=false; hand.closed=false;
                    continue;
                }
                hand.lostSince=-1;
                bool near=Near(position); anyNear |= near;
                if(ratio>=Mathf.Max(pinchOpenRatio,pinchCloseRatio+.05f))
                {
                    hand.armed=true;
                    hand.closed=false;
                    hand.eligible=false;
                    if(hand.openSince<0) hand.openSince=now;
                    if(owner==slot && now-hand.openSince>=releaseSeconds) Release();
                }
                else if(ratio<=pinchCloseRatio)
                {
                    hand.openSince=-1;
                    if(!hand.closed)
                    {
                        hand.closed=true; hand.closeSince=now;
                        hand.eligible=hand.armed && near && owner<0;
                        hand.armed=false;
                    }
                    if(owner<0 && hand.eligible && near && now-hand.closeSince>=confirmSeconds)
                        Grab(slot,position);
                }
                else hand.openSince=-1;

                if(owner==slot)
                {
                    Quaternion currentHandRot = SamplePalmRotation(slot);
                    Vector3 movement = position - handAtGrab;
                    Quaternion rotation = currentHandRot * Quaternion.Inverse(handRotAtGrab);
                    Vector3 desired = targetAtGrab + movement;
                    Quaternion desiredRot = rotation * targetRotAtGrab;
                    if(body != null)
                    {
                        body.MovePosition(desired);
                        body.MoveRotation(desiredRot);
                    }
                    else
                    {
                        target.position = desired;
                        target.rotation = desiredRot;
                    }
                }
            }
            Status=IsHolding ? (owner==0?"Left":"Right")+" hand holding - open fingers to release"
                : anyNear ? "Target in reach - pinch thumb + index" : "Move hand near target, then pinch";
        }

        void Grab(int slot,Vector3 position)
        {
            owner=slot; handAtGrab=position; targetAtGrab=target.position;
            handRotAtGrab=SamplePalmRotation(slot); targetRotAtGrab=target.rotation;
            if(body) { wasKinematic=body.isKinematic; usedGravity=body.useGravity; body.isKinematic=true; body.useGravity=false; }
            GrabCount++;
            Debug.Log("[ASJ Grab] Grabbed target with "+(slot==0?"Left":"Right")+" hand.",this);
            onGrab.Invoke();
        }

        // Build a palm-space rotation from three stable landmarks:
        //   wrist(0) → index-MCP(5) as the forward axis,
        //   index-MCP(5) → pinky-MCP(17) as the right axis.
        Quaternion SamplePalmRotation(int slot)
        {
            bool left=slot==0;
            var wrist=tracker.GetJoint(left,0);
            var indexMcp=tracker.GetJoint(left,5);
            var pinkyMcp=tracker.GetJoint(left,17);
            if(!wrist || !indexMcp || !pinkyMcp) return Quaternion.identity;
            Vector3 fwd=(indexMcp.position-wrist.position);
            Vector3 right=(pinkyMcp.position-indexMcp.position);
            if(fwd.sqrMagnitude<1e-8f || right.sqrMagnitude<1e-8f) return Quaternion.identity;
            Vector3 up=Vector3.Cross(fwd.normalized,right.normalized);
            if(up.sqrMagnitude<1e-8f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd.normalized,up.normalized);
        }

        public void Release()
        {
            if(owner<0) return;
            owner=-1;
            if(body)
            {
                body.isKinematic = wasKinematic;
                body.useGravity = usedGravity;
                if(!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            ReleaseCount++;
            Debug.Log("[ASJ Grab] Released target.",this);
            onRelease.Invoke();
        }

        void UpdateMirrorPreview()
        {
            Transform preview = tracker && tracker.rgbView ? tracker.rgbView.transform : null;
            if (mirroredPreview && (!mirrorHorizontal || mirroredPreview != preview))
            {
                mirroredPreview.localScale = previewScale;
                mirroredPreview = null;
            }
            if (mirrorHorizontal && preview && !mirroredPreview)
            {
                mirroredPreview = preview;
                previewScale = preview.localScale;
                // The child RenderTexture overlay flips with RGB, including all 3D objects.
                preview.localScale = new Vector3(-previewScale.x, previewScale.y, previewScale.z);
            }
        }

        void BeginSceneMirror(Camera camera)
        {
            if (!mirrorHorizontal || renderingCamera || camera != (sceneCamera ? sceneCamera : Camera.main)) return;
            renderingCamera = camera;
            savedProjection = camera.projectionMatrix;
            camera.ResetProjectionMatrix();
            restoreAutomaticProjection = camera.projectionMatrix == savedProjection;
            camera.projectionMatrix = Matrix4x4.Scale(new Vector3(-1, 1, 1)) * savedProjection;
            savedInvertCulling = GL.invertCulling;
            GL.invertCulling = !savedInvertCulling;
        }

        void EndSceneMirror(Camera camera)
        {
            if (!renderingCamera || camera != renderingCamera) return;
            if (restoreAutomaticProjection) camera.ResetProjectionMatrix();
            else camera.projectionMatrix = savedProjection;
            GL.invertCulling = savedInvertCulling;
            renderingCamera = null;
        }

        void BeginSceneMirrorSrp(ScriptableRenderContext context, Camera camera)
        {
            BeginSceneMirror(camera);
            if (renderingCamera != camera) return;
            var command = CommandBufferPool.Get("ASJ scene mirror");
            command.SetInvertCulling(!savedInvertCulling);
            context.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);
        }

        void EndSceneMirrorSrp(ScriptableRenderContext context, Camera camera)
        {
            if (renderingCamera != camera) return;
            var command = CommandBufferPool.Get("ASJ restore culling");
            command.SetInvertCulling(savedInvertCulling);
            context.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);
            EndSceneMirror(camera);
        }

        void OnDisable()
        {
            Release();
            if (mirroredPreview) mirroredPreview.localScale = previewScale;
            mirroredPreview = null;
            if (renderingCamera) EndSceneMirror(renderingCamera);
            Camera.onPreCull -= BeginSceneMirror;
            Camera.onPostRender -= EndSceneMirror;
            RenderPipelineManager.beginCameraRendering -= BeginSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering -= EndSceneMirrorSrp;
        }
        void OnGUI() { GUI.Label(new Rect(12,36,900,26),"ASJ Grab | "+Status); }
    }
}
