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

        public static Vector3 MapPose(Vector3 joint, float forward, bool backView, bool invertForward)
        {
            Vector3 result = backView ? BackView(joint) : joint;
            float direction = backView ? -1f : 1f;
            if (invertForward) direction = -direction;
            result.z += direction * forward;
            return result;
        }

        public static float RelativeForward(float referencePalmSize, float palmSize, float sensitivity, float limit)
        {
            if (referencePalmSize <= 0 || palmSize <= .0001f) return 0;
            float offset = sensitivity * (referencePalmSize / palmSize - 1);
            return Mathf.Clamp(offset, -Mathf.Abs(limit), Mathf.Abs(limit));
        }
    }
}
