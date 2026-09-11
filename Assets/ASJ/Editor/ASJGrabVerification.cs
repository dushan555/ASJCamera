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
    static void Run()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool("ASJGrabVerified",false)) return;
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
                Call(grab,"LateUpdate");
            };
            pose(.05f,.2f); pose(.05f,.02f);
            Check(grab.IsHolding,"near pinch should grab"); Check(target.transform.position.sqrMagnitude<1e-6,"grab must not snap target");
            pose(.25f,.02f); Check(Vector3.Distance(target.transform.position,new Vector3(.2f,0,0))<.001f,"target must follow relative hand movement");
            pose(.25f,.2f); Check(!grab.IsHolding,"open fingers must release");
            pose(2,.2f); pose(2,.02f); pose(.2f,.02f); Check(!grab.IsHolding,"closed hand entering target must not auto-grab");
            pose(.2f,.2f); pose(.2f,.02f); Check(grab.IsHolding,"reopening rearms grab");
            joints[0,4].gameObject.SetActive(false); Call(grab,"LateUpdate"); Check(!grab.IsHolding,"tracking loss must release");
            SessionState.SetBool("ASJGrabVerified",true);
            Debug.Log("[ASJ Grab Test] PASS: acquire, no snap, relative move, release, outside-pinch rejection, rearm, tracking-loss release.");
        }
        catch(Exception ex) { Debug.LogError("[ASJ Grab Test] FAIL: "+ex); }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
