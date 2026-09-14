using ASJ;
using UnityEngine;

public class HandTrakerState : MonoBehaviour
{
    public static HandTrakerState Instance;
    
    public UnityEngine.UI.Image tipImage;
    [SerializeField] private ASJCamera cameraSource;
    [SerializeField] private WebcamRgbSource webcamSource;
    [SerializeField] private ASJHandJointTracker handTracker;

    private void Awake()
    {
        Instance = this;
        
        SetState(TrackType.Error);
        
    }

    private void OnEnable()
    {
        if (!handTracker) handTracker = FindObjectOfType<ASJHandJointTracker>();
        if (!cameraSource && handTracker) cameraSource = handTracker.cameraSource;
        if (!cameraSource) cameraSource = FindObjectOfType<ASJCamera>();
        if (handTracker && handTracker.webcamSource) webcamSource = handTracker.webcamSource;
        if (!webcamSource) webcamSource = FindObjectOfType<WebcamRgbSource>();

        if (cameraSource) cameraSource.OnInitialized += RefreshState;
        if (webcamSource) webcamSource.OnInitialized += RefreshState;
        if (handTracker) handTracker.OnHandsUpdated += OnHandsUpdated;
        RefreshState();
    }

    private void OnDisable()
    {
        if (cameraSource) cameraSource.OnInitialized -= RefreshState;
        if (webcamSource) webcamSource.OnInitialized -= RefreshState;
        if (handTracker) handTracker.OnHandsUpdated -= OnHandsUpdated;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // Also reflect camera/tracker shutdown, which does not emit a hand result.
        RefreshState();
    }

    private void OnHandsUpdated(ASJHandJointTracker.Result result)
    {
        RefreshState();
    }

    private void RefreshState()
    {
        bool useWebcam = handTracker && handTracker.inputSource == ASJHandJointTracker.InputSource.Webcam;
        bool initialized = useWebcam
            ? webcamSource && webcamSource.isActiveAndEnabled && webcamSource.IsInitialized
            : cameraSource && cameraSource.isActiveAndEnabled && cameraSource.IsInitialized;
        if (!initialized)
        {
            SetState(TrackType.Error);
            return;
        }

        bool streaming = useWebcam ? webcamSource.IsStreaming : cameraSource.IsStreaming;
        bool isTracking = streaming && handTracker &&
            handTracker.isActiveAndEnabled && handTracker.TrackedHandCount > 0;
        SetState(isTracking ? TrackType.Playing : TrackType.Waiting);
    }

    private void SetState(TrackType type)
    {
        if (!tipImage) return;

        switch (type)
        {
            case TrackType.Error:
                tipImage.color = Color.red;
                break;
            case TrackType.Waiting:
                tipImage.color = Color.yellow;
                break;
            case TrackType.Playing:
                tipImage.color = Color.green;
                break;
        }
        
    }
}

public enum TrackType
{
    Error,
    Waiting,
    Playing
}
