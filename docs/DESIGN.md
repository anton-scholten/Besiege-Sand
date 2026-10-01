# Granular media in Besiege: the design

What is being built, and why each decision is the one the measurements in
[FINDINGS.md](FINDINGS.md) point at. Read that first; this assumes its numbers.

## The shape of it

One level-wide simulation, not a per-block one. A block is an *emitter*, a *sink*
or a *tool*; the medium itself belongs to the level, the way `Physics.gravity` does.
That follows the notes repository's rule for level-wide mods: publish to one shared
registry, reconcile rather than react, and put every global back on the way out.

```
  SandField            one per level. owns the particle state, steps it, restores globals
    +-- ISandSolver    CpuSolver (threads) | GpuSolver (compute shader)
    +-- ShapeFeed      colliders -> analytic shapes, uploaded each sand step
    +-- ForceReturn    per-block force/torque, read back once per sand step
    +-- WaterLink      the wave simulation's height texture, when a level has one
    +-- SandRenderer   GPU: StructuredBuffer + vertex shader. CPU: dynamic meshes
```

The solver sits behind an interface on purpose. The GPU path is the target; the CPU
path is what runs while the GPU path waits on a shader bundle, and stays afterwards
as the fallback for machines that report `supportsComputeShaders = false`.

## Stepping: not every FixedUpdate

Besiege runs physics at **100 Hz** (measured). Stepping a particle simulation from
every `FixedUpdate` would spend the whole frame budget on it and force a hundred
GPU readbacks a second.

So the medium has its own rate — `sandHz`, default 25, i.e. one sand step per four
fixed steps — with its own substeps inside. Visual positions interpolate between
sand steps. One shape upload and one force readback per sand step.

**Coupling to rigidbodies.** The solver reports, per body, the contact force summed over
the step's substeps. That sum lasted one substep each, so it is applied as an impulse of
`force x (dt / substeps)` -- not `AddForce`, which would be an impulse of the force times the
fixed step, once per sand step, and hand a block about 45% of the sand's weight. The speed
a body can gain from sand in one step is capped (`MaxDeltaVPerStep`), and so is the spin
(`MaxDeltaSpinPerStep`), so a bad frame shoves rather than launches.

What a contact hands back is everything it moved the grain by: the push out *and* the
friction along the surface, so a plough feels the sand dragging on it and a wheel has
something to drive against. The surface's velocity at the contact is the body's velocity
there plus its spin -- a wheel's centre can stand still while its rim tears at the sand --
and torque is taken about the body's centre of mass, where the force is applied. Shapes
are snapshotted when a sand step begins and moved along their velocity through its
substeps, so a block crossing a pile meets the sand where it is, not a step behind.
Sprayed sand pushes from the nozzle; only a region's lattice, which may start overlapping
something, waits `SettleSeconds`. `tools/test-bed.sh` checks that sand resting on a movable
box loads it with its own weight, that a spinning wheel in sand throws it back and is
pushed forward, and that a box dragged through sand is held back.

## The materials

