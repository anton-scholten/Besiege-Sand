# Where the frame rate goes

The reported case: a scene with sand runs at 35 fps with the sand simulating, 90 with *Pause sand
simulations* on, and 135 with no sand. So simulating costs about 17 ms a frame on average (1/35 - 1/90 s),
and sand that is only drawn and held as ground about 4 ms (1/90 - 1/135 s).

This file lists every place that time can come from, with what was measured, and what would cut it. Two
tools measure it:

- **In game, F9.** The report's `time per second` line gives each part's milliseconds per second of
  real time over the last two seconds, most first, with the frame rate. Pressing F9 while the slow case
  is on screen says which of the parts below it is.
- **Headless, `./tools/test-bed.sh --bench`** (`tools/tests/PerfBench.cs`). The simulation on its own at
  the game's cadence (100 fixed updates a second, a sand step every fourth), with a four-wheeled machine
  on a 30 m square of sand 30 cm deep. It cannot see the engine's share: drawing, remeshing, cooking
  colliders, finding objects in the scene.

## Measured in game (F9, 8 October)

A very big scene: 1,733,361 bed columns in 7,740 chunks, 30,930 m^3 of sand, a 117-block machine of
63 to 71 bodies driving on it, 330 of its shapes within reach of the bed, 422 to 539 chunk colliders
live around it, 1,000 to 1,800 grains in the air. **3 to 4 fps.** Two reports, ms per second of real time:

| part | first | second |
| --- | --- | --- |
| absorbing and digging | 131 | 123 |
| solver | 130 | 46 |
| cooking colliders | 77 | 103 |
| slumping | 53 | 68 |
| pressing | 21 | 27 |
| collider gather | 16 | 16 |
| drawing and remeshing the bed | 15 | 19 |
| drawing grains and spray | 8 | 10 |
| **the sand's own, in all** | **450** | **412** |

The governor says a sand step cost 30.9 ms against its 10 ms budget (24.7 us a grain), and the slowest
single fixed update was 49 ms, 39 of it cooking colliders.

What it says:

- **The sand's own work is under half of each second.** At 3 to 4 fps the frame is 250 to 330 ms, so
  more than half of the time is the engine's: PhysX with five hundred `MeshCollider`s under a 70-body
  machine, every one of those bodies switched to continuous collision detection by the mod
  (`SandField.Continuous`) -- which PhysX pays for against every mesh -- and drawing 7,740 chunks of bed,
  some two million vertices, again for each shadow cascade. None of that is in the table.
- **Digging scales with the machine's shapes**: 330 shapes, each tested against the columns in its box
  every step (item 9 below), is the biggest single part.
- **Colliders**: half a thousand chunk colliders live at once (`SandBedColliders.Reach`, 1.5 m round every
  shape, kept 50 steps), each a 16 x 16 mesh cooked again when the sand under it moves (item 5).
- **The slumping that never finishes** (item 3) is confirmed: 53 to 68 ms/s.
- **The solver** is 24.7 us a grain where the governor plans on far less, so it overruns its budget three
  times over (item 2).

## Measured, headless (ms per second of game time)

| part | parked | driving, wheels spinning |
| --- | --- | --- |
| solver (1,740 grains in the air at most) | 0 | 116 |
| slumping, every sand step | 0 | 30 |
| slumping, every fixed update | 0.7 | 22 |
| digging (release) | 3.6 | 20 |
| absorbing | 0.1 | 7 |
| pressing | 1.8 | 6 |
| **total** | **6** | **200** |

Parked, the simulation costs next to nothing, so the 17 ms comes from a machine driving: grains in the
air, the bed changing under the wheels, and the engine work that follows from that.

## The list, most likely first

