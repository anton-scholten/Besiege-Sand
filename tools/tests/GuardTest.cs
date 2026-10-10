using System;
using UnityEngine;
using SandMod;

/// <summary>
/// The guard on numbers that came from outside.
///
/// This is the only suite that needs no bed, no ground and no engine behaviour: it is four
/// functions, and what it has to hold down is that <c>Mathf.Clamp</c> and <c>Mathf.RoundToInt</c>
/// pass a NaN straight through, so every entry point that takes numbers from outside -- a brush
/// message from another player, a typed value in the panel, a settings file -- has to ask first.
/// Without it, one NaN into a column's height makes the bed's extent NaN and the rest of the
/// session has no ground under it.
/// </summary>
class Guard
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    public static int Main(string[] a)
    {
        float nan = float.NaN;
        float inf = float.PositiveInfinity;

        // The arithmetic this exists for, shown to be the problem it is here to solve.
        Check(SandGuard.Ok(nan) == false && SandGuard.Ok(inf) == false
              && SandGuard.Ok(-inf) == false && SandGuard.Ok(0f) == true && SandGuard.Ok(-1e30f) == true,
              "only NaN and the infinities are refused; every real number, however large, is not");
        Check(float.IsNaN(Mathf.Clamp(nan, 0.1f, 10f)) && float.IsNaN(Mathf.Clamp(nan, 0f, 0f)),
              "which is not something a clamp does: Mathf.Clamp hands a NaN straight back, and a "
              + "range whose ends are the same does not catch it either");

        // Clamping.
        Check(SandGuard.Clamp(nan, 0.1f, 10f, 10f) == 10f && SandGuard.Clamp(inf, 0.1f, 10f, 10f) == 10f
              && SandGuard.Clamp(-inf, 0.1f, 10f, 10f) == 10f,
              "a number that is not one clamps to the fallback, not through the range");
        Check(SandGuard.Clamp(5f, 0.1f, 10f, 10f) == 5f && SandGuard.Clamp(-3f, 0.1f, 10f, 10f) == 0.1f
              && SandGuard.Clamp(99f, 0.1f, 10f, 10f) == 10f,
              "and a real one clamps to the range exactly as it did before");
        Check(SandGuard.Clamp(0f, 0f, 0f, 7f) == 0f && SandGuard.Clamp(0f, -1f, 1f, 7f) == 0f,
              "including at and across zero, where a sign test would have let it through");

        // Rounding to whole numbers.
        Check(SandGuard.Whole(nan, 0, 6, 3) == 3 && SandGuard.Whole(inf, 0, 6, 3) == 3,
              "a number that is not one rounds to the fallback");
        Check(SandGuard.Whole(2.4f, 0, 6, 3) == 2 && SandGuard.Whole(2.6f, 0, 6, 3) == 3
              && SandGuard.Whole(-0.5f, 0, 6, 3) == 0 && SandGuard.Whole(-7f, 0, 6, 3) == 0
              && SandGuard.Whole(9f, 0, 6, 3) == 6,
              "and a real one rounds and clamps as it did before");
        // Past 2^24 a float holds no integers at all: RoundToInt gives the largest int there is,
        // which is then clamped, so a range is what stops it either way.
        Check(SandGuard.Whole(1e9f, 0, 1 << 24, 0) == (1 << 24),
              "a whole number past 2^24, where a float has no integers left in it, clamps to the range");

        // What the callers get, spelled out as the values they use.
        Check(SandGuard.Ok(1.5f, -1f) == 1.5f && SandGuard.Ok(nan, -1f) == -1f && SandGuard.Ok(inf, -1f) == -1f,
              "the plain form takes the number or the fallback, and nothing else");

        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}