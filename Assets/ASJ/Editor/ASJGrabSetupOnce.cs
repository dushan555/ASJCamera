using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class ASJGrabSetupOnce
{
    static ASJGrabSetupOnce() { EditorApplication.delayCall += Setup; }
    static void Setup()
    {
        if(EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool("ASJGrabSetupDone",false)) return;
        var scene=EditorSceneManager.GetActiveScene();
        if(scene.path!="Assets/Scenes/ASJ.unity") return;
        var target=GameObject.Find("target");
        var tracker=Object.FindObjectOfType<ASJ.ASJHandJointTracker>();
        if(!target || !tracker) return;
        var grab=target.GetComponent<ASJ.ASJPinchGrab>();
        if(!grab) grab=Undo.AddComponent<ASJ.ASJPinchGrab>(target);
        Undo.RecordObject(grab,"Configure hand grab"); grab.target=target.transform; grab.tracker=tracker;
        foreach(var child in target.GetComponentsInChildren<Transform>(true))
        { Undo.RecordObject(child.gameObject,"Show target in hand overlay"); child.gameObject.layer=31; }
        EditorUtility.SetDirty(grab);
        EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
        SessionState.SetBool("ASJGrabSetupDone",true);
        Debug.Log("[ASJ Grab] target configured. Open fingers, approach target, then pinch.");
    }
}
