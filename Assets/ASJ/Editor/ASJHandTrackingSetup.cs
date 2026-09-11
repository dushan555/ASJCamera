using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ASJHandTrackingSetup
{
    [MenuItem("ASJ/Setup Hand Joint Spheres")]
    public static void Setup()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var source=Object.FindObjectOfType<ASJ.ASJCamera>();
        var preview=Object.FindObjectOfType<ASJ.ASJCameraPreview>();
        if(!source || !preview) { Debug.LogError("Open the ASJ camera scene first."); return; }
        var previewData=new SerializedObject(preview);
        var rgb=previewData.FindProperty("rgbView").objectReferenceValue as RawImage;
        if(!rgb) { Debug.LogError("Assign the RGB RawImage on ASJCameraPreview first."); return; }
        Undo.RecordObject(rgb.gameObject,"Show RGB hand tracking");
        rgb.gameObject.SetActive(true);
        var depth=previewData.FindProperty("depthView").objectReferenceValue as RawImage;
        foreach(var view in new[] {rgb,depth}) if(view)
        {
            Undo.RecordObject(view.rectTransform,"Layout hand tracking preview");
            view.rectTransform.anchorMin=view.rectTransform.anchorMax=new Vector2(.5f,.5f);
            view.rectTransform.sizeDelta=new Vector2(460,345);
            view.rectTransform.anchoredPosition=new Vector2(view==rgb?-240:240,0);
        }
        var tracker=Object.FindObjectOfType<ASJ.ASJHandJointTracker>();
        if(!tracker) tracker=Undo.AddComponent<ASJ.ASJHandJointTracker>(source.gameObject);
        Undo.RecordObject(tracker,"Configure ASJ joint tracking");
        tracker.cameraSource=source; tracker.rgbView=rgb;
        tracker.sphereShader=Shader.Find("ASJ/JointSphere");
        var cameraData=new SerializedObject(source); cameraData.FindProperty("updateRgbTexture").boolValue=true; cameraData.ApplyModifiedProperties();
        EditorUtility.SetDirty(tracker);
        EditorSceneManager.MarkSceneDirty(source.gameObject.scene);
        EditorSceneManager.SaveScene(source.gameObject.scene);
        Debug.Log("[ASJ Hand] Setup saved. Press Play for 21 Sphere joints per detected hand.");
    }
}
