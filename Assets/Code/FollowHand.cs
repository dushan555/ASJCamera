using UnityEngine;

public class FollowHand : MonoBehaviour
{
    public static FollowHand Instance;
    
    //private bool started;
    //public bool isTracked;

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {

    }

    private void StartTracking()
    {
        //started = true;
    }

    private void OnDisable()
    {
        
    }

    private void LateUpdate()
    {
        //isTracked = false;

    }
}
