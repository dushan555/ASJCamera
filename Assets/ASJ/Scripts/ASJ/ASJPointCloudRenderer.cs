using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace ASJ
{
    /// <summary>
    /// Optional raw point-cloud preview.
    /// Assumes the SDK pointCloud frame is packed float XYZ XYZ ...
    /// The vendor demo writes pointCloud.data as float data into PCD output.
    /// If your build returns another layout, disable this component and inspect raw values first.
    /// </summary>
    public sealed class ASJPointCloudRenderer : MonoBehaviour
    {
        [SerializeField] private ASJCamera cameraSource;
        [SerializeField] private Material pointMaterial;
        [Min(1)] [SerializeField] private int decimation = 4;
        [SerializeField] private float unitsToMeters = 0.001f;
        [SerializeField] private Vector3 axisSign = new Vector3(1, -1, 1);
        [Min(0.05f)] [SerializeField] private float updateInterval = 0.1f;

        private IntPtr _nativeBuffer = IntPtr.Zero;
        private int _nativeFloatCapacity;
        private float[] _xyz;
        private Mesh _mesh;
        private uint _lastFrameId = uint.MaxValue;
        private float _nextUpdate;

        private void Update()
        {
            if (cameraSource == null || pointMaterial == null || Time.unscaledTime < _nextUpdate)
                return;

            _nextUpdate = Time.unscaledTime + updateInterval;

            if (!cameraSource.TryGetPointCloudInfo(
                    out _, out _, out int floatCount, out uint frameId))
                return;

            if (frameId == _lastFrameId || floatCount < 3 || (floatCount % 3) != 0)
                return;

            EnsureBuffer(floatCount);
            int copied = cameraSource.CopyPointCloud(_nativeBuffer, _nativeFloatCapacity);
            if (copied != floatCount)
                return;

            if (_xyz == null || _xyz.Length != floatCount)
                _xyz = new float[floatCount];

            Marshal.Copy(_nativeBuffer, _xyz, 0, floatCount);

            int sourcePointCount = floatCount / 3;
            int step = Mathf.Max(1, decimation);
            int pointCount = (sourcePointCount + step - 1) / step;

            var vertices = new Vector3[pointCount];
            var indices = new int[pointCount];

            int dst = 0;
            for (int p = 0; p < sourcePointCount; p += step)
            {
                int i = p * 3;
                Vector3 v = new Vector3(_xyz[i], _xyz[i + 1], _xyz[i + 2]);
                v = Vector3.Scale(v, axisSign) * unitsToMeters;
                vertices[dst] = v;
                indices[dst] = dst;
                dst++;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "ASJ HP60C Point Cloud" };
                _mesh.indexFormat = IndexFormat.UInt32;
            }
            else
            {
                _mesh.Clear();
            }

            _mesh.vertices = vertices;
            _mesh.SetIndices(indices, MeshTopology.Points, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100f);
            _lastFrameId = frameId;
        }

        private void OnRenderObject()
        {
            if (_mesh == null || pointMaterial == null)
                return;

            pointMaterial.SetPass(0);
            Graphics.DrawMeshNow(_mesh, transform.localToWorldMatrix);
        }

        private void EnsureBuffer(int floatCount)
        {
            if (_nativeBuffer != IntPtr.Zero && _nativeFloatCapacity >= floatCount)
                return;

            if (_nativeBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_nativeBuffer);

            _nativeBuffer = Marshal.AllocHGlobal(floatCount * sizeof(float));
            _nativeFloatCapacity = floatCount;
        }

        private void OnDestroy()
        {
            if (_nativeBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_nativeBuffer);
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
