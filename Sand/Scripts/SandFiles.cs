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
        private static bool navigate, createTried;
        private static float openedAt;

        // The folder the game's screen would have opened in, to give its own buttons back after a sand file was saved
        // or loaded (the game remembers the folder of the last save or load, and its own Save and Load reopen there).
        private static VirtualFolder before;
        private static bool fileDone;

        // The next thumbnail the game renders is the sand file's: a view of the sand instead of the machine.
        private static bool thumbnailPending;

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
            Camera.onPreCull += AimThumbnail;
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
                // Opened as the game's own buttons open it, in the folder it last saved or loaded in, so that folder
                // is known and can be given back afterwards.
                view.Open(FileBrowserType.LocalMachines, save, true);
                AbstractObjectCollection collection = view.Controller != null ? view.Controller.Collection : null;
                before = collection != null ? collection.CurrentFolder : null;
                if (before != null && before.Name == Folder) before = collection.GetRoot();
                mode = save ? Saving : Loading;
                navigate = true;
                createTried = false;
                fileDone = false;
                openedAt = Time.unscaledTime;
                Navigate();
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not open the game's " + (save ? "save" : "load") + " screen: " + e.Message); }
        }

        /// <summary>Called every frame by the panel: steers the screen into the folder, notices it closing, and puts a machine back.</summary>
        public static void Tick()
        {
            if (navigate) Navigate();
            if (mode != Idle)
            {
                FileBrowserView view = FindView();
                if (view == null || !view.IsOpen)
                {
                    mode = Idle;
                    navigate = false;
                    thumbnailPending = false;     // a thumbnail not taken by now is not the sand file's
                    if (fileDone) GiveFolderBack(view);
                }
            }
            if (keep != null) PutMachineBack();
        }

        // Into SavedMachines/Sand_mod, making it the first time. Held there for the first moment the screen is open:
        // the screen fills itself over a few frames, and with a machine loaded something in that put it back in the
        // machine's folder after it had been taken to the sand's. Given up as soon as the player clicks in it.
        private static void Navigate()
        {
            float open = Time.unscaledTime - openedAt;
            if (open > 1.5f || (open > 0.2f && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))) { navigate = false; return; }
            FileBrowserView view = FindView();
            if (view == null || !view.IsOpen) return;
            try
            {
                FileBrowserController controller = view.Controller;
                AbstractObjectCollection collection = controller != null ? controller.Collection : null;
                if (collection == null) return;
                VirtualFolder root = collection.GetRoot();
                if (root == null) return;
                VirtualFolder current = collection.CurrentFolder;
                if (current != null && current.Name == Folder) return;
                if (current != root) { controller.OpenFolder(root); return; }
                VirtualFolder sand = Child(root);
                if (sand == null)
                {
                    if (createTried) return;
                    createTried = true;
                    controller.CreateFolder(Folder);
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
                fileDone = true;
                thumbnailPending = true;
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
                if (mode != Idle) fileDone = true;
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

        // The game's own Save and Load open where it last saved or loaded. After a sand file that is the sand folder; this
        // puts back the folder they would have opened before. The screen is opened, taken there, told that is the folder
        // to come back to, and closed again, all in one frame, so nothing of it is drawn.
        private static void GiveFolderBack(FileBrowserView view)
        {
            fileDone = false;
            if (view == null) return;
            try
            {
                view.Open(FileBrowserType.LocalMachines, false, false);
                FileBrowserController controller = view.Controller;
                if (controller != null && before != null) controller.OpenFolder(before);
                view.SetRestoreFolder(false);
                view.Close();
            }
            catch (System.Exception)
            {
                try { if (view.IsOpen) view.Close(); } catch (System.Exception) { }
            }
            before = null;
        }

        // ---- the thumbnail. The game renders a machine file's thumbnail right after writing it, with its own camera aimed
        // at the machine; for a sand file that is a picture of the starting block. Just before that camera culls, it is
        // aimed at the sand instead, sees the whole scene, and is given the sand to draw; the game puts the camera back
        // itself when it has captured the picture.
        private static ThumbnailCreator thumbnails;

        private static void AimThumbnail(Camera cam)
        {
            if (!thumbnailPending || cam == null) return;
            try
            {
                if (thumbnails == null)
                {
                    ThumbnailCreator[] all = Resources.FindObjectsOfTypeAll<ThumbnailCreator>();
                    for (int i = 0; i < all.Length; i++)
                        if (all[i] != null && all[i].gameObject.scene.IsValid()) { thumbnails = all[i]; break; }
                }
                if (thumbnails == null || cam != thumbnails.thumbnailCamera) return;
                thumbnailPending = false;
                Bounds sand;
                if (!SandField.SandBounds(out sand)) return;
                float reach = Mathf.Max(Mathf.Max(sand.extents.x, sand.extents.z), Mathf.Max(sand.extents.y, 1f));
                float half = Mathf.Max(cam.fieldOfView, 10f) * 0.5f * Mathf.Deg2Rad;
                float distance = reach / Mathf.Tan(half) * 1.15f + reach;
                Quaternion look = Quaternion.Euler(50f, 35f, 0f);
                Vector3 forward = look * Vector3.forward;
                cam.transform.rotation = look;
                cam.transform.position = sand.center - forward * distance;
                cam.nearClipPlane = Mathf.Max(0.3f, distance * 0.01f);
                cam.farClipPlane = distance * 4f + reach * 4f;
                Camera main = Camera.main;
                cam.cullingMask = (main != null ? main.cullingMask : ~0) | 1;
                SandField.DrawSandFor(cam);
            }
            catch (System.Exception) { thumbnailPending = false; }
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
