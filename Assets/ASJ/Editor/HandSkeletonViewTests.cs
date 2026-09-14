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
        Check((HandSkeletonViewMath.BackView(thumb + new Vector3(0, 0, forward)) - mapped).z > 0,
            "Estimated forward motion must also reverse in back view");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .3f, .6f, .8f) < 0, "Approaching hand must move toward viewer");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .1f, .6f, .8f) > 0, "Receding hand must move away");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .2f, .6f, .8f) == 0, "Reference pose must have zero offset");
        Check(HandSkeletonViewMath.RelativeForward(.2f, .001f, .6f, .8f) <= .8f, "Forward travel limit exceeded");
        return "PASS: whole-position X/Z reversal, upright Y, all six movement directions, unchanged pinch/bone distances, reversed estimated forward motion and limit.";
    }
}
