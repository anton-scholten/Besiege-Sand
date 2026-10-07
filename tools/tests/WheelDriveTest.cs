using System;
using UnityEngine;
using SandMod;

/// <summary>
/// A wheel on the sand as it is in game, with the bed, the real solver and the bed's collider modelled: the
/// wheel stands on the highest sand under its tread, that height is three steps old (the collider is cooked
/// behind the sand), and it presses on the sand at every physics step. Spinning in place and driving, it has
/// to dig and throw a good deal of sand up into the air -- the thing a headless test that put the wheel back
/// on the sand every step said it did, while in game it did not.
/// </summary>
class DriveGround : ISandGround, IBedGround
{
    public bool Built { get { return true; } }
    public float Height(Vector3 p) { return 0f; }
    public float Height(float x, float z) { return 0f; }
    public Vector3 Normal(Vector3 p) { return Vector3.up; }
}

class DriveWheel : IBedShapes, IBedShapeMotion
{
    public Vector3 c, v, w; public float r;
    public int Count { get { return 1; } }
    public void Extent(int i, out Vector3 a, out Vector3 b) { Vector3 e = new Vector3(r, r, r); a = c - e; b = c + e; }
    public float Distance(int i, Vector3 p) { return (p - c).magnitude - r; }
    public void Union(out Vector3 a, out Vector3 b) { Extent(0, out a, out b); }
    public Vector3 SurfaceVelocity(int i, Vector3 p) { return v + Vector3.Cross(w, p - c); }
}

class DriveWorld : IBedKickSink
{
    public Grain[] grains; public int count, cap; public float thrown, up2; uint seed = 0x9E3779B9u;
    public static SandMaterials.Params[] Table = SandMaterials.Build();

    // A thrown grain counted as the ordinary grains of its medium it stands for: thrown grains are coarse ones.
    static float Worth(int m)
    {
        int b = SandMaterials.MediumOf(m);
        float r = Table[m].radius / Table[b].radius;
        return r * r * r;
    }
    public int Room { get { return cap - count; } }
    public void Release(Vector3 p, int m, uint col, Vector3 vel)
    {
        if (count >= cap) return;
        Grain g = new Grain(); g.pos = p; g.material = (uint)m; g.colour = col; g.vel = vel; g.prevPos = p - vel * 0.01f;
        g.life = 5400f; g.age = SandFormat.SettleSeconds + 0.1f; grains[count++] = g;
        float w = Worth(m); thrown += w; if (vel.y > 2f) up2 += w;
    }
    public void Release(Vector3 p, int m, uint col)
    {
        if (count >= cap) return;
        seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
        float a = (seed & 0xFFFF) / 65535f * Mathf.PI * 2f;
        Grain g = new Grain(); g.pos = p; g.material = (uint)m; g.colour = col;
        g.vel = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.5f; g.prevPos = p - g.vel * 0.01f;
        g.life = 5400f; g.age = SandFormat.SettleSeconds + 0.1f; grains[count++] = g;
    }
    public void Reap()
    {
        int i = 0;
        while (i < count) { if ((grains[i].material & SandFormat.FlagDead) != 0) { count--; grains[i] = grains[count]; } else i++; }
    }
}

