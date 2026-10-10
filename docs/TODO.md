# Work to do

**10 October: the grains, their solver, the governor, the sprayer and the F9 report were removed**
(see `DESIGN.md`, "The shape of it"). The bed, its colliders and meshes, and the drawn spray and dust
are what is left, and this list is what is still wrong with them.

A list of defects and outstanding work. It is a work list, not a design document: what
`DESIGN.md` says the mod should do, this says what it does not yet do.

## How this list was made, and what was not checked

- Everything here was read out of the sources and **confirmed to still be there** on 10 October 2026,
  after the solver's removal. Anything whose subject no longer exists is gone from this list, not
  merely struck through; see *What was removed and where it went* below.
- Nothing here has been reproduced in game. The bed's own defects can be reproduced headlessly and
  where one has been, the suite that holds it down says so.
- Any change must be checked with `./tools/verify-build.sh` and `./tools/test-bed.sh` before it is
  believed.

Done, and no longer on the list: the dead sticky-cap constants, the README, the dead code, the
repository hygiene, the GPU path and its blockers, the grain solver and everything that described
it, `collisionDetectionMode` being written and never restored (it is now captured and given back,
`SandField.continuous`), the sand-grain dust path (`NativePuffs` hands the spray to Unity's own
particle systems), the hitch report, and `SandBed.Displace` destroying and creating volume.

## What was removed and where it went

