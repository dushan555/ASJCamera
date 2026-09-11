using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class LineManager : MonoBehaviour
{
    public Transform targetTrans;
    private LineRenderer lineRenderer;

    public float lineWidth = 0.1f;
    public float lineLength = 100f;

    public bool IsRaycast;
    
    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        lineRenderer.startWidth = lineWidth;
        lineRenderer.endWidth = lineWidth;
        SetLineColor(false);
    }
    

    private void LateUpdate()
    {
        RefreshLineDir();
        UpdateState();
    }

    private void SetLineColor(bool isEnter)
    {
        lineRenderer.startColor = isEnter ? Color.green : Color.red;
        lineRenderer.endColor = isEnter ? Color.green : Color.red;
    }

    private void RefreshLineDir()
    {
        lineRenderer.SetPosition(0, targetTrans.position);
        lineRenderer.SetPosition(1, targetTrans.position + targetTrans.forward * lineLength);
    }

    private void UpdateState()
    {
        BoolHit(targetTrans.position, targetTrans.forward, lineLength);
        SetLineColor(IsRaycast);
    }

    private void BoolHit(Vector3 origin, Vector3 direction, float distance)
    {
        Debug.DrawRay(origin, direction * distance, Color.red);
        IsRaycast = Physics.Raycast(origin, direction, out _, distance, LayerMask.GetMask("Default"));
    }
    
}
