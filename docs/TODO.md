# Work to do

A list of defects and outstanding work, found by reading the sources on 2026-10-05 and
handed to whoever picks this up next. It is a work list, not a design document: what
`DESIGN.md` says the mod should do, this says what it does not yet do.

## How this list was made, and what was not checked

Everything in section A was read directly out of the sources and confirmed by hand.

- Line numbers, in the sources and in `DESIGN.md`, are those of commit `a366cd5`, which the list
  was written against; both have moved since, so read a file as it was with
  `git show a366cd5:Sand/Scripts/<file>` or `git show a366cd5:docs/DESIGN.md`.
- Nothing here has been reproduced at runtime or observed in game. Since then the tree has been
  built, checked against the loader's blacklist and put through every headless suite
  (`./tools/verify-build.sh`, `./tools/test-bed.sh --load`) on a machine that has Besiege.
- Section B is *reported but not confirmed* -- do not "fix" those without reproducing them
  first, several are probably not defects at all.
- Any change must be checked with `./tools/verify-build.sh` and `./tools/test-bed.sh` before it
  is believed.

Done since it was written, and so no longer on the list: the dead sticky-cap constants (A6), the
README (A15), the dead code (C7), the repository hygiene (C8), and the GPU path and its
blockers (D1), which was removed.

## What must not break

The mod is unusually well covered by assertions about invariants, and several of the bugs
below are a violation of one of these. Before changing anything, read the invariant, then
keep it:

- `DESIGN.md` "Volume is conserved exactly through absorb, slump and release", and the
  4000-grain and sphere-in-pile checks that hold it (`BedSolverTest`).
- Regions fill as *the shape they would slump to*, exact same volume, nothing left to slump
  (`BedPressTest`, `SandBed.RestHeights`/`DepositGrid`).
- A pile on a movable box loads it with 1.00 of its weight; a spinning wheel is pushed
  forward and throws sand back (`SolverShapeTest`).
- Sand piles at ~34 degrees, holds a stack, settles without hopping, does not sink into the
  ground or through a thin plate (`SolverPhysicsTest`).
- Snow and mud carry grains round a spinning wheel at pack gain 4 / cohesion 5 — the exact
  numbers in `DESIGN.md`; raising cohesion loses stickiness entirely.
- The governor's closed-loop pour: 13.6 m³, ~74,000 ordinary grains' worth, mean step
  13.6 ms, worst slice 7.7 ms (`tools/test-bed.sh --load`).

C# 5 only — no C# 6+ features, and **no `enum` declaration anywhere**, which segfaults
Besiege's own compiler. `enum` is why the media are `const int`.

---

# A. Verified defects

Ordered by how much damage they do, not by how hard they are to fix.

## A1. A worker-thread exception hangs the game permanently

`CpuSolver.cs:281-291`, and the wait it strands at `CpuSolver.cs:345`.

```csharp
events[id].WaitOne();
events[id].Reset();
if (stopping) return;

RunPass(id + 1);

if (Interlocked.Decrement(ref pending) == 0) allDone.Set();
```

`RunPass` is unguarded. If it throws, the worker leaves without decrementing `pending`, so
`allDone` is never set, so `Parallel`'s `allDone.WaitOne()` blocks the main thread forever.
The player has to kill the process. The game has no crash dialog, just a hang.

Reachable from A2 below, from an out-of-range material byte, and from anything else in
`Solve`/`Predict`/`Settle` that throws.

Fix: wrap the body in `try/finally { Interlocked.Decrement(ref pending)... }`, and decide
what a pass failure means — probably abort the step and flag it rather than continue with
half-updated state. Make sure `allDone.Set()` is reachable on *every* exit including
`stopping`. Consider whether `Dispose` can also strand a wait; it appears to set `stopping`
and signal, which is fine, but that is worth re-checking against the same argument.

Test: none exists for this today. A test that forces an exception inside a pass and asserts
the call returns would be worth having, but it is not easy headlessly.

## A2. The shape-grid budget check can overflow `int`

`CpuSolver.cs:815-825`.

```csharp
sgX = Mathf.Max(1, Mathf.CeilToInt(spanX / sgCell));
sgZ = Mathf.Max(1, Mathf.CeilToInt(spanZ / sgCell));
if (sgX * sgZ <= MaxShapeCells) break;
sgCell *= 1.5f;
...
int cells = sgX * sgZ;
if (sgStart.Length < cells + 1) sgStart = new int[cells + 1];
```

`sgX * sgZ` is an `int` product. `BuildGrid` gets the same check right four hundred lines
earlier with a cast, at `CpuSolver.cs:680`:

```csharp
if ((long)cellsX * cellsY * cellsZ <= MaxCells) break;
```

so line 819 is an inconsistency rather than a house convention. Once the product wraps
negative the guard passes, the loop breaks, and line 824 allocates a nonsense-sized array —
on a worker thread, which is to say it feeds straight into A1.

