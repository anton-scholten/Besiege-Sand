using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sand panel's folded icon: one pile of sand, with grains still falling on it, on a
    /// clear background. The plate behind it is not part of the picture: the panel lays the icon
    /// over the same translucent background its open top bar has (SandHud.MakeIcon), so the
    /// folded button is the colour and transparency of that bar. Drawn pixel by pixel, so changing it means changing
    /// this file and rebuilding the mod.
    ///
    /// The art is laid out on a square <see cref="Design"/> units across and drawn at whatever
    /// size <see cref="Draw"/> is asked for, all of it scaled by one factor. Nothing in it is a
    /// pixel of any one texture, which is what lets the same drawing be both the 52-unit button
    /// in the panel and the mod's Workshop thumbnail: <c>tools/make-thumbnail.sh</c> writes
    /// <c>Sand/Resources/Thumbnail.png</c> out of this, and the game loads that file back for
    /// the button, so there is one picture and not two that have to be kept alike.
    ///
    /// The sand is made of <b>pixels</b>: the surface is a grid of <see cref="Pixel"/>-unit
    /// squares and every one of them is a single flat colour, so the pile's silhouette steps
    /// rather than smoothing. Nothing on the sand is anti-aliased or blended; only the plate's
    /// rounded corners are smoothed, because those are the panel's own shape and not sand.
    ///
    /// How it is drawn, and what to change:
    /// <list type="bullet">
    /// <item>Pixel (0, 0) is the <b>bottom</b>-left corner: y runs up, as in every Unity texture.
    /// Colours come back as one flat array in the order <c>SetPixels</c> wants, bottom row first.</item>
    /// <item>The plate: a square with its corners rounded to <c>Corner</c> units. It is left
    /// clear -- only the sand is drawn -- and the sand is cut to its rounded corners, which are
    /// the one soft edge in the picture.</item>
    /// <item>The pile is one <see cref="Crest"/> curve on the bare plate -- no skirt, no second
    /// dune. Its flanks run past the left and right of the frame, so its base reaches the bottom
    /// edge of the image at both sides rather than standing on a shelf above it.</item>
    /// <item>Four colours, each with a slightly lighter and a slightly darker version of itself:
    /// <see cref="Main"/>, <see cref="Shadow"/>, <see cref="GrainDark"/>, <see cref="GrainLight"/>.
    /// The lit sand is drawn from a palette weighted towards <see cref="Main"/> and the sand in
    /// shadow from one weighted towards <see cref="Shadow"/>, with the grains mixed sparingly
    /// through both. Which of the twelve a pixel is comes from a hash of where it is, so the
    /// pile is speckled the same way every time it is drawn.</item>
    /// <item><b>The sun is low.</b> The sand below <see cref="ShadowFrac"/> of a column's height
    /// is in shadow and the sand above it is lit, so about half the pile is shadowed. The line
    /// between the two colours is a curve, because it follows the pile's own outline up over
    /// the crest and back down, tilted a little by <see cref="Tilt"/>.</item>
    /// <item>The grains are single blocks of <see cref="GrainPixels"/> sand pixels square --
    /// all of them exactly the same size -- falling on the crest, each lit as the sand it will
    /// land on.</item>
    /// </list>
    /// </summary>
    public static class SandIcon
    {
        /// <summary>The square the art is laid out on, in its own units. Every number below is
        /// in these, and they are scaled to the size asked for at draw time.</summary>
        public const int Design = 64;

        /// <summary>The size of one sand pixel, in design units. The pile and the falling grains
        /// are both made of these, so this sets how coarse the whole thing looks.</summary>
        public const int Pixel = 2;

        /// <summary>How far the plate's corners are rounded, in design units.</summary>
        private const float Corner = 12f;

        /// <summary>The plate: clear, so the background the panel puts behind the icon shows.</summary>
        private static readonly Color Plate = new Color(0f, 0f, 0f, 0f);

        /// <summary>The sand in the light: the main colour of the pile.</summary>
        public static readonly Color Main = Rgb(0xFB, 0xD9, 0x5E);

        /// <summary>The sand in shadow, under the low sun.</summary>
        public static readonly Color Shadow = Rgb(0xE3, 0xA2, 0x3E);

        /// <summary>A darker grain, mixed through both the lit sand and the shadow.</summary>
        public static readonly Color GrainDark = Rgb(0xC0, 0x95, 0x2F);

        /// <summary>A lighter grain, mixed through both.</summary>
        public static readonly Color GrainLight = Rgb(0xEA, 0xC4, 0x4B);

        /// <summary>How far <see cref="Nudge"/> lightens or darkens a colour to make the
        /// second version of it. Small: these are grains of one colour of sand, not two.</summary>
        private const float NudgeAmount = 0.08f;

        /// <summary>How much of the pile is in shadow, as a fraction of its height at that column.
        /// Half, because that is what a sun near the horizon does.</summary>
        private const float ShadowFrac = 0.5f;

        /// <summary>How far the shadow line is tilted across the frame, so the terminator is not
        /// the same height everywhere and the pile reads as lit from one side.</summary>
        private const float Tilt = 0.12f;

        /// <summary>The one pile: crest in the middle of the frame, and both flanks running out
        /// past its sides so the sand reaches the bottom edge at each of them.</summary>
        private const float Centre = 32f, Left = 30f, Right = 30f, Rise = 38f;

        /// <summary>How many sand pixels square a falling grain is. All of them are this size:
        /// grains of different sizes read as a mistake in a picture this flat, not as sand.</summary>
        private const int GrainPixels = 2;

        /// <summary>The grains still falling on the pile: the x and y of each one's bottom-left
        /// corner, two apiece. Every one is <see cref="GrainPixels"/> pixels square.</summary>
        private static readonly float[] Falling = { 26f, 46f, 33f, 50f, 22f, 52f, 38f, 45f, 29f, 56f };

        /// <summary>Everything a lit sand pixel can be: mostly the main colour, with a lighter
        /// and a darker version of it and a few of the two grain colours and their versions.</summary>
        private static readonly Color[] Lit =
        {
            Main, Main, Main, Main, Main, Main, Main, Main,          // the pile is mostly one colour
            Main, Main, Main, Main, Main, Main, Main, Main,
            Main, Main, Main, Main, Main, Main, Main, Main,
            Main, Main, Nudge(Main, 1f), Nudge(Main, 1f),
            Nudge(Main, -1f), Nudge(Main, -1f),
            GrainLight, Nudge(GrainLight, -1f), GrainDark, Nudge(GrainDark, -1f),
        };

        /// <summary>Everything a sand pixel in shadow can be: the shadow colour and its versions,
        /// with rather more of the dark grains, since shadowed sand is where the dark grains
        /// show.</summary>
        private static readonly Color[] Dim =
        {
            Shadow, Shadow, Shadow, Shadow, Shadow, Shadow,           // mostly the shadow colour
            Shadow, Shadow, Shadow, Shadow, Shadow, Shadow,
            Shadow, Shadow, Shadow, Shadow, Shadow, Shadow,
            Shadow, Shadow, Shadow, Shadow, Shadow, Shadow,
            Shadow, Shadow, Nudge(Shadow, 1f), Nudge(Shadow, 1f),
            Nudge(Shadow, -1f), Nudge(Shadow, -1f), Nudge(Shadow, -1f),
            GrainDark, Nudge(GrainDark, -1f), Nudge(GrainDark, -1f), GrainLight,
        };

        /// <summary>
        /// The icon's pixels, <paramref name="n"/> square, bottom row first and ready for
        /// <c>Texture2D.SetPixels</c>. <paramref name="n"/> may be anything: the drawing is scaled
        /// to it rather than sampled, so the 52-unit button and the Workshop thumbnail are the
        /// same picture rather than one an upscale of the other.
        ///
        /// Everything but the sand is clear; the sand itself is cut to the plate's rounded corners.
        /// </summary>
        public static Color[] Draw(int n)
        {
            int cells = Design / Pixel;
            Color[] cell = new Color[cells * cells];
            for (int i = 0; i < cell.Length; i++) cell[i] = Plate;

            Pile(cell, cells);
            Grains(cell, cells);

            // Every sand pixel is the same size at every texture size: the grid is the drawing,
            // and this only scales it up into the texture and cuts the plate's corners off.
            float k = n / (float)Design;
            float cellPx = n / (float)cells;
            Color[] px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float fx = x + 0.5f, fy = y + 0.5f;
                    float cx = Mathf.Clamp(fx, Corner * k, n - Corner * k);
                    float cy = Mathf.Clamp(fy, Corner * k, n - Corner * k);
                    float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy));
                    int gx = (int)(x / cellPx), gy = (int)(y / cellPx);
                    Color c = gx < cells && gy < cells ? cell[gy * cells + gx] : Plate;
                    c.a *= Mathf.Clamp01(Corner * k - d + 0.5f);      // the rounded corners only
                    px[y * n + x] = c;
                }
            return px;
        }

        /// <summary>The pile, laid onto the plate. Each column's sand is as tall as the crest is
        /// there, and the sand below <see cref="ShadowFrac"/> of that is in shadow -- measured
        /// against the pile that is actually visible, so every column gets its half.</summary>
        private static void Pile(Color[] cell, int cells)
        {
            for (int gx = 0; gx < cells; gx++)
            {
                float x = (gx + 0.5f) * Pixel;
                float top = Rise * Crest(x, Centre, Left, Right);
                if (top <= 0f) continue;

                // The terminator: a share of this column's own height, tilted a little across the
                // frame. It is a curve and not a line because it follows the pile's outline.
                float line = top * (ShadowFrac + Tilt * (x / Design - 0.5f));

                for (int gy = 0; gy < cells; gy++)
                {
                    float y = (gy + 0.5f) * Pixel;
                    if (y >= top) continue;
                    cell[gy * cells + gx] = Speckle(gx, gy, y < line);
                }
            }
        }

        /// <summary>The grains still falling on the pile: each one <see cref="GrainPixels"/>
        /// sand pixels square, every one of them the same size. A grain is one flat colour
        /// rather than four speckled pixels: at this size a grain with two shades in it reads
        /// as a smudge, not as a grain.</summary>
        private static void Grains(Color[] cell, int cells)
        {
            for (int g = 0; g < Falling.Length; g += 2)
            {
                int gx = (int)(Falling[g] / Pixel), gy = (int)(Falling[g + 1] / Pixel);
                if (gx < 0 || gy < 0 || gx >= cells || gy >= cells) continue;
                Color grain = Speckle(gx, gy, false);              // in the air, so in the light
                for (int j = 0; j < GrainPixels; j++)
                    for (int i = 0; i < GrainPixels; i++)
                    {
                        int px = gx + i, py = gy + j;
                        if (px < 0 || px >= cells || py < 0 || py >= cells) continue;
                        cell[py * cells + px] = grain;
                    }
            }
        }

        /// <summary>How high the pile is at column x: nothing off its flanks, and on them a half
        /// cosine, flat where it crosses the crest and easing away to the toe. Both flanks are
        /// wider than the frame, so the sand is still there at the left and right edges and
        /// runs off them.</summary>
        private static float Crest(float x, float centre, float left, float right)
        {
            float u = Mathf.Abs((x - centre) / (x < centre ? left : right));
            return u >= 1f ? 0f : 0.5f + 0.5f * Mathf.Cos(Mathf.PI * u);
        }

        /// <summary>One sand pixel: out of the shadow palette or the lit one, and which of the
        /// twelve it is comes from a hash of where the pixel is, so the pile is speckled the
        /// same way every time it is drawn.</summary>
        private static Color Speckle(int gx, int gy, bool shadow)
        {
            Color[] from = shadow ? Dim : Lit;
            uint h = (uint)(gx * 73856093) ^ (uint)(gy * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return from[h % (uint)from.Length];
        }

        /// <summary>A colour, nudged a shade lighter or darker: towards white and towards black,
        /// rather than by scaling, so brightening never clips a channel flat.</summary>
        private static Color Nudge(Color c, float way)
        {
            float t = way > 0f ? NudgeAmount * way : -NudgeAmount;
            float r = t > 0f ? c.r + (1f - c.r) * t : c.r * (1f + t);
            float g = t > 0f ? c.g + (1f - c.g) * t : c.g * (1f + t);
            float b = t > 0f ? c.b + (1f - c.b) * t : c.b * (1f + t);
            return new Color(r, g, b, 1f);
        }

        private static Color Rgb(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
    }
}