using UnityEngine;
using UnityEngine.Events;

namespace ASJ
{
    [DefaultExecutionOrder(100)]
    public sealed class ASJPinchGrab : MonoBehaviour
    {
        public ASJHandJointTracker tracker;
        public Transform target;
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
        int owner = -1;

        void OnEnable()
        {
            if (!tracker) tracker = FindObjectOfType<ASJHandJointTracker>();
            if (!target) target = transform;
            colliders = target.GetComponentsInChildren<Collider>();
            body = target.GetComponent<Rigidbody>();
            for(int i=0;i<2;i++) hands[i] = new HandState();
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

        void LateUpdate()
        {
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
                    if(owner==slot && now-hand.lostSince>=trackingLossSeconds) Release();
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
                    Vector3 desired=targetAtGrab+(position-handAtGrab);
                    // Preserve the acquisition offset; release leaves the object here.
                    target.position=desired;
                }
            }
            Status=IsHolding ? (owner==0?"Left":"Right")+" hand holding - open fingers to release"
                : anyNear ? "Target in reach - pinch thumb + index" : "Move hand near target, then pinch";
        }

        void Grab(int slot,Vector3 position)
        {
            owner=slot; handAtGrab=position; targetAtGrab=target.position;
            if(body) { wasKinematic=body.isKinematic; usedGravity=body.useGravity; body.isKinematic=true; body.useGravity=false; }
            GrabCount++;
            Debug.Log("[ASJ Grab] Grabbed target with "+(slot==0?"Left":"Right")+" hand.",this);
            onGrab.Invoke();
        }

        public void Release()
        {
            if(owner<0) return;
            owner=-1;
            if(body) { body.isKinematic=wasKinematic; body.useGravity=usedGravity; }
            ReleaseCount++;
            Debug.Log("[ASJ Grab] Released target.",this);
            onRelease.Invoke();
        }

        void OnDisable() { Release(); }
        void OnGUI() { GUI.Label(new Rect(12,36,900,26),"ASJ Grab | "+Status); }
    }
}
