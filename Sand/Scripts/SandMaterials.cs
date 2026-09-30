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

        /// <summary>Names as the block mapper shows them, in index order.</summary>
        public static readonly string[] Names = { "Sand", "Wet Sand", "Dust", "Snow", "Gravel" };

        /// <summary>
        /// Field order and types must match <c>MaterialParams</c> in Sand.compute
        /// exactly: twelve 4-byte fields, 48 bytes. A mismatch here does not fail to
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
        }

        public const int ParamsStride = 48;

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

            table[Sand] = Row(0.030f, 1600f, 0.60f, 0f, 0.10f, 0.20f, 6f, 0f, 0f, 14f, 6f, WetSand);
            table[WetSand] = Row(0.030f, 2000f, 0.85f, 9f, 0.05f, 0.20f, 7f, 0f, 3f, 0f, 6f, -1);
            table[Dust] = Row(0.008f, 400f, 0.35f, 0.4f, 0.05f, 6.00f, 9f, 0f, 0f, 2f, 4f, -1);
            table[Snow] = Row(0.035f, 300f, 0.70f, 4f, 0.02f, 0.80f, 8f, 0f, 9f, 2f, 8f, -1);
            table[Gravel] = Row(0.080f, 1800f, 0.90f, 0f, 0.40f, 0.10f, 5f, 0f, 0f, 0f, 3f, -1);

            // Buoyancy is the density ratio, so a grain lighter than water rises and
            // one heavier sinks without either case being special. Water is 1000.
            float[] density = { 1600f, 2000f, 400f, 300f, 1800f };
            for (int i = 0; i < Count; i++)
            {
                table[i].buoyancy = 1000f / density[i];
            }
            return table;
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
