using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// What the mod keeps between runs of the game: the sand panel's settings and the name of the last sand file, as
    /// <c>key=value</c> lines in <c>settings.txt</c> in the mod's data folder (<c>Besiege_Data/Mods/Data/&lt;mod&gt;/</c>,
    /// through <c>Modding.ModIO</c>, the only file access a mod has). Read once, when first asked; written a moment
    /// after anything changes, not on every change.
    /// </summary>
    public static class SandSettings
    {
        private const string FileName = "settings.txt";
        private static Dictionary<string, string> values;
        private static bool dirty;
        private static float writeAt;

        private static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>();
            try
            {
                if (!Modding.ModIO.ExistsFile(FileName, true)) return;
                string[] lines = Modding.ModIO.ReadAllLines(FileName, true);
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0) values[lines[i].Substring(0, eq).Trim()] = lines[i].Substring(eq + 1).Trim();
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not read the saved settings: " + e.Message); }
        }

        public static string Get(string key, string otherwise)
        {
            Load();
            string v;
            return values.TryGetValue(key, out v) ? v : otherwise;
        }

        public static float Get(string key, float otherwise)
        {
            float f;
            // "NaN" and "Infinity" parse, and either would then be a setting.
            return float.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out f)
                   && SandGuard.Ok(f) ? f : otherwise;
        }

        public static int Get(string key, int otherwise)
        {
            int n;
            return int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : otherwise;
        }

        public static bool Get(string key, bool otherwise)
        {
            string v = Get(key, "");
            return v == "1" ? true : v == "0" ? false : otherwise;
        }

        public static void Set(string key, string value)
        {
            Load();
            string was;
            if (values.TryGetValue(key, out was) && was == value) return;
            values[key] = value;
            if (!dirty) writeAt = Time.realtimeSinceStartup + 1f;
            dirty = true;
        }

        public static void Set(string key, float value) { Set(key, value.ToString("R", CultureInfo.InvariantCulture)); }
        public static void Set(string key, int value) { Set(key, value.ToString(CultureInfo.InvariantCulture)); }
        public static void Set(string key, bool value) { Set(key, value ? "1" : "0"); }

        /// <summary>Called every frame: writes the file a second after the last change.</summary>
        public static void Tick()
        {
            if (!dirty || Time.realtimeSinceStartup < writeAt) return;
            Write();
        }

        /// <summary>Writes the file now if anything changed (the game is closing).</summary>
        public static void Flush()
        {
            if (dirty) Write();
        }

        private static void Write()
        {
            dirty = false;
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                lines.Sort(System.StringComparer.Ordinal);
                Modding.ModIO.WriteAllLines(FileName, lines.ToArray(), true);
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not save the settings: " + e.Message); }
        }
    }
}
