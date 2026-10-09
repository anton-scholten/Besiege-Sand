using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Soft puffs in one of Unity's own particle systems: the drawn spray (SandSpray) and the sand's wheel dust
    /// (SandDust). Unity moves and draws them itself, in native code, where the spray used to be built into a mesh
    /// in managed code every frame. A puff is emitted with its own place, speed, size, life and colour
    /// (<c>ParticleSystem.Emit(EmitParams, 1)</c>); everything else -- gravity, how the air slows it, how it grows and
    /// fades -- is the system's.
    ///
    /// The look is <see cref="Material"/>: the stock <c>Particles/Alpha Blended</c> (in the player, found by name) with a
    /// neutral grey tint, so a puff is its own colour, on <see cref="Texture"/>, a white disc whose alpha falls smoothly
    /// to nothing at the rim. Unlit, and it darkens nothing behind it.
    /// </summary>
    public class NativePuffs
    {
        private readonly ParticleSystem system;
        private ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams();

        public bool Ready { get { return system != null; } }

        /// <param name="gravity">times the level's gravity: 1 for grains that fall, about nought for a cloud.</param>
        /// <param name="slowTo">the speed, m/s, the air brings a puff down towards; zero for no air at all.</param>
        /// <param name="slowing">the share of what is over that taken off, a frame (Unity's dampen).</param>
        /// <param name="grow">how much bigger a puff ends than it starts, over its life.</param>
        /// <param name="fade">whether it fades out over its life, or stays and is gone at the end.</param>
        public NativePuffs(string name, Transform parent, int most, float gravity, float slowTo, float slowing, float grow, bool fade)
            : this(name, parent, most, gravity, slowTo, slowing, grow, fade, false) { }

        /// <param name="dense">on <see cref="DenseMaterial"/>, whose puffs show about twice as much of themselves.</param>
        public NativePuffs(string name, Transform parent, int most, float gravity, float slowTo, float slowing, float grow, bool fade, bool dense)
        {
            Material material = dense ? DenseMaterial : Material;
            if (material == null) return;
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            system = go.AddComponent<ParticleSystem>();
            system.simulationSpace = ParticleSystemSimulationSpace.World;
            system.playOnAwake = true;
            system.loop = true;
            system.maxParticles = most;
            this.most = most;
            system.startSpeed = 0f;
            system.startSize = 1f;
            system.startLifetime = 1f;
            system.gravityModifier = gravity;
            ParticleSystem.EmissionModule emission = system.emission;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = system.shape;
            shape.enabled = false;
            if (slowing > 0f)
            {
                ParticleSystem.LimitVelocityOverLifetimeModule air = system.limitVelocityOverLifetime;
                air.enabled = true;
                air.limit = new ParticleSystem.MinMaxCurve(slowTo);
                air.dampen = slowing;
            }
            if (grow != 1f)
            {
                ParticleSystem.SizeOverLifetimeModule size = system.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, grow));
            }
            if (fade)
            {
                Gradient g = new Gradient();
                g.SetKeys(new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                          new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.4f), new GradientAlphaKey(0f, 1f) });
                ParticleSystem.ColorOverLifetimeModule colour = system.colorOverLifetime;
                colour.enabled = true;
                colour.color = new ParticleSystem.MinMaxGradient(g);
            }
            ParticleSystemRenderer r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            // The default caps a puff at half the screen across, and a cloud by the camera then shrank as it came near.
            r.maxParticleSize = 4f;
            system.Play();
        }

        /// <summary>One puff -- none past <see cref="Most"/>: <c>Emit</c> with <c>EmitParams</c> does not keep to the system's
        /// own <c>maxParticles</c> on this Unity, and the spray stood at ten thousand under a cap of six.</summary>
        public void Emit(Vector3 position, Vector3 velocity, float size, float life, Color32 colour)
        {
            if (system == null) return;
            if (Time.frameCount != countedFrame) { countedFrame = Time.frameCount; counted = system.particleCount; }
            if (counted >= most) return;
            counted++;
            emit.position = position;
            emit.velocity = velocity;
            emit.startSize = size;
            emit.startLifetime = life;
            emit.startColor = colour;
            system.Emit(emit, 1);
        }

        public int Count { get { return system != null ? system.particleCount : 0; } }

        public int Most
        {
            set { most = value; if (system != null && system.maxParticles != value) system.maxParticles = value; }
        }
        private int most = int.MaxValue, counted, countedFrame = -1;

        public void Clear() { if (system != null) system.Clear(); }

        // ------------------------------------------------------------------ the shared look

        private static Material material, denseMaterial;
        private static Texture2D texture, denseTexture;
        private static bool looked, lookedDense;

        /// <summary>A white disc, its alpha full in the middle and falling smoothly to nothing at the rim: a smoothstep,
        /// squared, so the edge is a long fade rather than a ring.</summary>
        public static Texture2D Texture
        {
            get { if (texture == null) texture = Disc(true); return texture; }
        }

        /// <summary>The same with the smoothstep not squared: as soft at the very rim, about twice as much of it shown.</summary>
        public static Texture2D DenseTexture
        {
            get { if (denseTexture == null) denseTexture = Disc(false); return denseTexture; }
        }

        private static Texture2D Disc(bool squared)
        {
            const int side = 64;
            Texture2D t = new Texture2D(side, side, TextureFormat.RGBA32, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            t.wrapMode = TextureWrapMode.Clamp;
            Color32[] texels = new Color32[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    float dx = (x + 0.5f) / side * 2f - 1f, dy = (y + 0.5f) / side * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float u = Mathf.Clamp01(1f - d);
                    float a = u * u * (3f - 2f * u);
                    if (squared) a *= a;
                    texels[y * side + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            t.SetPixels32(texels);
            t.Apply(false);
            return t;
        }

        /// <summary>The puffs' material on <see cref="DenseTexture"/>, or null where the shader is not in the player.</summary>
        public static Material DenseMaterial
        {
            get
            {
                if (denseMaterial != null || lookedDense) return denseMaterial;
                lookedDense = true;
                Material soft = Material;
                if (soft == null) return null;
                denseMaterial = new Material(soft);
                denseMaterial.hideFlags = HideFlags.HideAndDontSave;
                denseMaterial.mainTexture = DenseTexture;
                return denseMaterial;
            }
        }

        /// <summary>The puffs' material, or null where the shader is not in the player.</summary>
        public static Material Material
        {
            get
            {
                if (material != null || looked) return material;
                looked = true;
                Shader shader = Shader.Find("Particles/Alpha Blended");
                if (shader == null) return null;
                material = new Material(shader);
                material.hideFlags = HideFlags.HideAndDontSave;
                material.mainTexture = Texture;
                if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
                return material;
            }
        }
    }
}