The list used to carry about sixty items. Most described `CpuSolver.cs`, `Panel.cs` or
`SandRenderer.cs`, all of which were deleted on 10 October: the grain solver, the block-value panel
(block values are edited through the game's own `BlockMapper` now) and the managed mesh renderer for
grains (Unity's particle systems draw the spray). Those items were correct when written and are in
the history: `git show <commit>:docs/TODO.md` before `2a9638d`. Nothing in them can be acted on, and
a list that sends the next reader after deleted files is worse than a short one.

Two items were real and are fixed; they are recorded in `DESIGN.md` under the entry headed *Wheels
left spikes of sand behind them, and bumpy tracks*, with the tests that hold them down:
half of every pressed rut's displaced volume was annihilated whenever a machine's own contact
patches held the shoulder round them, and `Displace` both destroyed and created volume.

## What must not break

The mod is unusually well covered by assertions about invariants, and several of the bugs below are
a violation of one of these. Before changing anything, read the invariant, then keep it:

- **Volume is conserved** by every operation on the bed. `DESIGN.md` "Volume is conserved exactly
  through absorb, slump and release"; `BedUnitTest`, `BedPressTest` and `BedLayerTest` all check it,
  and two of the items below are volume going missing or appearing.
- A pile stands at its medium's angle of repose, holds a stack, and does not sink into the ground
  or through a thin plate. A wheel leaves a rut as deep as it was heavy, and its walls come down to
  the repose angle once it has gone.
- Snow and mud carry a spinning wheel round at pack gain 4 / cohesion 5 — the exact numbers in
  `DESIGN.md`; raising cohesion loses stickiness entirely.
- Saved sand indexes the material table: never reorder or remove rows (`SandMaterials.FirstVariant`).

C# 5 only — no C# 6+ features, and **no `enum` declaration anywhere**, which segfaults Besiege's own
compiler. `enum` is why the media are `const int`.

---

# A. Verified defects

Ordered by how much damage they do, not by how hard they are to fix.

## A1. One hostile brush message permanently corrupts the host's bed

`SandNet.OnBrush` (`SandNet.cs:202`), reached from any client's painting in a public game.

```csharp
float radius = Mathf.Clamp(d[3], 0.1f, SandField.MaxBrushRadius);
float amount = Mathf.Clamp(d[4], 0f, 5f);
```

`Mathf.Clamp` is `if (v < min) … else if (v > max) …`, and every comparison against NaN is false,
so NaN passes straight through. The position `d[0..2]` is not checked at all. `SandField.Paint`
re-clamps the radius (still NaN) and its guard `if (!bed.Enabled || rise <= 0f) return;` is false
for NaN, so it reaches `bed.AddVolume(x, z, NaN, …)`. That writes `c.top[k] = NaN` and sets the
bed's `extentMin`/`extentMax` to NaN, and `Extent` is then NaN for the rest of the session:
poisoning `GroundField.Ensure` and every subsequent brush.

Seven floats are enough to do it, and there is **no `float.IsNaN`/`IsInfinity` test anywhere in the
repository**. A shared finite-check helper applied at every entry point that takes data from outside
is the right answer; `OnBrush` is the only such entry point left now that grains and their palette
message are gone.

## A2. `variantsUsed` is never reset, so the Advanced settings die for the whole session

`SandField.cs:77`, `:94-96`:

```csharp
private int variantsUsed;
...
if (variantsUsed >= MaxVariants) return baseMaterial;
int slot = SandMaterials.FirstVariant + variantsUsed;
variantsUsed++;
```

`SceneLoaded` resets `count`, `bed`, `bedAtStart`, `countAtStart`, `water`, `ground`, `shapes` and
`rescanCountdown` — but not `variantsUsed`. `SandField` is `DontDestroyOnLoad`.

After 59 claims — sprayers with customised media, plus one per HUD brush via `SandHud.BrushRow` —
`ClaimVariant` permanently returns the base index for every remaining level. `SandHud.BrushRow` then
takes `customSlot < SandMaterials.FirstVariant`, and the panel's *Advanced* settings silently stop
applying. No log line, no way to recover except restarting the game. The `materials[]` and
`variantBase[]` rows also carry the previous level's emitters.

`DESIGN.md` promises 60 variant rows before falling back, per level. Reset it in `SceneLoaded` and
re-seed the variant rows from the base table.

## A3. `BedCodec` clamps on write but not on read

`BedCodec.cs` (encode):

```csharp
if (c.top[k] - c.bottom[k] <= minThickness) continue;
...
top[k] = Mm(c.top[k]);
if (top[k] <= bottom[k]) top[k] = bottom[k] + 1;
```

`BedCodec.Unpack` (decode):

```csharp
into.bottom[k] = bottom[k] * 0.001f;
into.top[k] = top[k] * 0.001f;
into.split[k] = into.bottom[k];
```

The reader imposes no `top > bottom` invariant, so the two are not a fixpoint: any column thinner
than a millimetre is packed 1 mm thicker than it was, and a column that encodes one way can come
back as one the next `Pack` *skips*. `Adopt`'s `c.filled` recount then disagrees with `into.filled`.
Every `SaveAll`/`LoadAll`, every network chunk and every level file goes through this, so it is not
a rare path.

The encoder is right to enforce the invariant — a column with `top <= bottom` cannot be rendered or
released. The decoder should enforce it too, so a chunk that arrives does not get to break an
assumption the writer guarantees.

Test: `BedPressTest`'s pack/unpack round trip is checked for a 20 m mound; extend it to assert the
*thin and empty* columns survive a pack/unpack unchanged, not just that the volume is close.

## A4. `SandBed.Slump` deposits before it withdraws

`SandBed.cs`, at the end of `Slump`:

```csharp
for (int q = layers - 1; q >= 0; q--)
    if (amount[q] > 0f && !Deposit(loI, loJ, amount[q], material2[q], colour2[q])) return;
Withdraw(hc, hiI, hiJ, move);
```

If a `Deposit` fails the `Withdraw` is skipped, and the layers already deposited are added without
being removed: sand manufactured. Reported as *speculative* because the two calls differ only in
material and colour, and the reporter could not construct a case where the first succeeds and a
later one fails — it needs `lower == MinThickness` exactly, where `Deposit`'s `had` test
(`top - bottom > MinThickness`) reads the column as empty and re-queries `ground.Height`.

Worth making atomic regardless: withdraw first, then deposit, or deposit into a temp. Note that
`Displace` had the same shape and was fixed on 10 October; `Slump` was left.

---

# B. Housekeeping, and one that is not

## B1. Static `GetInstanceID()` caches that are never invalidated

Near-identical caches keyed on `Rigidbody.GetInstanceID()`, all `static`, all
`DontDestroyOnLoad`-lifetime, none invalidated on scene load:

- `GroundField.cs:61` — `blockBodies`, capacity `Clear()` at 8192
- `SandHud.cs:62` — `machineBodies`, capacity `Clear()` at 4096

Unity recycles instance IDs after destruction, so after one level change an unrelated `Rigidbody`
can inherit a dead machine block's ID. The consequences differ:

- `GroundField.blockBodies` is the worst: a floor or scenery collider inheriting a dead block's
  `true` is skipped as "a machine being built, not the floor", so that 4 m tile samples as
  `NoGround`, and the bed's floor has a hole in it — sand cannot be laid there, and nothing drawn
  there is drawn. The converse bakes a machine into the floor.
- `SandHud.machineBodies`: suppresses painting over the column with only a grey ring to show for it.

The size caps are capacity limits, not invalidation. The clean fix is a small generation-stamped
cache cleared in `SceneLoaded`, or keying on the object rather than the ID. Whichever is chosen, do
it in both places at once — `GroundField` is the one that puts a hole in the world.

## B2. Static collider-ID set not cleared on teardown

`GroundField.cs:47` (`ignored`, a static `HashSet<int>`) is not cleared by `Forget()` (which clears
only `tiles`). `SandBedColliders.Destroy` unregisters each collider and `Dispose` drains them, so
normal teardown is balanced. The gap is if a `SandField` is destroyed while chunks sit in the bed's
`retired` list — then `OnRetire` never fires, `bedColliders.Destroy` never runs for them, and those
IDs stay forever. `IsIgnored` is consulted by every ground height query and by `SandField.Solid`, so
a later level's collider inheriting one of those IDs is treated as the mod's own bed surface.

## B3. `SandBed.Clear` leaves the network queue full

`SandBed.Clear` resets `chunks`, `dirty`, `active`, `batch`, `filledColumns` and `hasExtent`, but
not `netQueue`. That list holds strong references to every *retired* chunk, so `TakeNetDirty` still
hands the entire former chunk set to the network layer after a clear: on a host, "clear all sand"
queues every chunk the level had for re-sending. A `MarkNet` on a re-created chunk at the same
coordinates also queues a duplicate.

The rest of what the old list said about `Clear` is no longer true: holds now live in
`Chunk.holdUntil` (`ReleaseHolds`, 10 October), so a cleared bed holds nothing, and `digs`/`loads`
are per-chunk.

## B4. Four allocating `Stopwatch`s in per-frame and per-step paths

`SandBedRenderer.cs:198` (`Draw`), `SandBedColliders.cs:179` (`Update`, a sand step),
`GroundField.cs:96` (`Ensure`) and `SandBed.cs:2257`/`:3000`.

`SandField.cs` and `SandBed.cs` already hold `readonly` `Stopwatch` fields in some places and call
`Start()` — that is the pattern. These use the allocating constructor instead: one allocation per
call, on paths that run every frame or every sand step.

| Where | What | Note |
| --- | --- | --- |
| `SandHud.cs:1691`, `:1896` | `Camera.allCameras` up to 3× per frame | Allocates a fresh `Camera[]` per call, and Besiege has many cameras (level editor, water, depth, water-height renderers). Only ever needs the ones in `heldMasks` |
| `Chooser.cs:309` | `new Vector3[4]` per frame in `Reposition` | Called from `LateUpdate` whenever a list is open |
| `SandField.cs` closures | A closure allocated per `Paint`/`Erase` call | Per frame while the brush is held. A cached `Action<float,float,float>` with mutable captured state would do |

---

# C. Loose ends

## C1. `Lz.Decompress` can overflow on a hostile payload

`BedCodec`'s `Extra()` accumulates 255 per byte with no cap, so on a multi-megabyte payload
`r.At + lit` can overflow `int` negative and pass the guard, leaving `Array.Copy(..., lit)` with a
negative length. Speculative, but it is one cast away, and it is reachable from the network: a
client's chunk message goes through the same decoder.

The clean fix is to make `Extra()` stop at the input length rather than accumulate without bound,
which is one line and closes it whether or not the payload is hostile.