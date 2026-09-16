using System;
using ASJ;
using UnityEngine;

// Pure geometry checks; can run without a camera or Unity's native runtime.
public static class HandSkeletonViewTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Equal(Vector3 a, Vector3 b, string message) { Check((a - b).sqrMagnitude < 1e-9f, message); }
    public static string Run()
    {
        var center = new Vector3(.3f, -.2f, .7f);
        var thumb = center + new Vector3(-.2f, .1f, -.03f);
        var index = center + new Vector3(.1f, .3f, .02f);
        var mapped = HandSkeletonViewMath.BackView(thumb);
        Equal(HandSkeletonViewMath.BackView(center), new Vector3(-.3f, -.2f, -.7f), "Palm translation not rotated");
        Equal(HandSkeletonViewMath.BackView(mapped), thumb, "Back-view transform is not reversible");
        foreach (var move in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            Equal(HandSkeletonViewMath.BackView(thumb + move) - mapped, new Vector3(-move.x, move.y, -move.z),
                "Whole-hand translation direction incorrect: " + move);
        Check(Math.Abs((thumb - index).sqrMagnitude - (mapped - HandSkeletonViewMath.BackView(index)).sqrMagnitude) < 1e-7f,
            "Pinch/bone distance changed");
        Check(mapped.x == -thumb.x && mapped.z == -thumb.z && mapped.y == thumb.y, "Opposite-side pose incorrect");
        float forward = HandSkeletonViewMath.RelativeForward(.2f, .3f, .6f, .8f);
        foreach (bool back in new[] { false, true })
        {
            var origin = HandSkeletonViewMath.MapPose(thumb, 0, back, false);
            var normal = HandSkeletonViewMath.MapPose(thumb, forward, back, false);
            var inverted = HandSkeletonViewMath.MapPose(thumb, forward, back, true);
            Equal(normal - origin, -(inverted - origin), "Independent forward inversion failed");
            Check(normal.x == inverted.x && normal.y == inverted.y, "Forward inversion changed sideways/up movement");
            Equal(HandSkeletonViewMath.MapPose(thumb, 0, back, true), origin, "Forward inversion changed the hand pose");
        }
        Check((HandSkeletonViewMath.BackView(thumb + new Vector3(0, 0, forward)) - mapped).z > 0,
            "Estimated forward motion must also reverse in back view");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .3f, .6f, .8f) < 0, "Approaching hand must move toward viewer");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .1f, .6f, .8f) > 0, "Receding hand must move away");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .2f, .6f, .8f) == 0, "Reference pose must have zero offset");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .001f, .6f, .8f) <= .8f, "Forward travel limit exceeded");
        var wrist = Vector3.zero;
        var indexMcp = new Vector3(.03f,.07f,0);
        var pinkyMcp = new Vector3(-.03f,.05f,0);
        foreach (float angle in new[] { 0f, 45f, 80f })
        {
            float radians = angle * (float)Math.PI / 180f;
            var a = new Vector3(indexMcp.x*(float)Math.Cos(radians),indexMcp.y,indexMcp.x*(float)Math.Sin(radians));
            var b = new Vector3(pinkyMcp.x*(float)Math.Cos(radians),pinkyMcp.y,pinkyMcp.x*(float)Math.Sin(radians));
            foreach (float expectedScale in new[] { 2f, 4f })
            {
                float scale;
                Check(HandSkeletonViewMath.TryPalmScale(wrist,a*expectedScale,b*expectedScale,wrist,a,b,out scale),"Valid palm scale rejected");
                Check(Math.Abs(scale-expectedScale)<1e-5f,"Palm rotation changed depth scale");
            }
        }
        float invalidScale;
        Check(!HandSkeletonViewMath.TryPalmScale(wrist,indexMcp,pinkyMcp,wrist,wrist,wrist,out invalidScale),"Degenerate palm must not update depth");
        Check(!HandSkeletonViewMath.TryPalmScale(wrist,new Vector3(float.NaN,0,0),pinkyMcp,wrist,indexMcp,pinkyMcp,out invalidScale),"Invalid landmarks must not update depth");
        return "PASS: whole-position X/Z reversal, upright Y, all six movement directions, unchanged pinch/bone distances, reversed estimated forward motion and limit.";
    }
}
