using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ASJ
{
    /// <summary>
    /// HP60C / NOVATEK ASJ ZNX_NVT Unity receiver.
    /// Add one instance to a persistent GameObject.
    /// </summary>
    public sealed class ASJCamera : MonoBehaviour
    {
        [Header("HP60C")]
        [SerializeField] private string configRelativePath =
            "ASJ/hp60c_v2_00_20230704_configEncrypt.json";
        [SerializeField] private int width = 640;
        [SerializeField] private int height = 480;
        [SerializeField] private int fps = 20;

        [Header("Textures")]
        [SerializeField] private bool updateRgbTexture = true;
        [SerializeField] private bool updateDepthTexture = true;

        public Texture2D RgbTexture { get; private set; }
        public Texture2D DepthTexture { get; private set; }
        public bool DepthIsFloat { get; private set; }

        public bool IsStreaming { get; private set; }
        public bool IsInitialized { get; private set; }
        public event Action OnInitialized;
        public int CameraModel { get; private set; }
        public int LastVendorCode { get; private set; }
        public string LastError { get; private set; } = string.Empty;
        public string SdkVersion { get; private set; } = string.Empty;
        public string SerialNumber { get; private set; } = string.Empty;

        private IntPtr _rgbBuffer = IntPtr.Zero;
        private int _rgbBufferCapacity;

        private IntPtr _depthBuffer = IntPtr.Zero;
        private int _depthBufferCapacity;

        private uint _lastRgbFrameId = uint.MaxValue;
        private uint _lastDepthFrameId = uint.MaxValue;

        private Thread _sdkThread;
        private readonly ManualResetEvent _stopSdk = new ManualResetEvent(false);
        private volatile bool _initFinished;
        private int _initResult;
        private Exception _initException;
        private bool _reportedInit;
        private string _reportedError;

        private void Start()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string configPath = Path.Combine(Application.streamingAssetsPath, configRelativePath);
            if (!File.Exists(configPath))
            {
                Debug.LogError($"[ASJ] HP60C config file not found: {configPath}");
                enabled = false;
                return;
            }

            // Keep vendor COM initialization and shutdown off Unity's main thread.
            _sdkThread = new Thread(() =>
            {
                try
                {
                    _initResult = ASJNative.ASJ_Init(configPath, width, height, fps);
                }
                catch (Exception ex) { _initException = ex; }
                finally { _initFinished = true; }
                _stopSdk.WaitOne();
                if (_initException == null) ASJNative.ASJ_Shutdown();
            });
            _sdkThread.Name = "ASJ Camera SDK";
            _sdkThread.IsBackground = true;
            _sdkThread.SetApartmentState(ApartmentState.MTA);
            _sdkThread.Start();
#else
            Debug.LogError("[ASJ] This bridge currently supports Windows x64 only.");
            enabled = false;
#endif
        }

        private void Update()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (!_initFinished) return;
            if (!_reportedInit)
            {
                _reportedInit = true;
                if (_initException != null || _initResult != 0)
                {
                    LastError = _initException != null ? _initException.Message : GetLastError();
                    Debug.LogError($"[ASJ] Initialization failed: {_initResult}, {LastError}");
                    enabled = false;
                    return;
                }
                SdkVersion = ASJNative.ReadAnsi(ASJNative.ASJ_GetSdkVersion);
                Debug.Log($"[ASJ] SDK ready: {SdkVersion}; waiting for camera frames.");
                IsInitialized = true;
                OnInitialized?.Invoke();
            }
            if (ASJNative.ASJ_GetStatus(out var status) == 0)
            {
                IsStreaming = status.streaming != 0;
                CameraModel = status.cameraModel;
                LastVendorCode = status.lastVendorCode;
            }

            if (string.IsNullOrEmpty(SerialNumber) && IsStreaming)
                SerialNumber = ASJNative.ReadAnsi(ASJNative.ASJ_GetSerialNumber);

            if (updateRgbTexture)
                UpdateRgb();

            if (updateDepthTexture)
                UpdateDepth();

            if (LastVendorCode != 0)
            {
                LastError = GetLastError();
                if (_reportedError != LastError)
                {
                    _reportedError = LastError;
                    Debug.LogError($"[ASJ] Camera error {LastVendorCode}: {LastError}");
                }
            }
#endif
        }

        private void UpdateRgb()
        {
            int hasFrame = ASJNative.ASJ_GetRgbInfo(out var info);
            if (hasFrame <= 0 || info.bytes <= 0 || info.frameId == _lastRgbFrameId)
                return;

            int required = info.width * info.height * 3;
            EnsureUnmanagedBuffer(ref _rgbBuffer, ref _rgbBufferCapacity, required);

            int copied = ASJNative.ASJ_CopyRgb24(_rgbBuffer, _rgbBufferCapacity);
            if (copied != required)
                return;

            if (RgbTexture == null ||
                RgbTexture.width != info.width ||
                RgbTexture.height != info.height)
            {
                if (RgbTexture != null)
                    Destroy(RgbTexture);

                RgbTexture = new Texture2D(
                    info.width, info.height, TextureFormat.RGB24, false, false)
                {
                    name = "ASJ HP60C RGB"
                };
            }

            RgbTexture.LoadRawTextureData(_rgbBuffer, copied);
            RgbTexture.Apply(false, false);
            _lastRgbFrameId = info.frameId;
        }

        private void UpdateDepth()
        {
            int hasFrame = ASJNative.ASJ_GetDepthInfo(out var info);
            if (hasFrame <= 0 || info.bytes <= 0 || info.frameId == _lastDepthFrameId)
                return;

            TextureFormat textureFormat;
            if (info.format == ASJNative.FormatDepthU16)
            {
                textureFormat = TextureFormat.R16;
                DepthIsFloat = false;
            }
            else if (info.format == ASJNative.FormatDepthF32)
            {
                textureFormat = TextureFormat.RFloat;
                DepthIsFloat = true;
            }
            else
            {
                // Unknown depth packing: keep raw access available, skip Texture2D upload.
                return;
            }

            EnsureUnmanagedBuffer(ref _depthBuffer, ref _depthBufferCapacity, info.bytes);
            int copied = ASJNative.ASJ_CopyDepth(_depthBuffer, _depthBufferCapacity);
            if (copied != info.bytes)
                return;

            bool recreate =
                DepthTexture == null ||
                DepthTexture.width != info.width ||
                DepthTexture.height != info.height ||
                (DepthIsFloat && DepthTexture.format != TextureFormat.RFloat) ||
                (!DepthIsFloat && DepthTexture.format != TextureFormat.R16);

            if (recreate)
            {
                if (DepthTexture != null)
                    Destroy(DepthTexture);

                DepthTexture = new Texture2D(
                    info.width, info.height, textureFormat, false, true)
                {
                    name = DepthIsFloat ? "ASJ HP60C Depth F32" : "ASJ HP60C Depth U16"
                };
            }

            DepthTexture.LoadRawTextureData(_depthBuffer, copied);
            DepthTexture.Apply(false, false);
            _lastDepthFrameId = info.frameId;
        }

        public bool TryGetPointCloudInfo(
            out int width, out int height, out int floatCount, out uint frameId)
        {
            width = height = floatCount = 0;
            frameId = 0;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            int ret = ASJNative.ASJ_GetPointCloudInfo(out var info);
            if (ret <= 0 || info.bytes <= 0 || (info.bytes % sizeof(float)) != 0)
                return false;

            width = info.width;
            height = info.height;
            floatCount = info.bytes / sizeof(float);
            frameId = info.frameId;
            return true;
#else
            return false;
#endif
        }

        public int CopyPointCloud(IntPtr destination, int floatCapacity)
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return ASJNative.ASJ_CopyPointCloud(destination, floatCapacity);
#else
            return 0;
#endif
        }

        public string GetLastError()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return ASJNative.ReadAnsi(ASJNative.ASJ_GetLastErrorMessage);
#else
            return "Windows x64 only.";
#endif
        }

        private static void EnsureUnmanagedBuffer(
            ref IntPtr pointer, ref int capacity, int required)
        {
            if (pointer != IntPtr.Zero && capacity >= required)
                return;

            if (pointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pointer);

            pointer = Marshal.AllocHGlobal(required);
            capacity = required;
        }

        private void OnDestroy()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            _stopSdk.Set();
            if (_sdkThread != null) _sdkThread.Join();
            _stopSdk.Dispose();
#endif
            if (_rgbBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_rgbBuffer);
            if (_depthBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_depthBuffer);

            _rgbBuffer = IntPtr.Zero;
            _depthBuffer = IntPtr.Zero;

            if (RgbTexture != null)
                Destroy(RgbTexture);
            if (DepthTexture != null)
                Destroy(DepthTexture);
        }
    }
}

