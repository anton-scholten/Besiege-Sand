# Finer terrain deformation: options and next steps

A handoff for whoever (human or AI agent) picks up making the sand's tracks and ruts look more
detailed. It assumes nothing from earlier conversations: everything needed is here or in the files
it names. Read [AGENTS.md](../AGENTS.md) first for how to build and test.

## Where things stand (10 October 2026)

The sand is a height field and nothing else; there are no simulated grains (removed on 10 October,
see `DESIGN.md`, "The shape of it").

- **The grid.** `SandBed` (`Sand/Scripts/SandBed.cs`) stores columns `SandBed.Cell` = **0.12 m**
  apart, in chunks of `ChunkSide` = 16 x 16 columns (1.92 m). A column holds up to `MaxLayers`
  layers of different media, a bottom and a top.
- **Drawing.** `ChunkMesher` (`Sand/Scripts/ChunkMesher.cs`) builds each chunk as 17 x 17 vertices,
  one per column centre (`ChunkDraw.Verts`), on worker threads (`ChunkMesherPool`), reading the
  heights with `SandBed.ReadBlock` (a 19 x 19 block, so normals at the seams match).
  `SandBedRenderer` packs chunks into tiles of 4 x 4 chunks and redraws a dirty tile at most every
  `RebuildEvery` (0.1 s).
- **Colliders.** `SandBedColliders` cooks a `MeshCollider` per chunk at the **same** 17 x 17
  resolution (`SandBed.SurfaceOf`), only for chunks within reach of a machine's body, a few per
  sand step. Cooking is one of the measured costs in `PERFORMANCE.md`.
- **Deformation.** Every fixed update `SandBed.Press` sinks the columns under what the colliders
  report pushing on them, to `sqrt(pressure / firmness)` but never past the medium's `rutDepth`
  (compaction limit); half of what sinks is pushed out as a shoulder beside the rut. A load is a
  **smooth footprint** (`Falloff`, reaching `LoadSpread` = 2.5 columns) measured from where the
  contact actually is, and the shoulder is the nearest column in each of eight directions that
  nothing is pressing and nothing is standing on (`Shoulder`, `LipWalk` = 5). `SandBed.Plough`
  (each sand step, 25 a second) pushes sand aside from a face moving sideways into it and from a
  shape inside it, piling it on a footprint round where it lands (`BermReach` = 3, smeared twice
  as far along the push as across it). `SandBed.Relax`/`Slump` lets anything steeper than the
  medium's angle of repose (`friction x 1.15`, as height per metre) slide down, at the medium's
  `fillSpeed` (`SandBed.FlowLimited`, `Clock`, `Chunk.slidAt`). Columns something stands on are
  held (`Held`, `HoldSteps`) so a rut does not fill under the wheel making it, and let go again
  when the hold runs out (`ReleaseHolds`) so the walls do come down afterwards.
- **Per-medium numbers** are in `SandMaterials.Build` (`Sand/Scripts/SandMaterials.cs`):
  `rutDepth` from 4 cm (gravel) to 30 cm (snow, mud), `fillSpeed` from 1.2 m/s (dust) to 0.01 m/s
  (glue). The panel's Advanced section edits them per brush (`SandHud.BuildAdvanced`,
  `SandMaterials.Custom`).

## What the 10 October fixes already did for this

Three defects were found and fixed while picking this up, and they took most of the blockiness
out of a wheel's track at the level of the data -- so the remaining case for this document is
narrower than it was:

- The sand a shape ploughs aside went into **one** column, the whole column above the shape's
  underside however deep. A wheel ploughing along handed every column's sand to the next one,
  which handed on what it had been given, and the column the wheel stopped at stood **13 m**
  over half a metre of sand. It is piled on a footprint now: the same run ends with its tallest
  column 1.8 m up, settling to 0.7 m.
