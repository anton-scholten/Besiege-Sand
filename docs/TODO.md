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

---

# D. The October review of the whole tree

A full read of every file, in four passes, after the work above. What it found, and what was done
about it. Section C is older and still open; this is the newer sweep.

## D1. Two numbers off the wire, checked only against their top

`BedCodec.Unpack` reads a column index and a layer count for the deep-layer section as `uint`,
casts them to `int`, and compared each against only its upper bound. 2^32-1 becomes -1, which is
not "over the limit" by either test, so a 40-byte packet could name column -1 and have `bottom[-1]`
read, or ask for a list of 4294967295 layers. Both were reproduced before the fix -- an
`IndexOutOfRangeException` and an `ArgumentOutOfRangeException` -- and nothing catches them on the
way in: `SandNet.OnChunk` hands the bytes to `SandField.ReceiveChunk`, which hands them to
`bed.Decode`, with no `try` anywhere, unlike the brush and state handlers beside it.

**Fixed.** Both bounds checked, and a new suite, "a packet that arrived from another player",
covers the two built packets, a well-formed one of the same shape, every run of five bytes changed
anywhere in a real chunk's stream, that stream cut short at every length, and a stream claiming to
be 2 GB.

## D2. A blast was deleting a thin second medium

`Blast` skips any medium whose total across the whole blast came to less than a hundredth of a
column -- 144 cm^3 -- and the sand had already been withdrawn, so it went nowhere. 144 cm^3 is one
column of 5 mm snow on top of a sand bed, which is what a bomb landing on a dusted patch does. The
bed in the test is over 300 m^3, so the totals came out right and nothing noticed. A blast small
enough that no column centre fell in its rim band at all took everything and rebuilt nothing, the
whole rim having been inside `if (ringSum > 0)`.

**Fixed**, the way `Spread` has always done it: the shares are laid down and whatever none of them
can hold goes to the heaviest, and a blast with no rim falls back to the column under it.

## D3. A settle read a value it never wrote

Settling works on a scratch array that is kept between calls and only grown, never cleared. Every
entry was written except one -- a column with no ground under it, which is no part of the settle --
so `Fall` read whatever was there: `MaxValue` from an earlier use, or, on a fresh bed, the zero the
array was allocated with. A zero is exactly the value for "this column was cut". The sand beside a
hole was hung in a skirt below a cut that never happened, and the pile settled 2.6 cm low across
the gap. `Hold` had guarded its neighbour reads from the start; `Fall` had not.

**Fixed.** Volume was never at risk -- it is a settle that comes out at the wrong height.

## D4. Four more instance-ID caches

The same defect as B1, which was fixed everywhere it had been found and had in fact been found in
only two places. `SandDust.isWheel`, `ShapeFeed.entityOf`, `ShapeFeed.blockMesh` and
`SandField.continuous` were all keyed by `GetInstanceID` with nothing saying which object the
answer was about. The first three are `SandObjectCache` now. The fourth is worse than a wrong
answer: "already switched" made a fast body near the sand do nothing at all, so it never got the
continuous collision detection that exists to stop it going through a sand collider, and the entry
kept a destroyed `Rigidbody` for good.

**Fixed.** All four.

## D5. A level change took the sand out of its own ground raycasts

`SceneLoaded` calls `GroundField.Forget`, which clears the list of colliders the raycasts see
through, so that the last level's floor is not inherited. But the bed's own colliders hang off the
field and a scene load does not destroy it, and only a collider nothing has wanted for `KeepSteps`
steps is torn down. For the first fraction of a second of the new level the bed's own surface is
standing there without being in the list, and the first ground cast measures the sand as the floor
underneath it. Sand settles onto the bed, and those tiles are never cast again.

This could not happen before B1 was fixed: a leaked static set was never cleared, so a live
collider stayed ignored for good. It was collateral damage from that fix.

**Fixed** -- `SandBedColliders.Reignore`, called right after the `Forget` that took them out.

## D6. A floor pushed out of the raycast's buffer

`GroundField` samples the ground on a grid into a sixteen-slot buffer with `RaycastNonAlloc`, which
is not a filter: when a ray meets more than the buffer holds it returns whichever it liked, in no
order. A machine with more than sixteen blocks over one sample is ordinary, and the floor -- the one
thing the sample is for -- may not be in the sixteen. The sample reads as no ground, `Deposit`
refuses it, and the bed has a hole nothing fills afterwards, because a tile that has been cast is
not cast again.

**Fixed.** When the buffer comes back full the ray is shortened to just below the lowest thing found
and cast again, up to eight times, so nothing is left behind.

## D7. A level's sand could be dropped on the floor

`ApplyLevelSand` cleared its waiting flag and its data *before* the three checks that decide whether
the run had settled, so on any frame where one of them held -- and `lastRunToggle` is set on every
simulate toggle -- the level's saved sand was gone for the rest of the session. The comment said
"asked again a frame later", which is what the flag is for and what it no longer was.

