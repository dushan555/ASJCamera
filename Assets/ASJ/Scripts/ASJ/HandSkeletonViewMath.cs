using UnityEngine;

namespace ASJ
{
    public static class HandSkeletonViewMath
    {
        // Rotate the entire position around the view origin's vertical axis.
        // Pose and translation both reverse X/Z; Y stays upright.
        public static Vector3 BackView(Vector3 joint)
        {
            return new Vector3(-joint.x, joint.y, -joint.z);
        }

        public static float RelativeForward(float referencePalmSize, float palmSize, float sensitivity, float limit)
        {
            if (referencePalmSize <= 0 || palmSize <= .0001f) return 0;
            float offset = sensitivity * (referencePalmSize / palmSize - 1);
            return Mathf.Clamp(offset, -Mathf.Abs(limit), Mathf.Abs(limit));
        }
    }
}
