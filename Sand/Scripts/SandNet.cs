using System.Collections.Generic;
using Modding;
using UnityEngine;

namespace SandMod
{
    /// <summary>
    /// Sand in multiplayer. The host simulates it -- the grains, the bed, the regions, every
    /// machine's sprayers and what sand does to every machine, since the host is where
    /// Besiege runs a multiplayer level's physics -- and the clients draw what it sends:
    ///
    ///   Chunk    host to all: one chunk of bed that changed, as SandBed.Encode packs it
    ///   Grains   host to all: where the moving grains are and how fast they are going, a few
    ///            times a second; a client carries them along their velocity in between
    ///   Clear    host to all: the bed is about to be sent from scratch
    ///   Hello    client to host: send me everything
    ///   Brush    client to host: the client's brush, poured or erased where it points
    ///   State    host to all: whether the host lets other players use the brush
    ///
    /// A client that is in a local practice run of its own (Player.InLocalSim) is on its own
    /// and simulates its own sand, as in single player.
    /// </summary>
    public static class SandNet
    {
        private static MessageType chunkType, grainsType, clearType, helloType, brushType, stateType;
        private static bool ready;

        /// <summary>The host's switch: whether other players may use the brush.</summary>
        public static bool AllowOthers = true;

        /// <summary>A client's view of that switch, as the host last said.</summary>
        public static bool HostAllowsOthers = true;