**Fixed.** The flag is now cleared only once the sand goes in, and `LoadFromLevel`'s early return
forgets the previous level's sand so the retry cannot carry it into the next one.

## D8. Smaller things, all fixed

`Clear()` did not empty `flowing`, the columns still rolling down a slope, which `Unsettled` counts
-- so a cleared bed went on reporting the last level's pile as unsettled. Bodies switched to
continuous collision detection were never handed back when the bed went empty, because the sweep
that does it is only reached while the bed has sand. `Tick` assigned a local it never read. The
comments on the two re-scan periods had drifted by a factor of two and four. `UIF.Font` reached
`Besiege.UI.Make` with nothing between it and the caller; the gate is now in the getter, where every
caller cannot get past it. `NativePuffs` marked a failed `Shader.Find` permanent *before* making
it, so one lookup during a level's load cost every spray and every wheel's dust for the session; it
is now retried for 120 frames.

## D9. Dead code

Twelve members and one `using`, found by counting references across the tree rather than by eye --
the compiler reports unused private *fields* only, so every method, property and constant had to be
looked for. `Bytes` is down to the int (the float and the short went with the grain solver's
messages, the packed colour with the palette they carried); `GroundField.TileCount`,
`SandBed.ChunkCount`, `SandBed.TouchDistance`, `SandBed.Unhold`, `SandDust.Puffs` and
`UIF.TextPrefab` are gone, as is `using Modding.Modules`.

The unused using was settled with the compiler rather than by matching names, because **mcs
truncates its own error list**: stripping every using at once and seeing what breaks under-reports,
and only removing one using from one file at a time gave the right answer. Worth remembering before
trusting a compiler's list again.

## D10. Examined, and deliberately not changed

**`Press`'s clamp at `depth - sink < MinThickness`.** Reported as leaving a column at exactly
`MinThickness`, which `Volume` (which counts `t > MinThickness`) would then not see. Changing `<`
to `<=` changes nothing: the clamp sets `sink = depth - MinThickness` either way, so the column
lands on the same depth in both. The knife-edge is real and pre-existing -- a column exactly at the
threshold is in `filled` but not in `Volume` -- but the proposed fix is a no-op and the real fix
would change how deep a load can press, which is a design decision, not a repair.

**`Spread` ignoring `Deposit`'s return.** Real, and the same shape as D2: if the ground beside a rut
is not known, a share is not deposited and the sand is gone. It needs `GroundField` to report no
ground immediately beside a rut, which `Displace`'s pre-check normally rules out. Left, because the
fix is to fall back somewhere specific and there is no obviously right place.

**`PourColumns` when a worker throws.** `Workers.For` returns false and says the caller should do
the lot again; `PourColumns` deliberately does not, so buckets that never ran keep their flags from
a previous call and their columns are dropped. Only reachable on an exception inside `FastAdd`,
which is managed code over flat arrays. The cheap mitigation -- zeroing `bucketFlags` before the
fork -- is worth doing but changes behaviour on a path with no test.

**`ShapeFeed.fits`, keyed by `Mesh`.** The same id-keyed class as D4, but it stores a fitted box
rather than a yes-or-no, so `SandObjectCache` does not fit it and converting it means a second
cache type. It does revalidate on `vertexCount` and `scale`, which catches most of it.
# E. A thin collider does not go into the sand

A spike or a blade pressed into snow, dust or sand -- by its own weight, or by a
machine resting on it -- sits on the surface. It does not part the medium, and
in soft media it behaves as though it were a peg in a hole. Besiege's "metal
spike" and "metal blade" are the reported cases; both are thin, and both should
be the easiest things in the game to push into loose sand.

Nothing below has fixed it. Every mechanism tried is written down with what it
measured and why it was taken out again, so that none of it is worked over a
third time. Reverted commits stay in the history; the branch is
`fix-plough-track` with `support-force` merged in.

## E1. Ruled out: cutting ahead of the shape

The bed's colliders are cooked lazily, a couple of millimetres of drift at a
time. A shape therefore meets a collider that was cooked before it arrived,
and nothing recooks it until the sand under it moves -- which needs the shape
to arrive -- which needs the collider to move out of the way. The sand in front
of a blade is never cut at all.

The fix that followed from that reading: cut the sand *ahead* of a thin shape,
scaled by how thin it is at its thinnest and how fast it is going, which makes
drift, which makes the collider, which lets the shape in.

**It worked, on the bench.** Driving a blade through sand and asking what still
stood 40 cm beyond where it stopped, out of 30 cm:

| | left standing | |
|---|---|---|
| a plate | 15.7 cm | cut fourteen of them out of the pile |
| a block | 34.2 cm | pushed a heap up instead |
| a sphere | 34.2 cm | no edge anywhere |

