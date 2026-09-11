using System;
using System.Runtime.InteropServices;

namespace ASJ
{
    internal static class ASJNative
    {
        internal const int FormatUnknown = 0;
        internal const int FormatRgb24 = 1;
        internal const int FormatDepthU16 = 2;
        internal const int FormatDepthF32 = 3;
        internal const int FormatFloat32 = 4;

        [StructLayout(LayoutKind.Sequential)]
        internal struct FrameInfo
        {
            public int width;
            public int height;
            public int bytes;
            public uint frameId;
            public ulong timestamp;
            public int format;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Status
        {
            public int sdkInitialized;
            public int deviceAttached;
            public int cameraOpened;
            public int streaming;
            public int cameraModel;
            public int lastVendorCode;
        }

        private const string Dll = "ASJUnityBridge";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ASJ_Init(string configFilePath, int width, int height, int fps);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_Shutdown();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetStatus(out Status status);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ASJ_GetSdkVersion(IntPtr buffer, int capacity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int ASJ_GetSerialNumber(IntPtr buffer, int capacity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetRgbInfo(out FrameInfo info);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_CopyRgb24(IntPtr destination, int capacityBytes);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetDepthInfo(out FrameInfo info);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_CopyDepth(IntPtr destination, int capacityBytes);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetPointCloudInfo(out FrameInfo info);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_CopyPointCloud(IntPtr destination, int floatCapacity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetLastErrorCode();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ASJ_GetLastErrorMessage(IntPtr buffer, int capacity);

        internal static string ReadAnsi(Func<IntPtr, int, int> getter, int capacity = 512)
        {
            IntPtr p = Marshal.AllocHGlobal(capacity);
            try
            {
                Marshal.WriteByte(p, 0);
                getter(p, capacity);
                return Marshal.PtrToStringAnsi(p) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }
}
