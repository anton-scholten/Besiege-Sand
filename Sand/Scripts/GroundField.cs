using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The level's solid ground, as a height field the solver can ask cheaply.
    ///
    /// This exists because of two measured facts. A Besiege level has **no Unity
    /// Terrain** -- `Terrain.activeTerrains` is empty and no `TerrainCollider` appears
    /// in any scene -- so there is no heightmap to sample. And the ground is *concave*
    /// mesh geometry, which the shape feed deliberately does not send to the solver:
    /// a concave mesh is not an analytic shape and a grain cannot be tested against
    /// one in a few instructions.
    ///
    /// So the world is rasterised once, by raycasting a grid down onto it, and the
    /// solver treats the result as a floor. Besiege's own water does the same thing
    /// with `TerrainHeightRenderer`; this is that idea with raycasts instead of a
    /// camera, because it has to run on the CPU beside the CPU solver.
    ///
    /// Only static geometry is sampled. Anything with a rigidbody that can move is
    /// skipped, because it is already in the solver as a shape and would otherwise be
    /// baked into the floor where it happened to be standing.
    /// </summary>
    public class GroundField
    {
        private const int MaxSide = 96;
        private const float SkyHeight = 400f;
        private const float NoGround = -10000f;

        private float[] height;
        private int nx, nz;
        private float cell;
        private Vector3 origin;
        private bool built;

        private readonly RaycastHit[] hits = new RaycastHit[16];

        public bool Built { get { return built; } }

        public void Forget() { built = false; }

        /// <summary>
        /// Whether the built field still covers an area. Rebuilding is not cheap --
        /// up to nine thousand raycasts -- so it happens when the medium leaves the
        /// area it was built for, not every step.
        /// </summary>
        public bool Covers(Bounds b)
        {
            if (!built) return false;
            return b.min.x >= origin.x && b.min.z >= origin.z
                && b.max.x <= origin.x + nx * cell
                && b.max.z <= origin.z + nz * cell;
        }

        /// <summary>Rasterises the static world over an area, with margin.</summary>
        public void Build(Bounds area)
        {
            float margin = Mathf.Max(area.size.x, area.size.z) * 0.5f + 8f;
            float spanX = area.size.x + margin * 2f;
            float spanZ = area.size.z + margin * 2f;

            cell = Mathf.Max(spanX, spanZ) / MaxSide;
            cell = Mathf.Max(cell, 0.25f);

            nx = Mathf.Clamp(Mathf.CeilToInt(spanX / cell) + 1, 2, MaxSide);
            nz = Mathf.Clamp(Mathf.CeilToInt(spanZ / cell) + 1, 2, MaxSide);
            origin = new Vector3(area.center.x - nx * cell * 0.5f, 0f,
                                 area.center.z - nz * cell * 0.5f);

            if (height == null || height.Length < nx * nz) height = new float[nx * nz];

            float top = area.max.y + SkyHeight;
            for (int z = 0; z < nz; z++)
            {
                for (int x = 0; x < nx; x++)
                {
                    Vector3 from = new Vector3(origin.x + x * cell, top, origin.z + z * cell);
                    height[x + z * nx] = Cast(from);
                }
            }
            built = true;
        }

        /// <summary>
        /// Highest static surface under a point. RaycastAll rather than Raycast,
        /// because the first thing a ray meets may well be a block of the machine and
        /// the floor is behind it.
        /// </summary>
        private float Cast(Vector3 from)
        {
            int n = Physics.RaycastNonAlloc(new Ray(from, Vector3.down), hits,
                                            SkyHeight * 2f + 2000f);
            float best = NoGround;
            for (int i = 0; i < n; i++)
            {
                Collider c = hits[i].collider;
                if (c == null || c.isTrigger) continue;

                Rigidbody rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic) continue;   // that is a shape, not the floor

                if (hits[i].point.y > best) best = hits[i].point.y;
            }
            return best;
        }

        /// <summary>
        /// Ground height under a position, bilinear. Returns a value far below
        /// everything where the field has no answer, so a grain over a hole falls
        /// rather than resting on nothing.
        /// </summary>
        public float Height(Vector3 p)
        {
            if (!built) return NoGround;

            float fx = (p.x - origin.x) / cell;
            float fz = (p.z - origin.z) / cell;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            if (x0 < 0 || z0 < 0 || x0 >= nx - 1 || z0 >= nz - 1) return NoGround;

            float tx = fx - x0, tz = fz - z0;
            float h00 = height[x0 + z0 * nx];
            float h10 = height[x0 + 1 + z0 * nx];
            float h01 = height[x0 + (z0 + 1) * nx];
            float h11 = height[x0 + 1 + (z0 + 1) * nx];

            // A cell touching a hole is a hole: interpolating towards -10000 would
            // pull the whole cell down into a funnel.
            if (h00 <= NoGround || h10 <= NoGround || h01 <= NoGround || h11 <= NoGround)
                return NoGround;

            float a = Mathf.Lerp(h00, h10, tx);
            float b = Mathf.Lerp(h01, h11, tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>
        /// Surface normal from the height gradient. This is what makes a slope shed
        /// sand instead of holding it in a staircase of flat cells.
        /// </summary>
        public Vector3 Normal(Vector3 p)
        {
            float e = cell;
            float hl = Height(p + new Vector3(-e, 0f, 0f));
            float hr = Height(p + new Vector3(e, 0f, 0f));
            float hd = Height(p + new Vector3(0f, 0f, -e));
            float hu = Height(p + new Vector3(0f, 0f, e));
            if (hl <= NoGround || hr <= NoGround || hd <= NoGround || hu <= NoGround)
                return Vector3.up;

            Vector3 n = new Vector3(hl - hr, 2f * e, hd - hu);
            return n.sqrMagnitude > 1e-9f ? n.normalized : Vector3.up;
        }
    }
}