1. **Cosmetic spray, drawn on the CPU every frame.** Every drawn spray grain is a soft quad rebuilt
   in managed code and uploaded every frame (`SandRenderer` -> `SoftDust`), and stepped every frame
   (`SandSpray.Step`). Its cap is the sand limit, 40,000 by default: three spray grains per thrown grain
   and two per sprayed one, each living 0.6 to 1.4 s, so a machine digging keeps it near the cap, which is
   160,000 vertices built and sent each frame. *Fix:* a cap of its own, a few thousand; or hand it to a
   Unity `ParticleSystem` (`Emit` with `EmitParams`), which simulates and draws natively, and nothing
   per particle is managed code. F9 shows the count (`drawn spray grains`).
2. **The solver, while grains fly.** 116 ms/s for under 2,000 grains, 4.7 ms a step. *Fixes:* a ballistic
   fast path for grains with no neighbour and no shape near (most of a throw, most of the time), which
   skips the contact passes; fewer substeps or iterations for airborne grains; throwing fewer, coarser
   grains (`SandBed.KickShare`, `KickLevel`).
3. **Slumping that never finishes.** 52 ms/s between the two calls while 4 to 60 columns are waiting at
   any moment. `SandBed.Relax(budget, ms)` keeps going while anything is queued, and next to a wheel,
   where columns are pressed and dug every step, slumping one column re-queues its neighbour and the
   neighbour re-queues it: the loop always spends its whole budget, 2 ms every fixed update and 3 ms
   every sand step. *Fix:* stop when a pass moves less than a threshold of sand, or queue a column again
   only when what it lost or gained is more than that; and slump once per sand step, not also every
   fixed update.
4. **Remeshing the bed.** Up to 3 ms a frame (`SandBedRenderer.RebuildMs`) for as long as chunks are
   changing, which under driving wheels is every frame: up to a quarter of a second each second at 90
   fps. *Fixes:* rebuild a changed chunk at most once per sand step (25 a second, not every frame); patch
   only the rows that changed; skip the far, small ones until they are near.
5. **Cooking colliders.** Up to 3 ms a sand step (`SandField.ColliderBudgetMs`), 75 ms/s, while the sand
   under the wheels deforms, since a changed chunk's `MeshCollider` is cooked again. *Fixes:* raise the
   tolerance before a recook (`CookTolerance`); cook small patches under each wheel instead of whole
   16 x 16 chunks.
6. **The explosion scan.** While a run is going and there is sand: the machine's blocks looked through
   20 times a second, and six `Object.FindObjectsOfType` walks of the whole scene five times a second
   (`SandField.ExplosionScan`), each allocating an array. *Fixes:* look for explosion effects only, less
   often; or keep a list fed by the blocks and level objects as they spawn.
7. **Drawing the grains.** A lit disc of nine vertices per grain, rebuilt every frame in managed code,
   and the mesh is uploaded at its peak size every frame (`SandRenderer.Reserve` grows and never
   shrinks), so after one big throw a handful of grains costs as much as the throw did. *Fixes:* shrink
   after a few seconds below the peak; six vertices a grain; draw grains that have not moved without
   rebuilding them.
8. **Drawing the bed with its shadow.** The bed casts and takes shadows (`SandBedRenderer`, the
   `DrawMesh` calls), so every tile is drawn once per shadow cascade as well. That and the colliders
   following the machines (`FrozenStep`) are most of the 4 ms paused sand costs. *Fixes:* cast no
   shadow from the bed (it still takes them); a coarser mesh for far tiles.
9. **Digging, absorbing and pressing.** 33 ms/s together while driving. *Fixes:* test only columns under
   the wheels' footprints instead of the shapes' boxes; skip shapes that have not moved since the last
   step.
10. **The panel, every frame.** Fixed now: looking for the game's file screen walked every loaded object
    (`Resources.FindObjectsOfTypeAll`) twice a frame wherever the screen was not found; it is looked for
    at most twice a second. Still there: `Camera.allCameras` allocates a new array each time it is read,
    up to three times a frame while the pointer is on the panel or painting.
11. **Scene scans.** Every collider in the scene every 2,400 scans, and the rigidbodies every 25 sand
    steps (`ShapeFeed.Rescan`, `RescanBodies`). Cheap on average; a hitch on a big level.

Each fix should be measured with `--bench` where it is the simulation's, and with F9 in game where it is
the engine's.
