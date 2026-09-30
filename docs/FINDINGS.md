# What the probe measured, and what it means for sand

Everything here was read out of Besiege itself — either from its shipped assemblies
with the notes repository's `peek.sh`, or from `Player.log` while the game ran with
`Sand` installed. Nothing in this file is reasoning from Unity in general.
Where something is inferred rather than measured it says so.

Machine the numbers came from: Debian, Linux, NVIDIA TITAN Xp, driver 580.178.04,
6 cores, 32 GB. Besiege 5.4.0f3.

## The engine

Unity **5.4.0f3**, OpenGL 4.5 on Linux, shader level 50.

```
supportsComputeShaders = True          supportsInstancing = True
RFloat / ARGBFloat / ARGBHalf render targets = True
ComputeBuffer(4096,16) SetData/GetData round-trip: OK
RenderTexture ARGBFloat enableRandomWrite: created = True
```

The player binary carries `glDispatchCompute`, `GL_ARB_compute_shader` and
`GL_ARB_shader_storage_buffer_object`, so the GL compute path is compiled into the
build rather than merely declared by the API.

Four things 5.4 does **not** have, each of which kills a standard recipe:

- no `Graphics.DrawMeshInstanced` (5.6) — the "batch 1023 transforms" pattern and
  every sample built on it are unavailable;
- no `MaterialPropertyBlock.SetBuffer`, though `Material.SetBuffer` exists;
- no `Physics.ComputePenetration` (5.6), no `Collider.ClosestPoint` (2017) — a
  particle cannot ask PhysX how deep it is inside a collider;
- 16-bit mesh indices only: 65535 vertices, so 16383 billboard quads per mesh.

## Physics

```
fixedDeltaTime = 0.01  (100 Hz)        maximumDeltaTime = 0.333333
gravity = (0, -32.8, 0)
defaultSolverIterations = 20           defaultSolverVelocityIterations = 1
sleepThreshold = 0.005
```

**The 100 Hz fixed step is the single most consequential number here.** Besiege
runs physics twice as often as Unity's default, so a mod stepping a simulation from
`FixedUpdate` has half the wall-clock per step that the usual arithmetic assumes.
Sand must therefore have its own rate — every second or fourth fixed step, with its
own substeps — and interpolate visually between. One readback per sand step instead
of a hundred per second is the difference between the coupling being free and the
coupling being the whole cost.

## What sand would have to collide with

Collider census, taken automatically on level load:

| Scene | colliders | box | sphere | capsule | mesh convex | mesh concave | terrain | rigidbodies |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| level 30 | 212 | 131 | 41 | 1 | 2 | 37 | 0 | 74 |
| level 45 | 427 | 364 | 34 | 16 | 1 | 12 | 0 | 203 |
| title screen | 59 | 22 | 27 | 1 | 5 | 4 | 0 | 20 |

And the same census over every block the game can place, read from
`PrefabMaster.BlockPrefabs` so it works at the title screen with nothing built:

```
block prefabs: 104 (2 with no collider at all)
  box 342, sphere 138, capsule 67, meshConvex 5, meshConcave 1, wheel 0
  most colliders on one block: 32 on 'FuelBarrelBig'
```

Three things fall out:

- **Besiege's blocks are analytic shapes almost without exception.** Five convex
  meshes and one concave mesh across all 104 prefabs; everything else is a box, a
  sphere or a capsule, each a few instructions of signed-distance function in a
  compute kernel. A GPU sand solver can be handed the whole machine as primitives
  and never touch a triangle. This is the finding that makes the GPU route
  straightforward rather than merely possible.
- A worst case of 32 colliders on one block means a shape list sized by collider,
  not by block: a few hundred blocks can be a few thousand shapes. Still small,
  but not one-to-one.

