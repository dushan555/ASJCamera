using ASJ;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HandCameraInputSetup
{
    [MenuItem("ASJ/Hand Input/Use Ordinary Webcam")]
    public static void UseWebcam() { Configure(true); }

    [MenuItem("ASJ/Hand Input/Use ASJ Camera")]
    public static void UseASJ() { Configure(false); }

    static void Configure(bool useWebcam)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Stop Play mode before switching the hand camera input.");
            return;
        }
        var tracker = Object.FindObjectOfType<ASJHandJointTracker>(true);
        if (!tracker || !tracker.rgbView)
        {
            Debug.LogError("Open the configured hand tracking scene first (ASJ.unity).");
            return;
        }
        var asj = tracker.cameraSource;
        if (!asj) asj = Object.FindObjectOfType<ASJCamera>(true);
        if (!useWebcam && !asj)
        {
            Debug.LogError("No ASJCamera found in the scene.");
            return;
        }
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Switch hand camera input");
        Undo.RecordObject(tracker, "Switch hand camera input");
        var webcam = tracker.webcamSource;
        if (useWebcam && !webcam) webcam = Undo.AddComponent<WebcamRgbSource>(tracker.gameObject);
        tracker.webcamSource = webcam;
        tracker.cameraSource = asj;
        tracker.inputSource = useWebcam ? ASJHandJointTracker.InputSource.Webcam : ASJHandJointTracker.InputSource.ASJ;
        if (webcam) { Undo.RecordObject(webcam, "Toggle webcam"); webcam.enabled = useWebcam; }
        if (asj) { Undo.RecordObject(asj, "Toggle ASJ camera"); asj.enabled = !useWebcam; }
        foreach (var preview in Object.FindObjectsOfType<ASJCameraPreview>(true))
        {
            var data = new SerializedObject(preview);
            if (data.FindProperty("rgbView").objectReferenceValue != tracker.rgbView) continue;
            Undo.RecordObject(preview, "Toggle ASJ preview");
            preview.enabled = !useWebcam;
            // var depth = data.FindProperty("depthView").objectReferenceValue as UnityEngine.UI.RawImage;
            // if (depth) { Undo.RecordObject(depth.gameObject, "Toggle depth preview"); depth.gameObject.SetActive(!useWebcam); }
        }
        Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(tracker);
        EditorSceneManager.MarkSceneDirty(tracker.gameObject.scene);
        Selection.activeGameObject = webcam && useWebcam ? webcam.gameObject : tracker.gameObject;
        Debug.Log(useWebcam
            ? "Ordinary webcam configured. Set Device Name / Device Index on WebcamRgbSource, save the scene, then Play."
            : "ASJ camera input restored. Save the scene, then Play.");
    }
}
