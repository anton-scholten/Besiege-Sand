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

The sprayer entity is driven by **Modify Variable**, not by a modded event. A modded
event has no slider and no colour picker among its properties and cannot be aimed at
one placed object; Besiege's own variable event has a scope picker that names a
single entity. The variables are `sandspray` and `sandrate`, and **negative means no
opinion** -- nothing can delete a level variable once set, so without a reserved
value a level that took a sprayer over could never give it back.

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

## What blocks the GPU path today

Only one thing: the compute shader has to be baked into an asset bundle by a
licensed Unity 5.4.0f3 editor. The kernel source, the C# that drives it, and the
buffers it reads and writes are all authorable without one, and are being written
now. See [FINDINGS.md](FINDINGS.md) for the licence situation.
