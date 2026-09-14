using System;
using UnityEngine;

namespace ASJ
{
    /// <summary>Ordinary webcam input, normalized to top-down RGB24 like the ASJ input.</summary>
    public sealed class WebcamRgbSource : MonoBehaviour
    {
        [Tooltip("Exact WebCamTexture device name. Empty selects deviceIndex.")]
        public string deviceName = "";
        [Min(0)] public int deviceIndex;
        [Min(16)] public int requestedWidth = 640;
        [Min(16)] public int requestedHeight = 480;
        [Range(1, 60)] public int requestedFps = 30;
        [Min(1)] public float frameTimeout = 5f;

        public Texture2D RgbTexture { get; private set; }
        public string Status { get; private set; } = "Stopped";
        public long FrameVersion { get; private set; }
        public bool IsInitialized { get; private set; }
        /// <summary>Raised once per camera start, after the first valid RGB frame is available.</summary>
        public event Action OnInitialized;
        public bool IsStreaming => isActiveAndEnabled && webcam != null && webcam.isPlaying
            && RgbTexture != null && Time.realtimeSinceStartup - lastFrameTime < frameTimeout;

        WebCamTexture webcam;
        Color32[] pixels;
        byte[] rgb;
        float lastFrameTime;

        void OnEnable() { StartCamera(); }

        [ContextMenu("Log Available Cameras")]
        public void LogAvailableCameras()
        {
            var devices = WebCamTexture.devices;
            for (int i = 0; i < devices.Length; i++) Debug.Log($"Webcam [{i}]: {devices[i].name}", this);
            if (devices.Length == 0) Debug.LogWarning("No webcams detected.", this);
        }

        [ContextMenu("Restart Camera")]
        public void StartCamera()
        {
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            StopCamera();
            try
            {
                var devices = WebCamTexture.devices;
                int selected = string.IsNullOrEmpty(deviceName) ? deviceIndex
                    : Array.FindIndex(devices, d => d.name == deviceName);
                if (selected < 0 || selected >= devices.Length)
                {
                    Status = "Camera not found. Check Device Name / Device Index.";
                    Debug.LogWarning(Status, this);
                    return;
                }
                webcam = new WebCamTexture(devices[selected].name,
                    Mathf.Max(16, requestedWidth), Mathf.Max(16, requestedHeight), Mathf.Max(1, requestedFps));
                webcam.Play();
                lastFrameTime = Time.realtimeSinceStartup;
                Status = "Waiting for webcam frames: " + devices[selected].name;
            }
            catch (Exception ex)
            {
                StopCamera();
                Status = "Webcam start failed: " + ex.Message;
                Debug.LogError(Status, this);
            }
        }

        void Update()
        {
            if (webcam == null) return;
            if (!webcam.didUpdateThisFrame || webcam.width <= 16 || webcam.height <= 16)
            {
                if (Time.realtimeSinceStartup - lastFrameTime >= frameTimeout)
                    Status = "No webcam frames. Check camera permissions / device connection, then Restart Camera.";
                return;
            }
            try
            {
                int w = webcam.width, h = webcam.height;
                if (pixels == null || pixels.Length != w * h) pixels = new Color32[w * h];
                webcam.GetPixels32(pixels);
                int angle = ((webcam.videoRotationAngle % 360) + 360) % 360;
                bool rotated = angle == 90 || angle == 270;
                int outW = rotated ? h : w, outH = rotated ? w : h;
                if (RgbTexture == null || RgbTexture.width != outW || RgbTexture.height != outH)
                {
                    if (RgbTexture) Destroy(RgbTexture);
                    RgbTexture = new Texture2D(outW, outH, TextureFormat.RGB24, false)
                    { name = "Webcam RGB24", wrapMode = TextureWrapMode.Clamp };
                    rgb = new byte[outW * outH * 3];
                }
                // GetPixels32 starts at the bottom-left; MediaPipe expects top-down rows.
                bool flipped = webcam.videoVerticallyMirrored;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 p = pixels[(flipped ? y : h - 1 - y) * w + x];
                    int dx = x, dy = y;
                    switch (angle)
                    {
                        case 90: dx = h - 1 - y; dy = x; break;
                        case 180: dx = w - 1 - x; dy = h - 1 - y; break;
                        case 270: dx = y; dy = w - 1 - x; break;
                    }
                    int offset = (dy * outW + dx) * 3;
                    rgb[offset] = p.r; rgb[offset + 1] = p.g; rgb[offset + 2] = p.b;
                }
                RgbTexture.LoadRawTextureData(rgb);
                RgbTexture.Apply(false, false);
                lastFrameTime = Time.realtimeSinceStartup;
                FrameVersion++;
                Status = $"Webcam: {webcam.deviceName} ({outW} x {outH})";
            }
            catch (Exception ex)
            {
                StopCamera();
                Status = "Webcam capture failed: " + ex.Message;
                Debug.LogError(Status, this);
                return;
            }
            if (!IsInitialized)
            {
                IsInitialized = true;
                OnInitialized?.Invoke();
            }
        }

        void OnDisable() { StopCamera(); }

        void StopCamera()
        {
            IsInitialized = false;
            if (webcam != null) { webcam.Stop(); Destroy(webcam); webcam = null; }
            if (RgbTexture != null) { Destroy(RgbTexture); RgbTexture = null; }
            pixels = null; rgb = null;
            Status = "Stopped";
        }
    }
}
