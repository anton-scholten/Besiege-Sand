# Besiege Sandbox

Groundwork for granular material — sand, gravel, spoil — in Besiege, as a mod.

Right now this repository holds **a capability probe, not a sand mod**. The probe
answers, by measurement rather than argument, whether the game's ten-year-old
engine can carry a particle simulation big enough and fast enough to be worth
playing with, and it ships the toolchain needed to build the GPU half.

## What is here

| | |
| --- | --- |
| `Sand/` | the probe mod itself: no blocks, no gameplay, writes a report to `Player.log` |
| `Sand/Scripts/` | its C# sources, compiled by Besiege's own compiler |
| `shaders/` | a Unity 5.4 project holding the mod's `.compute` sources and the script that bakes them into asset bundles |
| `tools/` | build, install, verify, and the two scripts that fetch and drive a Unity editor |
| `docs/FINDINGS.md` | everything the probe has measured, and what it means for a sand mod |
| `vendor/` | fetched third-party pieces — a Unity editor and a Wine prefix. Not committed; `tools/fetch-unity.sh` puts them back |

Nothing here depends on anything outside the repository except Besiege itself and
`wine`.

## Building and running the probe

```sh
./tools/build.sh                 # compile with Besiege's own mcs, then check the
                                 # result against the mod loader's blacklist
./tools/install.sh               # build and link it into Besiege's Mods folder
./tools/install.sh --copy        # ... or copy it, if a link is awkward
./tools/install.sh --uninstall   # take it out again
```

No C# toolchain is needed: the build loads Besiege's own `libmono.so` and calls the
compiler inside its `mcs.dll`, which is the exact code path the game uses. A
compile that succeeds here is a compile that succeeds in game, and the blacklist
check afterwards means the loader will not silently refuse the assembly.

Start the game with the mod installed and the report appears in `Player.log`:

```sh
grep -a Sand ~/.config/unity3d/Spiderling\ Games/Besiege/Player.log
```

A second, scene-specific report is taken automatically on every level load, and F9
forces one at any time. To take one without touching the menus:

```sh
./Besiege.x86_64 -sandprobe-level 30,45
```

which loads those scenes in turn and reports on each. That path into a level is not
the game's own, so treat anything strange in such a run as an artefact of it.

## Building the shader bundles

Besiege has no runtime shader compiler, so a compute shader has to be baked into an
asset bundle by a **Unity 5.4.0f3** editor — the exact version the game was built
with, because a bundle records its version and a newer one loads as nothing at all.

Unity's experimental Linux editors from that era are still served, including one
for exactly 5.4.0f3, so on Linux no Wine is needed:

```sh
curl -fLO https://download.unity3d.com/download_unity/linux/unity-editor-5.4.0f3+20160727_amd64.deb
sudo apt install ./unity-editor-5.4.0f3+20160727_amd64.deb     # installs to /opt/Unity
```

They live under a flat `linux/` path with a build date in the filename, not under
the modern `download_unity/<hash>/LinuxEditorInstaller/` layout — that one answers
404 for 5.4.0f3 and is what makes people conclude no Linux editor exists.

```sh
./tools/build-bundles.sh     # uses /opt/Unity if present, else the Wine prefix
```

`tools/fetch-unity.sh` installs the **Windows** editor into a Wine prefix inside
`vendor/` instead, for machines where the native editor will not run.

### Building the bundles on another machine

A Unity licence is bound to the machine as the editor sees it, and a `.ulf`
activated on one machine is refused on another — the editor says so and then
deletes the licence file:

```
LICENSE SYSTEM 12345-oem-0000001-54321 != 00330-50000-00000-AAOEM
LICENSE SYSTEM U2VyaWFsIG51bWJlcg== != MA==
BatchMode: Unity has not been activated with a valid License.
```

Copying a licence between machines, or between two Wine prefixes, therefore does
not work. If the working editor lives somewhere else, move the *build* there
rather than the licence:

1. copy `shaders/` to that machine;
2. make sure that editor has **Linux** build target support installed — Besiege on
   Linux is an OpenGL build and the Windows bundle will not serve it
   (`UnitySetup-Linux-Support-for-Editor-5.4.0f3.exe`, kept in `vendor/unity/`);
3. run the same batch command `tools/build-bundles.sh` runs:

   ```
   Unity.exe -batchmode -quit -nographics \
             -projectPath <...>\shaders \
             -executeMethod SandShaderBuild.BuildAll \
             -logFile <...>\unity-build.log
   ```

4. copy `Sand/Resources/SandCompute/` back into this repository.

The bundles are ordinary files; nothing about them is machine-specific.

The activation step is not optional and not something free-ness avoids. Unity 5.4
will not start at all, batch mode included, until a `Unity_lic.ulf` exists, and
every route to one goes through a Unity account. The editor's own activation
window is a Chromium view that renders black under Wine, so
`tools/activate-unity.sh` activates from the command line instead. It prompts, and
stores nothing.

The bundles land in `Sand/Resources/SandCompute/{linux,win,mac}` and are named
in `Mod.xml`. The probe picks the one matching the player's graphics API and falls
back through the others, because a shader that did not compile for an API loads
perfectly well and then does nothing.

## Licence

See `LICENSE`.