Needs the `(long)` cast, plus a sane ceiling: `spanX`/`spanZ` come from
`bounds.size`, i.e. from the grains' extent, so reaching this needs a grain that has
escaped or gone non-finite — which A4 can produce. Note also that `Mathf.Max(1, ...)`
collapses a NaN or infinite `bounds.size` to 1, silently putting every shape in the level
into cell 0 and giving back exactly the `grains × colliders` blow-up the grid exists to
avoid. `GroundField.Ensure` has the right defence for the analogous case at
`GroundField.cs:133` (`// nonsense bounds`).

## A3. `firstPass` is true in both halves of solve iteration 0

`CpuSolver.cs:485` sets `passIteration` once per pass; `:490-495` then runs two `Parallel`
halves of the same iteration, `passPhase` 0 then 1. `CpuSolver.cs:891` derives:

```csharp
bool firstPass = passIteration == 0;
```

which is therefore true for *both* halves. Restitution (`CpuSolver.cs:941`, `:1051`,
`:1289`) and the `FirstTouch` path walk (`:992`) each fire twice per substep instead of
once. A one-substep bounce is currently a two-substep bounce, which is the exact regression
`DESIGN.md:879` records as fixed: *"Restitution was applied on every solve, three bounces a
contact; it is once a substep."*

Test: `SolverPhysicsTest` has the settling-without-hopping and no-block-burst checks, but
they evidently do not pin this down. Worth adding one that counts applied restitutions per
substep.

Careful: the two halves are also what makes `pressureAcc` accumulate twice per iteration
while being divided by `jobIterations` (`CpuSolver.cs:1452`), so snow's packing saturates
at about half the intended squeeze. Fixing A3 may change packing behaviour — re-check the
snow and mud wheel-grain counts in `SolverShapeTest`, which are pinned to specific numbers
in `DESIGN.md`.

## A4. `FirstTouch` discards position corrections already applied this pass

`CpuSolver.cs:992`:

```csharp
if (firstPass) g.pos = FirstTouch(sh, s, g.prevPos + sh.pointVel * h, g.pos, radius);
```

`FirstTouch` returns a point on the path `from → to`, where `to` is the *current* `g.pos`.
When it returns an interior point the assignment throws away the floor push-out already
applied at `CpuSolver.cs:946` and every `g.pos += push` from earlier shapes in the loop
(`:1057`). A grain that has been pushed out of the ground and then crosses a plate is
teleported backwards to where it first met the plate, losing the ground resolution it had
just been given.

The intent is sound — this is the fix for *"sand falling at 12 m/s passed through a 2 cm
plate"* (`DESIGN.md:876`) — so keep it, but the walk should start from where the grain would
have been without the shape's own push, or the pushes should be re-applied after it.

## A5. The sticky-media throw cap is dead for the case it was written for

`CpuSolver.cs:1432-1441`.

```csharp
if (clinging[i]) v = clingVel[i];
else if (v.y > StickyThrowUp)
{
    int sm = (int)(g.material & SandFormat.MaterialMask);
    if (mCohesion[sm] > 1f || mAdhesion[sm] > 20f || (g.material & SandFormat.FlagWet) != 0)
        v.y = StickyThrowUp;
}
```

