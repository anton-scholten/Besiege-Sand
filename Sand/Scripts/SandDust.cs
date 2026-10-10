using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The dust a wheel kicks up on the sand, in the colour of the sand it is on. Besiege's wheels carry their own little
    /// dust cloud (<c>WheelSmoke</c>: a particle system the wheel moves to where it touches the ground each physics step
    /// and emits from while it rolls or skids), grey-brown wherever it is, drawn with a shader that darkens what is
    /// behind it. On ordinary ground it is left exactly as it is.
    ///
    /// On the sand the wheel's own system goes on emitting -- that is how this knows the wheel is rolling or skidding,
    /// and where it touches -- but what it makes there is invisible and gone in a moment; the cloud the player sees comes
    /// from one particle system of the mod's own (<see cref="NativePuffs"/>: soft, unlit puffs that darken nothing), in
    /// the colour of the sand there and the size, amount, life and opacity of its row -- dust a huge cloud that hangs a
    /// long time, dry sand and snow nearly as big, gravel smaller, the wet and sticky media a little; the size and life
    /// are the material row's own (Params.cloudSize, cloudLength: the brush's advanced settings).
    /// Nothing already in the air is ever recoloured: the wheel's system only ever changes what it makes next, and the
    /// two systems' puffs keep their own looks. With the panel's "Wheel dust" off a wheel raises none on the sand, and
    /// its own off it.
    ///
    /// A few times a second each wheel of every machine is looked at, to see what it is on; the sand's puffs are made
    /// every frame, while the wheel's own system shows it emitting.
    /// </summary>
    public class SandDust
    {
        // How a medium's cloud differs from the wheel's own: how much of it, and how opaque. In table order: sand, wet
        // sand, dust, snow, gravel, mud, glue.
        private static readonly float[] RateOf = { 2.4f, 0.8f, 4f, 2.4f, 1.4f, 0.7f, 0.6f };
        private static readonly float[] AlphaOf = { 0.5f, 0.4f, 0.55f, 0.55f, 0.45f, 0.4f, 0.35f };

        private class Cloud
        {
            public ParticleSystem system;
            public Color colour;          // the wheel's own puffs, as the system had them
            public float size, rate, life;
            public int most;
            public bool dressed;          // anything set yet
            public int row = -1;          // the material row it is dressed for now; -1 the wheel's own
            public int medium = -1;
            public uint tint;
            public float big = 1f, longer = 1f;
            public bool off;              // on the sand, with the sand's dust switched off
            public Color32 puff;          // the colour of the sand's puffs
            public float carry;
        }

        private readonly Dictionary<int, Cloud> clouds = new Dictionary<int, Cloud>();
        // Keyed by instance ID *and* holding the block it was measured on: see SandObjectCache for
        // why the ID alone is not enough. A recycled ID would otherwise hand a fresh block one of
        // its block's particle systems as its dust, and Dress then hides it whenever it is over
        // sand -- a wheel's fire going invisible on every surface it rolls over.
        private readonly SandObjectCache isWheel = new SandObjectCache(1024);
        private readonly List<Machine> machines = new List<Machine>();
        private float nextLook;
        private NativePuffs puffs;
        private uint seed = 0x68E31DA4u;

        // The sand's puffs against the wheel's own: the cloud sizes and amounts (Params.cloudSize, RateOf) were set
        // against the wheel's own dark puffs, and as soft puffs that grow as they fade the same numbers made clouds
        // far too big. So a puff starts at this much of the wheel's puff times the row's cloud size, there are this
        // much as many, and it ends this much bigger than it starts.
        private const float PuffScale = 0.4f, PuffRate = 0.5f, Grow = 1.5f;

        /// <summary>How long the wheel's own puffs last on the sand, where they are invisible: long enough that one is
        /// always alive while the wheel is emitting, which is what says it is.</summary>
        private const float ProbeLife = 0.2f;

        /// <summary>Makes the sand's dust system, under <paramref name="parent"/>.</summary>
        public void Init(Transform parent)
        {
            if (puffs != null) return;
            // A cloud hangs: next to no gravity, the air slows it to a drift, and it grows and fades as it goes.
            puffs = new NativePuffs("SandWheelDust", parent, 8000, -0.02f, 0.4f, 0.06f, Grow, true);
        }

        /// <summary>Looks at every wheel's dust five times a second, and makes the sand's puffs every frame.</summary>
        public void Tick(SandBed bed, SandMaterials.Params[] rows)
        {
            if (!StatMaster.levelSimulating) { if (clouds.Count > 0) Forget(); return; }
            EmitPuffs(Time.deltaTime);
            if (Time.time < nextLook) return;
            nextLook = Time.time + 0.2f;
            machines.Clear();
            try
            {
                if (StatMaster.isMP && Playerlist.Players != null)
                    for (int i = 0; i < Playerlist.Players.Count; i++)
                        if (Playerlist.Players[i] != null && Playerlist.Players[i].machine != null) machines.Add(Playerlist.Players[i].machine);
                if (machines.Count == 0 && Machine.Active() != null) machines.Add(Machine.Active());
            }
            catch (System.Exception) { }

            for (int m = 0; m < machines.Count; m++)
            {
                List<BlockBehaviour> blocks = null;
                try { blocks = machines[m].isSimulating ? machines[m].SimulationBlocks : null; }
                catch (System.Exception) { }
                if (blocks == null) continue;
                for (int b = 0; b < blocks.Count; b++)
                {
                    BlockBehaviour block = blocks[b];
                    if (block == null) continue;
                    bool wheel;
                    if (!isWheel.TryGet(block, out wheel))
                    {
                        // Anywhere in the block: on some wheels it is not on the block's own object.
                        wheel = block.GetComponentInChildren<WheelSmoke>(true) != null;
                        isWheel.Put(block, wheel);
                    }
                    if (wheel) Dress(block, bed, rows);
                }
            }
        }

        // The wheel's dust is the particle system the wheel moves down to where it touches the ground; any other a wheel
        // carries (its fire, for one) stays at the wheel. So of the block's particle systems it is the one farthest from
        // the block's middle, once that is farther than a fifth of a metre -- until then it is not known, and looked for
        // again.
        private static ParticleSystem FindDust(BlockBehaviour block)
        {
            ParticleSystem[] all = block.GetComponentsInChildren<ParticleSystem>(true);
            Vector3 middle = block.transform.position;
            ParticleSystem best = null;
            float far = 0.2f * 0.2f;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                float d = (all[i].transform.position - middle).sqrMagnitude;
                if (d > far) { far = d; best = all[i]; }
            }
            return best;
        }

        private void Dress(BlockBehaviour block, SandBed bed, SandMaterials.Params[] rows)
        {
            int id = block.GetInstanceID();
            Cloud c;
            if (!clouds.TryGetValue(id, out c) || c.system == null)
            {
                ParticleSystem found = FindDust(block);
                if (found == null) return;
                c = new Cloud();
                c.system = found;
                c.colour = found.startColor;
                c.size = found.startSize;
                c.rate = found.emission.rate.constant;
                c.life = found.startLifetime;
                c.most = found.maxParticles;
                clouds[id] = c;
            }
            ParticleSystem ps = c.system;

            // Where the cloud is: the wheel puts it where it last touched the ground.
            Vector3 at = ps.transform.position;
            int row = -1, medium = -1;
            uint colour = 0;
            if (bed != null && bed.Enabled)
            {
                int i = Mathf.FloorToInt(at.x / SandBed.Cell), j = Mathf.FloorToInt(at.z / SandBed.Cell);
                float top, bottom;
                if (bed.Thickness(i, j, out top, out bottom, out colour) && Mathf.Abs(at.y - top) < 0.6f)
                {
                    row = bed.MaterialAt(i, j);
                    medium = SandMaterials.MediumOf(row);
                }
            }
            float big = 1f, longer = 1f;
            if (row >= 0 && rows != null && row < rows.Length) { big = rows[row].cloudSize; longer = Mathf.Max(rows[row].cloudLength, 0.05f); }
            bool off = row >= 0 && (!SandField.WheelDust || big <= 0f || puffs == null || !puffs.Ready);
            if (c.dressed && off == c.off && row == c.row && (row < 0 || (colour == c.tint && big == c.big && longer == c.longer))) return;
            bool wasOnSand = c.dressed && c.row >= 0;
            c.dressed = true;
            c.off = off;
            c.row = row;
            c.medium = medium;
            c.tint = colour;
            c.big = big;
            c.longer = longer;
            if (medium >= 0)
                c.puff = new Color32((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)((colour >> 16) & 0xFF),
                                     (byte)(AlphaOf[medium] * 255f));
            try
            {
                if (row < 0)
                {
                    // Ordinary ground: the wheel's own cloud, as it was.
                    if (!wasOnSand) return;
                    ps.startColor = c.colour;
                    ps.startSize = c.size;
                    ps.startLifetime = c.life;
                    return;
                }
                // The sand: what the wheel makes is invisible and brief, only there to say it is emitting.
                ps.startColor = new Color(c.colour.r, c.colour.g, c.colour.b, 0f);
                ps.startLifetime = Mathf.Max(ProbeLife, c.rate > 0f ? 2f / c.rate : ProbeLife);
            }
            catch (System.Exception) { }
        }

        private float Rand()
        {
            seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
            return (seed & 0xFFFFFF) / (float)0x1000000;
        }

        /// <summary>The sand's puffs for every wheel on the sand that is emitting, at its rate times its medium's.</summary>
        private void EmitPuffs(float dt)
        {
            if (puffs == null || !puffs.Ready || clouds.Count == 0) return;
            foreach (KeyValuePair<int, Cloud> kv in clouds)
            {
                Cloud c = kv.Value;
                if (c.system == null || c.row < 0 || c.off || c.medium < 0) continue;
                if (c.system.particleCount == 0) { c.carry = 0f; continue; }
                c.carry += c.rate * RateOf[c.medium] * PuffRate * dt;
                int n = (int)c.carry;
                if (n <= 0) continue;
                c.carry -= n;
                Vector3 at = c.system.transform.position;
                float size = c.size * c.big * PuffScale, life = c.life * c.longer;
                for (int q = 0; q < n; q++)
                {
                    Vector3 p = at + new Vector3(Rand() - 0.5f, Rand() * 0.3f, Rand() - 0.5f) * (size * 0.5f);
                    Vector3 v = new Vector3((Rand() - 0.5f) * 1.2f, 0.4f + Rand() * 0.6f, (Rand() - 0.5f) * 1.2f);
                    puffs.Emit(p, v, size * (0.8f + Rand() * 0.4f), life * (0.8f + Rand() * 0.4f), c.puff);
                }
            }
        }

        /// <summary>Gives every wheel its own dust back and forgets them (the run is over).</summary>
        public void Forget()
        {
            foreach (KeyValuePair<int, Cloud> kv in clouds)
            {
                Cloud c = kv.Value;
                if (c.system == null) continue;
                try
                {
                    c.system.startColor = c.colour;
                    c.system.startSize = c.size;
                    c.system.startLifetime = c.life;
                    c.system.maxParticles = c.most;
                }
                catch (System.Exception) { }
            }
            clouds.Clear();
            isWheel.Clear();
            if (puffs != null) puffs.Clear();
        }
    }
}
