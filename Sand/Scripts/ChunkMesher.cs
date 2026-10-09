using UnityEngine;

namespace SandMod
{
    /// <summary>What a chunk contributes to its tile, kept so the tile can be rebuilt without
    /// reworking all sixteen.</summary>
    public class ChunkDraw
    {
        public const int Verts = SandBed.ChunkSide + 1;
        public readonly Vector3[] vertices = new Vector3[Verts * Verts];
        public readonly Vector3[] normals = new Vector3[Verts * Verts];
        public readonly Color32[] colours = new Color32[Verts * Verts];
        public readonly Color32[] texels = new Color32[Verts * Verts];
        public int[] tris = new int[0];
        public float builtAt = -1f;     // when it was last built (SandBedRenderer.RebuildEvery)
    }

    /// <summary>
    /// Works out one chunk's vertices, normals, colours and triangles from the bed. Nothing in it
    /// touches the engine's objects, so any number of these can run at once on different chunks, each
    /// with its own scratch, while the main thread waits (<see cref="ChunkMesherPool"/>).
    ///
    /// The 17 by 17 vertices of a chunk sit on the centres of the columns, the last row and column
    /// being the next chunk's first. Normals are taken from the heights either side of a vertex, and
    /// at a chunk's edge that means the neighbouring chunk's columns: a block 19 by 19 is read for
    /// that. Taken from the chunk alone, an edge vertex had a one-sided slope and its twin in the
    /// next chunk the other one, so the light changed across every seam: a grid, every sixteen
    /// columns, over the whole of the sand.
    /// </summary>
    public class ChunkMesher
    {
        private const int Verts = ChunkDraw.Verts;
        private const int Block = Verts + 2;
        private const float EdgeDip = 0.02f;
        private static readonly Vector3 Light = new Vector3(0.4f, 0.8f, 0.3f).normalized;

        private readonly float[] tops = new float[Block * Block];
        private readonly float[] bottoms = new float[Block * Block];
        private readonly uint[] blockColour = new uint[Block * Block];
        private readonly int[] blockMaterial = new int[Block * Block];
        private readonly uint[] cols = new uint[Verts * Verts];
        private readonly bool[] has = new bool[Verts * Verts];
        private readonly bool[] known = new bool[Verts * Verts];
        private readonly uint[] shades = new uint[SandMaterials.VarietySteps];
        private uint shadeFor;
        private int shadeLook = -1;
        private readonly int[] scratchTris = new int[SandBed.ChunkSide * SandBed.ChunkSide * 6];

        public void Build(SandBed bed, IBedGround ground, SandBed.Chunk c, int[] looks, bool lit)
        {
            ChunkDraw draw = c.renderHandle as ChunkDraw;
            if (draw == null) { draw = new ChunkDraw(); c.renderHandle = draw; }
            Vector3[] vertices = draw.vertices;
            Vector3[] normals = draw.normals;
            Color32[] colours = draw.colours;
            Color32[] texels = draw.texels;
            int baseI = c.cx * SandBed.ChunkSide, baseJ = c.cz * SandBed.ChunkSide;

            // Columns baseI - 1 .. baseI + 17: index (vi + 1, vj + 1) is vertex (vi, vj).
            bed.ReadBlock(baseI - 1, baseJ - 1, Block, tops, bottoms, blockColour, blockMaterial);
            for (int bj = 0; bj < Block; bj++)
            {
                for (int bi = 0; bi < Block; bi++)
                {
                    int b = bi + bj * Block;
                    bool inside = bi >= 1 && bi <= Verts && bj >= 1 && bj <= Verts;
                    int row = blockMaterial[b];
                    if (row < 0)
                    {
                        tops[b] = ground.Height((baseI + bi - 0.5f) * SandBed.Cell, (baseJ + bj - 0.5f) * SandBed.Cell);
                        blockColour[b] = 0;
                    }
                    else if (inside && looks != null && row < looks.Length)
                    {
                        // A column's own shade of its medium, fixed by where it is, so a pile
                        // is speckled the way a pour of grains is, and stays the same speckle.
                        int i = baseI + bi - 1, j = baseJ + bj - 1;
                        uint h = (uint)(i * 73856093) ^ (uint)(j * 19349663);
                        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                        int look = looks[row], step = (int)(h & 0xFFFF);
                        uint colour = blockColour[b];
                        if (look == SandMaterials.Glue) colour = SandMaterials.Vary(colour, look, step);
                        else
                        {
                            // Only the step's last three bits matter, and a pile is mostly one colour
                            // of one medium: keep the eight shades of the last one asked for.
                            if (colour != shadeFor || look != shadeLook)
                            {
                                for (int q = 0; q < SandMaterials.VarietySteps; q++) shades[q] = SandMaterials.Vary(colour, look, q);
                                shadeFor = colour; shadeLook = look;
                            }
                            colour = shades[step & (SandMaterials.VarietySteps - 1)];
                        }
                        blockColour[b] = colour;
                    }
                    if (inside)
                    {
                        int k = (bi - 1) + (bj - 1) * Verts;
                        cols[k] = blockColour[b];
                        has[k] = row >= 0;
                        known[k] = tops[b] > -9000f;
                    }
                }
            }

            // An empty vertex borrows its colour from a sand neighbour, so the pile's edge is
            // sand-coloured all the way into the ground and not black.
            for (int vj = 0; vj < Verts; vj++)
            {
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    if (has[k]) continue;
                    uint borrowed = 0;
                    for (int dj = -1; dj <= 1 && borrowed == 0; dj++)
                        for (int di = -1; di <= 1 && borrowed == 0; di++)
                        {
                            int ni = vi + di, nj = vj + dj;
                            if (ni < 0 || nj < 0 || ni >= Verts || nj >= Verts) continue;
                            if (has[ni + nj * Verts]) borrowed = cols[ni + nj * Verts];
                        }
                    cols[k] = borrowed;
                }
            }

