using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The dust a wheel kicks up, in the colour of the sand it is on. Besiege's wheels carry their own little dust
    /// cloud (<c>WheelSmoke</c>: a particle system the wheel moves to where it touches the ground each physics step
    /// and emits from while it rolls or skids), grey-brown wherever it is. A few times a second each wheel of every
    /// machine is looked at: where its dust comes from is on the sand, the cloud takes the colour of the sand there
    /// and the size, amount and life of the medium -- dust a huge cloud that hangs a long time, dry sand and snow
    /// nearly as big, gravel a smaller one, wet sand, mud and glue a little -- times the row's own cloud size and
    /// length (the advanced settings of a sprayer or the brush), and off the sand it has its own back.
    /// </summary>
    public class SandDust
    {
        // How a medium's cloud differs from the wheel's own: its size, how much of it, how long it lasts, and how
        // opaque (the colour's alpha). In table order: sand, wet sand, dust, snow, gravel, mud, glue.
        private static readonly float[] SizeOf = { 4.5f, 0.9f, 6.5f, 4.8f, 1.6f, 0.9f, 0.9f };
        private static readonly float[] RateOf = { 2.4f, 0.8f, 4f, 2.4f, 1.4f, 0.7f, 0.6f };
        private static readonly float[] LifeOf = { 2.2f, 1f, 4.5f, 2.4f, 1.4f, 1f, 1f };
        private static readonly float[] AlphaOf = { 0.5f, 0.4f, 0.55f, 0.55f, 0.45f, 0.4f, 0.35f };

        // Room for the bigger, longer clouds: the wheel's own system holds few particles, and a cloud that lasts
        // four times as long at twice the rate needs eight times as many or it thins out to gaps.
        private const int MostParticles = 1500;

        private class Cloud
        {
            public ParticleSystem system;
            public Color colour;
            public float size, rate, life;
            public int most;
            public int row = -1;         // the material row it is dressed for now; -1 its own
            public int medium = -1;
            public float big = 1f, longer = 1f;
            public uint tint;
            public Material material;     // the renderer's own, whose tint darkens what it draws
            public Color shade;
            public bool hasShade;
        }

        private readonly Dictionary<int, Cloud> clouds = new Dictionary<int, Cloud>();
        private readonly Dictionary<int, bool> isWheel = new Dictionary<int, bool>();
        private readonly List<Machine> machines = new List<Machine>();
        private float nextLook;

        /// <summary>Looks at every wheel's dust, five times a second.</summary>
        public void Tick(SandBed bed, SandMaterials.Params[] rows)
        {
            if (!StatMaster.levelSimulating) { if (clouds.Count > 0) Forget(); return; }
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
                    int bid = block.GetInstanceID();
                    bool wheel;
                    if (!isWheel.TryGetValue(bid, out wheel))
                    {
                        // Anywhere in the block: on some wheels it is not on the block's own object.
                        wheel = block.GetComponentInChildren<WheelSmoke>(true) != null;
                        isWheel[bid] = wheel;
                    }
                    if (wheel) Dress(block, bed, rows);
                }
            }
        }

        // The wheel's dust is the particle system the wheel moves down to where it touches the ground; any other a wheel
        // carries (its fire, for one) stays at the wheel. So of the block's particle systems it is the one farthest from
        // the block's middle, once that is farther than a fifth of a metre -- until then it is not known, and looked for
        // again. (The first particle system under the wheel was taken for it, which need not have been the dust.)
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
                // The particle shader draws a puff as its colour times twice the material's tint, and the tint is a dark
                // brown (on the desert island the game sets it so): dust and snow came out nearly black. On the sand the
                // tint is a neutral grey, which leaves the sand's colour as it is.
                ParticleSystemRenderer r = found.GetComponent<ParticleSystemRenderer>();
                if (r != null)
                {
                    c.material = r.material;
                    if (c.material != null && c.material.HasProperty("_TintColor")) { c.shade = c.material.GetColor("_TintColor"); c.hasShade = true; }
                }
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
            // The row's own cloud, set by a sprayer's or the brush's advanced settings; a row that has none (never
            // written) is the medium's.
            float big = 1f, longer = 1f;
            if (row >= 0 && rows != null && row < rows.Length && rows[row].cloudLength > 0f) { big = rows[row].cloudSize; longer = rows[row].cloudLength; }
            if (row == c.row && (row < 0 || (colour == c.tint && big == c.big && longer == c.longer))) return;
            c.row = row;
            c.medium = medium;
            c.tint = colour;
            c.big = big;
            c.longer = longer;
            try
            {
                ParticleSystem.EmissionModule emission = ps.emission;
                if (c.hasShade && c.material != null) c.material.SetColor("_TintColor", medium < 0 ? c.shade : new Color(0.5f, 0.5f, 0.5f, 0.5f));
                if (medium < 0)
                {
                    ps.startColor = c.colour;
                    ps.startSize = c.size;
                    ps.startLifetime = c.life;
                    emission.rate = new ParticleSystem.MinMaxCurve(c.rate);
                    ps.maxParticles = c.most;
                    return;
                }
                Color sand = new Color((colour & 0xFF) / 255f, ((colour >> 8) & 0xFF) / 255f, ((colour >> 16) & 0xFF) / 255f, AlphaOf[medium]);
                ps.startColor = sand;
                ps.startSize = c.size * SizeOf[medium] * big;
                ps.startLifetime = c.life * LifeOf[medium] * longer;
                emission.rate = new ParticleSystem.MinMaxCurve(big > 0f ? c.rate * RateOf[medium] : 0f);
                ps.maxParticles = Mathf.Max(c.most, MostParticles);
            }
            catch (System.Exception) { }
        }

        /// <summary>Gives every wheel its own dust back and forgets them (the run is over).</summary>
        public void Forget()
        {
            foreach (KeyValuePair<int, Cloud> kv in clouds)
            {
                Cloud c = kv.Value;
                if (c.system == null || c.row < 0) continue;
                try
                {
                    ParticleSystem.EmissionModule emission = c.system.emission;
                    c.system.startColor = c.colour;
                    if (c.hasShade && c.material != null) c.material.SetColor("_TintColor", c.shade);
                    c.system.startSize = c.size;
                    c.system.startLifetime = c.life;
                    c.system.maxParticles = c.most;
                    emission.rate = new ParticleSystem.MinMaxCurve(c.rate);
                }
                catch (System.Exception) { }
            }
            clouds.Clear();
            isWheel.Clear();
        }
    }
}