Five, and they are one parameter block each rather than five code paths. A material
is a `const int` (Besiege's compiler segfaults on any `enum` declaration) indexing a
table.

| | grain radius | density | friction | cohesion | restitution | air drag | notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| **Sand** | 0.03 | 1600 | 0.60 | 0 | 0.10 | low | repose ~31°, the reference material |
| **Wet sand** | 0.03 | 2000 | 0.85 | high | 0.05 | low | holds a vertical face, clumps, heavy |
| **Dust** | 0.008 | 400 | 0.35 | very low | 0.05 | **very high** | billows, settles slowly, hangs in air |
| **Snow** | 0.035 | 300 | 0.70 | medium | 0.02 | medium | packs under load, light, quiet |
| **Gravel** | 0.08 | 1800 | 0.90 | 0 | 0.40 | low | repose ~40°, bounces, does not pack |

Cohesion is what separates them more than friction does: it is an attractive term
applied between neighbours within a small distance, and it is the reason wet sand
holds a dug face while dry sand slumps to its angle of repose.

Two derived behaviours fall out rather than being special-cased:

- **Wetting.** A dry grain whose position is below the water surface becomes its wet
  variant (sand → wet sand). The transition is a material index change, so it costs
  nothing and reverses on a timer when out of water.
- **Packing.** Snow's cohesion rises with the pressure it has been under, so tracks
  compact and stay compacted.

Materials are per particle, not per field, so one level can hold gravel spoil, a
snow drift and a dust cloud at once, and a digger can move one into the other.

## Colour and texture

Per particle: a packed 32-bit colour. Per material: a tint and a tile index into a
small atlas the mod ships. A particle is drawn as a camera-facing quad, so a texture
is an ordinary sprite — grain speckle for sand, soft blob for dust, faceted chip for
gravel.

Colour comes from three places, multiplied: the material's own tint, a per-emitter
colour the player sets in the block mapper, and a wetness darkening applied when the
wet flag is set. That last one is why wet sand reads as wet without a second texture.

On the GPU path the colour rides in the particle struct and the vertex shader reads
it; on the CPU path it goes into the mesh's vertex colours. Both end at the same
material, so the two paths look identical.

## The settings panel, and why one class builds two of them

The media picker, the colour and the advanced physical settings are one class,
`MediaSettings`, used by the sprayer block and by every level editor object.

It cannot simply take a common base type, which is the trap here. A level object's
`GenericEntity` comes from `SaveableDataHolder`, but a modded block's
`Modding.ModBlockBehaviour` is a plain `MonoBehaviour` with its own `AddSlider`,
`AddMenu` and the rest at different arities. The calls match; the classes do not. So
there is a two-method interface, `IMediaHost`, and one thin adapter each.

**Advanced settings do not redefine a medium for everyone.** Opening the toggle
claims a *variant* row seeded from the chosen medium, and the sliders write that row.
Two sprayers can therefore throw sand of different coarseness in the same level, and
neither changes what the other pours. Sixty variant rows exist; past that an emitter
falls back to the plain medium, so a level with too many customised emitters loses
the customising rather than the sand.

The sliders show **density**, while the solver's table keeps **inverse mass** --
because density is what a person has an intuition for and inverse mass is what the
contact solve wants. The conversion is in one place.

## The level editor objects

Four, all in the `Virtual` category beside Wind and the triggers:

| | |
| --- | --- |
| Media Box / Cylinder / Sphere Region | a volume that is already full. Scale it and it holds more |
| Media Sprayer | the same crate as the block, driven by the level instead of a key |

A region has the media picker and the advanced settings and nothing else: no rate,
no speed, no spread, no trigger, because it does not emit. The lattice step comes
from the grain the region will actually hold, so a gravel region places far fewer,
far bigger grains than a dust one over the same volume without anybody choosing a
number. The shape is the same lattice with the corners rejected -- one loop, three
objects.

**How a region is filled.** On the CPU path a region is held as *bed*, not grains: each column
under it takes the length of the shape along its vertical line (an average of a grid of lines
across the column, so a shape's edge costs no sand), which makes a box, a cylinder or a
sphere hold exactly its own volume, turned or not, and one placed above the ground drops
onto it. That is the only thing that works for a tall region. As grains, a block a metre
deep is sixteen layers, and under Besiege's gravity three solver iterations cannot pass the
weight down it fast enough, so the bottom compresses and a third of the grains are fired off:
a placed block went off like a charge. Held as bed it costs nothing, slumps gently to its
angle of repose, and turns into grains only around whatever touches it. The lattice above is
the fallback, for the GPU path and for a region too large to hold as columns; there the
jitter is limited to what the gap between grains allows and nothing is born below the ground.


The sprayer entity is driven by **Modify Variable**, not by a modded event. A modded
event has no slider and no colour picker among its properties and cannot be aimed at
one placed object; Besiege's own variable event has a scope picker that names a
single entity. The variables are `sandspray` and `sandrate`, and **negative means no
opinion** -- nothing can delete a level variable once set, so without a reserved
value a level that took a sprayer over could never give it back.

**Where a region sits.** A region's origin is the middle of its base: the entity XML raises
the unit mesh and its collider half a unit. Placed, it stands on the surface that was
clicked; scaled, it grows up out of the ground. It used to be centred on its origin with a
placement `<Offset>` lifting it half a unit -- which the editor applies once, at placement,
while it scales about the origin, so the first resize buried half the region, and saving
and reloading kept it there. A level saved before this change stored each region's centre,
which is now read as its base, so its regions appear half their height higher than they
were placed; lower them once and save. The collider is a trigger: the editor's
pick ray takes triggers (`QueryTriggerInteraction.Collide`), while the ground raycasts and
the shape feed skip them -- the object stays in the level while it runs, with only its
renderers hidden, so a solid box would be measured as the ground under the region, have
the sand laid on top of it, hold that sand up invisibly and stop machines driving through.
The cylinder's mesh was also wound inside out, so it was drawn as its far inner wall -- a
mirrored hollow -- and `tools/genshapes.py --check` now checks every region mesh.

Controls are added in `Awake` *and* `Start`, guarded by a flag. `CreateEntity`
instantiates the entity and loads its saved data in the same breath, while Unity does
not run `Start` until the end of the frame -- so a control added only in `Start` is
not there for the saved value to land in, and every setting silently resets on load.

## Water

Besiege's water is CodeAnimo *Surface Waves*, and it exposes exactly what is needed
without a single readback:

- `BuoyancyManager` is findable in the scene, and its `waveData` and `terrainData`
  are `SimulationOutput`s whose `outputData` is a live `RenderTexture`;
- `simPosition` is a `Dimensions` carrying `firstCorner`, `localSize`,
  `resolutionX`/`resolutionZ` — the mapping from world XZ to texel.

So the compute kernel binds the wave height texture and samples it per grain. A
grain below the surface gets buoyancy scaled by the density ratio, heavy drag, and
the wet flag. Nothing crosses to the CPU for any of it.

`terrainData` is a second gift: it is the level's ground height already rendered
into a texture, which is the answer to there being no Unity `Terrain` in a Besiege
level. Where a level has no water simulation, the mod renders its own height texture
the same way — one orthographic pass from above, once per level.

Sand displacing water in return — writing into the wave simulation's input — is
possible through the same asset's input sources and is deliberately left for later.
One-way is honest and cheap; two-way needs care about stability.

## Space

The space levels are ordinary scenes with different globals, so the rule is simply
never to assume:

- read `Physics.gravity` every step, never a literal. (The notes repository records
  a mod that hardcoded `-32.81` and broke every level whose gravity differed.)
- when `|g|` is near zero: no settling, no sleep threshold, air drag off — a vacuum
  does not damp anything — and cohesion becomes the dominant force, so a released
  handful of sand clumps into a slowly rotating ball instead of falling. That is the
  correct behaviour and it comes free from the same solver.
- `StatMaster.GodTools.GravityDisabled` is the player's own switch and is not the
  mod's to write, in either direction.

Anything the mod writes to a global is captured lazily on first write and restored
from whichever callback notices the run ended first, guarded to be idempotent.

## Sleeping sand: the bed

The GPU solver is waiting on a Unity licence (see below), and the CPU solver is the
thing that actually runs. Its cost is per grain, and most grains in a level are at rest
most of the time -- a pile is a surface, not a crowd. So sand that has come to rest is
stored as a **height field** and the grains are dropped. Only sand that is moving, or
that something is touching, is ever a grain. The same grain budget then describes a
far bigger pile.

Everything lives in `SandBed.cs`, and it is deliberately free of the engine so it can be
tested headlessly (`tools/test-bed.sh`).

* **Storage.** A sparse grid of 12 cm columns, sixteen by sixteen to a chunk. A column
  holds a bottom (the ground when sand first arrived), a top, a material and a colour.
* **Absorbing.** The CPU solver counts how many substeps each grain has been nearly
  still, in four spare bits of `Grain.material`. A grain that has been still for twelve,
  is not about to expire, is not within three radii of any shape and is at or below the
  surface it lies on is absorbed: its volume -- its sphere over a packing fraction of
  0.62 -- is added to its column and the grain is flagged dead. Grains with a short
  lifetime are left alone, so a sprayer set to make sand that goes away still does.
* **As ground.** Moving grains treat the bed's surface as floor wherever it is higher
  than the level's own (`CpuSolver.Solve`), with a normal from the height gradient. The
  bed holds no force of its own.
* **Releasing.** For every shape, the sand in each column it reaches into -- from just
  below its lowest point to the top of the pile -- becomes grains again, on a lattice so
  they are not born overlapped, and those grains push the shape like any others. A wheel
  driving through a pile carries a little patch of live sand with it and leaves the rest
  asleep. Bounded by a budget of distance tests per step and by the field's free room.
* **Slumping.** A pile steeper than its material's angle of repose (the friction
  coefficient, a little generous, over eight neighbours) sheds sand to the lower
  column. Only columns that changed are looked at, so a settled bed costs nothing.
* **Drawing.** One small mesh per chunk, rebuilt only when its columns change, six at
  most per frame. Unlit, so the slope is shaded into the vertex colour, and the edge of a
  pile fades into the ground. Drawn with `Hidden/Internal-Colored`, Unity's own line shader,
  which every player carries and which writes depth -- the particle shader it used before
  does not, so a heap's far slope was painted over its near one and read as a hollow. Grains
  without a sprite atlas use the same material, for the same reason.

Volume is conserved exactly through absorb, slump and release, and the tests check it:
4000 grains dropped on flat ground end as bed with the total volume unchanged to better
than a thousandth of a percent, and a sphere pushed into the finished pile wakes a patch
that settles back without losing or gaining any.

What it does not do. The bed is not a collider, so a block resting on it is held up by
the grains released beneath it, not by the bed. It is CPU-only: with the GPU solver there
is room for a quarter of a million grains and no reason for it. And it is only as good as
the ground it sits on -- the ground is rasterised at up to 96 cells a side, so over a big
level the floor under the bed is coarse. Nothing here has been run inside Besiege yet;
the headless tests cover the logic and not how it looks or feels.

## Making the CPU solver carry more

The GPU solver is waiting on a Unity licence, so the CPU solver has to do. Its cost is per
grain, and a profile of the original showed where it went. Eight thousand awake grains:

| collider count in range | per step, before | per step, after |
| --- | --- | --- |
| none | 63 ms | 18 ms |
| 100 | 258 ms | 20 ms |
| 300 | 710 ms | 29 ms |

The surprise was that grain-to-grain contact was not the main cost. Every grain tested
every collider in range, on every pass, and a machine is a few hundred colliders. Four
changes, in order of what they bought:

* **Collider culling.** Each shape gets a tight world box and a cached rotation and inverse
  once per step. A grid over the ground plane lists the shapes whose box touches each
  half-metre cell, so a grain tests only the few colliders in its own cell, and only those
  whose box it is inside get a real distance test. A machine of 300 colliders now costs
  about as little as one of 3.
* **A better grain grid.** A counting sort rather than fixed buckets: no per-cell cap (the
  old one silently dropped neighbours past sixteen), memory of one int per cell and two per
  grain, and the three cells in a row of the neighbour sweep are one contiguous run, so the
  27-cell search is nine runs. Cells are as small as the reach of a contact allows -- the
  widest pair of radii, doubled only while something present is cohesive -- so dry sand
  searches about an eighth of the volume a cell sized for the worst case would. The
  neighbour loop reads flat per-material arrays and grain fields in place, because
  copying a 60-byte grain and a 48-byte material row for every pair was a large share of
  what was left.
* **Slicing.** A step is a fixed list of passes. Run in one go it is a 20 ms stall every
  fourth fixed update; advanced a few passes per fixed update it is about five milliseconds
  a frame. Safe because the step works from a snapshot, grains appended during it are first
  stepped by the next one, and grains are only removed between steps. The worst single
  slice of a heavy pour is about 10 ms.
* **Coarse grains.** For sand that is moving, the only lever left is how much sand a grain
  stands for. Every medium has three coarser versions (rows `Count * level + medium`, radius
  times 1.5, 2.2 and 3.2, capped at 0.2 m) that are the same material with a bigger sphere.
  An emitter spawns fewer of them in proportion to the volume each stands for, so the rate
  of sand it makes does not change, and a region of coarse grains holds the same sand as a
  sparser lattice of fine ones would hold less of. This is the adaptive-sampling idea from
  the granular-simulation literature -- simulate a smaller set of particles accurately and
  get the volume from them -- done at emission time. The bed's volume per grain follows each
  row's own radius, so coarse and fine sand are interchangeable in it.

**The governor** (`SandGovernor.cs`) decides when to use them. Judging by step time reacts
only after the damage is done, which for a fast pour is thousands of grains too late, so it
measures instead: each step reports its time and its grain count, which gives a cost per
grain, and the budget over that is how many grains are affordable. That is the ceiling on
how many are admitted, set at once. When the live count fills the ceiling, quality is spent
in order of how little it shows -- fewer solver iterations, then coarser grains, then fewer
iterations again -- and returned, more slowly, when the count falls well below it. F9 in a
level prints where it stands.

A closed-loop pour (`./tools/test-bed.sh --load`: the real solver, bed and governor, four
thousand grains' worth of sand a second for twenty seconds) poured 7.1 cubic metres, about
39,000 ordinary grains, with at most 7,200 live at once, a mean step of 18 ms against the
20 ms budget and a worst slice of 12 ms, and every cubic metre accounted for in the bed and
the grains afterwards. The old cap was 12,000 grains and no budget.

**A second pass** (`CpuSolver.Solve`), taken together with the physics fixes below, on the
same eight thousand grains poured into a heap beside three hundred colliders, eight threads:

| | per step |
| --- | --- |
| before | 25.4 ms |
| after | 14.4 ms |

* **The floor is looked up once a substep**, per grain, as a height and a normal, instead of
  on every solve: over the bed that was a dozen dictionary lookups per grain per pass.
* **Grains are kept in the grid's order**, re-sorted at the start of every step, so the
  neighbours a grain reads are mostly next to it in memory instead of anywhere.
* **Each contact is handled once**, by whichever grain of the pair comes first in the sort,
  which moves both: half the pairs are searched, and (below) momentum is conserved.
* **The pair loop is written in floats.** The game's Mono does not inline `Vector3`'s
  operators, and in that loop every one of them was a call.
* **The solve is shared out by slab of the grid**, alternate slabs at a time, each thread
  taking the next free slab. Cutting the grains into one even range per thread left a dense
  heap's middle on two of them while the rest waited.

The closed-loop pour (`--load`) now handles 13.6 cubic metres, 74,000 ordinary grains' worth,
at a mean step of 13.6 ms and a worst slice of 7.7 ms; 10,700 grains were live at the peak.

What is left. The per-grain cost is about 1.8 microseconds, so an *active* pile is
bounded by the budget however it is divided; the bed is what makes a *resting* one free.
Only the CPU path is governed. The coarse grains are visible as a chunkier pour while the
governor is under pressure, and they apply to grains emitted from then on, so the fine ones
already in the air linger until they settle. And none of this has run inside Besiege yet:
the headless tests cover the logic, the timing and the volume, not how it looks.

## Sand as a material: what was wrong with the physics

The solver moved sand around convincingly enough that it took a user placing a block of it
and watching it go off, and a pile bounce along the ground, to show that it was not behaving
as a granular material at all. Each fix below came from measuring a tiny scene, not from
reading the code; `tools/tests/SolverPhysicsTest.cs` keeps them.

* **Grains could not hold each other up.** A grain placed in the pocket of three others, the
  one arrangement spheres stack stably in, fell to the floor. A 600-grain pour ended as a
  single layer one grain deep, flat as a sheet. Raising friction made it spread *more*, which
  is what said friction was not doing its job.
* **Friction could not see the slip.** Contact friction was built from each grain's velocity at
  the start of the substep. The sideways slip comes from the position corrections made *during*
  the substep -- the contacts themselves push a grain along -- and the start-of-substep
  velocity has none of it, so friction never resisted the motion it existed to resist.
  Friction now resists the displacement so far this substep, shared between the two grains by
  mass, which is the standard position-based form. For the ground and for colliders too.
* **Friction had almost nothing to push with.** A contact resists slip up to friction times how
  far it was pushed apart, but by the time the solver looks again that overlap is a millimetre
  or two. Scaled up threefold (`FrictionBoost`), a slow pour settles at about 17 degrees
  against 9 before; much more saturates, because friction is then limited by the geometry of
  spheres, which roll, and starts to make a pile creep. Real sand is nearer 30; piles that
  rest in the bed are slumped to their angle of repose, 34 for sand.
* **Sleeping grains sank.** A sleeping grain skipped the neighbour sweep to save time, but
  gravity still moved it and nothing pushed back, so it sank into whatever held it up until
  an awake neighbour noticed the overlap and woke it. That cycle collapsed stacks and made a
  pile shiver. Sleepers sweep like anyone else now.
* **Sand bounced.** Restitution was 0.10 for sand and 0.40 for gravel. With the stacking fixed
  it hardly matters, but it is 0.02 and 0.12 now. After the fixes, settled sand moves *up* at
  more than 0.3 m/s for 1 to 2 grains in 400 per step, down from 20 at up to 1.5 m/s.
* **What did not work**, for the next person: averaging a grain's contact corrections (it made
  the shimmer worse), velocity damping on contacts (likewise), a rolling-resistance term, and a
  hard "static" speed cutoff. Several were tried on the same scenes and removed.
* **A tall column of loose grains still bursts**, about a third of the grains over 3 m/s for a
  moment, at any friction. That is the compression wave described above and is why regions go
  into the bed. It remains for the fallback paths and for anything that wakes a deep pile; the
  bed's release only fluidises from a shape's lowest contact upwards, which is shallow.

Also corrected here: the force the solver reports for each body is summed over the step's
substeps, and was applied once per sand step with a plain `AddForce`, an impulse of the force
times the fixed step. It is now applied as an impulse of the force times the substep (see
*Coupling to rigidbodies* above).

### The second round

The fixes above left sand that piled at about 12 degrees in a pour, a block of grains that
still burst, and -- found while measuring this round -- a block under sand that carried half
the sand's weight. The test that should have caught it compared the force summed over two
substeps with one weight, and passed at 15%. Each fix below is in `CpuSolver` and is held by
`tools/tests/SolverPhysicsTest.cs` or `SolverShapeTest.cs`.

* **Contacts leaked momentum.** Each grain handled every contact from its own side, moving
  only itself, and in place: the second grain of a pair then met a contact the first had
  already half resolved. The pair separated by less than its overlap and the pile held
  itself up with momentum nothing paid for, so whatever it rested on felt half of it. Each
  contact is now handled once and moves both grains, by inverse mass; a pile on a box loads
  it with 1.00 of its weight.
* **A pile could not hold itself up.** Under Besiege's gravity a grain falls 1.3 cm in a
  substep, nearly half its radius, and a few solves cannot pass a pile's weight down to the
  floor in time; the pile compresses and spreads like a liquid however high friction is.
  Contacts are now resolved leaning towards the upper grain (*mass scaling*, Macklin et al.
  2014; `StackStiffness`), which with the grains visited bottom-up in the sort makes a stack
  stand in one sweep. That leaning invents momentum, exactly computable per contact, and it
  is charged to the lower grain and passed down through the supports to the ground or to the
  block underneath, so the loads stay right (`debtPrev`).
* **Overlap came out as speed.** In a position solver the distance moved to clear an overlap
  becomes velocity, so a squeezed grain was fired out. A grain may now leave a substep at
  most 0.3 m/s faster than it went in or than the surface that pushed it
  (`MaxSeparationSpeed`, PhysX's `maxDepenetrationVelocity` for the same reason). The loose
  block throws 2 grains upwards where it threw a third of them.
* **Friction was doing stiffness's job.** It had been raised threefold to make piles stand;
  with stacks standing on their own that made sand too stiff. At 1.5 a pour settles at 34
  degrees -- dry sand's angle of repose, and the bed's, so sand looks the same before and
  after it goes to sleep.
* **Grains cannot roll**, so friction props up a column of them that a real one would roll
  off: a block laid out in columns stood two seconds with vertical walls. Region lattices
  and the sand the bed gives back now shift every other layer half a step into the pockets
  of the one below; the block slumps at once into a heap.
* **Fast sand went through things.** The floor and the shapes pushed a grain out by at most
  a radius a pass, so sand landing at 15 m/s sank a quarter of a metre into the ground, and
  sand falling at 12 m/s passed through a 2 cm plate, being further past it in one substep
  than it is thick. The floor now pushes all the way out; for a shape, a grain that moved
  more than its radius relative to it is walked back along its path and stopped where it
  first touched.
* **Restitution was applied on every solve**, three bounces a contact; it is once a substep.

## What blocks the GPU path today

Two things, and the bundle is the smaller of them.

**The kernel is behind.** `Sand.compute` is the solver as it was before either round of
fixes above: no floor, no bed, every grain against every shape, contacts handled from both
sides with the old friction. It would need `CpuSolver.Solve` ported to it -- the slab
scheme becomes a graph colouring or a Jacobi pass with the debt kept -- and the GPU path has
no renderer yet (`SandField.Update` draws nothing on it). Bundle or not, it is not a switch
that can be thrown.

**The bundle needs a Unity 5.4.0f3 editor**, licensed. A `.compute` is compiled per graphics
API (GLSL through HLSLcc for OpenGL, DXBC from `fxc` for Direct3D 11) and serialised into a
`ComputeShader` object whose layout is the editor's, so there is no doing it in the mod.
Building one without the editor would mean writing that object by hand, and there is
nothing in the game to copy: of Besiege's 202 data files not one holds a `ComputeShader`
(counted; see FINDINGS.md). Compiling at runtime is out, the player having no shader
compiler, and so is calling OpenGL directly, since `System.Runtime.InteropServices` is on
the loader's blacklist.

What does not need a local editor: the bundle is a file. Built once, by anyone with 5.4.0f3
(`tools/build-bundles.sh`), it can be committed and shipped, and nobody else needs Unity.
See [FINDINGS.md](FINDINGS.md) for the licence situation.