The last two being *higher* than they started is the wall, measured.

**In the game it made no difference** -- "the cut is starting to work" and then
nothing further. Reverted (`2e790fe`). The reasoning it rested on -- that the
wall is the stale collider -- is unproven, and the trench it dug did not
evidently reach the collider.

## E2. Ruled out: the load-proportional support force

`SandBedColliders.Report` applied, per contact, a share of the incoming load
times a spring on how far the sand had gone. Both terms scale with the weight,
so a spike and a plate pressing equally got back equally and settled at the same
depth: there is no area anywhere in it, and nothing about a shape could win.

## E3. Ruled out: bearing pressure over a contact area

Replacing it with `f = firmness × area` -- the medium's own bearing pressure
over the face of the thing going into it, with no load term at all. Correct on
paper: the load decides only whether the bearing is enough, and the shape
decides what there is to bear against.

It was committed with a defect large enough to defeat its own purpose. The area
came from `c.collider.bounds.size`, the collider's whole world bounding box, so
a 2 m wheel block standing on sand bore on 4 m² -- 80,000 N, a catapult -- and
a 1 m × 1 m × 5 cm blade bore a square metre, and could not cut through
anything. Because the error only ever made the resistance *larger*, it
suppressed exactly the behaviour it was built to produce.

Fixed to use the real contact patch (`774601e`, `95657a5`), then reverted
anyway (`fdfabbf`): it did not help, and the whole family is now out of the
tree.

**What survives from that reasoning** and is worth keeping in mind: the force
had no damping and no velocity term, so anything it held would ring rather than
settle. Whatever replaces it will want both.

## E4. Ruled out: the hold, on inspection and then in game

`SandBed.Plough`, the burial branch:

```csharp
float buried = inside == float.MaxValue || Held(i, j) ? 0f : top - inside;
```

`Held` is laid by any press, on a five-column box, from every contact a step.
A spike pressed in by its own weight presses, so every column it is inside is
held, so `buried` is zero, so nothing is displaced. **The harder it was pushed
the more firmly it was held, and the less it went in** -- the opposite of what a
thing with an edge is for, and it is why a spike is worst exactly where it
should be best.

This is sound and still stood by inspection. Bypassing it for thin shapes
(`366d22c`) changed nothing in game. So the hold is **not** the binding
constraint, or something else is in front of it.

## E5. What is actually unexplained

Three things are consistent with the symptom and none has been checked:

1. **Whether the collider reaches the bed at all.** `ShapeFeed.Supported()`
   admits a box, sphere or capsule, or a **convex** mesh on a non-kinematic
   block. A thin block's collider being non-convex would make it invisible to
   the sand entirely -- no plough, no press, no load. One look at a spike with
   Colliderscope settles it, and nothing below matters if it is so.
2. **The fitted box.** A wheel's convex mesh is reduced by `Fit` to a box, and
   a spike's to a box that may not be thin in the axis that matters. A
   diagonal thin block fitted to a world-oriented box is fat. `ShapeFeed`'s fit
   is the input to any thinness test.
3. **Column-centre sampling.** The bed samples at column centres, 12 cm apart.
   A 2 cm spike lying between two centres may never be *inside* any column,
   and `inside` is what the burial branch keys on. A thin shape might be
   invisible to the discretisation rather than to the physics.

## E6. Where to look next, in order

1. Check (E5.1) in game. It is one observation and it can end the search.
2. If the collider does reach the bed, instrument rather than theorise: report
   from `Plough` how many columns a spike is seen in, and whether `inside` is
   ever set for one. That distinguishes (E5.3) from everything else at once.
3. Only then look at forces. The bed has no dynamics -- it cannot resist, only
   decline to cut -- so a resistance has to be expressed as what the sand
   refuses to do, or as a force fed back through PhysX. The latter was E3 and
   it is the only one of these that moves the machine at all.

## E7. Measurement traps found on the way

Worth recording, because each cost an afternoon and none of them was obvious.

- **Autocorrelation without detrending does not measure a period.** A track
  with a long-term trend puts its strongest peak at the longest lag allowed,
  and reports the whole length of the track as a period. It produced 1920 cm
  periods, and 0 cm with zero correlation, from a signal that was fine. Detrend
  first, and bound the lag to something physically possible.
- **A probe that forces a wheel below the surface is not a vehicle.** The
  original rut measurements were taken with a wheel buried 10-20 cm and pinned
  there every 10 ms. Press is smooth (0.4 mm); the plough makes every ridge.
- **Area read off a bounding box is not a cross-section.** E3.
- **Direction must be applied to the query, not to the shape.** Moving a
  shape's centre to sweep it also moves the centre its surface velocity is
  measured from, and on a small wheel one step of sweep is a third of a turn --
  enough to point the surface velocity downwards and stop it ploughing at all.
  `IBedShapes.Shift` moves where a shape is looked for, not where it stands.
