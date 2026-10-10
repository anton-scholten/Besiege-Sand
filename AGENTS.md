# Working on this repository

Notes for anyone, human or AI agent, changing this mod. The [README](README.md) is for people who
just want to use it.

## What it is

A [Besiege](https://store.steampowered.com/app/346010/Besiege/) mod: sand, wet sand, dust, snow,
gravel, mud and glue painted onto any level with a brush on an on-screen panel. The sand is a
height field (`SandBed`): machines stand on its colliders, press ruts into it, plough it aside and
blow craters in it; a spray of sand and wheel dust that are only drawn go with it. There are no
simulated grains and no blocks. [docs/DESIGN.md](docs/DESIGN.md) explains how it works and why.

## Layout

```
Sand/                       the folder Besiege loads (and the Workshop uploads)
Sand/Mod.xml                manifest: assembly, resources, the mod's ID (never change <ID>)
Sand/SandAssembly.dll       built by tools/build.sh; committed, because Mod.xml loads it
Sand/Resources/             Icon.png (panel's folded icon), Thumbnail.png (Workshop picture)
Sand/Scripts/*.cs           the source
  SandField.cs              the level-wide field: brush, runs, explosions, water/fire, stepping
  SandBed.cs                the height field: Press, Plough, Blast, Relax/Slump, undo journal
  SandBedRenderer.cs, ChunkMesher.cs, SandBedColliders.cs   drawing and colliders
  ShapeFeed.cs, BedShapes.cs, ShapeMath.cs                  machines' colliders as shapes
  SandSpray.cs, SandDust.cs, NativePuffs.cs                 what is only drawn
  SandHud.cs, UIF.cs, UIFGate.cs, Chooser.cs, ValueField.cs the panel (UI Factory or plain)
  SandNet.cs                multiplayer: host simulates, clients draw
  SandFiles.cs, BedCodec.cs sand saved through the game's own save/load screens, and packed
  SandMaterials.cs          the media and their numbers
tools/build.sh              compile with Besiege's own compiler, then check the result
tools/verify-build.sh       compile and check without writing the assembly
tools/install.sh            build and symlink Sand/ into Besiege_Data/Mods
tools/test-bed.sh           the headless tests (no game, no display)
tools/tests/                the tests, and BlacklistCheck.cs (the mod loader's rules)
tools/make-icon.sh, make-thumbnail.sh   the art
docs/                       DESIGN, PERFORMANCE, FINDINGS, TODO, TERRAIN-DETAIL
```

## Build, install, test

```sh
./tools/verify-build.sh     # after editing any .cs
./tools/build.sh            # compile + blacklist + XML checks, writes Sand/SandAssembly.dll
./tools/install.sh          # build, then link into the game
./tools/test-bed.sh         # all headless tests; every suite must print ALL PASSED
```

No .NET toolchain is needed: the build loads Besiege's own `libmono.so` and calls the compiler in
its `mcs.dll`. Set `BESIEGE_DIR` if the game is not found (here it is
`/media/game_drive/SteamLibrary/steamapps/common/Besiege`), and `UIFACTORY_DIR` for UI Factory
(Workshop item 2913469777), which is needed to compile though optional at run time.

## Rules that are not obvious

- **Old compiler**: C# about 4. No `enum` declarations (they crash it; use `const int`), no
  string interpolation, no `?.`, no expression-bodied members.
- **Mod loader blacklist**: `System.IO`, `System.Reflection` (including `GetType().Name`),
  `System.Diagnostics` (except `Stopwatch`), `System.Net`, and more are refused at load, after a
  clean compile. `tools/build.sh` runs `BlacklistCheck`; trust it.
- **UI Factory** types may only be touched after `UIFGate.Loaded` is true: Mono caches a failed
  assembly load for good.
- **Volume is conserved** by every operation on the bed; the tests check it.
- **Saved sand** indexes the material table: never reorder or remove rows
  (`SandMaterials.FirstVariant`).
- **Threads** are fork-join from the main thread (`Workers.For`); worker code must not touch Unity
  objects.
- **Unity 5.4**: 65 535 vertices per mesh at most; no `Physics.ComputePenetration`.

The general, mod-independent rules of Besiege's modding API are in the notes repository,
[Besiege-Modding-AI-notes](https://github.com/anton-scholten/Besiege-Modding-AI-notes): read its
`notes/INDEX.md` before guessing at how the game behaves.

## Workflow the owner expects

Finish each change with the tests passing, then commit, push and run `./tools/install.sh`.
Commit `Sand/SandAssembly.dll` with the source it was built from.

## Open work

- [docs/TODO.md](docs/TODO.md): the defects still in the code. It was pruned on 10 October 2026 —
  everything it said about the grain solver, the block-value panel and the grain meshes was removed
  with those files, and what is left has been confirmed to still be there.
- [docs/TERRAIN-DETAIL.md](docs/TERRAIN-DETAIL.md): making tracks and ruts finer (options,
  costs, and the recommended order).
- [docs/PERFORMANCE.md](docs/PERFORMANCE.md): what the frame time goes on, most likely first.