            float spare = 0f;
            for (int k = 0; k < Block * Block; k++) if (tops[k] > -9000f) { spare = tops[k]; break; }

            // Scalars throughout: this runs for every vertex of every chunk a brush touches, and the
            // engine's vector types cost several times what the arithmetic does in this runtime.
            float cell = SandBed.Cell, lx = Light.x, ly = Light.y, lz = Light.z;
            for (int vj = 0; vj < Verts; vj++)
            {
                float wz = (baseJ + vj + 0.5f) * cell;
                for (int vi = 0; vi < Verts; vi++)
                {
                    int k = vi + vj * Verts;
                    int b = (vi + 1) + (vj + 1) * Block;
                    float y = known[k] ? (has[k] ? tops[b] + 0.006f : tops[b] - EdgeDip) : spare;
                    vertices[k] = new Vector3((baseI + vi + 0.5f) * cell, y, wz);

                    float hl = tops[b - 1], hr = tops[b + 1], hd = tops[b - Block], hu = tops[b + Block];
                    float nx = hl - hr, ny = 2f * cell, nz = hd - hu;
                    float len2 = nx * nx + ny * ny + nz * nz;
                    if (len2 > 1e-9f && hl > -9000f && hr > -9000f && hd > -9000f && hu > -9000f)
                    {
                        float inv = 1f / (float)System.Math.Sqrt(len2);
                        nx *= inv; ny *= inv; nz *= inv;
                    }
                    else { nx = 0f; ny = 1f; nz = 0f; }
                    normals[k] = new Vector3(nx, ny, nz);

                    uint p = cols[k];
                    byte r = (byte)(p & 0xFF), g = (byte)((p >> 8) & 0xFF), bl = (byte)((p >> 16) & 0xFF);
                    texels[k] = new Color32(r, g, bl, 255);

                    // Unlit, the slope is shaded into the colour and an empty vertex fades out.
                    float dot = nx * lx + ny * ly + nz * lz;
                    float shade = 0.62f + 0.38f * (dot < 0f ? 0f : dot > 1f ? 1f : dot);
                    colours[k] = new Color32((byte)(r * shade), (byte)(g * shade), (byte)(bl * shade),
                                             (byte)(has[k] || lit ? 255 : 0));
                }
            }

            int t = 0;
            for (int qj = 0; qj < SandBed.ChunkSide; qj++)
            {
                for (int qi = 0; qi < SandBed.ChunkSide; qi++)
                {
                    int a = qi + qj * Verts, b = a + 1, c2 = a + Verts, d = c2 + 1;
                    if (!(has[a] || has[b] || has[c2] || has[d])) continue;
                    if (!(known[a] && known[b] && known[c2] && known[d])) continue;
                    scratchTris[t++] = a; scratchTris[t++] = c2; scratchTris[t++] = d;
                    scratchTris[t++] = a; scratchTris[t++] = d; scratchTris[t++] = b;
                }
            }
            if (draw.tris.Length != t) draw.tris = new int[t];
            System.Array.Copy(scratchTris, draw.tris, t);
        }
    }

    /// <summary>
    /// Runs <see cref="ChunkMesher.Build"/> over a list of chunks on every core there is
    /// (<see cref="Workers.For"/>), each thread with a mesher of its own, and returns when all are done. The
    /// bed and the ground are only read, and the caller changes neither while this runs, so the threads
    /// need no locks; each chunk's result is written to that chunk alone.
    /// </summary>
    public class ChunkMesherPool
    {
        private readonly ChunkMesher[] meshers = new ChunkMesher[Workers.Helpers + 1];
        private readonly bool parallel;

        // The job being run.
        private SandBed bed;
        private IBedGround ground;
        private System.Collections.Generic.List<SandBed.Chunk> chunks;
        private int[] looks;
        private bool lit;

        public ChunkMesherPool() : this(true) { }

        /// <param name="parallel">False to do every chunk on the calling thread, for comparing.</param>
        public ChunkMesherPool(bool parallel)
        {
            this.parallel = parallel;
            for (int i = 0; i < meshers.Length; i++) meshers[i] = new ChunkMesher();
        }

        /// <summary>Threads the chunks are shared between, the caller's among them.</summary>
        public int Threads { get { return parallel ? Workers.Helpers + 1 : 1; } }

        public void Run(SandBed bed, IBedGround ground, System.Collections.Generic.List<SandBed.Chunk> chunks, int count, int[] looks, bool lit)
        {
            // A few chunks are quicker alone than handed round.
            if (!parallel || count < 6)
            {
                for (int i = 0; i < count; i++) meshers[0].Build(bed, ground, chunks[i], looks, lit);
                return;
            }
            this.bed = bed; this.ground = ground; this.chunks = chunks; this.looks = looks; this.lit = lit;
            bool all = Workers.For(count, Work);
            this.bed = null; this.ground = null; this.chunks = null;
            // Whatever a thread died halfway through is redone, on this one.
            if (!all) for (int i = 0; i < count; i++) meshers[0].Build(bed, ground, chunks[i], looks, lit);
        }

        private void Work(int i, int worker)
        {
            meshers[worker].Build(bed, ground, chunks[i], looks, lit);
        }
    }
}
