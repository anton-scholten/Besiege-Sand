using System.Collections.Generic;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// The sand saved and loaded on its own, through Besiege's own save and load screens, in a folder of its
    /// own (<c>SavedMachines/Sand_mod</c>).
    ///
    /// A mod cannot write a machine file -- <c>XmlSaver.Save</c> is blacklisted and <c>ModIO</c> will not
    /// reach <c>SavedMachines</c> -- so the game writes it: the panel's Save opens the game's save screen in
    /// that folder, and when the game saves there (<c>XmlSaver.OnSave</c>, which runs before anything is
    /// written) the sand goes into the file's machine data and every block but the starting block comes
    /// out. A sand file is a machine of one block that carries the sand.
    ///
    /// Loading one, from the panel's Load or from anywhere, puts its sand in place of the sand there is
    /// (<c>XmlLoader.OnLoad</c>, as the file is read). The game then loads the file as a machine: with
    /// "add to machine" that adds nothing (the file says it was saved without its starting block); with a
    /// plain load it replaces the machine with the one block, and the machine as it was is put back as
    /// soon as that load has finished.
    /// </summary>
    public static class SandFiles
    {
        public const string Folder = "Sand_mod";
        private const string DataKey = "SandModSand";
        private const string NoStartingBlockKey = "SavedWithoutStartingBlock";

        private const int Idle = 0, Saving = 1, Loading = 2;
        private static int mode = Idle;
        private static bool navigate;
        private static int navigateFrames;

        // The machine a sand file's plain load replaced, to put back once that load is done.
        private static MachineInfo keep;
        private static int keepFrames;

        private static bool hooked;

        /// <summary>Listens for the game saving and loading machines. Once, at load.</summary>
        public static void Init()
        {
            if (hooked) return;
            hooked = true;
            XmlSaver.OnSave += OnSave;
            XmlLoader.OnLoad += OnLoad;
        }

        /// <summary>Whether the panel's buttons can do anything now: building, not running a machine.</summary>
        public static bool Available
        {
            get { return !StatMaster.levelSimulating && !SandNet.IsClient && FindView() != null; }
        }

        /// <summary>Opens the game's save screen in the sand folder.</summary>
        public static void Save() { Open(true); }

        /// <summary>Opens the game's load screen in the sand folder.</summary>
        public static void Load() { Open(false); }

        private static void Open(bool save)
        {
            FileBrowserView view = FindView();
            if (view == null || StatMaster.levelSimulating || SandNet.IsClient) return;
            try
            {
                view.Open(FileBrowserType.LocalMachines, save, false);
                mode = save ? Saving : Loading;
                navigate = true;
                navigateFrames = 0;
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not open the game's " + (save ? "save" : "load") + " screen: " + e.Message); }
        }

        /// <summary>Called every frame by the panel: steers the screen into the folder, notices it closing, and puts a machine back.</summary>
        public static void Tick()
        {
            if (navigate) Navigate();
            if (mode != Idle && !navigate)
            {
                FileBrowserView view = FindView();
                if (view == null || !view.IsOpen) mode = Idle;
            }
            if (keep != null) PutMachineBack();
        }

        // Into SavedMachines/Sand_mod, making it the first time. The screen fills itself over a frame or two
        // after it opens, so this tries for a little while.
        private static void Navigate()
        {
            navigateFrames++;
            if (navigateFrames > 120) { navigate = false; return; }
            FileBrowserView view = FindView();
            if (view == null || !view.IsOpen) { if (navigateFrames > 10) navigate = false; return; }
            try
            {
                FileBrowserController controller = view.Controller;
                AbstractObjectCollection collection = controller != null ? controller.Collection : null;
                if (collection == null) return;
                VirtualFolder root = collection.GetRoot();
                if (root == null) return;
                VirtualFolder current = collection.CurrentFolder;
                if (current != null && current.Name == Folder) { navigate = false; return; }
                VirtualFolder sand = Child(root);
                if (sand == null)
                {
                    if (current != root) { controller.OpenFolder(root); return; }
                    controller.CreateFolder(Folder);
                    collection.Refresh();
                    sand = Child(root);
                    if (sand == null) return;
                }
                controller.OpenFolder(sand);
            }
            catch (System.Exception e)
            {
                navigate = false;
                Debug.LogWarning("[Sand] could not open the " + Folder + " folder: " + e.Message);
            }
        }

        private static VirtualFolder Child(VirtualFolder parent)
        {
            foreach (IVirtualObject o in parent.GetObjects())
                if (o != null && o.IsFolder && o.Name == Folder) return o as VirtualFolder;
            return null;
        }

        private static bool InSandFolder(FileBrowserView view)
        {
            if (view == null || !view.IsOpen) return false;
            FileBrowserController controller = view.Controller;
            AbstractObjectCollection collection = controller != null ? controller.Collection : null;
            VirtualFolder current = collection != null ? collection.CurrentFolder : null;
            return current != null && current.Name == Folder;
        }

        // The game is about to write a machine file. Only the panel's own save, into the sand folder, becomes
        // a sand file: the sand in, the blocks out.
        private static void OnSave(MachineInfo info)
        {
            try
            {
                if (mode != Saving || info == null) return;
                FileBrowserView view = FindView();
                if (!InSandFolder(view) || !view.IsSaveMenu) return;
                byte[] data = SandField.SandFileBytes();
                if (data == null) return;
                info.MachineData.Write(DataKey, System.Convert.ToBase64String(data));
                info.MachineData.Write(NoStartingBlockKey, true);
                BlockInfo start = null;
                for (int i = 0; i < info.Blocks.Count; i++)
                    if (info.Blocks[i] != null && info.Blocks[i].ID == BlockType.StartingBlock) { start = info.Blocks[i]; break; }
                info.Blocks.Clear();
                if (start != null) info.Blocks.Add(start);
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not save the sand: " + e.Message); }
        }

        // The game has read a machine file. A sand file's sand replaces the sand there is, and the machine it is
        // about to replace is kept to put back.
        private static void OnLoad(MachineInfo info)
        {
            try
            {
                if (info == null || info.MachineData == null || !info.MachineData.HasKey(DataKey)) return;
                SandField.LoadSandFile(System.Convert.FromBase64String(info.MachineData.ReadString(DataKey)));
                Machine machine = Machine.Active();
                keep = machine != null && machine.BuildingBlocks != null && machine.BuildingBlocks.Count > 1 ? machine.CreateMachineInfo(true) : null;
                keepFrames = 0;
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not load the sand: " + e.Message); }
        }

        // Once the load that followed has finished: if it left the machine as the sand file's one block, the
        // machine as it was goes back. An "add to machine" load added nothing and leaves it be.
        private static void PutMachineBack()
        {
            keepFrames++;
            Machine machine = Machine.Active();
            if (machine == null) { keep = null; return; }
            if (keepFrames < 3 || machine.IsLoadingMachine) { if (keepFrames > 600) keep = null; return; }
            MachineInfo was = keep;
            keep = null;
            try
            {
                if (machine.BuildingBlocks != null && machine.BuildingBlocks.Count <= 1 && was.Blocks.Count > 1)
                    machine.LoadMachineInfo(was, false);
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not put the machine back after loading sand: " + e.Message); }
        }

        private static FileBrowserView view;

        // The screen is inactive while closed, so FindObjectOfType does not see it.
        private static FileBrowserView FindView()
        {
            if (view != null) return view;
            FileBrowserView[] all = Resources.FindObjectsOfTypeAll<FileBrowserView>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject.scene.IsValid()) { view = all[i]; break; }
            return view;
        }
    }
}
