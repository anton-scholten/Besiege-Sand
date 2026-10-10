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
particle systems), the hitch report, `SandBed.Displace` destroying and creating volume, a client's
brush message poisoning the host's bed with a NaN (`SandGuard`, `GuardTest`), the static
instance-ID caches letting one collider's or body's answer stand for another's
(`SandObjectCache`), the brush's variant rows filling up over a session and never being
released (`SandField.SeedTable`), and a layer of sand 4.1 mm thick not surviving a save at all
(`BedCodec`, which was writing columns a millimetre clear of zero rather than of the threshold
the bed counts sand at).

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

## A1. One hostile brush message permanently corrupted the host's bed — fixed 10 October

`SandNet.OnBrush` clamped the numbers in a client's brush message with `Mathf.Clamp`, and did not
check the position at all:

```csharp
float radius = Mathf.Clamp(d[3], 0.1f, SandField.MaxBrushRadius);
float amount = Mathf.Clamp(d[4], 0f, 5f);
```

`Mathf.Clamp` is `if (v < min) … else if (v > max) …`, and every comparison against NaN is false,
so NaN passed straight through. `SandField.Paint`'s guard `if (!bed.Enabled || rise <= 0f) return;`
is false for NaN, so it reached `bed.AddVolume(x, z, NaN, …)`. That wrote `c.top[k] = NaN` and set
the bed's `extentMin`/`extentMax` to NaN, and `Extent` was then NaN for the rest of the session:
poisoning `GroundField.Ensure` and every subsequent brush. Seven floats, from any player in a
public game.

What the old list said here -- that there is no `float.IsNaN` test anywhere in the repository --
was already out of date: the panel guarded its own typed values (`SandHud.SetNumber`). What it did
not say is that the panel's guard was the only one, and in one file.

The check is now one shared thing (`SandGuard`, in `SandTypes.cs`, so every headless suite can
reach it), applied where numbers come in: `OnBrush` refuses a position that is not a number and
clamps the rest to the host's own values, `SandSettings.Get` will not return a NaN out of a
settings file, and `SandField.Paint`/`Erase` refuse anything that is not a number as the last of
it. `GuardTest` holds the guard down, including the fact it exists to answer: that
`Mathf.Clamp(NaN, lo, hi)` is NaN even when `lo == hi`.

## A2. `variantsUsed` was never reset, so the Advanced settings died for the whole session — fixed 10 October

`SandField.ClaimVariant` hands out a row of the material table per brush whose *Advanced* settings
are open, and gave the base index back unchanged once the table was full:

```csharp
if (variantsUsed >= MaxVariants) return baseMaterial;
int slot = SandMaterials.FirstVariant + variantsUsed;
variantsUsed++;
```

`SceneLoaded` reset `bedAtStart`, the ground, the shapes and the rescan counter — but not
`variantsUsed`, and `SandField` is `DontDestroyOnLoad`. After 59 claims — a sprayer per customised
medium, plus one per brush in the panel — every later level's brushes got the base index, and their
*Advanced* settings stopped applying. No log line, no way back but restarting the game. The
`materials[]` and `variantBase[]` rows also carried the previous level's emitters.

The seeding is now one method, `SeedTable`, called at startup and again on every level change: the
rows a brush claimed belong to that brush, and it went with the level, so they are the medium's
again. `DESIGN.md` promises 60 rows per level, which is now what happens.

## A3. `BedCodec` dropped every column a millimetre thick or two thinner — fixed 10 October

The bed's `MinThickness` is 4 mm: `Pack` skips a column at or below it and `Adopt` recounts
`filled` the same way, so a column that comes back at 4 mm is not sand as far as the bed is
concerned. The writer kept a column only a millimetme clear of *zero*:

```csharp
top[k] = Mm(c.top[k]);
if (top[k] <= bottom[k]) top[k] = bottom[k] + 1;
```

Rounding to the millimetre can take a column from just over the threshold to exactly it, or to
less: a layer of **4.1 mm did not survive a save and load at all** — it packed, unpacked at 4 mm,
and was counted as nothing, so the chunk went into the bed empty. And a column that made it to the
threshold could be dropped between two saves of the same chunk, which is what the old list called
"not a fixpoint". Every `SaveAll`/`LoadAll`, every network chunk and every level file goes through
this, so it is not a rare path.

Both ends enforce the same invariant now, and it is the threshold rather than zero: the writer
keeps every column it writes a millimetre clear of `minThickness`, and the reader will not accept
a column at or below `MinColumn` (5 mm) whatever the bytes say — the reader should not have to take
the writer's word for something the writer only guarantees about its own output.

The test is a chunk packed, unpacked and packed again, at eight thicknesses from 4.1 mm up, and
checks the two packings are the same bytes and the chunk is not empty. It fails against the code
as it was, on 4.1 mm and 4.4 mm.

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

## B1. Static `GetInstanceID()` caches that were never invalidated — fixed 10 October

Near-identical caches keyed on `Rigidbody.GetInstanceID()`, all `static`, all
`DontDestroyOnLoad`-lifetime, none invalidated:

- `GroundField` — `blockBodies`, capacity `Clear()` at 8192, consulted by every ground raycast
- `SandHud` — `machineBodies`, capacity `Clear()` at 4096

Unity recycles instance IDs after destruction, so after one level change an unrelated `Rigidbody`
could inherit a dead machine block's ID. The consequences differed:

- `GroundField.blockBodies` was the worst: a floor or scenery collider inheriting a dead block's
  `true` is skipped as "a machine being built, not the floor", so that 4 m tile samples as
  `NoGround`, and the bed's floor has a hole in it — sand cannot be laid there, and nothing drawn
  there is drawn. The converse baked a machine into the floor.
- `SandHud.machineBodies`: suppressed painting over the column with only a grey ring to show for it.

The capacity limits are limits, not invalidation. Both now go through `SandObjectCache`, which is
keyed by the instance ID but holds the object the answer was measured on: Unity's own comparison
says no for a destroyed object, so a spent entry is recognised and dropped rather than believed.
Clearing the cache on overflow still empties it, but nothing else relies on that.

**Not covered by a test.** The behaviour turns on engine object lifetime, and the headless suites
run with no engine: `GameObject` and `Rigidbody` cannot be made or destroyed outside the game. It
needs a scene, a machine built and destroyed, and a look at whether the floor under it is still
there.

## B2. Static collider-ID set not cleared on teardown — fixed 10 October

`GroundField.ignored` was a static `HashSet<int>` that `Forget()` did not touch, so a bed destroyed
while chunks sat in its `retired` list left every one of those IDs in it forever, and
`IsIgnored` is consulted by every ground height query: a later level's collider inheriting one of
them is treated as the mod's own bed surface and looked through.

It is keyed by instance ID and holding the collider now, as B1, so a recycled ID cannot answer for
a different collider either, and `Forget()` clears it — which `SandField.SceneLoaded` calls for
every level change.

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