class WheelDriveTest
{
    static int fails = 0;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    // Four seconds of a 0.3 m wheel at the spin and speed given, on 30 cm of a medium. Returns grains thrown,
    // grains thrown up faster than 2 m/s, and grain-steps spent more than 20 cm above the sand's first surface.
    static void Run(int medium, float spin, float speed, out float thrown, out float up2, out long airborne)
    {
        SandMaterials.Params[] table = SandMaterials.Build();
        DriveGround ground = new DriveGround();
        SandBed bed = new SandBed(); bed.Enabled = true; bed.SetGround(ground); bed.SetMaterials(table);
        float cell = SandBed.Cell;
        for (int i = Mathf.FloorToInt(-2f / cell); i < Mathf.FloorToInt(10f / cell); i++)
            for (int j = Mathf.FloorToInt(-2f / cell); j < Mathf.FloorToInt(2f / cell); j++)
                bed.AddVolume((i + 0.5f) * cell, (j + 0.5f) * cell, 0.3f * cell * cell, medium, 0xFFFFFFFFu);
        while (bed.Unsettled > 0) bed.Relax(100000);

        CpuSolver solver = new CpuSolver(); solver.Init(40000, table); solver.Ground = ground; solver.Bed = bed;
        DriveWorld world = new DriveWorld(); world.grains = solver.Grains; world.cap = 40000;
        DriveWheel wheel = new DriveWheel(); wheel.r = 0.3f; wheel.v = new Vector3(speed, 0f, 0f); wheel.w = new Vector3(0f, 0f, -spin);
        wheel.c = new Vector3(0f, 0.3f + wheel.r, 0f);
        Vector3 gravity = new Vector3(0f, -32.8f, 0f);
        float standing = wheel.c.y;
        airborne = 0;
        for (int s = 0; s < 100; s++)
        {
            world.Reap();
            if (s % 3 == 0)
            {
                float top = 0f;
                for (float dx = -0.15f; dx <= 0.151f; dx += 0.06f) for (float dz = -0.15f; dz <= 0.151f; dz += 0.06f)
                { float t; if (bed.TryTop(wheel.c.x + dx, wheel.c.z + dz, out t)) top = Mathf.Max(top, t - (dx * dx + dz * dz) / (2f * wheel.r)); }
                standing = top + wheel.r;
            }
            wheel.c.y = standing;
            for (int f = 0; f < 4; f++) { bed.AddLoad(new Vector3(wheel.c.x, standing - wheel.r, wheel.c.z), Vector3.up, 150f, 0f); bed.Press(0.01f); }
            bed.Absorb(world.grains, world.count, wheel, 12, 600f, 0.09f, 3000);
            bed.Release(wheel, world, 0.09f, 12000, 600);
            SandShape sh = new SandShape(); sh.centre = wheel.c; sh.radius = wheel.r; sh.rot = new Vector4(0, 0, 0, 1);
            sh.kindBlock = (uint)SandFormat.ShapeSphere; sh.movable = 1f; sh.pointVel = wheel.v;
            solver.ShapeSpin = new Vector3[] { wheel.w }; solver.BodyCentres = new Vector3[] { wheel.c };
            solver.SetShapes(new SandShape[] { sh }, 1, 1);
            if (world.count > 0)
            {
                Vector3 lo = world.grains[0].pos, hi = lo;
                for (int k = 1; k < world.count; k++) { lo = Vector3.Min(lo, world.grains[k].pos); hi = Vector3.Max(hi, world.grains[k].pos); }
                Bounds b = new Bounds((lo + hi) * 0.5f, hi - lo); b.Expand(1f); b.Encapsulate(wheel.c + Vector3.one); b.Encapsulate(wheel.c - Vector3.one);
                solver.Step(0.04f, 2, 3, gravity, null, b, world.count);
            }
            bed.Relax(300, 3f);
            for (int k = 0; k < world.count; k++) if (world.grains[k].pos.y > 0.5f) airborne++;
            wheel.c += wheel.v * 0.04f;
        }
        solver.Dispose();
        thrown = world.thrown; up2 = world.up2;
    }

    public static int Main(string[] args)
    {
        string[] names = { "sand", "wet sand", "dust", "snow", "gravel", "mud", "glue" };
        int[] media = { SandMaterials.Sand, SandMaterials.Gravel, SandMaterials.Dust, SandMaterials.Mud, SandMaterials.Glue };
        float[] inPlace = new float[SandMaterials.Count], inPlaceUp = new float[SandMaterials.Count];
        for (int n = 0; n < media.Length; n++)
        {
            int m = media[n]; float thrown, up2; long air;
            Run(m, 20f, 0f, out thrown, out up2, out air);
            inPlace[m] = thrown; inPlaceUp[m] = up2;
            Console.WriteLine("   " + names[m] + ", spinning in place at 20 rad/s: " + thrown + " grains' worth thrown, " + up2 + " upward faster than 2 m/s, " + air + " grain-steps over 20 cm");
        }
        {
            float thrown, up2; long air;
            Run(SandMaterials.Sand, 14f, 2f, out thrown, out up2, out air);
            Console.WriteLine("   sand, spinning at 14 rad/s driving at 2 m/s: " + thrown + " grains' worth thrown, " + up2 + " upward faster than 2 m/s, " + air + " grain-steps over 20 cm");
            Check(up2 > 1500, "a wheel slipping as it drives through sand throws a stream of it up (" + up2 + " grains upward over 2 m/s in 4 s)");
        }
        Check(inPlaceUp[SandMaterials.Sand] > 2000, "a wheel spinning in place in sand throws a lot of it up into the air (" + inPlaceUp[SandMaterials.Sand] + " grains in 4 s)");
        Check(inPlaceUp[SandMaterials.Dust] > 500, "and dust (" + inPlaceUp[SandMaterials.Dust] + ")");
        Check(inPlaceUp[SandMaterials.Gravel] > 40, "and gravel, a grain the size of a column (" + inPlaceUp[SandMaterials.Gravel] + ")");
        Check(inPlaceUp[SandMaterials.Mud] < inPlaceUp[SandMaterials.Sand], "mud less than sand, its stickiness holding it");
        Check(inPlaceUp[SandMaterials.Glue] == 0, "and glue not at all");
        Console.WriteLine(fails == 0 ? "ALL PASSED" : (fails + " FAILED"));
        return fails;
    }
}
