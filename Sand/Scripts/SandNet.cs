using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Sand in multiplayer. The host simulates it -- the bed and what every machine does to it,
    /// since the host is where Besiege runs a multiplayer level's physics -- and the clients
    /// draw what it sends, each making its own spray and dust:
    ///
    ///   Chunk    host to all: one chunk of bed that changed, as SandBed.Encode packs it
    ///   Clear    host to all: the bed is about to be sent from scratch
    ///   Hello    client to host: send me everything
    ///   Brush    client to host: the client's brush, poured or erased where it points
    ///   State    host to all: the host's switches -- whether other players may use the brush
    ///            and load sand files, the sand paused, changes kept -- when one changes and when a player arrives; the clients' panels show them greyed out
    ///   Load     client to host: a sand file, in pieces, to load in place of the sand
    ///
    /// A client that is in a local practice run of its own (Player.InLocalSim) is on its own
    /// and simulates its own sand, as in single player.
    /// </summary>
    public static class SandNet
    {
        private static MessageType chunkType, clearType, helloType, brushType, stateType, loadType;
        private static bool ready;

        /// <summary>The host's switch: whether other players may use the brush.</summary>
        public static bool AllowOthers = true;

        /// <summary>The host's switch: whether other players may load a sand file in place of the sand.</summary>
        public static bool AllowLoad;

        /// <summary>A client's view of the host's switches, as the host last said.</summary>
        public static bool HostAllowsOthers = true, HostAllowsLoad, HostFrozen, HostKeeps = true;

        /// <summary>Goes up each time the host's switches arrive changed, for the panel to show them.</summary>
        public static int HostStateVersion;

        /// <summary>Called once, from the mod's OnLoad: message types are made at load.</summary>
        public static void Init()
        {
            if (ready) return;
            try
            {
                chunkType = ModNetworking.CreateMessageType(DataType.ByteArray);
                clearType = ModNetworking.CreateMessageType(DataType.Integer);
                helloType = ModNetworking.CreateMessageType(DataType.Integer);
                brushType = ModNetworking.CreateMessageType(DataType.SingleArray);
                stateType = ModNetworking.CreateMessageType(DataType.SingleArray);
                loadType = ModNetworking.CreateMessageType(DataType.ByteArray);

                ModNetworking.Callbacks[chunkType] += OnChunk;
                ModNetworking.Callbacks[clearType] += OnClear;
                ModNetworking.Callbacks[helloType] += OnHello;
                ModNetworking.Callbacks[brushType] += OnBrush;
                ModNetworking.Callbacks[stateType] += OnState;
                ModNetworking.Callbacks[loadType] += OnLoadPiece;
                ready = true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Sand] multiplayer messages could not be set up; sand will be local: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ roles

        /// <summary>In a multiplayer session at all.</summary>
        public static bool InSession { get { return ready && StatMaster.isMP; } }

        /// <summary>
        /// In a session with somebody else in it. The level editor runs as a multiplayer session
        /// of one, so being in a session is not enough to show a switch about other players.
        /// </summary>
        public static bool OthersPresent
        {
            get
            {
                if (!InSession) return false;
                try
                {
                    List<Modding.Common.Player> all = Modding.Common.Player.GetAllPlayers();
                    return all != null && all.Count > 1;
                }
                catch (System.Exception) { return false; }
            }
        }

        /// <summary>
        /// A multiplayer client drawing the host's sand rather than simulating its own: not the
        /// host, and not in a local practice run.
        /// </summary>
        public static bool IsClient
        {
            get
            {
                if (!InSession || StatMaster.isHosting) return false;
                Modding.Common.Player me = null;
                try { me = Modding.Common.Player.GetLocalPlayer(); }
                catch (System.Exception) { }
                return me == null || !me.InLocalSim;
            }
        }

        /// <summary>The multiplayer host, which simulates the sand for everybody.</summary>
        public static bool IsHost { get { return InSession && StatMaster.isHosting; } }

        // ------------------------------------------------------------------ the host's side

        private const int ChunksPerSecond = 60;

        private static float chunkBudget;
        private static int lastFlags = -1;

        /// <summary>Sends what changed. Called every frame on the host.</summary>
        public static void HostSend(SandField field, SandBed bed)
        {
            if (!IsHost) return;
            bed.TrackNet = true;

            // The bed: changed chunks, at a steady rate, so a big level reaches someone who
            // has just joined over a few seconds rather than in one burst.
            chunkBudget = Mathf.Min(chunkBudget + ChunksPerSecond * Time.unscaledDeltaTime, 30f);
            while (chunkBudget >= 1f)
            {
                SandBed.Chunk c = bed.TakeNetDirty();
                if (c == null) break;
                chunkBudget -= 1f;
                Send(chunkType.CreateMessage(bed.Encode(c)));
            }

            // The host's switches: when one changes, and to everyone when somebody arrives (OnHello forgets what was
            // sent). Messages arrive in order and are not lost, so nothing is repeated on a clock.
            int flags = (AllowOthers ? 1 : 0) | (AllowLoad ? 2 : 0) | (SandField.Frozen ? 4 : 0) | (SandField.KeepBetweenRuns ? 8 : 0);
            if (flags != lastFlags)
            {
                lastFlags = flags;
                float[] state = { flags & 1, (flags >> 1) & 1, (flags >> 2) & 1, (flags >> 3) & 1 };
                Send(stateType.CreateMessage(state));
            }
        }

        /// <summary>Tells the clients the bed is being sent again from nothing.</summary>
        public static void HostCleared()
        {
            if (!IsHost) return;
            Send(clearType.CreateMessage(0));
        }

        private static void Send(Message m)
        {
            try { ModNetworking.SendToAll(m); }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not send to the other players: " + e.Message); }
        }

        // ------------------------------------------------------------------ the client's side

        private static bool saidHello;

        /// <summary>Asks the host for everything, once per session. Called every frame on a client.</summary>
        public static void ClientTick()
        {
            if (!IsClient) { saidHello = false; return; }
            if (saidHello) return;
            saidHello = true;
            try { ModNetworking.SendToHost(helloType.CreateMessage(0)); }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not reach the host: " + e.Message); }
        }

        /// <summary>The brush, from a client: the host pours or erases for it.</summary>
        public static void RequestBrush(Vector3 at, float radius, float amount, int medium, bool removing, int style, int seed, uint colour)
        {
            if (!IsClient || !HostAllowsOthers) return;
            // The colour's red, green and blue as one float: 24 bits, which a float holds exactly.
            float[] data = { at.x, at.y, at.z, radius, amount, medium, removing ? 1f : 0f, style, seed, colour & 0xFFFFFFu };
            try { ModNetworking.SendToHost(brushType.CreateMessage(data)); }
            catch (System.Exception) { }
        }

        private static void OnChunk(Message m)
        {
            if (!IsClient) return;
            SandField f = SandField.Instance;
            f.ReceiveChunk((byte[])m.GetData(0));
        }

        private static void OnClear(Message m)
        {
            if (!IsClient) return;
            SandField.Instance.ReceiveClear();
        }

        private static void OnHello(Message m)
        {
            if (!IsHost) return;
            SandField f = SandField.Existing;
            if (f != null) f.ResendAll();
            lastFlags = -1;
        }

        private static void OnBrush(Message m)
        {
            if (!IsHost || !AllowOthers) return;
            float[] d = (float[])m.GetData(0);
            if (d == null || d.Length < 7) return;
            // Every number here came from another player's machine. A NaN in any of them passes
            // through a clamp untouched -- every comparison against NaN is false -- and reaches
            // the bed, whose extent is then NaN for the rest of the session: no ground height
            // anywhere, and every brush after it poisoned. Seven floats are enough, from any
            // player in a public game. A position that is not a number is refused outright; a
            // number out of range becomes this host's own.
            if (!SandGuard.Ok(d[0]) || !SandGuard.Ok(d[1]) || !SandGuard.Ok(d[2])) return;
            Vector3 at = new Vector3(d[0], d[1], d[2]);
            float radius = SandGuard.Clamp(d[3], 0.1f, SandField.MaxBrushRadius, SandField.MaxBrushRadius);
            // Metres of rise this frame: a few at most, whatever a client asks for.
            float amount = SandGuard.Clamp(d[4], 0f, 5f, 0f);
            int style = SandGuard.Whole(d.Length > 7 ? d[7] : SandField.BrushCircle, 0, 255, SandField.BrushCircle);
            int seed = SandGuard.Whole(d.Length > 8 ? d[8] : 0f, 0, 1 << 24, 0);
            int medium = SandGuard.Whole(d[5], 0, SandMaterials.Count - 1, SandMaterials.Sand);
            uint colour = d.Length > 9 && SandGuard.Ok(d[9])
                ? ((uint)Mathf.RoundToInt(d[9]) & 0xFFFFFFu) | 0xFF000000u
                : SandMaterials.PackColour(SandMaterials.Tints[medium]);
            SandField f = SandField.Instance;
            if (d[6] > 0.5f) f.Erase(at, radius, amount, style, seed);
            else f.Paint(at, radius, amount, medium, style, seed, colour);
        }

        private static void OnState(Message m)
        {
            if (!IsClient) return;
            float[] d = (float[])m.GetData(0);
            if (d == null || d.Length < 4) return;
            bool others = d[0] > 0.5f, load = d[1] > 0.5f, frozen = d[2] > 0.5f, keeps = d[3] > 0.5f;
            if (others == HostAllowsOthers && load == HostAllowsLoad && frozen == HostFrozen && keeps == HostKeeps) return;
            HostAllowsOthers = others; HostAllowsLoad = load; HostFrozen = frozen; HostKeeps = keeps;
            HostStateVersion++;
        }

        // ------------------------------------------------------------------ a sand file from a client

        // A sand file can be far bigger than one message should be, so it goes in pieces: an id for the file, the
        // piece's place and how many there are, then the bytes.
        private const int PieceBytes = 32 * 1024;
        private const int MostPieces = 512;   // sixteen megabytes
        private static int loadId, loadTotal, loadGot;
        private static byte[][] loadPieces;

        /// <summary>A client's sand file, sent to the host to load for everybody (when the host allows it).</summary>
        public static void RequestLoad(byte[] data)
        {
            if (!IsClient || !HostAllowsLoad || data == null) return;
            int total = (data.Length + PieceBytes - 1) / PieceBytes;
            if (total < 1 || total > MostPieces) { Debug.LogWarning("[Sand] that sand file is too big to send to the host"); return; }
            int id = Random.Range(1, int.MaxValue);
            try
            {
                for (int i = 0; i < total; i++)
                {
                    int at = i * PieceBytes, n = Mathf.Min(PieceBytes, data.Length - at);
                    byte[] piece = new byte[8 + n];
                    int h = Bytes.PutInt(piece, 0, id);
                    piece[h++] = (byte)(i & 0xFF); piece[h++] = (byte)(i >> 8);
                    piece[h++] = (byte)(total & 0xFF); piece[h++] = (byte)(total >> 8);
                    System.Array.Copy(data, at, piece, 8, n);
                    ModNetworking.SendToHost(loadType.CreateMessage(piece));
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not send the sand file to the host: " + e.Message); }
        }

        private static void OnLoadPiece(Message m)
        {
            if (!IsHost || !AllowLoad) return;
            byte[] b = (byte[])m.GetData(0);
            if (b == null || b.Length < 8) return;
            int h = 0;
            int id = Bytes.GetInt(b, ref h);
            int index = b[4] | (b[5] << 8), total = b[6] | (b[7] << 8);
            if (total < 1 || total > MostPieces || index >= total) return;
            if (id != loadId || loadPieces == null || loadTotal != total)
            {
                loadId = id; loadTotal = total; loadGot = 0;
                loadPieces = new byte[total][];
            }
            if (loadPieces[index] != null) return;
            byte[] piece = new byte[b.Length - 8];
            System.Array.Copy(b, 8, piece, 0, piece.Length);
            loadPieces[index] = piece;
            if (++loadGot < total) return;

            int length = 0;
            for (int i = 0; i < total; i++) length += loadPieces[i].Length;
            byte[] data = new byte[length];
            int at = 0;
            for (int i = 0; i < total; i++) { System.Array.Copy(loadPieces[i], 0, data, at, loadPieces[i].Length); at += loadPieces[i].Length; }
            loadPieces = null; loadId = 0;
            SandField.LoadSandFile(data, false);
        }
    }
}
