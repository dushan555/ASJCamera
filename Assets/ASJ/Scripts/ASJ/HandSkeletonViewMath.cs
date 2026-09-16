using UnityEngine;

namespace ASJ
{
    public static class HandSkeletonViewMath
    {
        // Compare matching projected palm edges. Their foreshortening cancels when
        // image and world landmarks describe the same pose (weak perspective).
        public static bool TryPalmScale(Vector3 wrist, Vector3 index, Vector3 pinky,
            Vector3 worldWrist, Vector3 worldIndex, Vector3 worldPinky, out float scale)
        {
            float imageSize = ProjectedSquare(index-wrist) + ProjectedSquare(pinky-wrist)
                + ProjectedSquare(pinky-index);
            float worldSize = ProjectedSquare(worldIndex-worldWrist) + ProjectedSquare(worldPinky-worldWrist)
                + ProjectedSquare(worldPinky-worldIndex);
            scale = 0;
            if (worldSize < 1e-6f || imageSize < 1e-8f) return false;
            scale = Mathf.Sqrt(imageSize / worldSize);
            return scale > 0 && !float.IsNaN(scale) && !float.IsInfinity(scale);
        }

        static float ProjectedSquare(Vector3 v) { return v.x*v.x + v.y*v.y; }

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
