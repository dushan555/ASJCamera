using UnityEngine;
using UnityEngine.UI;

namespace ASJ
{
    public sealed class ASJCameraPreview : MonoBehaviour
    {
        [SerializeField] private ASJCamera cameraSource;
        [SerializeField] private RawImage rgbView;
        [SerializeField] private RawImage depthView;
        [SerializeField] private bool flipY = true;

        [Tooltip("Depth value -> millimeters multiplier. Usually 1 for HP60C raw U16.")]
        [SerializeField] private float depthUnitToMillimeters = 1f;

        [SerializeField] private float maxDepthMillimeters = 4000f;

        private Material _depthMaterial;

        private void Start()
        {
            if (depthView != null)
            {
                Shader shader = Shader.Find("ASJ/DepthPreview");
                if (shader != null)
                {
                    _depthMaterial = new Material(shader);
                    depthView.material = _depthMaterial;
                }
            }

            Rect uv = flipY ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);
            if (rgbView != null) rgbView.uvRect = uv;
            if (depthView != null) depthView.uvRect = uv;
        }

        private void LateUpdate()
        {
            if (cameraSource == null)
                return;

            if (rgbView != null && cameraSource.RgbTexture != null)
                rgbView.texture = cameraSource.RgbTexture;

            if (depthView != null && cameraSource.DepthTexture != null)
            {
                depthView.texture = cameraSource.DepthTexture;

                if (_depthMaterial != null)
                {
                    _depthMaterial.SetFloat(
                        "_EncodedScale", cameraSource.DepthIsFloat ? 1f : 65535f);
                    _depthMaterial.SetFloat(
                        "_DepthUnitToMillimeters", depthUnitToMillimeters);
                    _depthMaterial.SetFloat(
                        "_MaxDepthMillimeters", Mathf.Max(1f, maxDepthMillimeters));
                }
            }
        }

        private void OnDestroy()
        {
            if (_depthMaterial != null)
                Destroy(_depthMaterial);
        }
    }
}