- **There is no Unity `Terrain` in a Besiege level.** `Terrain.activeTerrains` is
  empty and no `TerrainCollider` exists in any scene measured. The ground is mesh
  geometry. So there is no `TerrainData` heightmap to sample, and "how high is the
  ground here" has to come from somewhere else — rendering the static world
  top-down into a height texture once per level is the cheap answer, and Besiege's
  own water does exactly that (`TerrainHeightRenderer`, `WaveDepthRenderer`).
- **The movable world is overwhelmingly boxes, spheres and capsules.** Those three
  are analytic signed-distance functions costing a few instructions each, which is
  what a GPU solver wants. The concave meshes are static scenery and belong in the
  baked height/depth texture, not in the per-step shape list.

## How fast the CPU is

```
mono throughput: 200000 particles x 50 integrate passes in 98 ms
                 = 1.96 ms/pass = 9.8 ns/particle, one thread
```

Roughly one operation per nanosecond per core — old Mono is slower than a modern
runtime, but not by the order of magnitude people assume. Integration is free; the
contact solve is the entire cost, which is the same shape as on any runtime.

`System.Threading` is not on the mod loader's blacklist and `System.Threading.dll`
ships in `Managed`, so worker threads are available. There is no Job System, no
Burst, no `Unity.Mathematics` and no SIMD to rely on. Nothing on a worker may touch
a Unity object.

## The coupling, and why it is the interesting part

Besiege's own water is CodeAnimo *Surface Waves*, and `BuoyancyManager` is a
worked example of exactly the problem sand has: a simulation living on the GPU that
has to push `Rigidbody`s PhysX owns.

- a `Buoy` carries a radius and a trigger `SphereCollider`, and registers itself
  with the manager;
- the manager's `FixedUpdate` gathers positions and velocities into `Vector4[]`,
  dispatches a `ComputeKernel1D`, and reads results back;
- `computeForces` then `applyBuoyForces` applies one force per body.

The rule to copy: **sample points cross the boundary, not geometry.** Upload a
small array of shapes, read back a small array of forces, apply them with
`AddForceAtPosition`. Size whatever crosses back by the number of *bodies*, never
the number of particles — a few hundred blocks is a few kilobytes.

Besiege also ships `CodeAnimo.GPGPU.SM3Kernel`, the same simulation step written as
a fragment shader rendering into a `RenderTexture`, used when compute is
unsupported. That is the portable floor under any GPU route here, and it needs no
`ComputeBuffer` at all.

## Where it stands

Working, measured, no obstacles:

- compute shaders, compute buffers and random-write render targets on this build;
- a mod-side simulation stepped at its own rate, coupled through sample points;
- a collider mix that suits analytic SDFs.

The one unproven link: **a compute shader the mod authored itself.** Besiege has no
runtime shader compiler, so a `.compute` has to be baked into an asset bundle by a
Unity 5.4.0f3 editor. The editor is obtainable — Unity's experimental Linux
editor for exactly 5.4.0f3 is still served as
`download_unity/linux/unity-editor-5.4.0f3+20160727_amd64.deb`, and the Windows
editor plus Linux build-target support are served under the modern hash layout —
but Unity 5.4 refuses to run in batch mode without an activated licence:

```
BatchMode: Unity has not been activated with a valid License.
```

`-createManualActivationFile`, the documented way to activate without the editor's
own UI, does not help: in 5.4 it still tries the network first and fails the same
way. The editor's activation window is a Chromium view and renders black under
Wine — captured, and its log shows the login web packages (`unity-editor-home`,
`unityeditor-cloud-hub`) missing from the install — so signing in inside the editor
is not a route here either. What is left is command-line activation with a Unity
account, which is what `tools/activate-unity.sh` does.

Until that is settled, `Sand`'s dispatch test reports the bundle as absent:

```
bundle 'SandComputeLinux' not loadable: Could not find resource named SandComputeLinux!
mod compute bundle: not present -- build it with tools/build-bundles.sh
```

Everything around it is already in place and will answer the moment a bundle
exists.