The comment immediately above explains the cap: *"a wheel throws them, but not high. Riding
a wheel round they came off the top at its rim speed and shot up like dry sand from a
roost."* That is a grain **riding a rim** — which is clinging (`adhesion > 0f && dist <
radius`, `CpuSolver.cs:1062`) *and* moving upward at rim speed. `clinging` and
`v.y > StickyThrowUp` are not mutually exclusive, so the `else` never runs for precisely
the population it targets. Trigger: any sticky medium (mud, snow, glue — `mAdhesion > 20`)
on a wheel above ~3 m/s rim speed.

`DESIGN.md:101-106` claims this is fixed. It is not.

Test: `SolverShapeTest` already measures grains carried round a spinning wheel (86 for
snow, 104 for mud). That is the test to extend.

## A7. Four unchecked network message paths in `SandNet.cs`

All four are the same class of bug: a length or count read off the wire and trusted. Besiege
hands `ModNetworking` callbacks a `byte[]` from another player, so none of this is
hypothetical in a public game.

### A7a — `pc` palette entries read without a bounds check

`SandNet.cs:265-267`:

```csharp
int pc = b[h++];
uint[] pal = new uint[Mathf.Max(pc, 1)];
for (int i = 0; i < pc; i++) pal[i] = Bytes.GetColour(b, ref h);
```

`pc` is a wire byte, 0-255, and each entry costs 3 more bytes. The guard at `:261` only
covers the 17-byte fixed header. `Bytes.GetColour` (`Bytes.cs:58-62`) indexes `b[n]`,
`b[n+1]`, `b[n+2]` unchecked. A 17-byte packet with `pc = 255` throws
`IndexOutOfRangeException` inside the callback.

`pal` itself is sized correctly — only the source read is unchecked. Fix: bound the loop by
the bytes remaining.

### A7b — `kept` is never validated, and `count` is set from the claim, not the parse

`SandNet.cs:264` reads `kept` as a plain int32; it may be negative or enormous.
`SandField.ReceiveGrains` (`SandField.cs:831-851`):

```csharp
int n = Mathf.Min(kept, grains.Length);
for (int k = 0; k < n && h + 11 <= b.Length; k++) { ... grains[k] = g; }
count = n;
```

Two defects. The loop can exit early on `h + 11 <= b.Length`, but `count` is still set to
`n`, so the client integrates, solves and renders up to 250,000 grains that were never
written — stale values left over from previous frames. And `kept < 0` makes `count < 0`,
which then throws in `StartRun`'s `System.Array.Copy(grains, grainsAtStart, count)`
(`SandField.cs:889`).

Fix: track how many grains actually parsed and assign that to `count`; clamp `kept` to
`[0, grains.Length]`.

### A7c — `Mathf.Clamp` does not sanitise NaN, so a hostile brush permanently poisons the host's bed

`SandNet.cs:291-293`:

```csharp
float radius = Mathf.Clamp(d[3], 0.1f, SandField.MaxBrushRadius);
float amount = Mathf.Clamp(d[4], 0f, 5f);
```

`Mathf.Clamp` is `if (v < min) … else if (v > max) …`, and every comparison against NaN is
false, so NaN passes straight through. `SandField.Paint` re-clamps the radius (still NaN)
and its guard `if (!bed.Enabled || rise <= 0f) return;` is false for NaN, so it reaches
`bed.AddVolume(x, z, NaN, …)`. That writes `c.top[k] = NaN` and sets the bed's
`extentMin`/`extentMax` to NaN, and `Extent` is then NaN for the rest of the session,
poisoning `GroundField.Ensure(bed.Extent, …)` and every subsequent brush.

One 7-float hostile message permanently corrupts the host's bed.

There is **no `float.IsNaN`/`IsInfinity` check anywhere in the repository** — not in
`CpuSolver`, `SandMaterials`, `SandGovernor`, `ShapeMath`, `WaterLink`, `Panel`, `SandNet`
or `SandField`. See also A7e and B4. A shared finite-check helper is probably the right
answer here, applied at every entry point that takes data from outside.

### A7d — an unvalidated chunk payload reaches the LZ decoder

`SandNet.cs:250-255` hands the raw payload to `SandBed.Decode` → `BedCodec.Unpack` →
`Lz.Decompress` (`BedCodec.cs:391-397`):

```csharp
int length = (int)r.Unsigned();
if (length < 0 || length > 64 * 1024 * 1024) return null;
byte[] o = new byte[length];
```

A 5-byte hostile chunk message declares `length = 64 MB` and forces the allocation,
repeatably, even though the compressed payload is 7 bytes and the copy loop bails on the
first bounds check. Nothing relates input size to declared output size.

The bed chunk is large and high-value to a client; consider a size cap on the incoming
message in `OnChunk` before decoding at all.

### A7e — the whole field is constructed inside a network callback

`SandNet.OnChunk`/`OnGrains`/`OnClear` all use `SandField.Instance`, the *creating*
accessor, which allocates a `DontDestroyOnLoad` GameObject whose `Awake` sets up the GPU
solver, `new Grain[250000]`, meshes, materials and colliders. `OnHello` at `SandNet.cs:280`
correctly uses `SandField.Existing` and deliberately does not.

`SandField.cs:1002-1007` documents the opposite decision — *"but it is not made here, in the
middle of the game's own loading: the next frame's hook does it."* Network callbacks are
no better a place. Use `Existing` and skip if null.

## A8. `collisionDetectionMode` is written and never restored

`SandField.cs:1783-1793`:

```csharp
private readonly System.Collections.Generic.HashSet<int> continuous = ...;
...
if (rb == null || !continuous.Add(rb.GetInstanceID())) continue;
if (rb.isKinematic || rb.collisionDetectionMode != CollisionDetectionMode.Discrete) continue;
rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
```

Grep finds exactly two references to `collisionDetectionMode` in the whole tree — the guard
and the write. Nothing ever puts it back.

This is the only write to a Unity object-global anywhere in the mod with no restore, and
the repo's own rule is explicit (`DESIGN.md:473-474`): *"Anything the mod writes to a global
is captured lazily on first write and restored from whichever callback notices the run ended
first, guarded to be idempotent."* The other globals the mod touches — `Camera.eventMask`
via `SandHud.heldMasks`, `StatMaster.DisableCameraZoom` and `SetInMenu` via `ZoomGuard` —
are all correctly captured and given back.

The `continuous` set has a second problem. `Clear()` (`SandField.cs:777`) and `SceneLoaded`
(`:357`) do not clear it, and `SandField` is `DontDestroyOnLoad`. Unity recycles
`GetInstanceID()` after destruction, so after a level change a new `Rigidbody` can inherit
a stale ID, `continuous.Add` returns false, and the body is silently skipped at the guard —
which is exactly the failure the function exists to prevent. The `Count > 20000` clear is a
capacity cap, not an invalidation.

Fix: capture the original mode alongside the ID (a `Dictionary<int, CollisionDetectionMode>`)
and restore it in the same places `Camera.eventMask` is released. Track `Rigidbody`
references rather than bare IDs so destruction is detectable, or at minimum clear the set
in `SceneLoaded` and `Clear()`.

## A9. `variantsUsed` is never reset, so the Advanced settings die for the whole session

`SandField.cs:124`, `:169-171`:

```csharp
private int variantsUsed;
...
if (variantsUsed >= MaxVariants) return baseMaterial;
int slot = SandMaterials.FirstVariant + variantsUsed;
variantsUsed++;
```

`SceneLoaded` (`:357-374`) resets `count`, `bed`, `pendingFills`, `bedAtStart`,
`countAtStart`, `regionsFilled`, `regionsKept`, `water`, `ground`, `shapes` and
`rescanCountdown` — but not `variantsUsed`. `SandField` is `DontDestroyOnLoad`.

After 59 claims — sprayers with customised media, plus one per HUD brush via
`SandHud.BrushRow` — `ClaimVariant` permanently returns the base index for every remaining
level. `SandHud.BrushRow` then takes `customSlot < SandMaterials.FirstVariant`, and the
panel's *Advanced* settings silently stop applying. No log line, no way to recover except
restarting the game. The `materials[]` and `variantBase[]` rows also carry the previous
level's emitters.

`DESIGN.md:180-185` promises 60 variant rows before falling back, per level. Reset it in
`SceneLoaded` and re-seed the variant rows from the base table.

## A10. `SprayerEntityBehaviour`'s medium presets have drifted from the block's

Two defects in the same 30 lines, and both are the duplication `MediaHost` exists to
prevent (see `DESIGN.md:169-178`) failing at its job.

### A10a — the preset table is missing Mud and Glue

`SprayerBehaviour.cs:73-93` has `WetSand`, `Dust`, `Snow`, **`Mud`**, **`Glue`**, `Gravel`,
default. `SprayerEntityBehaviour.cs:69-76` has `WetSand`, `Dust`, `Snow`, `Gravel`,
default — **no Mud, no Glue**. So the level-editor sprayer set to Mud or Glue gets sand
defaults while the identical block gets the right ones.

### A10b — presets are armed too early, so a saved sprayer is clobbered on load

`SprayerEntityBehaviour.cs:63-64` does this in `Build()`:

```csharp
settings.MediaPicked += MediaChanged;
settings.Arm();
```

`SprayerBehaviour` deliberately does not (`:54`, and `:57-63`):

```csharp
public override void BuildingUpdate()
{
    // BuildingUpdate only runs while nothing is simulating, which makes it a
    // safe place to decide the block has finished being loaded.
    presetsArmed = true;
    settings.Arm();
}
...
private void MediaChanged(int value)
{
    if (!presetsArmed) return;
```

with the reasoning spelled out at `SprayerBehaviour.cs:67-68`: *"loading a machine also
raises ValueChanged, and clobbering a saved setting on load would be indistinguishable from
the setting not saving."* The entity has no equivalent latch, so a saved entity sprayer's
rate/speed/spread get overwritten by the medium preset on every load.

`Build()` is called from both `Awake` and `Start` (`:43-44`, correctly guarded by
`built`), so the entity does have a place to put the latch — it just does not have one.

## A11. `EndRun` skips `Abort()` on the keep-sand path

`SandField.cs:897-907`:

```csharp
private void EndRun()
{
    if (KeepBetweenRuns)
    {
        regionsKept.UnionWith(regionsFilled);
        regionsFilled.Clear();
        bedAtStart = null;
        return;                          // <-- returns here
    }

    if (stepper != null) stepper.Abort();
```

On the CPU path a step is deliberately spread over several fixed updates. If the run stops
mid-step with *Keep sand* on, `stepper.InProgress` is still true, so the next `FixedUpdate`
runs `AdvanceSlice` → `EndStep(true)` → `ApplyForces()` (`SandField.cs:1757`) and
`StepBed()` — all after `StatMaster.levelSimulating` went false and Besiege has put every
machine back in its built pose. `AddForce(..., ForceMode.Impulse)` (`:1910`) and
`AddTorque` (`:1924`) then hit a machine that is no longer simulating, with forces computed
from sand that is no longer there.

The non-keep path is correct; the keep path is simply missing the same two lines. StartRun
(`:886`) does it unconditionally, which is the pattern to copy.

Window is about 1 in 4 fixed updates per sand step, so it is rare, not exotic.

## A12. Half of every pressed rut's displaced volume is annihilated

`SandBed.cs:1980-1996`:

```csharp
c.top[k] -= sink;
c.packed[k] += sink;
...
// Half of it out to the sides -- past the footprint, as a lip beside the rut.
float spill = sink * 0.5f;
int free = 0;
for (int n2 = 0; n2 < 8; n2++)
{
    int ti = i + LipI[n2] * LipReach, tj = j + LipJ[n2] * LipReach;
    if (!pressure.ContainsKey(Key(ti, tj)) && !Held(ti, tj)) free++;
}
if (free == 0) continue;                 // <-- spill is dropped on the floor
```

`sink` has already been taken off the column. `spill` is half of it. When all eight lip
columns are held or pressurised, nothing is deposited and the volume is simply gone.

`Held` covers every column within `HoldReach = 2` of a load centre (`SandBed.cs:2017`) and
the lip is at `LipReach = 3` (`:2018`). A single contact patch cannot hold a lip — its patch
is only ±2 — but a machine with two contact patches less than about six cells (0.72 m)
apart merges their ±2 patches and holds the lip between them. Under a heavy multi-contact
machine resting on a pile this fires on every press, every step, permanently eating sand.

Contradicts the class's own contract at `SandBed.cs:57-59`, *"Volume is conserved"*, and
`DESIGN.md:519-522`, which claims the tests hold it to better than a thousandth of a percent.

Test: `BedSolverTest`'s sphere-into-pile check is the right place to add a multi-contact
press case.

## A13. `BedCodec` clamps on write but not on read

`BedCodec.cs:62-71` (encode):

```csharp
if (c.top[k] - c.bottom[k] <= minThickness) continue;
...
top[k] = Mm(c.top[k]);
if (top[k] <= bottom[k]) top[k] = bottom[k] + 1;
```

`BedCodec.cs:145-148` (decode):

```csharp
into.bottom[k] = bottom[k] * 0.001f;
into.top[k] = top[k] * 0.001f;
into.split[k] = into.bottom[k];
```

The reader imposes no `top > bottom` invariant, so the two are not a fixpoint: any column
thinner than a millimetre is packed 1 mm thicker than it was, and a column that encodes one
way can come back as one the next `Pack` *skips*. `Adopt`'s `c.filled` recount then
disagrees with `into.filled` (`:150`). Every `SaveAll`/`LoadAll`, every network chunk and
every level file goes through this, so it is not a rare path.

The encoder is right to enforce the invariant — a column with `top <= bottom` cannot be
rendered or released. The decoder should enforce it too, so a chunk that arrives does not
get to break an assumption the writer guarantees.

Test: `BedCodec`'s own round-trip is checked in `BedPressTest` for a 20 m mound; extend it
to assert the *thin and empty* columns survive a pack/unpack unchanged, not just that the
volume is close.

## A14. `GroundField.Ensure` can wedge a region-fill queue permanently

`GroundField.cs:133`:

```csharp
if ((long)(tx1 - tx0 + 1) * (tz1 - tz0 + 1) > 4096) return false;   // nonsense bounds
```

There is no way for a caller to tell "not ready, come back next frame" from "this can never
succeed". Both callers treat `false` as retryable:

- `SandField.FillPending` (`SandField.cs:549-552`) — `if (!ready) return;`, head-of-line
  blocking on `pendingFills[0]`;
- `SandField.FillLattice` (`SandField.cs:701-702`).

So one oversized region blocks itself *and every region queued behind it* for the rest of
the session, with no log line and no fallback to the lattice path. At 4 m per tile the
ceiling is 256 m × 256 m of ground per request.

Compounded by `SandField.cs:701`, which builds its cast area from the *magnitude* of the
half-extent:

```csharp
Bounds area = new Bounds(centre, Vector3.one * (halfExtent.magnitude * 2f));
ground.Ensure(area, -1f);
```

`new Bounds(c, size)` treats `size` as the full edge, so this is `2·|halfExtent|` on **every**
axis — the Y extent is inflated by the same factor as X and Z, which is never needed. For a
region with `half = (2.5, 50, 2.5)` that is a 102 m cube instead of 5 m, roughly 400× the
tiles, at `budgetMs = -1f`, i.e. **no budget at all**, in one synchronous call.

Fix all three parts: return a tri-state or add a `CanServe(Bounds)` predicate, size the
bound correctly (`halfExtent * 2f`), and give the lattice path a real budget.

# B. Reported but NOT confirmed

These came out of the same read but were **not** independently reproduced against the
source. Each is plausible and worth a look, several may be non-defects. Confirm before
changing anything — a speculative "fix" here costs more than it saves.

## B1. `SandBed.Displace` can both destroy and create volume

`SandBed.cs:1838-1848`:

```csharp
float amount = c.top[k] - Mathf.Max(layerCut, c.bottom[k]);
...
Withdraw(c, i, j, amount);
Deposit(ti, tj, amount, material, colour);   // bool result discarded
```

Two claimed problems. `Withdraw` truncates (`SandBed.cs:554-560` rounds a column to empty
when it drops below `MinThickness`), so it may remove less than `amount` while the full
`amount` is deposited — sand manufactured. And `Deposit`'s `bool` is discarded here, though
every other call site checks it (`:500-502`, `:2157-2158`); if it returns `false` the sand
has already left the source column and is gone.

The guard at `:1834-1836` is claimed not to be equivalent to `Deposit`'s own `NoGround`
check, because `TryTop` interpolates the 2×2 columns around `(ti,tj)` — so it can return a
height from neighbouring sand while the ground directly under `(ti,tj)` is still unknown.

The *amount* defect looks real on inspection; the `Deposit`-failure path needs the
`Deposit` return condition re-derived before it is believed.

## B2. `SandBed.Slump` deposits before it withdraws

`SandBed.cs:2155-2159`:

```csharp
if (lower > 0f && !Deposit(loI, loJ, lower, ...)) return;
if (upper > 0f && !Deposit(loI, loJ, upper, ...)) return;
Withdraw(hc, hiI, hiJ, move);
```

If the first `Deposit` succeeds and the second returns `false`, the `Withdraw` is skipped
and the lower layer has been added without being removed. Reported as *speculative* because
the two `Deposit` calls differ only in material and colour, and the reporter could not
construct a case where the first succeeds and the second fails — it needs `lower ==
MinThickness` exactly, where `Deposit`'s `had` test (`top - bottom > MinThickness`) reads the
column as empty and re-queries `ground.Height`.

Worth making atomic regardless: withdraw first, then deposit, or deposit into a temp.

## B3. `RestHeights` cannot keep its volume exactly, because `DepositGrid` drops the slivers

`SandBed.RestHeights` (`:765`, `:846-853`) scales `heights` so the total equals `volume`
exactly, including the ring of margin cells whose `Height()` returns a small positive value.
`DepositGrid` then drops every cell at or below `MinThickness` (`:878-879`):

```csharp
if (f > MinThickness && Deposit(i0 + i, j0 + j, f, material, colour)) placed++;
```

So a placed region's volume is systematically less than requested, and the shortfall scales
with the region's **perimeter** rather than being a constant rounding error. This reads as a
genuine contradiction of `DESIGN.md:269-270` ("the same volume to the litre").

Related, and the same shape of problem across the module: `Withdraw`, `FluidizeSurface`
(`:1412-1419`), `RemoveVolume` (`:1771-1774`) and `Deposit` each clamp at `MinThickness`
independently, so each can discard up to 4 mm × `Cell²` = 5.8e-5 m³ as a side effect of an
operation that is supposed to move sand rather than destroy it.

## B4. NaN survives every guard in `Settle`

`CpuSolver.Settle` (`CpuSolver.cs:1442-1457`) clamps with comparisons:

```csharp
if (speedSq > allowed * allowed) { ... }
if (settles && speedSq < 0.0004f) g.material |= SandFormat.FlagAsleep;
```

Every comparison against NaN is false, so a NaN grain is not clamped, does not sleep, and
is never reaped — `Reap` only tests `FlagDead`. One NaN therefore costs a grain slot for
the life of the process, and adds a NaN to every `Bounds` in `SandField.Bounds`, which
widens `BuildGrid`'s cell count towards `MaxCells` — i.e. it is also a route into A2.

The reported source is the unguarded cohesion branch at `CpuSolver.cs:1189-1210`: unlike the
overlapping branch (`:1218` guards `dist < 1e-6f`), it has no guard on `dist0`, and
`rest = radius + mRadius[mq]` divides by `rest` at `:1209`. A material row with
`radius == 0` gives `rest == 0` → NaN position. Rows at `[FirstVariant, FirstVariant +
MaxVariants)` are never seeded from `baseTable`, and `Predict` (`:571`) writes
`(uint)m.wetMaterial` into a grain's low byte without clamping it to `materials.Length`.

There is no `float.IsNaN` or `IsInfinity` test anywhere in the mod. A cheap sanitiser at
the end of `Settle` — reject non-finite pos/vel, mark the grain dead — would close the whole
class at once, and would also make A7c much harder to exploit.

## B5. `debtPrev`/`supPrev` are per-index and `Reap` shuffles the grains under them

`CpuSolver.cs:444-451` clears `debtPrev`/`supPrev` only for *appended* slots. But
`SandField.Reap()` compacts with `grains[i] = grains[count]; count--;` whenever a grain
dies, which happens most steps. The swapped-in grain inherits the dead grain's support
debt, so the charge is applied to the wrong grain and the block-load figure drifts.
`supPrev[i]` is also the denominator of `holds / supPrev[i]` (`:1121`).

Small, but it means the momentum-invention accounting described at `CpuSolver.cs:101` and
`DESIGN.md:855-859` is not exact once anything dies. Compaction is the natural place to
remap the two arrays alongside `grains`.

## B6. `SandNet.IsClient` is called once per spawned grain

`SandNet.cs:88-98`, reached from `SandField.Spawn` (`:387`) as
`if (SourcesPaused || SandNet.IsClient) return;`. In multiplayer that is
`Modding.Common.Player.GetLocalPlayer()` per grain, and `SprayerBehaviour.Emit` loops
`field.Spawn(...)` up to 50× per frame per entity. Single player short-circuits on
`StatMaster.isMP` so the cost is MP-only.

Cache it per step, or per frame, if `GetLocalPlayer()` is not free.

## B7. `HostAllowsOthers` is never reset when a session ends

`SandNet.cs:32`, set from the state message (`:307`). `ClientTick` resets `saidHello` but
not this, so a client joining a new session believes the last host's answer until the first
state message arrives. Speculative — depends on Besiege's session lifecycle.

---

# C. Housekeeping and allocations

None of these are correctness bugs. They are all in paths the repo's own docs treat as
GC-critical — `DESIGN.md:750-755` records Mono collector pauses of tens of milliseconds
landing in the middle of the simulation, which is why `BedCodec` keeps its buffers — so
these are cheap to fix and worth doing in the same pass as section A.

| Where | What | Note |
| --- | --- | --- |
| `SandBedRenderer.cs:195` | `Stopwatch.StartNew()` per frame in `Draw` | ~60/s for the life of the session |
| `SandBedColliders.cs:154` | `Stopwatch.StartNew()` per sand step | 25/s |
| `RegionSupport.cs:58` | `Stopwatch.StartNew()` per fixed update | |
| `GroundField.cs:135` | `Stopwatch.StartNew()` per `Ensure` call | |
| `SandBed.cs:1595`, `:2072` | `Stopwatch.StartNew()` per step | |
| `SandField.cs:582` | `Stopwatch.StartNew()` in the region fill path | |

`SandField.cs:140` and `:1997` already hold `readonly` `Stopwatch` fields and call
`Start()` — that is the pattern. Six sites use the allocating constructor instead.

| Where | What | Note |
| --- | --- | --- |
| `SandBedColliders.cs:274-278` | `new int[t]` on every collider recook | The class exists to *avoid* recooks, and already owns a correctly sized `scratch` array. Use `e.mesh.SetTriangles(scratch, 0, 0, t)` |
| `SandHud.cs:1223`, `:1415` | `Camera.allCameras` up to 3× per frame | Allocates a fresh `Camera[]` per call, and Besiege has many cameras (level editor, water, depth, water-height renderers). `HoldShield` only ever looks at cameras in `heldMasks` |
| `Chooser.cs:304-325` | `new Vector3[4]` per frame in `Reposition` | Called from `LateUpdate` whenever a list is open |
| `GroundField.cs:186` | `new float[256]` per cast tile | A full-level cast is thousands of tiles; `Ensure` spreads the *time* but not the *allocation`. A free list keyed on tile coords would do |
| `GroundField.cs:169-172` | `List<long>.Contains` per grain in `EnsureUnder` | Linear search once per grain whose tile differs from the previous grain's. The method's own comment ("*a lookup a grain and a handful of tiles*") only holds for contiguous ordering. Scattered sand gives 40,000 × 512 ≈ 20 M comparisons per step. A `HashSet<long>` removes it |
| `SandField.cs:1432`, `:1448` | Closure allocated per `Paint`/`Erase` call | Per frame while the brush is held. A cached `Action<float,float,float>` with mutable captured state would do |
| `SandField.cs:2017-2023` | `ReportHitch` builds an 8-part string on **every** slow fixed update and frame | `Debug.Log` is correctly throttled to 1 Hz, but the string is assembled unconditionally — so exactly on the machines suffering most. ~8 allocations per call, 100+ Hz |
| `Panel.cs:1163-1166` | `Figure(value)` allocates per cell per frame while a slider is dragged | Reached from `Dragged` and `Nudged`; also forces a font mesh rebuild |
| `SandHud.cs:1024-1034` | ~10 strings per `Refresh` in plain-panel mode, plus `new List<string>(SandMaterials.Names)` per medium change | `Refresh` fires on every slider `onValueChanged`. The `echo` guards are correct — this is purely allocation |
| `WaterLink.cs:137` | `readback.GetPixels()` per `CacheForCpu` | Up to 64 KB at 25 Hz. `heights` is correctly reused; `pixels` is not needed past the copy loop |

## C1. Static `GetInstanceID()` caches that are never invalidated

Four near-identical caches keyed on `Rigidbody.GetInstanceID()`, all `static`, all
`DontDestroyOnLoad`-lifetime, none invalidated on scene load:

- `GroundField.cs:99-108` (`blockBodies`, capacity `Clear()` at 8192)
- `SandHud.cs:59`, `:1311-1316` (`machineBodies`, capacity `Clear()` at 4096)
- `RegionSupport.cs:35`, `:96-109` (`machine`, capacity `Clear()` at 4096)

Unity recycles instance IDs after destruction, so after one level change an unrelated
`Rigidbody` can inherit a dead machine block's ID. The consequences differ:

- `GroundField.blockBodies` is the worst: a floor or scenery collider inheriting a dead
  block's `true` is skipped at `:220` as "a machine being built, not the floor", so that
  4 m tile samples as `NoGround`, and `Height` turns any cell touching the hole into a hole.
  **Grains over that tile fall through the world.** The converse bakes a machine into the
  floor — the bug the comment at `:216-219` says this was written to fix.
- `RegionSupport.machine`: a stale `true` fills a region as if in mid-air; a stale `false`
  lays a region on top of a machine about to drive off.
- `SandHud.machineBodies`: suppresses painting over the column with only a grey ring to
  show for it.

The size caps are capacity limits, not invalidation. The clean fix is a small
generation-stamped cache cleared in `SceneLoaded`, or keying on the object rather than the
ID. Whichever is chosen, do it in all four places at once — `GroundField` is the one that
can drop sand out of the world.

## C2. Static collider-ID set not cleared on teardown

`GroundField.cs:61` (`ignored`, a static `HashSet<int>`) is not cleared by
`Forget()` (`:118`). `SandBedColliders.Destroy` unregisters each collider and `Dispose`
drains them, so normal teardown is balanced. The gap is if a `SandField` is destroyed while
chunks sit in the bed's `retired` list — then `OnRetire` never fires, `bedColliders.Destroy`
never runs for them, and those IDs stay forever. `IsIgnored` is consulted by every ground
raycast (`:221`) and by `SandField.Solid` (`:656`), so a later level's collider inheriting
one of those IDs is treated as the mod's own bed surface.

## C3. `SandBed.Clear` leaves state behind

`SandBed.cs:218-229` resets `chunks`, `dirty`, `active`, `batch`, `activeSet`,
`filledColumns` and `hasExtent`, but not:

- `netQueue` (`:1468`) — keeps strong references to every retired chunk, so
  `TakeNetDirty` (`:1485-1495`) still hands the *entire former chunk set* to the network
  layer after a clear. A `MarkNet` on a re-created chunk at the same coordinates also
  queues a duplicate.
- `held` (`:2013`) — `Held()` returns true for surviving entries until the 64-step sweep
  (`:1931-1936`) clears them, so slumping into a freshly cleared area is blocked for up to
  10 presses.
- `digs` (`:1860`), `loads` (`:1874`), `pressStep` (`:2015`), `releaseFrom` (`:1372`).

## C4. `ReportHitch`-adjacent: a `Describe()` O(count × shapes) freeze

`SandField.cs:249-268`. 40,000 grains × every shape on the first F9 press. Probe-only, so
acceptable by design, but worth knowing it is a deliberate multi-hundred-millisecond freeze
if you go looking for it.

## C5. `Panel` accepts `NaN`/`Infinity` into block values and the save file

`Panel.cs:814-856`:

```csharp
if (slider == null || !float.TryParse(typed, out number)) { Show(cell); return; }
Write(slider, number);
...
private static void Write(MSlider slider, float number)
{
    if (slider.Min >= 0f && number < slider.Min) number = slider.Min;   // NaN < Min is false
    slider.Value = Free(slider) ? number : Mathf.Clamp(number, slider.Min, slider.Max);
}
```

`float.TryParse` accepts `"NaN"` and `"Infinity"`, and `Mathf.Clamp(NaN, …)` returns NaN.
Downstream: `Sync` (`:882-889`) sets `normalizedValue = Clamp01(NaN) = NaN` and the handle's
anchored position goes NaN; `Commit` → `BlockMapper.OnEditField` **serialises NaN into the
machine XML**; and `Figure(NaN)` reads back `"NaN"`, which re-parses to NaN, so it is
self-sustaining.

Note colour cells (`cell.Colour == false`) have no `onValidateInput` and no
`characterLimit`, so arbitrary text reaches `Entered`. Needs a finite check on the parse →
clamp → sync → commit path. Same root cause as A7c — one shared helper would serve both.

## C6. Resources not destroyed

- `Panel.cs:1309-1327` — the static `Texture2D` and `Sprite` for the hue band are
  `HideFlags.HideAndDontSave` and never destroyed. Bounded (allocated once) but the rest of
  the codebase destroys every `Texture2D`/`Mesh`/`Material` it makes, so this is an
  outlier.
- `SandRenderer.cs:296-318` — `Dispose()` clears `meshes` but **not** `meshQuads`. The two
  lists are assumed to be the same length (`MeshAt` appends to both). After a `Dispose`,
  `Draw`'s `if (meshQuads[meshIndex] != capacityQuads)` can compare a stale value against
  `capacityQuads`, match, and leave a brand-new `Mesh` with no triangles or normals — it
  silently draws nothing. Not currently reachable (`Dispose` is only called from
  `OnDestroy`, and `renderer` is a `readonly` field), so this is a latent invariant
  violation rather than an observed bug. Fix `Dispose`, not `Draw`.

## C9. Notable behaviour to re-check after any A3/A5 fix

Fixing A3 and A5 both change physics, and the affected numbers are pinned in
`DESIGN.md`. Before touching them, record the current values so a regression is visible:

- *Clinging is easy to lose to cohesion* — snow 86 and mud 104 grains carried round a spinning
  wheel (`SolverShapeTest`)
- the glue figures (*Sticky grains shook and pushed light blocks*): a 2 kg block pushed 58 m before / 21 m after;
  under 12 N sideways at any stickiness; mud 2 m where it moved 15; glue carried round a
  wheel 349 → 157 grains
- the dig speeds (the media table) (sand 0.8, dust 0.4, snow 0.6, mud 0.7 m/s), which
  were raised specifically so only a genuinely spinning wheel digs
- a slow pour settling at ~17 degrees (*Friction had almost nothing to push with*); the bed's angle
  of repose is now 30 degrees for sand, friction x 1.15 as a slope

---

---

# D. Loose ends

## D1. `Lz.Decompress` can overflow on a hostile payload

`Lz.Decompress`'s `Extra()` accumulates 255 per byte with no cap, so on a multi-megabyte
payload `r.At + lit` can overflow `int` negative and pass the guard at `BedCodec.cs:404`,
leaving `Array.Copy(..., lit)` with a negative length. Speculative, but it is one cast away;
see A7d, which reaches the same decoder from the network.

## D2. Checking the generated art

If `tools/genicons.py` or `tools/genshapes.py` are touched, `tools/genshapes.py --check`
validates the region meshes -- `DESIGN.md` records that the cylinder's mesh was once wound
inside out, and that check is what catches it now.
