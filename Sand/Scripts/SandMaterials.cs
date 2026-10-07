using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The media, as one parameter row each. Nothing about a material is a code
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
        public const int Mud = 5;
        public const int Glue = 6;
        public const int Count = 7;

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
        /// No grain is smaller than this: dust's. Below it the same volume of sand is too many
        /// grains to simulate -- at 8 mm, seventy times sand's count and forty times its cost.
        /// A saved emitter asking for less gets this.
        /// </summary>
        public const float MinRadius = 0.025f;

        /// <summary>
        /// Which of the media each row of a table of this length looks like. A row of
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
        /// one of the media -- an emitter's own variant -- is returned unchanged.</summary>
        public static int LodRow(int material, int level)
        {
            if (level <= 0 || material < 0 || material >= Count) return material;
            return Count * Mathf.Min(level, LodLevels) + material;
        }

        /// <summary>Names as the block mapper shows them, in index order.</summary>
        public static readonly string[] Names = { "Sand", "Wet Sand", "Dust", "Snow", "Gravel", "Mud", "Glue" };

        /// <summary>
        /// How much one grain's colour may differ from the next, per medium, as a fraction of
        /// its brightness: real sand is a mix of light and dark grains, gravel is stones of
        /// all sorts, snow is nearly uniform. Applied when a grain is made and, on the
        /// settled bed, per column (see <see cref="Vary"/>).
        /// </summary>
        public static readonly float[] Variety = { 0.12f, 0.08f, 0.05f, 0.03f, 0.28f, 0.12f, 0.06f };

        /// <summary>Steps of variety a grain can take. Few, so the renderer's palette of grain
        /// colours stays small however many grains there are.</summary>
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
        /// A row of the solver's material table: how a grain of one medium behaves and
        /// how the bed meets a machine, one set of numbers each.
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


        /// <summary>Per-material tint, multiplied with the emitter's own colour.</summary>
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
        /// How hard a tyre grips each medium where it lies settled, as the friction of the bed's collider: the
        /// dynamic friction, with static friction a third more, combined with a block's by the lower of the two.
        /// Loose and wet media let a driven wheel spin and a turning machine slide -- mud most, then snow, gravel
        /// and dry sand, as a car drifts on gravel -- and glue holds. (The collider was 0.55 for everything,
        /// averaged with the tyre's, and a wheel gripped mud like tarmac.)
        /// </summary>
        public static readonly float[] Grip = { 0.45f, 0.7f, 0.35f, 0.25f, 0.4f, 0.2f, 1.0f };

        /// <summary>The medium a table row is (a coarse row is its medium's), or Sand for a row it cannot say.</summary>
        public static int MediumOf(int row)
        {
            return row >= 0 && row < Count * (1 + LodLevels) ? row % Count : Sand;
        }

        /// <summary>Tile index into the mod's sprite atlas, per material.</summary>
        public static readonly int[] Tiles = { 0, 0, 1, 2, 3, 0, 0 };

        /// <summary>
        /// Builds the table. Densities are in kg/m^3 and grain radii in metres, so
        /// the numbers read like the real materials; everything the solver wants is
        /// derived here rather than being a second set of magic numbers.
        /// </summary>
        public static Params[] Build()
        {
            Params[] table = new Params[Count];

            table[WetSand] = Row(0.030f, 2000f, 0.85f, 9f, 0.01f, 0.20f, 7f, 0f, 3f, 0f, 6f, -1);
            table[Snow] = Row(0.035f, 300f, 0.70f, 4f, 0.00f, 0.80f, 8f, 0f, 4f, 2f, 8f, -1);
            table[Gravel] = Row(0.080f, 1800f, 0.90f, 0f, 0.12f, 0.10f, 5f, 0f, 0f, 0f, 3f, -1);
            table[Mud] = Row(0.030f, 1700f, 0.80f, 3f, 0.00f, 0.20f, 8f, 0f, 4f, 0f, 6f, -1);
            // Glue: big, soft blobs that stick to anything they touch. Not to each other much: a
            // layer bound to itself is one a wheel cannot take any of (mud taught that).
            table[Glue] = Row(0.045f, 1300f, 0.90f, 3f, 0.00f, 0.20f, 8f, 0f, 2f, 0f, 6f, -1);

            // Dry sand is loose: it piles at about thirty degrees, takes a rut readily and digs
            // under a wheel that spins. Wet sand is what dry sand used to be and more: firm,
            // steep, a little sticky. Dust is barely there -- light, slippery, slow to settle.
            // Snow is soft, packs down a long way, and clings to whatever moves through it.
            // Gravel is hard to press and hard to dig.
            table[Sand] = Row(0.030f, 1600f, 0.50f, 0f, 0.02f, 0.20f, 6f, 0f, 0f, 14f, 6f, WetSand);
            // Dust's grain is not dust-sized: each is a light, loose clump 10 cm across. At 8 mm a
            // scoop of it was seventy times as many grains as the same scoop of sand and forty
            // times the solver's time. What makes it dust is that it weighs next to nothing --
            // a wheel that catches it throws it up -- slides flat and drifts down slowly.
            table[Dust] = Row(0.050f, 60f, 0.12f, 0.1f, 0.01f, 6.0f, 9f, 0f, 0f, 2f, 4f, -1);
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

            // Buoyancy is the density ratio, so a grain lighter than water rises and
            // one heavier sinks without either case being special. Water is 1000.
            float[] density = { 1600f, 2000f, 60f, 300f, 1800f, 1700f, 1300f };
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
                    p.invMass = InvMass(r, density[m]);
                    if (p.wetMaterial >= 0) p.wetMaterial = LodRow(p.wetMaterial, level);
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
        /// metre, sinking with its square root: see <c>SandBed.Press</c>), dig speeds by
        /// <see cref="DigEase"/>, and a grain's mass by <see cref="Lightness"/> -- its weight on a
        /// block, not its buoyancy, which stays the real density ratio.
        ///
        /// Lightness is Besiege's scale. A small wooden block, a unit cube, weighs about 0.3, and at
        /// half real density a grain of sand weighed a third of one and a cubic unit of sand as much
        /// as some two thousand seven hundred blocks: a spray of it flattened a machine, and a wheel
        /// pushed through grains as through lead. At 0.002 a cubic unit of sand weighs about ten
        /// wooden blocks -- heavier than wood, as sand is, if by more than the real three times.
        /// </summary>
        public const float Softness = 0.4f;
        public const float DigEase = 0.085f;
        public const float Lightness = 0.002f;

        private static void Bed(ref Params p, float adhesion, float firmness, float packLimit, float digSpeed)
        {
            p.adhesion = adhesion;
            p.firmness = firmness * Softness;
            p.packLimit = packLimit;
            p.digSpeed = digSpeed * DigEase;
        }

        /// <summary>
        /// A medium with the five advanced settings applied: a grain <paramref name="radius"/>
        /// across, its weight, grip and softness times its own, and <paramref name="stick"/> for
        /// its stickiness. Everything else is the medium's. With its own radius and stickiness and
        /// ones for the rest, this is the medium exactly. (The old row from twelve sliders also
        /// held firmness to at least 1 kPa a metre, five times what sand is now, so opening the
        /// settings and touching nothing made a sprayer's sand five times firmer.)
        /// </summary>
        public static Params Custom(Params from, float radius, float weightTimes,
                                                  float gripTimes, float stick, float softTimes)
        {
            Params p = from;
            float density = DensityOf(from) * Mathf.Max(weightTimes, 0.01f);
            p.radius = Mathf.Max(radius, MinRadius);
            p.invMass = InvMass(p.radius, density);
            p.buoyancy = 1000f / Mathf.Max(density, 1f);
            p.friction = from.friction * Mathf.Max(gripTimes, 0f);
            p.adhesion = Mathf.Max(stick, 0f);
            float soft = Mathf.Max(softTimes, 0.01f);
            p.firmness = from.firmness / soft;
            p.digSpeed = from.digSpeed / soft;
            return p;
        }

        /// <summary>The inverse mass the solver uses for a grain of a real density.</summary>
        public static float InvMass(float radius, float density)
        {
            float volume = 4f / 3f * Mathf.PI * radius * radius * radius;
            return 1f / Mathf.Max(volume * density * Lightness, 1e-6f);
        }

        /// <summary>The real density a row's grain stands for, as the settings show it.</summary>
        public static float DensityOf(Params p)
        {
            float volume = 4f / 3f * Mathf.PI * p.radius * p.radius * p.radius;
            return 1f / Mathf.Max(p.invMass, 1e-9f) / Mathf.Max(volume, 1e-9f) / Lightness;
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
            p.invMass = InvMass(radius, density);
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
