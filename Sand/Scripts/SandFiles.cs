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
            get { return !StatMaster.levelSimulating && FindView() != null; }
        }

        /// <summary>Whether this player may load a sand file now: anyone may save one, but a client's sand is the
        /// host's, and is loaded through the host only when it allows that.</summary>
        public static bool CanLoad { get { return Available && (!SandNet.IsClient || SandNet.HostAllowsLoad); } }

        /// <summary>A sand file's load is still being followed: the machine it replaced is waiting to be put back.</summary>
        public static bool Busy { get { return keep != null; } }

        /// <summary>Opens the game's save screen in the sand folder.</summary>
        public static void Save() { Open(true); }

        /// <summary>Opens the game's load screen in the sand folder.</summary>
        public static void Load() { Open(false); }

        private static void Open(bool save)
        {
            FileBrowserView view = FindView();
            if (view == null || StatMaster.levelSimulating || (!save && !CanLoad)) return;
            try
            {
                // The game's own buttons open the screen where it last saved or loaded, with the file of that save
                // selected -- its name already in the box (restoreFolder and restoreObject, set by SetRestoreFolder).
                // The sand's buttons open it the same way, after setting those two to the sand folder and the last sand
                // file, all in this frame: opened as the game would to learn the folder to give back afterwards, opened
                // again to go to the sand folder and pick the file and say "come back here", and opened a third time,
                // for real, as the game's own buttons do. Taking it there after it was open did not hold.
                view.Open(FileBrowserType.LocalMachines, save, true);
                AbstractObjectCollection collection = view.Controller != null ? view.Controller.Collection : null;
                before = collection != null ? collection.CurrentFolder : null;
                if (before != null && before.Name == Folder) before = OpenedRoot(collection);
                view.Close();
                Prime(view, save);
                view.Open(FileBrowserType.LocalMachines, save, true);
                mode = save ? Saving : Loading;
                navigate = true;
                createTried = true;
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

                    // Whether or not anything was saved or loaded: the screen was primed to come back to the sand
                    // folder, and the game's own Save and Load would otherwise open there.
                    GiveFolderBack(view);
                }
            }
            if (keep != null) PutMachineBack();
            // The thumbnail is taken a moment after the file is written, after the screen has closed; one not taken in a
            // few seconds is not the sand file's. And the camera's glow, turned off for the picture, is turned back on.
            if (thumbnailPending && Time.unscaledTime > thumbnailUntil) thumbnailPending = false;
            if (glowOff.Count > 0 && !thumbnailPending) GlowBack();
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
                // There already: nothing to do. (The root was read from disk first, every frame for a second and a half,
                // and the screen took that long to fill.)
                VirtualFolder current = collection.CurrentFolder;
                if (current != null && current.Name == Folder) return;
                VirtualFolder root = OpenedRoot(collection);
                if (root == null) return;
                if (current == null || current.ObjectPath.Path != root.ObjectPath.Path) { controller.OpenFolder(root); return; }
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

        // The screen's memory of where to reopen, set to the sand folder and the last sand file (see Open). It is opened at
        // the root, taken into the folder -- made, the first time -- the file picked, that remembered, and closed.
        private static void Prime(FileBrowserView view, bool save)
        {
            try
            {
                view.Open(FileBrowserType.LocalMachines, save, false);
                FileBrowserController controller = view.Controller;
                AbstractObjectCollection collection = controller != null ? controller.Collection : null;
                VirtualFolder root = OpenedRoot(collection);
                if (root == null) { view.Close(); return; }
                VirtualFolder sand = Child(root);
                if (sand == null)
                {
                    controller.OpenFolder(root);
                    controller.CreateFolder(Folder);
                    root = OpenedRoot(collection);
                    sand = Child(root);
                }
                if (sand != null)
                {
                    controller.OpenFolder(sand);
                    IVirtualObject last = Named(sand, LastName);
                    if (last != null)
                    {
                        FileBrowserView.FieldValues[sand.ObjectPath.ToString()] = last;
                        view.SelectObject(last);
                    }
                    view.SetRestoreFolder(false);
                }
                view.Close();
            }
            catch (System.Exception)
            {
                try { if (view.IsOpen) view.Close(); } catch (System.Exception) { }
            }
        }

        // ---- the sand file's name, as the game's machines keep theirs: the one last saved or loaded comes up in the box,
        // this session or the next (kept in the settings file).
        private static string LastName
        {
            get { return SandSettings.Get("lastSandFile", ""); }
            set { if (!string.IsNullOrEmpty(value)) SandSettings.Set("lastSandFile", value); }
        }

        private static IVirtualObject Named(VirtualFolder folder, string name)
        {
            if (folder == null || string.IsNullOrEmpty(name)) return null;
            foreach (IVirtualObject o in folder.GetObjects())
                if (o != null && !o.IsFolder && o.Name == name) return o;
            return null;
        }

        // What is in the screen's name box now: the name a file is being saved under, or the file being loaded.
        private static string TypedName(FileBrowserView view)
        {
            if (view == null) return null;
            Selectors.TextHolder[] boxes = view.GetComponentsInChildren<Selectors.TextHolder>(false);
            for (int i = 0; i < boxes.Length; i++)
            {
                string t = boxes[i] != null ? boxes[i].ValueText : null;
                if (!string.IsNullOrEmpty(t)) return t.Trim();
            }
            return null;
        }

        // The collection's root, read from disk. GetRoot makes a new folder object every time it is asked, with nothing in
        // it until it is opened: looked through as it came, it never held the sand folder, and the screen was never taken
        // there (except the first time, when making the folder opened it).
        private static VirtualFolder OpenedRoot(AbstractObjectCollection collection)
        {
            if (collection == null) return null;
            VirtualFolder root = collection.GetRoot();
            LocalFolder local = root as LocalFolder;       // its own Open is the one that reads the disk
            if (local != null) local.Open(); else if (root != null) root.Open();
            return root;
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
                thumbnailPending = true;
                thumbnailUntil = Time.unscaledTime + 5f;
                LastName = TypedName(view);
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
                // Only a sand file the player is loading, from a load screen. The game reads machine files for itself
                // too -- saving over one first reads the old one to keep it as a version (VersionMachine) -- and the old
                // file's sand replaced the sand being saved: the tracks were gone from the level and from the file.
                FileBrowserView screen = FindView();
                if (screen == null || !screen.IsOpen || screen.IsSaveMenu || mode == Saving) return;
                SandField.LoadSandFile(System.Convert.FromBase64String(info.MachineData.ReadString(DataKey)));
                if (mode != Idle)
                {
                        LastName = TypedName(FindView());
                }
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
        private static float thumbnailUntil;
        private static readonly List<Behaviour> glowOff = new List<Behaviour>();

        // Whether a camera is one the game takes thumbnails with: any ThumbnailCreator's (there can be more than one), or
        // failing that one rendering by hand into a texture.
        private static bool IsThumbnailCamera(Camera cam)
        {
            ThumbnailCreator[] all = Resources.FindObjectsOfTypeAll<ThumbnailCreator>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].thumbnailCamera == cam) return true;
            return false;
        }

        // The thumbnail camera's bloom and glow, off for the picture: bright sand came out doubled.
        private static void GlowOffFor(Camera cam)
        {
            MonoBehaviour[] effects = cam.GetComponents<MonoBehaviour>();
            for (int i = 0; i < effects.Length; i++)
            {
                MonoBehaviour b = effects[i];
                if (b == null || !b.enabled) continue;
                if (b is Bloom || b is FastBloom || b is BloomAndLensFlares || b is GlowEffect) { b.enabled = false; glowOff.Add(b); }
            }
        }

        private static void GlowBack()
        {
            for (int i = 0; i < glowOff.Count; i++) if (glowOff[i] != null) glowOff[i].enabled = true;
            glowOff.Clear();
        }

        private static void AimThumbnail(Camera cam)
        {
            if (!thumbnailPending || cam == null) return;
            try
            {
                if (!IsThumbnailCamera(cam)) return;
                thumbnailPending = false;
                GlowOffFor(cam);
                Bounds sand;
                if (!SandField.SandBounds(out sand)) return;
                // Looking down on it, as far back as puts every corner of the sand's box just inside the picture.
                Quaternion look = Quaternion.Euler(55f, 35f, 0f);
                Vector3 forward = look * Vector3.forward, right = look * Vector3.right, upward = look * Vector3.up;
                float tanV = Mathf.Tan(Mathf.Max(cam.fieldOfView, 10f) * 0.5f * Mathf.Deg2Rad), tanH = tanV * Mathf.Max(cam.aspect, 0.1f);
                float distance = 0.5f, nearest = float.MaxValue, farthest = 0f;
                Vector3 ext = sand.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 o = new Vector3((corner & 1) != 0 ? ext.x : -ext.x, (corner & 2) != 0 ? ext.y : -ext.y, (corner & 4) != 0 ? ext.z : -ext.z);
                    float x = Vector3.Dot(o, right), y = Vector3.Dot(o, upward), z = Vector3.Dot(o, forward);
                    distance = Mathf.Max(distance, Mathf.Abs(x) / tanH - z, Mathf.Abs(y) / tanV - z);
                }
                distance *= 1.03f;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 o = new Vector3((corner & 1) != 0 ? ext.x : -ext.x, (corner & 2) != 0 ? ext.y : -ext.y, (corner & 4) != 0 ? ext.z : -ext.z);
                    float z = distance + Vector3.Dot(o, forward);
                    nearest = Mathf.Min(nearest, z); farthest = Mathf.Max(farthest, z);
                }
                cam.transform.rotation = look;
                cam.transform.position = sand.center - forward * distance;
                cam.nearClipPlane = Mathf.Max(0.05f, nearest * 0.5f);
                cam.farClipPlane = farthest * 3f + 50f;
                Camera main = Camera.main;
                cam.cullingMask = (main != null ? main.cullingMask : ~0) | 1;
                SandField.DrawSandFor(cam);
            }
            catch (System.Exception) { thumbnailPending = false; }
        }

        private static FileBrowserView view;

        // The screen is inactive while closed, so FindObjectOfType does not see it.
        private static float nextLook;

        // Looked for at most twice a second while it is not there: the panel asks every frame whether its buttons
        // work, and FindObjectsOfTypeAll walks every object the game has loaded, assets included.
        private static FileBrowserView FindView()
        {
            if (view != null) return view;
            if (Time.unscaledTime < nextLook) return null;
            nextLook = Time.unscaledTime + 0.5f;
            FileBrowserView[] all = Resources.FindObjectsOfTypeAll<FileBrowserView>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject.scene.IsValid()) { view = all[i]; break; }
            return view;
        }
    }
}
