using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The five media, as one parameter row each. Nothing about a material is a code
    /// path: a grain carries an index into this table and the solver reads the row.
    /// That is what lets a digger move gravel into snow and have the contact between
    /// them be the average of the two.
    ///
    /// Indices are <c>const int</c> rather than an enum because Besiege's compiler
    /// segfaults on any enum declaration. See the notes repository,
    /// 18-vendoring-unity-code.md.
    /// </summary>
    public class SandMaterials
    {
        public const int Sand = 0;
        public const int WetSand = 1;
        public const int Dust = 2;
        public const int Snow = 3;
        public const int Gravel = 4;
        public const int Count = 5;

        /// <summary>
        /// Coarser versions of every medium, for when the solver is short of time. A
        /// coarse grain is a bigger sphere of the same material that stands for several
        /// ordinary ones, so the same volume of sand costs far fewer grains. Rows
        /// <c>Count * level + medium</c> for levels 1 to <see cref="LodLevels"/>; a
        /// customised emitter's own rows start after them at <see cref="FirstVariant"/>.
        /// </summary>
        public const int LodLevels = 3;
        public const int FirstVariant = Count * (1 + LodLevels);

        /// <summary>Radius multiplier per level; level 0 is the ordinary grain.</summary>
        public static readonly float[] LodScale = { 1f, 1.5f, 2.2f, 3.2f };

        /// <summary>No grain is made bigger than this, however coarse the level: a
        /// boulder is not sand.</summary>
        public const float MaxLodRadius = 0.2f;

        /// <summary>
        /// Which of the five media each row of a table of this length looks like. A row of
        /// a medium looks like itself, a coarse row like the medium it is a coarse version
        /// of, and an emitter's row that has not been claimed yet looks like the first
        /// medium until it is. Every entry is a valid index into <see cref="Tiles"/> and
        /// <see cref="Tints"/>, which the renderer relies on for rows nothing is using.
        /// </summary>
        public static int[] InitialLooks(int tableLength)
        {
            int[] looks = new int[tableLength];
            for (int i = 0; i < tableLength; i++)
                looks[i] = i < FirstVariant ? i % Count : 0;
            return looks;
        }

        /// <summary>The table row for a medium at a detail level. Anything that is not
        /// one of the five media -- an emitter's own variant -- is returned unchanged.</summary>
        public static int LodRow(int material, int level)
        {
            if (level <= 0 || material < 0 || material >= Count) return material;
            return Count * Mathf.Min(level, LodLevels) + material;
        }

        /// <summary>Names as the block mapper shows them, in index order.</summary>
        public static readonly string[] Names = { "Sand", "Wet Sand", "Dust", "Snow", "Gravel" };

        /// <summary>
        /// Field order and types must match <c>MaterialParams</c> in Sand.compute
        /// exactly: sixteen 4-byte fields, 64 bytes. A mismatch here does not fail to
        /// compile and does not throw -- it silently reads the wrong numbers.
        /// </summary>
        public struct Params
        {
            public float radius;
            public float invMass;
            public float friction;
            public float cohesion;
            public float restitution;
            public float airDrag;
            public float waterDrag;
            public float buoyancy;
            public float packGain;
            public float wetCohesion;
            public float wetSeconds;
            public int wetMaterial;   // material to become when submerged; -1 to stay

            // How the medium meets a machine. Grains: how hard they cling to a block's
            // surface, as an acceleration (snow coats a spinning wheel; dry sand runs off).
            // The bed: how much pressure it takes to press it a metre down, in pascals; how
            // much of its depth can be pressed out of it; and how fast a surface must move
            // through it to dig it up rather than ride on it.
            public float adhesion;
            public float firmness;
            public float packLimit;
            public float digSpeed;
        }

        public const int ParamsStride = 64;

        /// <summary>Per-material tint, multiplied with the emitter's own colour.</summary>
        public static readonly Color[] Tints =
        {
            new Color(0.83f, 0.71f, 0.47f),   // sand
            new Color(0.52f, 0.43f, 0.30f),   // wet sand: the same grain, darker
            new Color(0.74f, 0.70f, 0.64f),   // dust
            new Color(0.96f, 0.97f, 1.00f),   // snow
            new Color(0.55f, 0.54f, 0.52f)    // gravel
        };

        /// <summary>Tile index into the mod's sprite atlas, per material.</summary>
        public static readonly int[] Tiles = { 0, 0, 1, 2, 3 };

        /// <summary>
        /// Builds the table. Densities are in kg/m^3 and grain radii in metres, so
        /// the numbers read like the real materials; everything the solver wants is
        /// derived here rather than being a second set of magic numbers.
        /// </summary>
        public static Params[] Build()
        {
            Params[] table = new Params[Count];

            table[WetSand] = Row(0.030f, 2000f, 0.85f, 9f, 0.01f, 0.20f, 7f, 0f, 3f, 0f, 6f, -1);
            table[Snow] = Row(0.035f, 300f, 0.70f, 4f, 0.00f, 0.80f, 8f, 0f, 9f, 2f, 8f, -1);
            table[Gravel] = Row(0.080f, 1800f, 0.90f, 0f, 0.12f, 0.10f, 5f, 0f, 0f, 0f, 3f, -1);

            // Dry sand is loose: it piles at about thirty degrees, takes a rut readily and digs
            // under a wheel that spins. Wet sand is what dry sand used to be and more: firm,
            // steep, a little sticky. Dust is barely there -- light, slippery, slow to settle.
            // Snow is soft, packs down a long way, and clings to whatever moves through it.
            // Gravel is hard to press and hard to dig.
            table[Sand] = Row(0.030f, 1600f, 0.50f, 0f, 0.02f, 0.20f, 6f, 0f, 0f, 14f, 6f, WetSand);
            table[Dust] = Row(0.008f, 120f, 0.12f, 0.1f, 0.01f, 14.0f, 9f, 0f, 0f, 2f, 4f, -1);
            Bed(ref table[Sand], 0f, 50000f, 0.35f, 0.4f);
            Bed(ref table[WetSand], 30f, 200000f, 0.15f, 1.2f);
            Bed(ref table[Dust], 0f, 15000f, 0.6f, 0.2f);
            Bed(ref table[Snow], 120f, 30000f, 0.6f, 0.4f);
            Bed(ref table[Gravel], 0f, 400000f, 0.1f, 1.5f);

            // Buoyancy is the density ratio, so a grain lighter than water rises and
            // one heavier sinks without either case being special. Water is 1000.
            float[] density = { 1600f, 2000f, 120f, 300f, 1800f };
            for (int i = 0; i < Count; i++)
            {
                table[i].buoyancy = 1000f / density[i];
            }

            // The coarse rows: the same medium with a bigger grain. Everything but the
            // size and the mass that follows from it is copied, and a coarse sand that
            // gets wet becomes coarse wet sand rather than shrinking back to a fine grain
            // in mid-air, which would change how much sand there is.
            Params[] full = new Params[FirstVariant];
            for (int i = 0; i < Count; i++) full[i] = table[i];
            for (int level = 1; level <= LodLevels; level++)
            {
                for (int m = 0; m < Count; m++)
                {
                    Params p = table[m];
                    float r = Mathf.Max(Mathf.Min(p.radius * LodScale[level], MaxLodRadius), p.radius);
                    p.radius = r;
                    float volume = 4f / 3f * Mathf.PI * r * r * r;
                    p.invMass = 1f / Mathf.Max(volume * density[m], 1e-6f);
                    if (p.wetMaterial >= 0) p.wetMaterial = LodRow(p.wetMaterial, level);
                    full[Count * level + m] = p;
                }
            }
            return full;
        }

        private static void Bed(ref Params p, float adhesion, float firmness, float packLimit, float digSpeed)
        {
            p.adhesion = adhesion;
            p.firmness = firmness;
            p.packLimit = packLimit;
            p.digSpeed = digSpeed;
        }

        private static Params Row(float radius, float density, float friction, float cohesion,
                                  float restitution, float airDrag, float waterDrag,
                                  float buoyancy, float packGain, float wetCohesion,
                                  float wetSeconds, int wetMaterial)
        {
            Params p = new Params();
            p.radius = radius;
            // Mass of a sphere of this radius at this density; the solver only ever
            // wants the inverse.
            float volume = 4f / 3f * Mathf.PI * radius * radius * radius;
            p.invMass = 1f / Mathf.Max(volume * density, 1e-6f);
            p.friction = friction;
            p.cohesion = cohesion;
            p.restitution = restitution;
            p.airDrag = airDrag;
            p.waterDrag = waterDrag;
            p.buoyancy = buoyancy;
            p.packGain = packGain;
            p.wetCohesion = wetCohesion;
            p.wetSeconds = wetSeconds;
            p.wetMaterial = wetMaterial;
            return p;
        }

        /// <summary>Packs a colour the way the kernel and the vertex shader read it.</summary>
        public static uint PackColour(Color c)
        {
            uint r = (uint)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            uint g = (uint)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            uint b = (uint)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            uint a = (uint)Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
            return r | (g << 8) | (b << 16) | (a << 24);
        }
    }
}
