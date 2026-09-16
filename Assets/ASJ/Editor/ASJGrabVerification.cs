using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class ASJGrabVerification
{
    static ASJGrabVerification() { EditorApplication.delayCall+=Run; }
    static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    static void Call(object obj,string method) { obj.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(obj,null); }
    static void Tick(ASJ.ASJPinchGrab grab) { typeof(ASJ.ASJPinchGrab).GetMethod("UpdateGrab",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(grab,new object[] { 10f }); }
    [MenuItem("ASJ/Verify Pinch Grab")]
    public static void RunManually()
    {
        SessionState.SetBool("ASJGrabVerifiedV3",false);
        Run();
    }
    static void Run()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool("ASJGrabVerifiedV3",false)) return;
        GameObject root=new GameObject("Grab verification") {hideFlags=HideFlags.HideAndDontSave};
        try
        {
            var tracker=root.AddComponent<ASJ.ASJHandJointTracker>();
            var joints=(Transform[,])typeof(ASJ.ASJHandJointTracker).GetField("joints",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(tracker);
            for(int i=0;i<21;i++) { var joint=new GameObject("Joint"+i); joint.transform.SetParent(root.transform); joints[0,i]=joint.transform; }
            var target=new GameObject("TestTarget"); target.transform.SetParent(root.transform);
            var grab=target.AddComponent<ASJ.ASJPinchGrab>(); grab.tracker=tracker; grab.target=target.transform;
            grab.confirmSeconds=0; grab.releaseSeconds=0; grab.trackingLossSeconds=0; grab.grabDistance=.1f;
            Call(grab,"OnEnable");
            Action<float,float> pose=(x,gap)=> {
                joints[0,5].localPosition=new Vector3(x-.1f,0,0); joints[0,17].localPosition=new Vector3(x+.1f,0,0);
                joints[0,4].localPosition=new Vector3(x-gap*.5f,0,0); joints[0,8].localPosition=new Vector3(x+gap*.5f,0,0);
                Tick(grab);
            };
            pose(.05f,.2f); pose(.05f,.02f);
            Check(grab.IsHolding,"near pinch should grab"); Check(target.transform.position.sqrMagnitude<1e-6,"grab must not snap target");
            pose(.25f,.02f); Check(Vector3.Distance(target.transform.position,new Vector3(.2f,0,0))<.001f,"target must follow relative hand movement");
            pose(.25f,.2f); Check(!grab.IsHolding,"open fingers must release");
            pose(2,.2f); pose(2,.02f); pose(.2f,.02f); Check(!grab.IsHolding,"closed hand entering target must not auto-grab");
            pose(.2f,.2f); pose(.2f,.02f); Check(grab.IsHolding,"reopening rearms grab");
            joints[0,4].gameObject.SetActive(false); Tick(grab); Check(!grab.IsHolding,"tracking loss must release");
            joints[0,4].gameObject.SetActive(true);
            var hand=((Array)typeof(ASJ.ASJPinchGrab).GetField("hands",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(grab)).GetValue(0);
            Action<string,float> setTimer=(name,value)=>hand.GetType().GetField(name).SetValue(hand,value);
            Func<string,float> timer=name=>(float)hand.GetType().GetField(name).GetValue(hand);
            grab.confirmSeconds=.1f;
            pose(.2f,.2f); pose(.2f,.02f);
            setTimer("closeSince",9f);
            pose(.2f,.08f); // Ratio .4: between close and open thresholds.
            pose(.2f,.02f);
            Check(!grab.IsHolding,"hysteresis band must restart continuous close confirmation");
            setTimer("closeSince",9f);
            pose(2,.02f); pose(.2f,.02f);
            Check(!grab.IsHolding,"leaving range must cancel the pending pinch");
            pose(.2f,.2f); pose(.2f,.02f);
            setTimer("closeSince",9f); pose(.2f,.02f);
            Check(grab.IsHolding,"continuous close confirmation must acquire");
            grab.Release(); pose(.2f,.02f);
            Check(!grab.IsHolding,"external release must not reuse the previous pinch");
            grab.confirmSeconds=0;
            pose(.2f,.2f); pose(.2f,.02f);
            grab.releaseSeconds=.1f; grab.trackingLossSeconds=10;
            tracker.estimateForwardMotion=false;
            pose(.2f,.2f);
            setTimer("openSince",9f);
            joints[0,8].gameObject.SetActive(false); Tick(grab);
            Check(grab.IsHolding,"brief loss should preserve holding during grace period");
            Check(timer("openSince")<0,"tracking loss must clear release confirmation");
            joints[0,8].gameObject.SetActive(true); pose(.2f,.2f);
            Check(grab.IsHolding,"release must require continuous open samples after recovery");
            setTimer("openSince",9f); pose(.2f,.2f);
            Check(!grab.IsHolding,"continuous open confirmation must release");
            tracker.estimateForwardMotion=true;
            Tick(grab); // Apply the view-setting reset before arming a new grab.
            pose(.2f,.2f); pose(.2f,.02f);
            Check(grab.IsHolding,"forward estimation should allow grabbing");
            joints[0,4].gameObject.SetActive(false); Tick(grab);
            Check(grab.IsHolding,"forward estimation must respect tracking-loss grace period");
            setTimer("lostSince",0f); Tick(grab);
            Check(!grab.IsHolding,"forward estimation must release after tracking-loss timeout");
            SessionState.SetBool("ASJGrabVerifiedV3",true);
            Debug.Log("[ASJ Grab Test] PASS: acquire, no snap, relative move, release, outside-pinch rejection, rearm, tracking loss, continuous confirmation, range cancellation, external release.");
        }
        catch(Exception ex) { Debug.LogError("[ASJ Grab Test] FAIL: "+ex); }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
