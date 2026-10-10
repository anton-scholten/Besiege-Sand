using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The media, as one parameter row each. Nothing about a material is a code
    /// path: a column of the bed carries an index into this table and reads the row.
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
        public const int Mud = 5;
        public const int Glue = 6;
        public const int Count = 7;

        /// <summary>
        /// Rows past the media: three copies of every medium (once coarser grains for a solver that is gone, kept so
        /// the rows saved in sand files stay where they were), then a customised brush's own rows from
        /// <see cref="FirstVariant"/>.
        /// </summary>
        public const int LodLevels = 3;
        public const int FirstVariant = Count * (1 + LodLevels);

        /// <summary>
        /// Which of the media each row of a table of this length looks like. A row of
        /// a medium looks like itself, a copy row like its medium, and a brush's row that has
        /// not been claimed yet looks like the first
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

        /// <summary>Names as the block mapper shows them, in index order.</summary>
        public static readonly string[] Names = { "Sand", "Wet Sand", "Dust", "Snow", "Gravel", "Mud", "Glue" };

        /// <summary>
        /// How much one bit of a medium's colour may differ from the next, as a fraction of its
        /// brightness: real sand is a mix of light and dark grains, gravel is stones of all
        /// sorts, snow is nearly uniform. Applied per column of the bed and per puff of spray
        /// (see <see cref="Vary"/>).
        /// </summary>
        public static readonly float[] Variety = { 0.12f, 0.08f, 0.05f, 0.03f, 0.28f, 0.12f, 0.06f };

        /// <summary>Steps of variety a colour can take.</summary>
        public const int VarietySteps = 8;

        /// <summary>
        /// A packed colour lightened or darkened by step <paramref name="step"/> (any int;
        /// it is wrapped) of a medium's variety. Gravel's darker stones are a little warmer and
        /// its lighter ones a little cooler, so a pile of it is not just a grey speckle.
        /// </summary>
        public static uint Vary(uint colour, int medium, int step)
        {
            if (medium < 0 || medium >= Count) return colour;
            if (medium == Glue)
            {
                // Lime, with a rare grain of cyan or purple: about one in forty of each.
                uint h = (uint)step * 2654435761u;
                int pick = (int)((h >> 16) % 80u);
                if (pick == 0) return 0xFFE8D21Au;          // cyan   (26, 210, 232)
                if (pick == 1) return 0xFFD02E8Cu;          // purple (140, 46, 208)
            }
            float v = Variety[medium];
            if (v <= 0f) return colour;
            int k = ((step % VarietySteps) + VarietySteps) % VarietySteps;
            float t = (k / (float)(VarietySteps - 1)) * 2f - 1f;      // -1 .. 1
            float f = 1f + v * t;
            float warm = medium == Gravel ? -t * 0.06f : 0f;
            float r = (colour & 0xFF) * (f + warm), g = ((colour >> 8) & 0xFF) * f, b = ((colour >> 16) & 0xFF) * (f - warm);
            uint R = (uint)Mathf.Clamp(Mathf.RoundToInt(r), 0, 255);
            uint G = (uint)Mathf.Clamp(Mathf.RoundToInt(g), 0, 255);
            uint B = (uint)Mathf.Clamp(Mathf.RoundToInt(b), 0, 255);
            return R | (G << 8) | (B << 16) | (colour & 0xFF000000u);
        }

        /// <summary>
        /// A row of the material table: how one medium piles, presses, ploughs and sprays,
        /// one set of numbers each.
        /// </summary>
        public struct Params
        {
            // The size of its grain, in metres: the size of its puffs of spray. How steep it piles. How much the
            // air holds back what a blast throws of it. What it becomes when wet; -1 to stay.
            public float radius;
            public float friction;
            public float airDrag;
            public int wetMaterial;

            // How the medium meets a machine: how much it clings (its spray leaves a slipping surface slower);
            // how much pressure it takes to press it a metre down, in pascals; how much of its depth can be
            // pressed out of it; and how fast a face must move through it to plough it.
            public float adhesion;
            public float firmness;
            public float packLimit;
            public float digSpeed;

            // The dust a wheel raises from it (SandDust): how big the puffs are, times the wheel's own,
            // and how long they hang in the air, times the wheel's own.
            public float cloudSize;
            public float cloudLength;
        }


        /// <summary>Per-material tint: each medium's own colour.</summary>
        public static readonly Color[] Tints =
        {
            new Color(0.83f, 0.71f, 0.47f),   // sand
            new Color(0.52f, 0.43f, 0.30f),   // wet sand: the same grain, darker
            new Color(0.74f, 0.70f, 0.64f),   // dust
            new Color(0.96f, 0.97f, 1.00f),   // snow
            new Color(0.55f, 0.54f, 0.52f),   // gravel
            new Color(0.36f, 0.26f, 0.17f),   // mud
            new Color(0.55f, 0.92f, 0.12f)    // glue: lime
        };

        /// <summary>
        /// How hard a tyre grips each medium where it lies settled, as the friction of the bed's collider:
        /// standing (static) and sliding (dynamic), in table order -- sand, wet sand, dust, snow, gravel, mud,
        /// glue. Combined with a block's own by the lower of the two, except glue's, by the higher.
        ///
        /// The gap between the two is the point. Dry sand and gravel hold a wheel that rolls (0.6, 0.55) but
        /// give little to one that has broken away (0.22, 0.3): a wheel given too much torque spins on, digs
        /// and throws instead of finding its grip again, and a machine turning fast on gravel drifts. Mud,
        /// snow and dust are slippery either way; wet sand is firm; glue holds at 2 whatever the tyre, so
        /// nothing slips on it. (It was one number a medium, 0.2 to 1.0, and before that 0.55 for everything,
        /// averaged with the tyre's, so a wheel gripped mud like tarmac.)
        /// </summary>
        public static readonly float[] GripStatic = { 0.6f, 0.8f, 0.2f, 0.15f, 0.55f, 0.12f, 2.0f };
        public static readonly float[] GripSliding = { 0.22f, 0.6f, 0.08f, 0.06f, 0.3f, 0.05f, 2.0f };

        /// <summary>The medium a table row is (a coarse row is its medium's), or Sand for a row it cannot say.</summary>
        public static int MediumOf(int row)
        {
            return row >= 0 && row < Count * (1 + LodLevels) ? row % Count : Sand;
        }

        /// <summary>Tile index into the mod's sprite atlas, per material.</summary>
        public static readonly int[] Tiles = { 0, 0, 1, 2, 3, 0, 0 };

        /// <summary>
        /// Builds the table. Grain radii are in metres, so the numbers read like the real
        /// materials.
        /// </summary>
        public static Params[] Build()
        {
            Params[] table = new Params[Count];

            // Radius, friction, air drag (the share of its speed the air takes away each second: dust and snow, light for
            // their size, drift), and what it becomes when wet.
            table[WetSand] = Row(0.030f, 0.85f, 0.05f, -1);
            table[Snow] = Row(0.035f, 0.70f, 0.40f, -1);
            table[Gravel] = Row(0.080f, 0.90f, 0.02f, -1);
            table[Mud] = Row(0.030f, 0.80f, 0.05f, -1);
            table[Glue] = Row(0.045f, 0.90f, 0.20f, -1);

            // Dry sand is loose: it piles at about thirty degrees, takes a rut readily and digs
            // under a wheel that spins. Wet sand is what dry sand used to be and more: firm,
            // steep, a little sticky. Dust is barely there -- light, slippery, slow to settle.
            // Snow is soft, packs down a long way, and clings to whatever moves through it.
            // Gravel is hard to press and hard to dig.
            table[Sand] = Row(0.030f, 0.50f, 0.05f, WetSand);
            // Dust: a light, loose clump 10 cm across, that slides flat and drifts down slowly.
            table[Dust] = Row(0.050f, 0.12f, 1.5f, -1);
            Bed(ref table[Sand], 0f, 50000f, 0.35f, 0.8f);
            Bed(ref table[WetSand], 40f, 200000f, 0.15f, 1.2f);
            // Dust: next to nothing to press or to dig.
            Bed(ref table[Dust], 0f, 5000f, 0.6f, 0.13f);
            Bed(ref table[Snow], 200f, 30000f, 0.6f, 0.6f);
            Bed(ref table[Gravel], 0f, 400000f, 0.1f, 1.5f);
            // Mud: heavy, cohesive, soft to drive into and as sticky as snow -- a spinning
            // wheel comes out of it caked.
            Bed(ref table[Mud], 250f, 25000f, 0.5f, 0.7f);
            // Glue: firm enough to drive on, and it grabs: anything that touches it comes away
            // coated, and a wheel spinning in it picks up a load.
            Bed(ref table[Glue], 60000f, 120000f, 0.2f, 1.0f);

            // A wheel's dust: dust a huge cloud that hangs a long time, dry sand and snow nearly as big, gravel smaller,
            // the wet and sticky media a little.
            float[] cloudSize = { 4.5f, 0.9f, 6.5f, 4.8f, 1.6f, 0.9f, 0.9f };
            float[] cloudLength = { 2.2f, 1f, 4.5f, 2.4f, 1.4f, 1f, 1f };
            for (int i = 0; i < Count; i++)
            {
                table[i].cloudSize = cloudSize[i];
                table[i].cloudLength = cloudLength[i];
            }

            // The copy rows: the same medium, its own wet medium the copy's.
            Params[] full = new Params[FirstVariant];
            for (int i = 0; i < Count; i++) full[i] = table[i];
            for (int level = 1; level <= LodLevels; level++)
            {
                for (int m = 0; m < Count; m++)
                {
                    Params p = table[m];
                    if (p.wetMaterial >= 0) p.wetMaterial = Count * level + p.wetMaterial;
                    full[Count * level + m] = p;
                }
            }
            return full;
        }

        /// <summary>
        /// How much softer than the table's numbers the media are against a machine. The numbers
        /// are real sand's; Besiege's machines are light for their size and held together by
        /// joints that give, and against real sand a machine barely marked it and could not dig.
        /// Firmness is scaled by <see cref="Softness"/> (and read as the pressure that presses a
        /// metre, sinking with its square root: see <c>SandBed.Press</c>) and dig speeds by
        /// <see cref="DigEase"/>.
        /// </summary>
        public const float Softness = 0.4f;
        public const float DigEase = 0.085f;

        private static void Bed(ref Params p, float adhesion, float firmness, float packLimit, float digSpeed)
        {
            p.adhesion = adhesion;
            p.firmness = firmness * Softness;
            p.packLimit = packLimit;
            p.digSpeed = digSpeed * DigEase;
        }

        /// <summary>
        /// A medium with the advanced settings applied, each the number itself, not a multiple of the medium's:
        /// <paramref name="friction"/> (its grip: how steep it piles), <paramref name="softness"/> (see
        /// <see cref="SoftnessOf"/>: how far a load presses it), <paramref name="digSpeed"/> (how fast a face must move
        /// through it to plough it, and a surface slip on it to throw spray) and the cloud a wheel raises from it.
        /// Everything else is the medium's. Given the medium's own numbers, this is the medium exactly.
        /// </summary>
        public static Params Custom(Params from, float friction, float softness, float digSpeed, float cloudSize, float cloudLength)
        {
            Params p = from;
            p.friction = Mathf.Max(friction, 0f);
            p.firmness = SoftnessScale / Mathf.Max(softness, 0.001f);
            p.digSpeed = Mathf.Max(digSpeed, 0.0001f);
            p.cloudSize = Mathf.Max(cloudSize, 0f);
            p.cloudLength = Mathf.Max(cloudLength, 0.05f);
            return p;
        }

        /// <summary>The pressure, in pascals, that presses a medium of softness 1 a metre down.</summary>
        public const float SoftnessScale = 10000f;

        /// <summary>A row's softness as the settings show it: <see cref="SoftnessScale"/> over its firmness (the
        /// pressure that presses it a metre), so dry sand is 0.5, dust 5 and gravel 0.06.</summary>
        public static float SoftnessOf(Params p) { return SoftnessScale / Mathf.Max(p.firmness, 1f); }

        private static Params Row(float radius, float friction, float airDrag, int wetMaterial)
        {
            Params p = new Params();
            p.radius = radius;
            p.friction = friction;
            p.airDrag = airDrag;
            p.wetMaterial = wetMaterial;
            p.cloudSize = 1f;
            p.cloudLength = 1f;
            return p;
        }

        /// <summary>Packs a colour the way the bed and the shaders read it.</summary>
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
