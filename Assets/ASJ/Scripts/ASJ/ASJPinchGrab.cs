using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace ASJ
{
    /// <summary>
    /// 根据拇指与任意指尖的捏合动作抓取目标，并同步手部位移和旋转。
    /// 支持双手识别、悬停提示、追踪丢失容错和水平镜像。
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class ASJPinchGrab : MonoBehaviour
    {
        // 手部关节追踪器；未指定时自动查找。
        public ASJHandJointTracker tracker;
        // 抓取目标；未指定时使用当前物体。
        public Transform target;
        [Tooltip("Mirror the RGB/hand overlay and the 3D scene together. Tracking and grabbing remain in the same world space.")]
        // 同时镜像预览和场景画面，不改变抓取的世界坐标。
        public bool mirrorHorizontal;
        [Tooltip("3D scene camera to mirror. Uses Main Camera when unassigned.")]
        // 镜像使用的相机；未指定时使用主相机。
        public Camera sceneCamera;
        [Tooltip("Distance in the hand preview's Unity units.")]
        // 抓取点到碰撞体表面的最大距离；无有效碰撞体时使用目标原点。
        public float grabDistance = .08f;
        [Tooltip("Minimum distance from the thumb tip to any other fingertip, divided by palm width.")]
        // 拇指到最近指尖的距离除以掌宽；低于此阈值时视为捏合。
        public float pinchCloseRatio = .32f;
        // 张开阈值，与捏合阈值形成滞回区间以减少抖动。
        public float pinchOpenRatio = .55f;
        // 持续捏合达到此时长才确认抓取，单位为秒。
        public float confirmSeconds = .1f;
        // 持续张开达到此时长才释放，单位为秒。
        public float releaseSeconds = .08f;
        // 持有目标的手连续丢失追踪超过此时长后自动释放。
        public float trackingLossSeconds = .25f;
        [Tooltip("Invoked when a tracked hand enters grab range while the target is not held.")]
        // 目标未被持有时，有手进入范围便触发。
        public UnityEvent onHoverEnter = new UnityEvent();
        [Tooltip("Invoked when all hands leave grab range, tracking is lost, or grabbing starts.")]
        // 所有手离开范围、采样失效或开始抓取时结束悬停。
        public UnityEvent onHoverExit = new UnityEvent();
        // 抓取成功和释放完成事件，供外部绑定反馈逻辑。
        public UnityEvent onGrab = new UnityEvent();
        public UnityEvent onRelease = new UnityEvent();
        // 对外暴露交互状态、提示文本和累计操作次数。
        public bool IsHovering { get; private set; }
        public bool IsHolding { get { return owner >= 0; } }
        public string Status { get; private set; } = "Move hand near target, then pinch";
        public int GrabCount { get; private set; }
        public int ReleaseCount { get; private set; }

        // 每只手独立维护手势状态和计时。
        sealed class HandState
        {
            // closed：已捏合；eligible：本次捏合允许抓取；armed：已张开，可重新捏合。
            public bool closed, eligible, armed;
            // -1 表示尚未计时；分别记录捏合、张开和追踪丢失的起始时间。
            public float closeSince = -1, openSince = -1, lostSince = -1;
        }
        // 槽位 0 为左手，槽位 1 为右手。
        readonly HandState[] hands = { new HandState(), new HandState() };
        Collider[] colliders;
        Rigidbody body;
        // 保存抓取前的刚体配置，以便释放时恢复。
        bool wasKinematic, usedGravity;
        // 记录抓取瞬间的手部和目标姿态，用于计算相对运动。
        Vector3 handAtGrab, targetAtGrab;
        Quaternion handRotAtGrab, targetRotAtGrab;
        // 当前持有目标的手部槽位；-1 表示无人持有。
        int owner = -1;
        // 缓存镜像前的预览缩放、相机投影和剔除状态。
        Transform mirroredPreview;
        Vector3 previewScale;
        Camera renderingCamera;
        Matrix4x4 savedProjection;
        bool savedInvertCulling, restoreAutomaticProjection;
        // 记录影响追踪坐标的选项；配置变化时重置抓取状态。
        bool lastBackView, lastForwardMotion, lastInvertForward;

        private GameObject tipObj;
        private Material tipMat;
        
        
        // 初始化依赖并订阅内置渲染管线和 SRP 的相机回调。
        void OnEnable()
        {
            if (!tracker) tracker = FindObjectOfType<ASJHandJointTracker>();
            if (!target) target = transform;
            colliders = target.GetComponentsInChildren<Collider>();
            body = target.GetComponent<Rigidbody>();
            for(int i=0;i<2;i++) hands[i] = new HandState();
            lastBackView = tracker && tracker.backOfHandView;
            lastForwardMotion = tracker && tracker.estimateForwardMotion;
            lastInvertForward = tracker && tracker.invertForwardMotion;
            Camera.onPreCull -= BeginSceneMirror;
            Camera.onPreCull += BeginSceneMirror;
            Camera.onPostRender -= EndSceneMirror;
            Camera.onPostRender += EndSceneMirror;
            RenderPipelineManager.beginCameraRendering -= BeginSceneMirrorSrp;
            RenderPipelineManager.beginCameraRendering += BeginSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering -= EndSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering += EndSceneMirrorSrp;
        }

        // 采样抓取点（拇指与最近指尖的世界坐标中点）和按掌宽归一化的捏合比例。
        bool Sample(int slot, out Vector3 position, out float ratio)
        {
            position=Vector3.zero; ratio=1;
            if(!tracker || !tracker.isActiveAndEnabled) return false;
            var thumb=tracker.GetJoint(slot==0,4);
            var a=tracker.GetJoint(slot==0,5);
            var b=tracker.GetJoint(slot==0,17);
            if(!thumb || !a || !b || !thumb.gameObject.activeInHierarchy
                || !a.gameObject.activeInHierarchy
                || !b.gameObject.activeInHierarchy) return false;
            // 食指根部（5）到小指根部（17）的局部距离作为掌宽。
            float width=Vector3.Distance(a.localPosition,b.localPosition);
            if(!IsFinite(width) || width < .015f) return false;
            float closestDistance=float.PositiveInfinity;
            Transform closestTip=null;
            // MediaPipe 指尖编号：食指 8、中指 12、无名指 16、小指 20。
            // 要求所有指尖有效，避免缺失手指导致捏合比例误判为张开。
            for(int joint=8;joint<=20;joint+=4)
            {
                var tip=tracker.GetJoint(slot==0,joint);
                if(!tip || !tip.gameObject.activeInHierarchy) return false;
                float distance=Vector3.Distance(thumb.localPosition,tip.localPosition);
                if(!IsFinite(distance)) return false;
                if(distance<closestDistance) { closestDistance=distance; closestTip=tip; }
            }
            ratio=closestDistance/width;
            position=(thumb.position+closestTip.position)*.5f;
            return !float.IsNaN(ratio) && !float.IsInfinity(ratio)
                && IsFinite(position.x) && IsFinite(position.y) && IsFinite(position.z);
        }

        static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        // 优先检查抓取点到任意有效碰撞体表面的距离。
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

        // 获取目标下名为 tip 的提示物体及独立材质实例。
        private void Start()
        {
            var tip = target ? target.Find("tip") : null;
            tipObj = tip ? tip.gameObject : null;
            if (tipObj)
            {
                tipMat = tipObj.GetComponent<Renderer>().material;
            }
        }

        private void Update()
        {
            if (tipObj)
            {
                // 悬停时显示黄色，持有时显示绿色，其余时间隐藏。
                tipObj.SetActive(IsHovering || IsHolding);
                tipMat.color = IsHolding ? Color.green : Color.yellow;
                //tipObj.GetComponent<Renderer>().material = tipMat;
            }
        }

        void LateUpdate()
        {
            // 使用不受 timeScale 影响的时间，在 LateUpdate 中读取本帧追踪结果。
            UpdateGrab(Time.unscaledTime);
        }

        // 显式传入时间，便于编辑器验证时直接推进计时而无需等待。
        void UpdateGrab(float now)
        {
            if (tracker && (lastBackView != tracker.backOfHandView || lastForwardMotion != tracker.estimateForwardMotion
                || lastInvertForward != tracker.invertForwardMotion))
            {
                // 追踪方向改变后旧姿态基准不再适用，释放目标并重新识别手势。
                Release();
                SetHovering(false);
                for (int i = 0; i < 2; i++) hands[i] = new HandState();
                lastBackView = tracker.backOfHandView;
                lastForwardMotion = tracker.estimateForwardMotion;
                lastInvertForward = tracker.invertForwardMotion;
                return;
            }
            UpdateMirrorPreview();
            if(!target || !target.gameObject.activeInHierarchy)
            {
                Release();
                SetHovering(false);
                foreach (var hand in hands) ResetGesture(hand);
                return;
            }
            bool anyNear=false;
            for(int slot=0;slot<2;slot++)
            {
                HandState hand=hands[slot];
                Vector3 position; float ratio;
                if(!Sample(slot,out position,out ratio))
                {
                    // 短暂丢帧只重置手势；持有状态保留到追踪丢失超时。
                    if(hand.lostSince<0) hand.lostSince=now;
                    if(owner==slot && now-hand.lostSince>=Mathf.Max(0,trackingLossSeconds))
                    {
                        Debug.Log("丢帧释放！！！");
                        Release();
                    }
                    ResetGesture(hand);
                    continue;
                }
                hand.lostSince=-1;
                bool near=Near(position); anyNear |= near;
                // 离开范围会取消本次捏合资格，必须重新张开后才能再次尝试。
                // if (!near)
                // {
                //     Debug.Log("离开范围!!!");
                //     hand.eligible=false; hand.closeSince=-1;
                // }
                // 张开阈值至少比闭合阈值大 0.05，确保存在滞回区间。
                if(ratio>=Mathf.Max(pinchOpenRatio,pinchCloseRatio+.5f))
                {
                    hand.armed=true;
                    hand.closed=false;
                    hand.eligible=false;
                    hand.closeSince=-1;
                    if(hand.openSince<0) hand.openSince=now;
                    if(owner==slot && now-hand.openSince>=releaseSeconds) Release();
                }
                else if(ratio<=pinchCloseRatio)
                {
                    hand.openSince=-1;
                    if(!hand.closed)
                    {
                        hand.closed=true;
                        // 只有先张开、再在范围内捏合且目标空闲时，才允许确认抓取。
                        hand.eligible=hand.armed && near && owner<0;
                        hand.armed=false;
                    }
                    if(owner<0 && hand.eligible && near)
                    {
                        if (hand.closeSince<0) hand.closeSince=now;
                        if (now-hand.closeSince>=Mathf.Max(0,confirmSeconds)) Grab(slot,position);
                    }
                }
                else
                {
                    // 滞回区间保留手势状态，但重置计时，要求连续捏合或张开。
                    hand.openSince=-1;
                    hand.closeSince=-1;
                }

                if(owner==slot)
                {
                    // 将相对抓取瞬间的手部位移和旋转增量应用到目标初始姿态。
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
            SetHovering(!IsHolding && anyNear);
            Status=IsHolding ? (owner==0?"Left":"Right")+" hand holding - open fingers to release"
                : anyNear ? "Target in reach - pinch thumb + any fingertip" : "Move hand near target, then pinch";
        }

        // 仅在悬停状态改变时触发事件。
        void SetHovering(bool hovering)
        {
            if (IsHovering == hovering) return;
            IsHovering = hovering;
            if (hovering) onHoverEnter.Invoke();
            else onHoverExit.Invoke();
        }

        // 重置手势但保留追踪丢失的起始时间，以便累计超时。
        static void ResetGesture(HandState hand)
        {
            hand.armed=false;
            hand.eligible=false;
            hand.closed=false;
            hand.closeSince=-1;
            hand.openSince=-1;
        }

        // 记录抓取基准，并临时将刚体设为运动学模式、关闭重力。
        void Grab(int slot,Vector3 position)
        {
            owner=slot; handAtGrab=position; targetAtGrab=target.position;
            handRotAtGrab=SamplePalmRotation(slot); targetRotAtGrab=target.rotation;
            if(body) { wasKinematic=body.isKinematic; usedGravity=body.useGravity; body.isKinematic=true; body.useGravity=false; }
            GrabCount++;
            // 清除双手待确认的抓取，避免本次抓取或后续外部释放后残留旧尝试。
            foreach (var hand in hands) { hand.eligible=false; hand.closeSince=-1; }
            SetHovering(false);
            Debug.Log("[ASJ Grab] Grabbed target with "+(slot==0?"Left":"Right")+" hand.",this);
            onGrab.Invoke();
        }

        // 用三个稳定关节点构造掌部旋转：手腕（0）指向食指根部（5）为前向，
        // 食指根部（5）指向小指根部（17）为横向，通过叉积求掌面法线。
        // 关节点缺失、方向过短或共线时返回单位旋转。
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

        /// <summary>释放目标，恢复抓取前的刚体配置并触发释放事件。</summary>
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
                    // 恢复动态刚体时清空速度，避免残留速度导致突然弹飞。
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            ReleaseCount++;
            Debug.Log("[ASJ Grab] Released target.",this);
            onRelease.Invoke();
        }

        // 镜像 RGB 预览；关闭镜像或切换预览对象时恢复原缩放。
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
                // 子级 RenderTexture 叠加画面随 RGB 一起翻转，其中也包含三维物体。
                preview.localScale = new Vector3(-previewScale.x, previewScale.y, previewScale.z);
            }
        }

        // 渲染指定相机前翻转投影 X 轴，并反转面剔除以匹配镜像后的绕序。
        void BeginSceneMirror(Camera camera)
        {
            if (!mirrorHorizontal || renderingCamera || camera != (sceneCamera ? sceneCamera : Camera.main)) return;
            renderingCamera = camera;
            savedProjection = camera.projectionMatrix;
            camera.ResetProjectionMatrix();
            // 判断原投影是否自动生成，结束镜像时恢复对应的投影管理方式。
            restoreAutomaticProjection = camera.projectionMatrix == savedProjection;
            camera.projectionMatrix = Matrix4x4.Scale(new Vector3(-1, 1, 1)) * savedProjection;
            savedInvertCulling = GL.invertCulling;
            GL.invertCulling = !savedInvertCulling;
        }

        // 渲染结束后恢复投影和全局剔除状态，避免影响其他相机。
        void EndSceneMirror(Camera camera)
        {
            if (!renderingCamera || camera != renderingCamera) return;
            if (restoreAutomaticProjection) camera.ResetProjectionMatrix();
            else camera.projectionMatrix = savedProjection;
            GL.invertCulling = savedInvertCulling;
            renderingCamera = null;
        }

        // SRP 通过命令缓冲同步剔除状态，使镜像设置进入渲染上下文。
        void BeginSceneMirrorSrp(ScriptableRenderContext context, Camera camera)
        {
            BeginSceneMirror(camera);
            if (renderingCamera != camera) return;
            var command = CommandBufferPool.Get("ASJ scene mirror");
            command.SetInvertCulling(!savedInvertCulling);
            context.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);
        }

        // SRP 渲染结束时先恢复剔除状态，再恢复相机状态。
        void EndSceneMirrorSrp(ScriptableRenderContext context, Camera camera)
        {
            if (renderingCamera != camera) return;
            var command = CommandBufferPool.Get("ASJ restore culling");
            command.SetInvertCulling(savedInvertCulling);
            context.ExecuteCommandBuffer(command);
            CommandBufferPool.Release(command);
            EndSceneMirror(camera);
        }

        // 停用时释放目标、还原镜像并解除订阅，避免残留状态或回调。
        void OnDisable()
        {
            Release();
            SetHovering(false);
            if (mirroredPreview) mirroredPreview.localScale = previewScale;
            mirroredPreview = null;
            if (renderingCamera) EndSceneMirror(renderingCamera);
            Camera.onPreCull -= BeginSceneMirror;
            Camera.onPostRender -= EndSceneMirror;
            RenderPipelineManager.beginCameraRendering -= BeginSceneMirrorSrp;
            RenderPipelineManager.endCameraRendering -= EndSceneMirrorSrp;
        }
        // 在屏幕左上角显示抓取状态，便于调试。
        void OnGUI() { GUI.Label(new Rect(12,36,900,26),"ASJ Grab | "+Status); }
    }
}