- A load's footprint was the nine columns around its contact **at one pressure each, snapped to
  the column grid**. That put a step at the edge of every rut and another wherever a rolling load
  crossed a column boundary. A rut across is now `49, 42, 43, 45, 45, 45, 45, 45, 52 cm`;
  before it was `53, 52, 42, 42, 42, 42, 42, 52, 53`.
- **A rut's walls never came down at all.** A column something stands on is taken off the list of
  columns to be checked for slumping and nothing put it back, so the rut and the shoulder beside
  it kept whatever step the wheel made them -- a slope of 1.00 against an angle of repose of
  0.58 -- for as long as the sand was there. `ReleaseHolds` looks at a column again when its
  hold runs out. A wheel's track now has no wall past 0.64.

`BedPressTest` measures all three, and fails against the code as it was. What is left is the
part this document is about: a rut is still three columns of flat bottom with a facet each side,
because the grid is 0.12 m and there is one vertex per column.

## The problem

A wheel is 0.3 to 0.6 m wide, so its track is 3 to 5 columns across. Ruts, lips and tyre marks
still come out blocky. **The limit is the data, not the mesh**: the drawn mesh and the colliders
both have exactly one vertex per column, so a finer mesh over the same heights can only smooth
them, not add detail that is not stored.

## Options, with their costs

### A. Smooth the drawing only (recommended first)

Draw each chunk at 2x (or 3x) the vertices, the extra heights interpolated smoothly between the
columns (Catmull-Rom or a bicubic B-spline across the 19 x 19 block the mesher already reads).
Colliders and simulation unchanged.

- Gains: rounded rut edges and lips instead of steps; no change to pressing, slumping, saving,
  multiplayer or the colliders.
- Costs: 4x (or 9x) vertices and triangles per chunk to build and draw. Building is threaded and
  happens only when a chunk changes, so the cost is mostly GPU and upload. A tile of 4 x 4 chunks
  at 2x is 33 x 33 x 16 = about 17 400 vertices, under Unity 5.4's 65 535 per mesh; at 3x it is
  about 39 000, still under, but leave headroom.
- Risk: the drawn surface and the collider differ by the interpolation, a centimetre or two. A
  smoothing spline that passes through the column heights (Catmull-Rom) keeps that smallest.
- Where: `ChunkDraw` (array sizes from `Verts`), `ChunkMesher.Build` (vertices, normals, colours,
  triangles), `SandBedRenderer` (tile assembly assumes `ChunkDraw.Verts`), and
  `ChunkMesherTest` (seam and thread checks must still hold). Colours are per vertex: interpolate
  them too, or sample the column a fine vertex falls in.

### B. A finer grid only where machines have been (sparse detail layer)

Give a chunk an optional detail grid, e.g. 4 x 4 sub-columns per column (0.03 m), allocated the
first time `Press` or `Plough` touches it, freed (folded back into the coarse columns) when it has
been still for a while. Pressing, ploughing and slumping act on the detail grid where it exists;
the coarse column is kept as the average so everything else (colliders, saves, multiplayer,
`TryTop`) keeps working on the coarse grid.

- Gains: real detail: tread-width ruts, sharp lips, footprints.
- Costs: 16x the columns in touched chunks for pressing and slumping; memory per detail chunk
  (16 x 16 x 16 floats x a few arrays); slumping across the boundary between a detail chunk and a
  coarse one; saving (`BedCodec`) and multiplayer (`SandBed.Encode`) either ignore the detail
  (send only the coarse average: clients see smoother tracks) or carry it (bigger messages).
- Risk: highest of the options. Volume must be conserved across the coarse/fine boundary
  (`BedUnitTest`, `BedPressTest`, `BedLayerTest` all check volume).

### C. Two meshes: coarse colliders, fine visual

Keep colliders on the coarse grid, or make them coarser (one vertex per 2 columns) away from
machines, and draw a fine visual mesh (option A or B) with no collider.

- This is how most games do deformable ground, and it pairs naturally with A or B: the physics
  never needed finer than 0.12 m (a wheel is a sphere or capsule, `ShapeFeed`), and cooking
  fewer triangles is cheaper.
