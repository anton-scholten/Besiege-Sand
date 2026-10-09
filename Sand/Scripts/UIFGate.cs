namespace SandMod
{
    /// <summary>
    /// Whether UI Factory has loaded, asked without touching any of its types. Mono resolves an assembly the first time
    /// code that mentions it is compiled, and when the assembly is not there yet -- UI Factory loading after this mod,
    /// with other mods in between -- it marks the reference missing for good: every use of UI Factory fails from then
    /// on, for the rest of the session, though it loads a moment later. The panel went plain, and every frame threw
    /// "Could not load file or assembly 'Besiege.UI.Bridge'". So nothing that mentions UI Factory (UIF) runs until this
    /// says it is loaded. <c>Modding.Mods.IsModLoaded</c> is the mod loader's own answer, by UI Factory's mod id.
    /// </summary>
    public static class UIFGate
    {
        private static readonly System.Guid Id = new System.Guid("61d89dcf-88a2-4a16-8eb2-08aeed441f1d");

        public static bool Loaded
        {
            get
            {
                try { return Modding.Mods.IsModLoaded(Id); }
                catch (System.Exception) { return false; }
            }
        }
    }
}
