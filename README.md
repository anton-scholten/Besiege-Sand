# Besiege Sand

Granular media for Besiege, as a mod: sand, wet sand, dust, snow, gravel, mud and glue,
simulated level-wide, that machines can drive on, dig, plough, scoop and be buried by, that
explosions throw about, and that can be sprayed, painted and placed in the level editor.

It runs on the CPU, on every core there is. (A GPU solver was begun and dropped: Besiege has
no runtime shader compiler, and a compute shader needs a bundle from a licensed Unity
5.4.0f3 editor. See the last section of [docs/DESIGN.md](docs/DESIGN.md).)

## How it works, in a paragraph

Sand that is moving, or that something is touching, is a grain, simulated with
position-based dynamics. Sand at rest is a height field of layered columns (up to six media,
one on another), drawn as meshes and given colliders where a machine is, and turned back
into grains wherever something digs, ploughs or lands on it. A brush that is held in one
place grows its pile in place and lets it down to its angle of repose as it grows.
`docs/DESIGN.md` has all of it, with the measurements behind each decision.

## What is here

| | |
| --- | --- |
| `Sand/` | the mod: `Mod.xml`, the block and entity definitions, the art, the assembly |
| `Sand/Scripts/` | its C# sources, compiled by Besiege's own compiler |
| `tools/` | build, install, verify and test scripts, and the small generators for the art |
| `tools/tests/` | headless tests of the solver, the bed, the brush and the mesher |
| `docs/DESIGN.md` | what the mod does and why, and what was measured |
| `docs/FINDINGS.md` | what the original capability probe measured about Besiege's engine |
| `docs/TODO.md` | defects found by review that are not fixed yet |

Nothing here depends on anything outside the repository except Besiege itself.

## Building, installing, testing

```sh
./tools/build.sh                 # compile with Besiege's own mcs, then check the
                                 # result against the mod loader's blacklist
./tools/install.sh               # build and link it into Besiege's Mods folder
./tools/install.sh --copy        # ... or copy it, if a link is awkward
./tools/install.sh --uninstall   # take it out again
./tools/verify-build.sh          # compile and check without writing the assembly
./tools/test-bed.sh              # the headless tests, no game needed
./tools/test-bed.sh --load       # ... and a 40 second closed-loop pour
```

The build needs **UI Factory 3** (Workshop item 2913469777) installed, because the sand
panel and the sprayer's settings panel are built from its prefabs. The mod does not need it
to *run*: without it the panel is a plain one and the sprayer uses Besiege's own block
mapper. Set `UIFACTORY_DIR` if it is not found, and `BESIEGE_DIR` if Besiege is not.

No C# toolchain is needed: the build loads Besiege's own `libmono.so` and calls the compiler
inside its `mcs.dll`, which is the exact code path the game uses. A compile that succeeds
here is a compile that succeeds in game, and the blacklist check afterwards means the loader
will not silently refuse the assembly. The tests run on the same Mono, with no game and no
display.

## Using it in the game

- **Media Sprayer** block: a pipe that sprays a medium out of its open end. Pick a medium and it
  presets rate, speed and spread; every number stays editable, and switching on *Advanced* shows
  the medium's own numbers for that sprayer. Also in the level editor, standing upright.
- **Regions** in the level editor: a box, a cylinder or a ball of a medium, placed already
  settled.
- **The sand panel**: a brush (circle, flat circle, square, random, ring) for painting and
  removing sand, with its size, rate, medium and colour; switches to pause sprayers and the sand,
  to show or hide the *cosmetic particles* (a spray of fine grains that is only drawn, with what
  wheels, blasts and sprayers throw), and a **Sand limit** on how many grains are simulated at
  once; and **Save** and **Load**, which keep the
  sand on its own as a file in Besiege's own save and load screens, in `SavedMachines/Sand_mod`.
  Loading sand leaves the machine as it is. The panel remembers its settings between runs of the game.
- **F9** writes a report to `Player.log` (the mod writes nothing else there but errors): what the
  sand is doing -- grains, the bed, what a digging wheel did last, the last block pressing on the
  sand and its friction, explosions seen, anything that failed:

```sh
grep -a Sand ~/.config/unity3d/Spiderling\ Games/Besiege/Player.log
```

## Licence

See `LICENSE`.

The sprayer's pipe is "Pipe" by [Quaternius](https://quaternius.com), from
[poly.pizza](https://poly.pizza/m/CpCnSuo786), public domain (CC0), recoloured to the mod's
cardboard.