- Risk: a wheel visibly floats above or sinks into the fine surface by the difference between
  the two. Keep the fine surface within a few centimetres of the coarse one (Catmull-Rom does;
  a detail grid must keep its coarse average equal to the column).

### D. One adaptive mesh: fine chunks where deformed, coarse elsewhere (level of detail)

Per chunk, draw at a resolution chosen by how deformed it is (or how near the camera).

- Gains: less to draw on large untouched areas.
- Costs: cracks where a fine chunk meets a coarse one, which need skirts or stitched edges
  (`ChunkMesher` already reads neighbouring columns for seam normals; stitching needs the same
  for vertex positions).
- Note: untouched sand is already cheap. A flat plateau is skipped by slumping (`SandBed.Level`),
  and a chunk's mesh is built once and only rebuilt when it changes. So D mostly saves GPU
  triangles on very large levels. Worth it only after A or B makes chunks expensive to draw.

### E. Cheaper visual tricks

A track decal or a normal-map stamp along the wheel's path; darker, smoother colour in pressed
columns (`Chunk.packed` already records how much each column was pressed). Cheap and good-looking,
but they add no shape.

## Recommended next steps, in order

1. **Option A at 2x**, Catmull-Rom through the column heights. Add a check to
   `tools/tests/ChunkMesherTest.cs` that the fine vertices at column centres equal the column
   heights and that seams still match. Look at it in game (ruts in sand, mud, snow).
   Note what is left to smooth is the *facet*, not the shape: the data-level work above has
   already removed the steps in the rut's profile, so what option A buys now is rounded corners
   and a smooth normal at a rut's edge rather than a continuous profile.
2. **Colour by pressing** (from E): shade pressed columns from `Chunk.packed`, so tracks read at
   a distance. Small change in `ChunkMesher.Build`.
3. Only if A is not enough: **option B** for chunks touched by `Press`/`Plough`, coarse average
   kept for colliders, saves and multiplayer. Start with pressing only; add slumping on the
   detail grid second.
4. Optionally **coarser colliders** away from machines (part of C), measured against
   `PERFORMANCE.md`'s collider cooking numbers.
5. Leave **D** until drawing cost is measured to matter.

## A cost to know about before touching `Press`

`Press` is now two to three times as expensive per contact as it was, because a load presses a
footprint of up to 25 columns instead of nine and every pressed column looks for a shoulder. A
car's four contacts cost 0.029 ms a fixed update (was 0.020) and sixteen cost 0.056 (was 0.020);
a contrived machine with 300 contacts standing on the sand at once costs 1.4 ms, about 14% of a
core at 100 Hz (was 0.4 ms). If that matters, the lever is to move the pressure out of the
`Dictionary<long, float>` and into a per-chunk array, which is where it belongs anyway.

## Things that will bite

- **Besiege's compiler is old Mono `mcs` (C# 4-ish)**: no `enum` declarations (they crash the
  compiler: use `const int`), no newer syntax. See the notes repository's
  `18-vendoring-unity-code.md`.
- **The mod loader blacklists namespaces**: no `System.IO`, `System.Reflection` (so not even
  `GetType().Name`), `System.Diagnostics` beyond `Stopwatch` as already used, and others.
  `tools/build.sh` checks this; a build that passes the compiler can still be refused by the game.
- **Unity 5.4**: no `Mesh.indexFormat` (65 535 vertices per mesh at most), no
  `Physics.ComputePenetration`, no `Collider.ClosestPoint`.
- **Threads**: the mesher runs on worker threads (`Workers.For`). Nothing in it may touch a Unity
  object; read the bed only through `ReadBlock`/`SurfaceOf`-style copies. `SandBed.FindChunk`
  caches per thread; `Workers.Running` must be above zero while helpers run.
- **Volume is sacred**: every test file checks that moving sand neither makes nor loses any.
- **Saves**: the material table's row layout (`SandMaterials.FirstVariant`) is what saved sand
  files index into. Do not reorder or remove rows.
