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
released (`SandField.SeedTable`), a layer of sand 4.1 mm thick not surviving a save at all
(`BedCodec`, which was writing columns a millimetre clear of zero rather than of the threshold
the bed counts sand at), `SandBed.Slump` handing on sand before taking it out, and a host handing
a client the sand it had just cleared, because a chunk cleared and remade in the same place was two
objects with one set of coordinates and the retired one was still queued, and the allocations on
the per-frame and per-step paths: eight `Stopwatch`s made to be thrown away, a `Camera[]` per
read, a `Vector3[4]` a frame and two closures a frame.

---

# C. Still to do

**Pressing: what is actually left, and a lead that is not.** A load presses a footprint of up to 25
columns rather than nine, and every pressed column looks for somewhere to put the half of its sink
that is not packed, so `Press` is dearer per contact than it was before 10 October — 0.022 ms a fixed
update for a car's four contacts, 0.71 ms for a machine with 300 blocks all touching the sand at
once. The pressure has been moved out of a `Dictionary<long, float>` into per-chunk arrays
(`Chunk.press` and `Chunk.bite`, beside the `packed` it always belonged with), which took nearly
half of it back; the numbers before and after are in `PERFORMANCE.md`.

An earlier version of this note said what was left was a dictionary lookup a column for the chunk
the column is in, and that a one-entry cache of the last chunk would take most of it out. **That was
wrong: `FindChunk` has had a one-entry cache all along**, and it is answering most of them.
Counted over a press:

| contacts on the sand | chunk lookups a press | answered by the cache |
| --- | --- | --- |
| 4 | 176 | 63% |
| 64 | 3,039 | 80% |
| 300 | 28,236 | 85% |

and spreading the contacts ten times further apart, so each footprint is over different ground,
only takes it to 70–72%. Keeping the chunk beside the key in `batch` — so the two passes that walk
it do not look it up — was measured at about 1% of the press path (the bench's 300-contact case
313–317 ms a second against 317–318), which is inside the noise. It was not taken: two lists that
have to stay the same length, so that a key added without its chunk presses the wrong sand, is a
poor trade for a percent. The cost here is the sheer number of lookups — a footprint of 25 columns
per contact, and eight directions of walking out for the shoulder — not the cost of any one of
them.

**`ReleaseHolds`'s sweep** is the one thing in this area measured as a real, if small, win, and
taken: it walked every chunk with sand in it, six times a second, to find the handful a machine has
a foot on. On a 100 m bed of 2,916 chunks, press with four contacts on it went from 0.0131 to
0.0113 ms a fixed update. The bench's 30 m bed shows no change at all, because 157 chunks are not
enough to notice.

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

## A4. `SandBed.Slump` deposited before it withdrew — fixed 10 October

At the end of `Slump`, layer by layer:

```csharp
for (int q = layers - 1; q >= 0; q--)
    if (amount[q] > 0f && !Deposit(loI, loJ, amount[q], material2[q], colour2[q])) return;
Withdraw(hc, hiI, hiJ, move);
```

If a deposit failed the withdraw was skipped, and the layers already down were added without being
taken out of the column above: sand made. Reported as *speculative*, and it stayed that way: a
deposit only fails on a column that is empty and has no ground under it, and the first layer down
makes it not empty, so no failing case can be built. `Displace` had the same shape, and it *was*
reachable — it was fixed on 10 October.

It is atomic now: the sand is taken out first and put down after, and what was taken goes back
whole if a deposit does fail. The ordering is the fix; nothing observable changed.

The same call had `if (avail - move < MinThickness) move = avail;` where `Withdraw` empties a
column left at or below the minimum — so at exactly the minimum it handed on the part asked for and
the rest was lost with the column. `<=`, like `Displace` now.

The layered path the ordering touches is covered: `BedLayerTest` slumps a five-medium pile and
checks each medium's volume separately. The failing deposit itself cannot be constructed, and
cannot be tested.

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

## B3. `SandBed.Clear` left the network queue full — fixed 10 October

`Clear` resets `chunks`, `dirty`, `active`, `batch`, `filledColumns` and `hasExtent`, but not
`netQueue`, which holds a strong reference to every chunk it has just retired. A host that cleared
its sand with thousands of chunks' worth of sand queued was walking the lot to find none of them
live, and holding every retired chunk until it did.

The old list claimed `TakeNetDirty` "still hands the entire former chunk set to the network layer",
which is wrong: it checks that the chunk is still in `chunks` before returning it. The part that
*was* wrong is subtler and it took a test to find. A chunk cleared and then made again at the same
place is two objects with one set of coordinates, and both were in the queue. The live one went
out; the retired one followed, and `chunks.ContainsKey` said yes to its coordinates because the
live chunk was standing there — so a client was handed the sand the host had just cleared.

`TakeNetDirty` now compares the object, not its coordinates, and `Clear` empties the queue.

The test marks a chunk, clears the bed, lays sand on the same spot, marks again, and takes twice:
the first must be the chunk that is there and the second nothing. It fails against the code as it
was. (An earlier version of the test passed against the bug, because it drained the queue with a
check of its own before the interesting part — worth remembering.)

The rest of what the old list said about `Clear` is no longer true: holds now live in
`Chunk.holdUntil` (`ReleaseHolds`, 10 October), so a cleared bed holds nothing, and `digs`/`loads`
are per-chunk.

## B4. Allocations in per-frame and per-step paths — fixed 10 October

None of these are correctness bugs. They are all in paths `DESIGN.md` treats as GC-critical —
it records Mono collector pauses of tens of milliseconds landing in the middle of the simulation,
which is why `BedCodec` keeps its buffers — and they were cheap to fix.

Eight `Stopwatch.StartNew()` in per-frame, per-step and per-sweep code, each an object made to be
thrown away a moment later: `SandBedRenderer.cs` (every frame), `SandBedColliders.cs` (a sand
step), `GroundField.cs` (`Ensure`, every stroke and every run), `SandBed.cs` (twice: `Relax`'s
budget, and packing a kept run), `SandField.cs` (twice: a brush stroke, and its settling),
`BrushShape.cs` (every frame a brush is held). All are now `readonly` fields. `SandField` already
held two, which is where the pattern came from.

**The trap, which the headless tests caught**: `Stopwatch.Start()` *does nothing to a watch that is
already running*, and not one of these is ever stopped — they leave their budget. So a timer that
was only started reads as having been running since the first call, and `Relax`'s loop
`while (waiting && elapsed < budget)` never runs again. Sand stopped settling and every mound
stood at 64 degrees. All eight now `Reset()` first. Worth knowing before writing any more of
these.

Also: `Camera.allCameras` in `SandHud`, read up to three times a frame wherever the pointer is
over the panel, allocates a fresh array each time (Besiege has a good many cameras). It is read
once a frame now and shared — a camera that appears mid-frame is picked up on the next one, which
is what gathering every call was doing anyway. And `Chooser.Reposition` made a `new Vector3[4]`
every frame a list was open, from `LateUpdate`; it is a field.

---

# C. Loose ends

## C1. `Lz.Decompress` can overflow on a hostile payload

`BedCodec`'s `Extra()` accumulates 255 per byte with no cap, so on a multi-megabyte payload
`r.At + lit` can overflow `int` negative and pass the guard, leaving `Array.Copy(..., lit)` with a
negative length. Speculative, but it is one cast away, and it is reachable from the network: a
client's chunk message goes through the same decoder.

The clean fix is to make `Extra()` stop at the input length rather than accumulate without bound,
which is one line and closes it whether or not the payload is hostile.