        /// <summary>Called once, from the mod's OnLoad: message types are made at load.</summary>
        public static void Init()
        {
            if (ready) return;
            try
            {
                chunkType = ModNetworking.CreateMessageType(DataType.ByteArray);
                grainsType = ModNetworking.CreateMessageType(DataType.ByteArray);
                clearType = ModNetworking.CreateMessageType(DataType.Integer);
                helloType = ModNetworking.CreateMessageType(DataType.Integer);
                brushType = ModNetworking.CreateMessageType(DataType.SingleArray);
                stateType = ModNetworking.CreateMessageType(DataType.Integer);

                ModNetworking.Callbacks[chunkType] += OnChunk;
                ModNetworking.Callbacks[grainsType] += OnGrains;
                ModNetworking.Callbacks[clearType] += OnClear;
                ModNetworking.Callbacks[helloType] += OnHello;
                ModNetworking.Callbacks[brushType] += OnBrush;
                ModNetworking.Callbacks[stateType] += OnState;
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
        private const float GrainInterval = 0.2f;
        private const int MaxGrainsSent = 3000;
        private const float StateInterval = 2f;

        private static float chunkBudget;
        private static float grainClock, stateClock;
        private static bool sentEmpty;
        private static bool lastAllow = true;

        /// <summary>Sends what changed. Called every frame on the host.</summary>
        public static void HostSend(SandField field, SandBed bed, Grain[] grains, int count)
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

            grainClock += Time.unscaledDeltaTime;
            if (grainClock >= GrainInterval)
            {
                grainClock = 0f;
                if (count > 0 || !sentEmpty)
                {
                    Send(grainsType.CreateMessage(EncodeGrains(grains, count)));
                    sentEmpty = count == 0;
                }
            }

            stateClock += Time.unscaledDeltaTime;
            if (stateClock >= StateInterval || lastAllow != AllowOthers)
            {
                stateClock = 0f;
                lastAllow = AllowOthers;
                Send(stateType.CreateMessage(AllowOthers ? 1 : 0));
            }
        }

        /// <summary>Tells the clients the bed is being sent again from nothing.</summary>
        public static void HostCleared()
        {
            if (!IsHost) return;
            Send(clearType.CreateMessage(0));
            sentEmpty = false;
        }

        private static void Send(Message m)
        {
            try { ModNetworking.SendToAll(m); }
            catch (System.Exception e) { Debug.LogWarning("[Sand] could not send to the other players: " + e.Message); }
        }

        private static readonly List<uint> palette = new List<uint>();
        private static readonly Dictionary<uint, int> paletteIndex = new Dictionary<uint, int>();

        /// <summary>
        /// The moving grains: an origin, a palette of their colours, and per grain its offset in
        /// centimetres, its velocity in quarters of a metre a second, its row and its colour's
        /// place in the palette -- eleven bytes. Past <see cref="MaxGrainsSent"/> every so-many
        /// is sent.
        /// </summary>
        private static byte[] EncodeGrains(Grain[] grains, int count)
        {
            int stride = Mathf.Max(1, (count + MaxGrainsSent - 1) / MaxGrainsSent);
            int sent = (count + stride - 1) / stride;
            palette.Clear(); paletteIndex.Clear();
            Vector3 origin = count > 0 ? grains[0].pos : Vector3.zero;

            byte[] body = new byte[sent * 11];
            int n = 0, kept = 0;
            for (int k = 0; k < count; k += stride)
            {
                Vector3 d = (grains[k].pos - origin) * 100f;
                if (Mathf.Abs(d.x) > 32000f || Mathf.Abs(d.y) > 32000f || Mathf.Abs(d.z) > 32000f) continue;
                uint colour = grains[k].colour;
                if ((grains[k].material & SandFormat.FlagWet) != 0) colour = Darken(colour);
                int pi;
                if (!paletteIndex.TryGetValue(colour, out pi))
                {
                    if (palette.Count >= 255) pi = 0;
                    else { pi = palette.Count; palette.Add(colour); paletteIndex[colour] = pi; }
                }
                n = Bytes.PutShort(body, n, Mathf.RoundToInt(d.x));
                n = Bytes.PutShort(body, n, Mathf.RoundToInt(d.y));
                n = Bytes.PutShort(body, n, Mathf.RoundToInt(d.z));
                Vector3 v = grains[k].vel * 4f;
                body[n++] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.x), -127, 127);
                body[n++] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.y), -127, 127);
                body[n++] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(v.z), -127, 127);
                body[n++] = (byte)(grains[k].material & SandFormat.MaterialMask);
                body[n++] = (byte)pi;
                kept++;
            }

            byte[] b = new byte[12 + 4 + 1 + palette.Count * 3 + n];
            int h = 0;
            h = Bytes.PutFloat(b, h, origin.x);
            h = Bytes.PutFloat(b, h, origin.y);
            h = Bytes.PutFloat(b, h, origin.z);
            h = Bytes.PutInt(b, h, kept);
            b[h++] = (byte)palette.Count;
            for (int i = 0; i < palette.Count; i++) h = Bytes.PutColour(b, h, palette[i]);
            System.Array.Copy(body, 0, b, h, n);
            return b;
        }

        private static uint Darken(uint c)
        {
            uint r = (c & 0xFF) * 166 >> 8, g = ((c >> 8) & 0xFF) * 166 >> 8, b = ((c >> 16) & 0xFF) * 166 >> 8;
            return r | (g << 8) | (b << 16) | (c & 0xFF000000u);
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
        public static void RequestBrush(Vector3 at, float radius, float amount, int medium, bool removing)
        {
            if (!IsClient || !HostAllowsOthers) return;
            float[] data = { at.x, at.y, at.z, radius, amount, medium, removing ? 1f : 0f };
            try { ModNetworking.SendToHost(brushType.CreateMessage(data)); }
            catch (System.Exception) { }
        }

        private static void OnChunk(Message m)
        {
            if (!IsClient) return;
            SandField f = SandField.Instance;
            f.ReceiveChunk((byte[])m.GetData(0));
        }

        private static void OnGrains(Message m)
        {
            if (!IsClient) return;
            byte[] b = (byte[])m.GetData(0);
            if (b == null || b.Length < 17) return;
            int h = 0;
            Vector3 origin = new Vector3(Bytes.GetFloat(b, ref h), Bytes.GetFloat(b, ref h), Bytes.GetFloat(b, ref h));
            int kept = Bytes.GetInt(b, ref h);
            int pc = b[h++];
            uint[] pal = new uint[Mathf.Max(pc, 1)];
            for (int i = 0; i < pc; i++) pal[i] = Bytes.GetColour(b, ref h);
            SandField.Instance.ReceiveGrains(b, h, kept, origin, pal);
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
            stateClock = StateInterval;
        }

        private static void OnBrush(Message m)
        {
            if (!IsHost || !AllowOthers) return;
            float[] d = (float[])m.GetData(0);
            if (d == null || d.Length < 7) return;
            Vector3 at = new Vector3(d[0], d[1], d[2]);
            float radius = Mathf.Clamp(d[3], 0.1f, SandField.MaxBrushRadius);
            float amount = Mathf.Clamp(d[4], 0f, 2000f);
            SandField f = SandField.Instance;
            if (d[6] > 0.5f) f.Erase(at, radius, amount);
            else f.Paint(at, radius, amount, Mathf.RoundToInt(d[5]));
        }

        private static void OnState(Message m)
        {
            if (!IsClient) return;
            HostAllowsOthers = (int)m.GetData(0) != 0;
        }
    }
}
