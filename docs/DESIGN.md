# Granular media in Besiege: the design

What is being built, and why each decision is the one the measurements in
[FINDINGS.md](FINDINGS.md) point at. Read that first; this assumes its numbers.

## The shape of it

One level-wide simulation, not a per-block one. A block is an *emitter*, a *sink*
or a *tool*; the medium itself belongs to the level, the way `Physics.gravity` does.
That follows the notes repository's rule for level-wide mods: publish to one shared
registry, reconcile rather than react, and put every global back on the way out.

```
  SandField            one per level. owns the grain state, steps it, restores globals
    +-- CpuSolver      position-based dynamics on every core; per-body force and torque out
    +-- SandBed        sand at rest as a height field of layered columns (chunks of 16 x 16)
    |     +-- SandBedRenderer, ChunkMesher   its meshes, built on every core
    |     +-- SandBedColliders               the colliders machines drive on
    +-- ShapeFeed      colliders -> analytic shapes, handed to the solver each sand step
    +-- GroundField    the level's ground as a height field, raycast once per tile
    +-- WaterLink      the wave simulation's height texture, when a level has one
    +-- SandRenderer   loose grains, as dynamic meshes
```

There is one solver, on the CPU. A GPU solver was begun and dropped: Besiege has no runtime
shader compiler, so a compute shader needs a bundle from a licensed Unity 5.4.0f3 editor
(see the last section).

## Stepping: not every FixedUpdate

Besiege runs physics at **100 Hz** (measured). Stepping a particle simulation from
every `FixedUpdate` would spend the whole frame budget on it.

So the medium has its own rate — `SandField.StepsPerSecond`, default 25, i.e. one sand step per four
fixed steps — with its own substeps inside. Visual positions interpolate between
sand steps. One shape upload and one force readback per sand step.

**Coupling to rigidbodies.** The solver reports, per body, the contact force summed over
the step's substeps. That sum lasted one substep each, so it is applied as an impulse of
`force x (dt / substeps)` -- not `AddForce`, which would be an impulse of the force times the
fixed step, once per sand step, and hand a block about 45% of the sand's weight. The speed
a body can gain from sand in one step is capped (`MaxDeltaVPerStep`, 6 m/s), and so is the spin
(`MaxDeltaSpinPerStep`, 5 rad/s), so a bad frame shoves rather than launches. The impulse is not
given at once: it is held and given in equal shares, one per fixed update, until the next sand
step (`SandField.GivePending`). All at once it was four times the force the sand exerted over its
40 ms, landing in a single 10 ms physics step -- and a joint breaks on force, not on impulse.
Sand also never gives a body its speed back: along its push it may add only the speed the body
has against it, plus what carries the body for a step (`SandCoupling.Allowed`), so a core block
dropped on a pile stops on it and lies flat rather than being thrown off by the grains it pressed.

What a contact hands back is everything it moved the grain by: the push out *and* the
friction along the surface, so a plough feels the sand dragging on it and a wheel has
something to drive against. The surface's velocity at the contact is the body's velocity
there plus its spin -- a wheel's centre can stand still while its rim tears at the sand --
and torque is taken about the body's centre of mass, where the force is applied. Shapes
are snapshotted when a sand step begins and moved along their velocity through its
substeps, so a block crossing a pile meets the sand where it is, not a step behind.
Sprayed sand pushes from the nozzle; only grains that may start overlapping something wait
`SettleSeconds`. `tools/test-bed.sh` checks that sand resting on a movable
box loads it with its own weight, that a spinning wheel in sand throws it back and is
pushed forward, and that a box dragged through sand is held back.

## The materials

Seven, and they are one parameter block each rather than seven code paths. A material
is a `const int` (Besiege's compiler segfaults on any `enum` declaration) indexing a
table.

| | grain radius | density | friction | cohesion | restitution | air drag | sticks | firmness | packs | digs at | notes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **Sand** | 0.03 | 1600 | 0.50 | 0 | 0.02 | low | 0 | 50 kPa/m | 35% | 0.8 m/s | loose: repose ~30 deg, ruts readily, digs under a spinning wheel |
| **Wet sand** | 0.03 | 2000 | 0.85 | high | 0.01 | low | 40 | 200 kPa/m | 15% | 1.2 m/s | holds a face, firm to drive on, a little sticky |
| **Dust** | 0.05 | 60 | 0.12 | very low | 0.01 | **high** (1.5) | 0 | 5 kPa/m | 60% | 0.13 m/s | barely there: drifts, slides flat, a wheel sinks in it |
| **Snow** | 0.035 | 300 | 0.70 | medium | 0 | medium | **200** | 30 kPa/m | 60% | 0.6 m/s | packs down a long way and clings to what turns in it |
| **Gravel** | 0.08 | 1800 | 0.90 | 0 | 0.12 | low | 0 | 400 kPa/m | 10% | 1.5 m/s | steep, hard to rut, hard to dig |
| **Mud** | 0.03 | 1700 | 0.80 | medium | 0 | low | **250** | 25 kPa/m | 50% | 0.7 m/s | heavy and soft; leaves deep tracks, and wheels come out caked |
| **Glue** | 0.045 | 1300 | 0.90 | low | 0 | low | **60000** | 120 kPa/m | 20% | 1.0 m/s | lime green with a rare cyan or purple grain; firm to drive on, sticks to anything |

The last four columns are how a medium meets a machine. *Sticks* is how hard grains cling
to a block's surface, as an acceleration: grains within a little of a surface are pulled onto
it and gripped against slipping, up to that hard -- enough for snow to go round a wheel spinning
at ten radians a second, where dry sand, with none, falls off. *Firmness* is the pressure it
takes to press the settled medium down a metre, and the depth goes with the square root of the
pressure: a quarter of it presses half a metre (Bekker's p = k z^n, n = 2). *Packs* is how much of a
column's depth packs tighter as it is pressed -- half of what it sinks by goes into packing and half
out to a lip round the rut -- and past it all of the sand it sinks by is pushed aside. *Digs at* is how fast a surface touching the settled medium must move through it before it
is dug up as grains rather than ridden on. Stickiness is one of an emitter's five advanced
settings, and firmness and dig speed follow its *Softness*; packing follows the medium. Sand used to be what
wet sand now is -- firm, 34 degrees, hard to dig -- and dust was three times heavier and three
times grippier than it is.

**Softer and lighter than real sand, on purpose.** The table's numbers are real sand's, and against
them Besiege's machines -- light for their size, held together by joints that give -- barely marked a
pile and could not dig into it. Every medium's firmness is scaled by 0.4, its dig speed by 0.085 and
its grains' mass by 0.002 (`SandMaterials.Softness`, `DigEase`, `Lightness`); buoyancy stays the real
density ratio. After scaling, sand takes 20 kPa to press a metre: 20 Pa (a small block on a corner)
presses it 3 cm, 200 Pa 10 cm, 1 kPa 22 cm and 3 kPa (a loaded wheel) 39 cm; gravel 1, 4, 8 and 14 cm;
mud 4, 14, 32 and 55; dust 10, 32, 71 and 122. A column sinks at up to 2 m/s (`SandBed.PressRate`), so a
wheel crossing it in a few hundredths of a second, or a block landing, presses it most of the way its
load says (at 6 the sand fell away from under a wheel faster than its collider was cooked again, and the
wheel stood over it out of touch). Until now the firmness was scaled by 0.0008, so that anything at all pressed a column to its
packing limit and no further, at 0.6 m/s: a rut was never deeper than a third of the sand, a wheel
driving across it left a few centimetres whatever it weighed, and a heavy block marked it no more than
a light one.

The mass is Besiege's scale. A small wooden block, a unit cube, weighs about 0.3; at half real density
a grain of sand weighed a third of one and a cubic unit of sand as much as some 2700 blocks. At 0.002 a
cubic unit of sand weighs about ten wooden blocks -- sand heavier than wood, as it is, if by more than the
real three times. Forces between grains and blocks scale with it (a wheel spinning in loose sand is
pushed by 1.7 N where it was pushed by 415; `SolverShapeTest` holds its force checks in proportion);
the bed's collider, which is what a machine stands and drives on, does not. A side contact digs at
0.5 m/s or 300 N. The table above shows the numbers before scaling.

**Ploughing.** To the physics, a pile's edge is a slope of the bed's collider, and a blade
meeting it was lifted up and over as by a ramp: nothing could scrape sand off from the side.
So a face of a shape moving sideways into the sand -- faster than the medium's dig speed and
0.3 m/s, mostly horizontally, above the shape's underside rather than under it, and on its
leading side -- pushes the sand above the shape's underside ahead of it, to the first column
it is clear of, before the blade gets there (`SandBed`, in `Release`). The berm that builds is
pushed along in turn and slumps aside, as a plough's does. A rolling wheel's front moves down
as fast as it moves forward, so wheels still climb piles; a locked, skidding wheel ploughs.

**Deep sand comes back coarse.** A column given back as grains is the whole of what lies above the
cut, and a machine at the foot of a pile ten metres high asked for thousands of grains in a few steps.
Sand more than 1.5, 3 and 6 m above the cut comes back as grains one, two and three levels coarser
(`SandBed.CoarseDepth1..3`, the same rows the governor gives a sprayer): 6789 grains became 284 in the
test, the volume the same to the last grain (`BrushShapeTest`).

**What a brush shows, and how soon.** The bed is drawn a chunk at a time. A chunk's mesh is worked out
by `ChunkMesher` -- about 40 us, from 77 -- on every core there is (`ChunkMesherPool`: the main thread
and `ProcessorCount - 1` helpers, a few chunks each, joined before the tiles are put together); it is
three to four times quicker on six cores and gives the same bytes as on one (`ChunkMesherTest`). Chunks
that have never been drawn go first, and their tiles, so ground a brush has just
reached shows at once and what is already drawn catches up; before, new squares came in one at a time
behind the old ones. The turn is 3 ms, 4 with a long queue; it was briefly twice that and a machine
driving over sand, which dirties chunks every frame, felt it.

**No grid on the sand.** (Three causes, found one after another. The seam in the light, below. The shader: `Standard`'s specular and fresnel show every 12 cm facet as a square, so sand is drawn with plain Lambert `Legacy Shaders/Diffuse`. And the colour texture had no mipmaps, so the column-by-column speckle shimmered into a lattice at a distance; it has them now.) The 17 by 17 vertices of a chunk sit on column centres and the last row is the
next chunk's first. Normals were taken from the heights either side, clamped at the chunk's edge, so
an edge vertex had a one-sided slope and its twin in the next chunk the other one: the light changed
across every seam, a grid every sixteen columns (0.03 of a unit on average, 0.1 at worst, over the
sand). The mesher now reads a block 19 by 19 and the seam normals agree to the last bit. A chunk's
mesh therefore depends on a column's neighbours one over the edge, and `SandBed.MarkDirty` marks the
neighbouring chunk for a column within one of an edge.

**Threads.** The solver's workers read the bed (`TryTop`) while the main thread waits, so anything the
bed shares between readers has to be safe to share. A "last chunk looked up" cache of three fields
shared by all threads was read torn and answered one height in six from the wrong chunk (nothing threw;
sand stopped being thrown up and driving stuttered): it is per thread now, keyed by the bed and a
generation that `Clear` moves on (`BedThreadTest`: six threads against one). The entry is one object
read with a single thread-static access: five thread-static fields read one by one cost Besiege's Mono
more than the dictionary lookup they saved (a settle's 60,000 column reads went from 720 ms to 110 over a
stroke when it stopped going through it at all, and every lookup on the main thread pays it). `Workers.For`
waits for its items and not for its helpers, and on an event, not a monitor: the caller waited for every
helper the pool had been asked for to start and finish, though it had often done every item alone, and a
helper taking the monitor's lock to pulse the caller now and then sat a tenth of a second on it -- a
100 ms frame two or three times in four seconds of painting, and the same in chunk meshing. The brush's columns are
added to on every core too (`SandBed.PourColumns`, `Workers.For`): bucketed by chunk, each chunk's
columns on one thread, reading a neighbouring chunk's heights as they are at that moment, which a
tolerance of one layer in the stability check absorbs; whatever cannot be added to in place (a new
column, another medium, a step too steep) is poured by hand afterwards, on the main thread. Added to
column by column that was 0.6 us a column; it is 0.05 to 0.1 now, and a slope-heavy brush went from 15 ms
to 6 ms with the rolling cheaper as well (`RollTop`).

**What a wheel digs.** A column is dug where a shape's surface within 6 cm of it slides over
the sand faster than the medium's dig speed (`SandBed.TouchDistance`, from 3 cm: a wheel resting on the
bed's collider is a centimetre or two above the column tops, and at the edge of its patch farther, so
a spinning wheel dug only the middle of it). Driving with a wheel that slips (spin 25 rad/s at 4 m/s, a wheel of
0.3 m) throws 75,000 grain-steps above 0.9 m in the headless test, twice what it did before the throw
was restored (a throwaway driving test, not kept in the repository); a wheel that only just slips
(14 rad/s) throws 82,000.

**A spinning wheel throws what it digs.** A grain handed back by a dig that is within 15 cm of the
digging shape's surface leaves with the speed of that surface where it was, 85% of it, less what
runs into the shape, at most 14 m/s (`SandBed.KickShare`, `KickMost`; `IBedKickSink`): dry sand and
gravel are thrown back and up as a wheel's rim goes, and what a medium clings by takes the throw out
of it, in the ratio 1 : 1 + stickiness/150 -- mud at a third, glue not at all. Dug grains used to be
born inside the wheel and thrown at the rim's speed, which shot mud and wet sand straight up, and
were then moved to rest on top of it, which stopped dry sand flying at all (`BrushShapeTest`,
`Kick`: 557 of 676 grains of dry sand leave faster than 1.5 m/s, 162 of 676 of mud at no more than 2 m/s,
none of glue).

**A slipping wheel bites, and lets go up the back of the wheel.** A surface slipping over the sand it
rests on takes off a layer as thick as a third of how far it slips in a step, at least a grain's worth
and at most 8 cm (`SandBed.DigBite`, `DigSkinMost`, `StepSeconds`): 4 cm a step for a wheel slipping
3 m/s. It took the whole column from below where the wheel reached, about 14 cm; on the bed's collider
the wheel then stood over the hole, out of touch, until the collider was cooked again -- one bite and
nothing -- and most of what it dug was too far from the rim to be thrown. Where a column runs up into
the shape, the shape is in the sand and that all goes, as before. And a turning shape carries what it
digs round with it: a grain is let go 40 degrees further round the rim (`SandBed.KickExit`), with the
surface's velocity there, the spin read off the velocity field and the centre off the shape's box.
Under the wheel the rim moves level, and a grain thrown from there left at a metre or two a second,
which under Besiege's gravity of 32.8 is a few centimetres high. In a headless drive with the wheel
standing on the highest sand under it, as on the collider, and the collider three steps behind: a
0.3 m wheel spinning at 20 rad/s in place throws its grains up at 4.5 m/s on average (1135 of 1506 over
4 m/s); at 25 rad/s driving at 4 m/s, 4.5 m/s, half of them over 4.

**Standing on the collider, not on the sand.** A machine stands on the bed's collider, which is cooked a
few steps behind the sand, so whatever presses or digs the sand down under a wheel leaves the wheel on the
old surface, over it. A column something has pressed on in the last ten presses therefore counts as
touched by a moving shape up to 25 cm over it (`SandBed.HeldTouch`; not further, or a machine's body
would dig its own wheels' ruts), and what a slipping wheel bites from it starts at the tread. Before, the
digging stopped as soon as the sand went down: in game a spinning wheel threw nothing anyone saw, while a
headless test that put the wheel back on the sand every step threw plenty. With the wheel pressing 150 N,
standing on the highest sand under it and the collider three steps behind, a 0.3 m wheel spinning at
14 rad/s and driving at 2 m/s throws 4100 grains in four seconds, 2000 of them upward faster than 2 m/s
(1800 and 860 before). `BrushShapeTest` checks a wheel 15 cm over sand it presses on still throws.

**Wheels slip.** The bed's collider takes the grip of the medium most of each chunk's surface is,
standing and sliding (`SandMaterials.GripStatic`, `GripSliding`):

| | sand | wet sand | dust | snow | gravel | mud | glue |
| --- | --- | --- | --- | --- | --- | --- | --- |
| standing | 0.6 | 0.8 | 0.2 | 0.15 | 0.55 | 0.12 | 2.0 |
| sliding | 0.22 | 0.6 | 0.08 | 0.06 | 0.3 | 0.05 | 2.0 |

combined with a block's by the lower of the two -- glue's by the higher, which outranks every other
combine mode, so nothing slips on glue. The gap between standing and sliding is what makes a medium
loose: dry sand and gravel hold a wheel that rolls, but one given too much torque breaks away and finds
little to drive against, so it spins on, digs and throws rather than gripping again, and a machine turning
fast on gravel drifts. Mud, snow and dust are slippery whether rolling or not; wet sand is firm. It was
0.55 for every medium, averaged with the tyre's, and a wheel gripped mud like tarmac. A block whose own
material combines friction by Multiply or Maximum outranks Minimum and keeps its own grip on the
other media.

**A wheel spinning in place keeps throwing.** A column a slipping wheel digs stops being held (see *held*,
under ruts), so the sand round the hole runs into it and is dug and thrown in turn; held, the hole's
walls stood, the wheel was through 30 cm of sand to the ground in half a second and threw almost nothing
after. The bite is a whole step's slip (`SandBed.DigBite` 1.0, at most 15 cm a step), at least one
grain's worth whatever the shape reaches (a gravel grain is a column 28 cm deep, and rounded down to none
gravel was never dug), and every grain a slipping surface digs starts at its tread and leaves it somewhere
20 to 70 degrees up the back of the rim (`KickExitLeast`, `KickExitMost`), a fan rather than a jet.
`WheelDriveTest` spins a 0.3 m wheel at 20 rad/s in place for four seconds on 30 cm of each medium, with
the collider modelled (the wheel standing on the highest sand under it, three steps old, pressing 150 N),
and counts the grains thrown upward faster than 2 m/s: sand 2340, dust 1320, mud 355 (thrown slower: its
stickiness takes the speed out), gravel 87 (each nineteen grains of sand's worth), glue none; driving at
2 m/s and spinning at 14 rad/s through sand, 4100.

**Every wheel gets its turn.** Digging goes round a machine's shapes from where the last step stopped, its
budget of tests or of grains spent; it started from the same shape every step, so with more wheels than one
step could serve the first few dug, threw and came away with sand on them and the rest never did (six
spinning wheels and eight grains a step: 40, 40, 40, 40, 32 and 0 grains; now 20 each, `BrushShapeTest`,
`EveryWheelGetsATurn`).

**Weight bites.** The pressure a column was last pressed with (by the bed's collider, see ruts) scales how
deep a slipping surface bites into it, by its square root over 800 Pa, from 0.6 to 2.5 times
(`SandBed.BitePressure`, `BiteFor`): a loaded wheel spinning in sand digs itself in and throws more, one
barely touching it skims. Whether a wheel spins at all is PhysX's: torque against grip, grip being the
normal force times the friction (see *Wheels slip*), so more weight means more grip, as on any ground; on
loose sand it is the sliding friction, low, that a wheel which has broken away meets.

**Thrown sand is coarse, and a spray that is only drawn goes with it.** What a slipping surface digs comes
back as grains one level coarser (`SandBed.KickLevel`; half again as wide, three and a half ordinary grains
each), never so coarse that what there is makes less than one, and rounded down so a layer's last grain does
not take the rest of its volume out of the layer below (a sliver left over joins that layer). Each grain
thrown, by a wheel or a blast, takes three fine grains with it that are only drawn (`SandSpray`): scattered
a little, flying under gravity and the medium's air drag, gone within a second or when they fall back past
where they were thrown from, touching nothing. The sand is all in the coarse grains; the spray is the look
of it. The solver carries a third as many grains for a wheel's throw, and its budget a step is now 10 ms
(`SandField.StepBudgetMs`; it was 20, half of every second at 25 steps a second).

**Grains are drawn between steps** (as discs wound clockwise as the camera sees them, Unity's front: wound
the other way the lit material culled every grain, and none were seen). The solver moves the grains 25 times a second; each frame draws a grain
between where it was at the end of the last two steps, by how far the frame is into the next
(`Grain.shown`, `shownFrom`; `SandRenderer.Draw`), so it moves smoothly at the frame rate, a step behind.
A multiplayer client, whose grains are the host's, draws them where they are. And a grain is drawn lit as a
little disc that shades like a ball -- a centre facing the camera and eight rim vertices leaning out -- and
a little out of round, one of sixty-four shapes (0.75 to 1.3 of round along two axes, at a slant) picked
by a seed it keeps (`Grain.look`): a heap of pebbles rather than of identical coins. Dust is the
exception: its grains, moving or only drawn, are soft puffs (`SoftDust`) -- a camera-facing square 2.6 grain
radii across each way of a white disc whose alpha fades smoothly to nothing at its rim, made at start, drawn
with the stock `Particles/Alpha Blended` under a neutral grey tint so the puff is the grain's own colour, at
about 60 per cent opacity in its middle. Four vertices a grain where a lit disc is nine; what it costs is the
overdraw of the soft edges, where a cloud is thick and close. Every grain that is only drawn is a soft
puff too, whatever its medium: a wheel's throw reads as a fine haze. The spray's fine grains are half the medium's grain to the
grain itself. A dug grain that starts inside the shape digging it is put out of it the nearest way, along
the surface's normal, never below the ground under its column; lifted straight up a grain at a time, one
started inside the bottom of a wheel came out through its top.

**A landing grain is spread.** A grain going back into the sand where it lands is spread over a square of columns,
as many as leave each at least 2 cm (`SandBed.AddVolumeSpread`, `SpreadEach`); a grain under four times that goes
into one column. Into one column, a coarse grain -- what wheels throw -- stood up as a spike, dust's most of all (a
coarse grain of dust is over a metre of one column), until slumping took it down. Not thinner: a share under the
bed's thinnest column counts as nothing and was lost.

**The spray is the shapes', not the grains'.** A wheel's spray and a blast's are made from what is happening to the
sand, not from the grains it throws: every sand step, every shape slipping over the sand throws puffs from along the
back of its rim, where its grains would leave, at the rim's speed there and climbing, as much as the medium would be
thrown (`SandField.ShapeSpray`); a blast throws puffs from the sand over its crater as its grains go
(`BlastSpray`). So the spray is the same at any sand limit, none included, does not stop and start as the solver
catches up, and goes on with the sand paused (a wheel's; nothing explodes into paused sand). Only *Show cosmetic
particles* turns it off. In multiplayer every player makes their own (`SandField.ClientCosmetics`): only the
physical sand is the host's and sent, so a client's spray has no lag and its own switch. A client sees the machines
as the network moves them, kinematic, so a body's speed there is read off how it moved since it was last looked at
(`ShapeFeed.Moved`) -- taken to be how a client's copies of the machines move; not yet seen in a game with others.
Every grain, drawn or simulated, comes in a spread of sizes round its medium's: a simulated grain is drawn from
three quarters of its row's size to a quarter over (the solver's grain stays the row's), and dust's, which are soft
puffs, from three quarters of a fine grain to twice their own; a puff of spray is a grain somewhere in its medium's
range (`SandField.SpraySizes`, multiples of the fine grain): most media 1.5 to 2.5, gravel 0.75 to 1.25, mud 3 to 5,
snow 1.5 to 3, dust 0.75 to 2; and however much a
medium clings, its spray leaves with at least 0.4 of the surface's speed (`CosmeticShareLeast`) -- glue's, by its
stickiness, lay on the sand unseen. A grain of dust is drawn its own size, from three quarters of a fine grain (the
smallest puff of its spray) up to its own, so thrown dust varies as its spray does. Sprayers still spray their puffs with their grains, and make none while paused.

**A plough pushes sand off its face.** A face meeting the sand going forward pushes the sand in front of it the way
its own surface faces (`SandBed.PlowAway`), so a blade set at an angle sends its sand towards its trailing end -- a
snow plough's windrow -- where it pushed it straight ahead, and it ploughs wherever its face faces the way it goes,
not only ahead of its middle: half an angled blade's face is behind its middle, and there it dug the sand out as
grains instead (`BedPressTest`: heaped 0.22 m further towards the trailing end, the same sand moved). Four distance
tests more for each column ploughed, nothing else.

**UI Factory is only touched once it has loaded** (`UIFGate`, `Modding.Mods.IsModLoaded` by its mod id). When
it loaded after this mod, the panel's first call into it was compiled before it was there, Mono marked the
reference missing for the session, and the panel came up plain with an error every frame.

**A wheel's dust is the sand's.** Besiege's wheels carry their own dust cloud (`WheelSmoke`: a particle
system the wheel moves to where it touches the ground each physics step and emits from as it rolls or
skids), grey-brown whatever the ground and drawn with a shader that darkens what is behind it, so it read as
the dust's shadow. On ordinary ground it is left exactly as it is. On the sand the wheel's own system goes on
emitting, but what it makes there is invisible and lasts a fifth of a second -- only there to say the wheel is
emitting, and where it touches -- and the cloud the player sees comes from one particle system of the mod's
own (`SandDust`, `NativePuffs`: soft unlit puffs, `Particles/Alpha Blended` with a neutral tint, that darken
nothing), every frame the wheel's shows it emitting, at its rate times the medium's, in the colour of the
sand there, the medium's opacity, and the row's size and life -- the puffs start at 0.4 of the wheel's own
times the row's size, half as many, and grow by half as they fade, since the numbers were set against the
wheel's small dark puffs and as soft growing puffs they made clouds far too big (`cloudSize`, `cloudLength` -- dust a huge
cloud that hangs a long time, 6.5 times the wheel's puff and 4.5 times as long; dry sand and snow nearly as
big; gravel 1.6; wet sand, mud and glue about 0.9; the advanced settings of a sprayer or the brush set them,
and a cloud size of 0 raises none). The puffs drift up slowly, the air slows them, and they grow to over
twice their size as they fade. Nothing in the air is ever recoloured: the wheel's system only changes what it
makes next, and the two systems' puffs keep their own looks. (Before this the wheel's own cloud was dressed
in the sand's colour, which meant neutralising the material's dark tint -- a tint recolours every puff in the
air at once -- and folding it into start colours.) Five times a second each wheel of every machine is looked
at to see what it is on (the dust is the particle system under the wheel block that the wheel has moved down
to the ground). With the panel's *Wheel dust* off, wheels raise none on the sand; off it they raise the
game's own, as without the mod.

**The panel's limit, and what is only drawn.** *Sand limit* (slid from 1,000 to 80,000, typed anywhere from 0 to
100,000 -- the grain array holds 100,000 -- kept between runs) is the most
grains simulated at once: the governor admits no more whatever the machine could afford, and digging and
blasts none past it. The drawn spray is one of Unity's own particle systems (`SandSpray`, `NativePuffs`), soft
puffs that Unity moves and draws in native code -- they were quads built in managed code every frame -- at
most 6,000 of them whatever the limit (`SandField.SprayMost`). Three go with every grain the sand gives up,
thrown or dug (glue and mud, which a wheel lifts rather than throws, had next to none), each looking like the
medium's own grains: the size of its fine grain (an emitter's own row, its own), with the same spread of sizes
and lives, in the grain's colour -- dust's as the soft part-clear puffs its grains are drawn as (`SoftDust`,
the soft texture, 150 of 255), every other medium's opaque on the denser texture, about the size of the discs
its grains are. Size and opacity are each puff's own (`EmitParams`), so this costs nothing over all alike but
the GPU's share of bigger puffs for coarse media. *Show cosmetic particles* off makes none and draws none. Sprayers throw two drawn grains with every grain, as wheels throw three and blasts three.

**The sprayer is a pipe.** "Pipe" by Quaternius (public domain), converted with `tools/glb2obj.py` and
its `--zup` and `--uv` flags -- the model is authored Z-up with its node turning it Y-up, so the node's turn
is left off and only the handedness flipped, and it has no texture coordinates, so they are wrapped round its
axis for the cardboard texture. It stands a unit tall on its base, open end forward, and the medium leaves
just past its mouth (`SprayerBehaviour.NozzleZ` 1.05) from inside its bore; the toolbar tile looks down on
the open end (`<Icon>` x -125). The sprayer panel's ADVANCED section collapses while its
switch is off (`Panel.Section` with collapse, for a section at the bottom of a panel).

**Grains become sand where they land.** A grain down on the sand or the ground, slower than 1.2 m/s and at
least 0.3 s old, and not beside a machine, goes into the bed at once (`SandBed.LandOnContact`, `LandSpeed`,
`LandAge`) instead of after lying still for twelve steps. The solver carries far fewer grains for a pour or
a wheel's throw; what is given up is the last half second of a grain rolling down the slope it landed on,
which the bed's slumping does instead, at the medium's angle. A grain beside a machine still waits, as a
machine may dig it straight up again.

**Air drag.** The share of a grain's speed the air takes each second: it sets how far a thrown grain
carries and how fast it falls at most (gravity over drag). Sand, wet sand and mud 0.05, gravel 0.02, snow
0.4, dust 1.5 (22 m/s at most), glue 0.2 (its damping is what lets a sticky heap on a block settle; at
0.05 it weighed 30% over its weight). Dust's was 6: a grain thrown at a bomb's speed stopped within three
metres and fell at no more than 5.5 m/s, hanging by the wheel that threw it.

**Room to throw.** Digging may fill the field up to the panel's sand limit, and only that (the governor's
ceiling, which sinks while the solver is busy, stopped a machine's wheels throwing until what they had
thrown settled), a twelfth of the limit a step (200 to 1500), with 40,000 distance tests a step to share
among a machine's shapes near the sand (at 12,000 they took turns, a few a step); and the drawn spray holds 20,000: at a quarter over the
ceiling and a twentieth a step, a machine's wheels threw for a second, ran out of room, and threw nothing
until the grains in the air had settled.

**Stranded grains go back into the sand.** A grain loose for 8 s and slower than 1 m/s goes into the
column under it wherever it lies (`SandBed.StrandedSeconds`, `StrandedSpeed`) -- wedged beside a wheel,
never quite still -- **unless a block is under it** (`SandBed.ShapeBelow`): sand heaped on a build
surface dropped through it to the ground after eight seconds, so a grain over a shape's box stays a grain
on the block. The sand limit caps what lies on machines. Absorbing waited for a grain to lie still, clear of every shape and
down on the sand, and grains a wheel threw onto its own machine met none of those: over a long run they
piled up to the field's ceiling, the solver carried them every step, and sprayers and wheels had no room
left. Digging may also hand the solver no more than a twentieth of the governor's ceiling a step (100 to
600, `SandField.ReleasePerStep`), and fill no more than a quarter over the ceiling (it was twice it): with
wheels throwing as they now do, driving on sand lagged. The array itself holds 80,000 grains; what is live
is the governor's to say, from the measured cost of a step.

**The level's people stand on the sand and leave it be.** A knight, a peasant or an animal (anything under
an `EntityAI`) walks on the bed's collider like on any ground, and the collider is kept where they are, but
they press no ruts, dig nothing and take no push from the grains, which treat them as fixed obstacles
(`ShapeFeed.IsEntity`, `BedShapes.StanderCount`). A walking ragdoll pressed ruts under its feet, the
collider was cooked again under it every step and it staggered on the new one, and its kicks dug sand up:
lag, and people moving very oddly. They climb it, too: their AI takes a contact whose normal is further
from up than its `MaxAscent` (a cosine) for a wall and walks round it, and the sand at thirty degrees, its
collider's facets steeper here and there, was a wall to them; on the sand they take anything up to
fifty-three degrees for ground and climb it (`walkUpSlopes`). And they do not take it for something to jump
or turn back from: their AI measures what it walks into by its collider's bounding box, jumping what is up
to its jump height and turning from what is higher, and a sand collider is a whole chunk whose box's top is
the highest sand in it -- at the foot of a pile a person saw the pile's top as a wall or a jump every step.
On the sand that check is off (`canJump`); they have their limits and their jump back when they step off.
Their climbing speed is their own: three times it, they ran into the next slope hard enough to die. And they stand on it properly: their AI asks whether it is on the
ground by a raycast against its own `groundLayerMask`, which need not name the floor layer the sand's
collider is on -- a knight on sand thought itself falling, glided along and fell over dead -- so a person
touching the sand has its layer added to that mask; and while it touches the sand its collider wears a
material of 0.6 combined by the higher (`BedContact`), so the slippery media do not make it skate, and gets
its own back when it steps off.

**The god tool explodes on the sand.** Explode on click raycasts from the cursor against the scenery, and the
sand only has a collider near a machine, so the explosion happened on the ground under the sand. While the
tool is on and a run is going, the sand under the cursor is given a collider each frame
(`SandBedColliders.WantAt`): the tool's ray meets the sand's surface, and the explosion blows a hole in it.

**The camera looks through the sand.** Besiege's camera keeps out of the environment by sphere-casting
from what it orbits, and skips colliders with "Floor" in their name or the `CamIgnored` tag. The sand's
colliders are now named `SandFloor` and tagged so: sand between the camera and the machine -- a pile, or
the collider still standing over a hole a wheel had dug -- pulled the camera in close.

**And up out of its rut.** A throw climbs at least 0.8 of what it goes sideways (`SandBed.KickLift`).
Under a wheel resting on the sand -- which is where a wheel on the bed's collider is -- the rim moves
level, and a grain thrown level from the bottom of its own column ran into the sand beside it and lay
there to be absorbed again: in game a spinning wheel threw nothing anyone could see. In a headless drive
(bed, solver and a 0.3 m wheel kept on the sand, a throwaway test) a wheel spinning at 14 rad/s at 2 m/s
kept grains above 25 cm for 3,200 grain-steps before this, 7,900 before the throw was added, and 12,500
now; at 25 rad/s and 4 m/s, 1,300, 3,400 and 15,000. `BrushShapeTest` checks that every grain a wheel
resting on the sand throws is climbing.

**Only the medium being settled moves.** A column of another medium is fixed ground to a settle, and so
is whatever lies under the settled medium's own surface layer. Settled at dust's flat angle, a sand pile
beside a stroke of dust was cut down and laid back round it as dust: dust seemed to appear where the
last medium had been (`BrushShapeTest`, `OtherMediaStay`).

**A pile grown in one place is settled as it grows.** A brush held where it was made the frame longer
every second (2 ms, then 10 at twenty seconds): the sand added at a pile's top moves every column down
its slope out by a little, and rolling that a piece at a time is the slope's length for every piece.
Now the columns that cannot take their share where they stand are given it there (`PourBatch` with
`defer`), and `SandBed.SettleDeferred` lets the pile down to its angle in one go: what stands steeper
than the angle is cut down to the steepest surface that fits under it (two sweeps of a cone from every
low point), and what was cut off is laid back round it as a skirt that falls away at the angle, at the
level that holds exactly what was cut (Newton's method from above on the piecewise-linear volume, a few
passes over a shrinking list of columns; forty halvings over the whole box were 40% of a settle). What
is actually taken and given is counted and the difference laid back on the cut, so the crumbs -- a skirt
thinner than a layer on bare ground, the remainder of a column cut to under one -- are not lost (settled
every frame they were 2% of a big pile). The brush settles the pile it grows while it is held
(`SandField.SettleWhilePainting`), so it shows the sand where it will lie rather than a tower that falls
into shape when the button is let go: every frame while that costs under `BrushSettleMs` (4 ms), every
few frames by what the last settle cost once the pile is big, and only when the brush is let go
(`StrokeTick`) for a pile that would take more than four frames' budget at once -- a brush tens of
metres across. A circle brush 4.8 m across held four seconds, settled every frame: 7.6 ms a frame on
average, steepest step 1.05 of the angle at every look, the sand kept to 0.03% (`BrushShapeTest`,
`SettledAsItGrows`). Settled once at the end, four seconds of a 40-column disc at 1.8 m/s: 2550 ms rolled
every frame, 200 ms grown and 27 ms settled (`Deferred`).
A brush too big for a frame, which lays every other column, leaves a speckle, which
`SandBed.SmoothBox` evens out when the brush is let go (pairs of neighbours pass a quarter of their
difference, so nothing is lost).

**Sticky media are not flung high.** A grain of a cohesive or sticky medium, or a wet one,
leaves a wheel at no more than 3 m/s upward -- about fifteen centimetres under Besiege's
gravity. Riding round a wheel, mud and wet sand came off its top at the rim's speed and shot
up like a roost of dry sand. And a column a wheel digs is given back with no grain inside the
wheel: the column ran up through it, and grains started inside were thrown out at the rim's
speed, straight up.

**Glue grabs.** Its stickiness is 60000, and the stickier a medium, the further a grain
reaches for a surface (`CpuSolver.AdhesionReachPer`, up to 1.2 grain radii: further, and whole layers of glue counted as stuck to a block and pushed it about) and
the more of it comes away on whatever moves through it -- glue at almost any speed, a thicker
skin, several times as many grains (`SandBed` pickup).

**Snow melts.** A burning block, a flamethrower's flame and a boiling water cannon's steam
take snow away: off the bed (`SandBed.Melt`), up to a quarter of a metre a second right at a
burning block and none three metres off, measured to each column's top so a pile melts from
the side nearest the fire, and loose snow grains one by one, the nearest likeliest. It rides
on the drying -- the same sources, found the same way, and nothing at all while none is lit.

**No grain is smaller than dust's 25 mm** (`SandMaterials.MinRadius`), including one an
emitter's advanced settings ask for: below that the same sand is too many grains to simulate.

**Sticky media come away on wheels.** Wet sand, snow, mud and glue stick to blocks through the
solver, but only grains can stick, and a machine rolling over the settled bed makes none. So
under a moving shape, sand at least as sticky as snow (`SandBed.PickupAdhesion`) gives up a
skin of grains now and then -- at most a dozen a shape a step -- which cling to the wheel and go
round with it. Mud picked up this way, and its soft bed, leave a machine caked and tracked.

**Colour variety.** Grains of one medium are not one colour: each is made a step lighter or
darker than its emitter's colour (`SandMaterials.Variety`: sand 12%, gravel 28% and warmer or
cooler with it, mud 12%, snow 3%), in eight steps so the renderer's palette of grain colours
stays small. The settled bed does the same per column, by a hash of where the column is, so a
pile keeps its speckle.

**Clinging is easy to lose to cohesion.** Snow first packed to a cohesion of thirteen under
load and mud started at twelve; either way a layer held its grains together harder than a
wheel could pull them off, and nothing stuck to it. At snow's packing gain of 4 and mud's
cohesion of 5, a spinning wheel carries 86 and 104 grains round with it (`SolverShapeTest`).

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

**Plates hold from the side the sand came from.** A build surface collides as thin boxes a few
centimetres thick (curved: one box per quad; flat: a convex mesh slab). Measured by its nearest face, a
grain the heap pressed past a plate's middle was put out underneath, one pressed right through was not
held at all, and one near an edge was put out sideways, into the joint between two surfaces. So for a box
under three grain radii thick (`CpuSolver.PlateSide`) a grain that started the substep over one face and
is over the plate now is measured from that face alone, on every pass. A convex mesh is no longer its
bounds in its own frame -- for a tilted surface a box as tall as the surface is long -- but the tighter of
that and a box turned to the plane its vertices lie in (`ShapeFeed.Fit`), and a block's convex mesh is
kept on a kinematic body too (a multiplayer client's machine). `SolverShapeTest` pours 1,500 grains onto
two plates meeting edge to edge: 27 went through before, none now.

## Colour and texture

Per particle: a packed 32-bit colour. Per material: a tint and a tile index into a
small atlas the mod ships. A particle is drawn as a camera-facing quad, so a texture
is an ordinary sprite — grain speckle for sand, soft blob for dust, faceted chip for
gravel.

Colour comes from three places, multiplied: the material's own tint, a per-emitter
colour the player sets in the block mapper, and a wetness darkening applied when the
wet flag is set. That last one is why wet sand reads as wet without a second texture.

Loose grains carry it in the mesh's vertex colours, and the bed in a colour texture per
tile, so loose sand matches the pile.

## The sprayer's settings

The media picker, the colour and the advanced physical settings are one class,
`MediaSettings`, built on the sprayer block's mapper.

**Advanced settings do not redefine a medium for everyone.** Opening the toggle
claims a *variant* row seeded from the chosen medium, and the sliders write that row.
Two sprayers can therefore throw sand of different coarseness in the same level, and
neither changes what the other pours. Sixty variant rows exist; past that an emitter
falls back to the plain medium, so a level with too many customised emitters loses
the customising rather than the sand.

**Toggle** (`SprayToggle`) makes the key a switch: a press starts the spray and the next stops it,
emulated presses included. It shares a row with the *Advanced* heading, half each (`Panel.Switch` and
`Section` with a part of the row).

The sliders show **density**, while the solver's table keeps **inverse mass** --
because density is what a person has an intuition for and inverse mass is what the
contact solve wants. The conversion is in one place.

## The sand panel

A small window on the right of the screen, there while building and while a level runs,
hidden with the rest of the interface by Tab (`SandHud`). With UI Factory installed it is
UI Factory's own window, built the way the node editor mod builds its board: the window's
own opaque background with its title bar and scroll view switched off, and a darker strip of
our own across the top to drag it by, with a button that folds the panel into a small
sand icon (drawn by the mod) in its top-right corner. The icon opens it again in the
same place; dragged, it only moves, and the panel opens wherever it was left (Unity calls a
press and release on one button a click however far it moved between, so the icon notes that
it was dragged and lets that click go). The panel is held by its top-right corner, so
rows coming and going -- Advanced, the multiplayer switch -- grow and shrink it downwards
only. In it are the
game's toggles and button, the spotlight's selectors for the brush's shape and the medium
(arrows step, the name or a right click opens the list of all of them) and sliders with a box
beside them for the brush's size and rate. The title is centred on the strip. Without UI
Factory it is a plain panel of the same controls on an opaque plate with rounded corners drawn
by the mod. Like the node editor, it gets out of the way when another of the game's menus
opens (`StatMaster.inMenuCounter` above the holds the mod's own pieces have made, which
`ZoomGuard.Raised` counts), and it is not there in the menus at all (`SandField.InMenu`).
Every row is the same height with the same 3-pixel gap under it. From the top: *Paint* and *Remove
sand* side by side, the brush's shape, *Size*, *Rate*, the medium, *Colour*, *Advanced* and its rows,
*Pause sprayers*, *Show cosmetic particles*, *Wheel dust*, *Sand limit*, *Pause sand simulations*,
*Keep changes to sand*, *Clear all sand*, *Save* and *Load*, and the multiplayer switches.

**The keyboard is the game's.** Besiege's keys -- block keys, the camera's -- stand down while a
menu is open, and the panel used to say it was one for as long as the pointer was over it, so a
key pressed with the pointer there did nothing. It now says so only while a press that began on
it is held (so a drag on a slider does not turn the camera or box-select blocks) and while one
of its boxes is being typed into (so the letters do not drive the camera); hovering only stops
the wheel zooming (`SandHud.MenuHold`).

uGUI on its own canvas, per the notes
repository: Besiege draws its uGUI over IMGUI, and asks the EventSystem whether the pointer is
over UI before placing a block, so a uGUI panel occludes the clicks the game asks about; while
the pointer is over it every camera's `eventMask` is held at zero for the clicks it does not.
Nothing goes through it: a press that begins on the panel or on an open list holds that shield
and keeps the brush off until the button comes up, wherever the pointer has gone by then.
The same goes for the game's own interface -- the building bars, the level editor's menus,
popups -- which is mostly not uGUI but meshes with colliders, drawn by cameras of their own and
clicked through `OnMouseOver`. The brush asks the way Unity does: a ray from each of those
cameras (any that does not draw the world's own layer) against the layers it draws and takes
the mouse on. The cameras are written to `Player.log` once, so a menu the brush still pours
through can be traced.

* **Paint** draws the brush's outline on the ground and the sand under the cursor. Holding the
  left button raises the sand under it by **Rate** metres a second where the brush is at full
  strength (slider 0.1 to 5, typed 0.01 to 100) -- a depth, not a volume, so a big brush lays
  the layer a small one does -- and slumping shapes it, so a brush held in one place grows a
  heap at the medium's own angle of repose: its friction times 1.15 as a slope, so dust about 13
  degrees, sand 30, gravel 46 (`BedPressTest`, `BrushShapeTest`). **Remove sand** switches it to taking sand away, settled and loose alike, at
  the same rate. It does nothing over any interface or over the machine, where a click is the
  game's, and it waits, grey, until the ground under the whole brush has been measured: sand
  poured where the ground is not known yet was lost, which is why big brushes came out thin
  and patchy.
* **Colour** is the sprayer's colour control for the brush: a band of hues to slide along, the
  colour itself, and a box that takes any colour as six hex digits. Choosing a medium sets it
  to that medium's own colour.
* **Advanced** makes a sand of one's own from the medium with eight settings, the same eight
  as the sprayer's (`SandMaterials.Custom`), each one number of the material row as it is, set to
  the medium's own whenever a medium is chosen:

  | setting | the row's | dry sand | dust | gravel |
  | --- | --- | --- | --- | --- |
  | *Grain size* | grain diameter, cm (`radius` x 200) | 6 | 10 | 16 |
  | *Weight* | density, kg/m^3: `invMass = 1 / (4/3 pi r^3 x density x Lightness)`, `buoyancy = 1000 / density` | 1600 | 60 | 1800 |
  | *Grip* | `friction`: how steep it piles, how it holds a tyre | 0.5 | 0.12 | 0.9 |
  | *Stickiness* | `adhesion`, m/s^2 a grain clings to a block with | 0 | 0 | 0 |
  | *Softness* | `firmness = 10000 / softness`: the pressure, Pa, that presses it a metre | 0.5 | 5 | 0.06 |
  | *Dig speed* | `digSpeed`, m/s a surface must move through it to dig it | 0.068 | 0.011 | 0.128 |
  | *Cloud size* | `cloudSize`: a wheel's dust puffs, times the wheel's own | 4.5 | 6.5 | 1.6 |
  | *Cloud length* | `cloudLength`: how long they hang, times the wheel's own | 2.2 | 4.5 | 1.4 |

  Softness used to scale firmness and dig speed together; they are two settings now, so each is
  one number. Everything else -- how grains clump, bounce, drift in air and water, pack, get
  wet -- follows the medium, so opened and left alone they pour it exactly (`BedPressTest`). The brush then pours a row of the
  panel's own (one of the field's emitter rows, claimed once and rewritten as the numbers
  change). A level saves such sand as the medium it was made from: the row is made while the
  game runs and is not in the file. A client's brush pours the host's plain medium.
* **Undo and redo** are Besiege's own: the machine's history (`UndoSystem`), its buttons and keys. Each
  brush stroke, and each *Clear all sand*, is one step among the machine's edits (`SandUndo`, an
  `UndoAction` with none of its change flags set, so the history does nothing round it for the machine).
  A stroke runs from the press that first pours or removes sand to the release, when its pile is settled in
  place. Its step goes into the history the moment it begins, empty, and is filled when it is finished:
  added only once its pile had settled, a block placed in between went in ahead of it and undo took the two
  back in the wrong order. Undoing a stroke still open finishes it and then undoes it. It is kept chunk by chunk (`SandBed.BeginJournal`, `JournalArea`, `EndJournal`, `Apply`): the
  chunks within reach of the brush, and of the box its pile settles into, are copied before they are
  worked on, every change to a chunk passes through `MarkNet`, which notes the chunk, and at the end the
  copies of the chunks it changed are packed (`BedCodec`, to the millimetre) and the rest dropped. Copying
  the whole bed at each press was fifty megabytes a stroke on a big level. A clear is every chunk packed
  (`SandBed.PackAll`), and so is loading a sand file: the sand there was, plus every chunk the file filled
  as none (`SandBed.AddMissing`). The load's step goes into the history as the load starts, so it can be undone
  while the sand is still appearing -- right after the machine step the load screen adds of its own
  (`ReplaceMachineUndoAction`; now if it is already in, else on the next frame), so the sand's is the first
  undone; the machine step before it changes nothing visible, since the machine is put back (`SandFiles`).
  (`UndoSystem.Overwrite` could take that step out, but it also rewrites every block step's stored state
  from the blocks as they are now.) A file
  another player sends the host is no step in the host's history. Undone while a big file is still being drawn,
  the chunks it emptied are drawn again before anything else and their tiles go to the front
  (`SandBed.TakeDirty`, `emptied`), and the redrawing goes on whether or not the bed still holds any sand --
  it used to stop when the bed was empty, which left the last surface on screen. Undoing a stroke whose pile is still
  being placed drops the rest of the placing (`SandBed.ForgetDeferred`) and puts the chunks back at once.
  Undo and redo are the same swap: put the step's chunks back, keep how they were.
  Only while building, as the machine's own undo is. A step whose sand has since been replaced
  wholesale -- a sand file or a level's sand loaded, a run whose changes were kept, a multiplayer session
  joined -- stands down by returning false, which `UndoSystem` takes as nothing done and goes on to the
  step before, as does a stroke that changed nothing.
* **The brush's shape** (`BrushShape`): *Circle*, a dome; *Flat circle*, an even
  layer; *Square*; *Random*, smooth lumps a third of the brush across, new for each press; and
  *Ring*. Every shape tapers over its last few columns, so an edge is a slope, not a step.
* **Size** is the brush's width: the slider covers 1 to 25 m, and the box takes anything typed
  from 0.5 to 10000 m. It stops growing at 200 m across (`SandField.MaxBrushRadius`) -- each
  square metre of brushed bed is held in memory. **The brush pours; it does not pile and slump.** Sand
  goes in with `SandBed.PourVolume`: onto a column if the column can hold it at the medium's angle
  of repose, else down the steepest way to one that can, in pieces no thicker than a column can
  step. A pile made so is at its angle as it grows and leaves nothing to slump. Added a layer at
  a time instead, a flat brush held for three seconds was a nine-metre plateau with cliffs, and
  `Relax` spent about a minute of CPU spreading it. Flat circle and square also lay their edge
  as a slope (`SandField.BrushTaper`: the depth under the centre over the medium's repose), so
  most of what they lay needs no rolling at all. **A brush too big for `SandField.BrushBudgetMs`**
  (3 ms) is swept every *n*th column a frame (`BrushSweep`), a different set each time, each
  given *n* frames' worth, *n* being what the last frame's cost says fits and at most what keeps
  a column under 4 cm proud of its neighbours (`MostSpeckle`: under the step sand stands at, so
  there are no spikes). Past that it paints slower, not rougher: at 1 m/s a 6 m radius brush lays
  its full rate, a 12.5 m one half to all of it, a 25 m one about a sixth. (Whole bands of rows
  were tried first: strips a metre thick beside bare strips, each a cliff the brush's length. A
  looser stride left spikes that moved from frame to frame and stayed where the brush was let
  go.) A row's weights are worked out once and only where the shape has weight (`BrushShape.Row`, `Span`), and the bed keeps its
  last chunk, reads a column's eight neighbours straight from the chunk before it rolls
  anything (`SandBed.Stable`), and `Relax` skips a column whose neighbours are all within the step
  (`SandBed.Level`), so settled sand costs next to nothing. All of this is held by
  `BrushShapeTest`. It used to pour on a random sample instead, each sampled column standing for
  many, and that is what left large brushes as a field of thin spikes.
* **Pause sprayers** pauses every sprayer. **Pause sand simulations** pauses the sand: no grain
  moves or is made -- nothing is dug, kicked, blasted or sprayed -- and nothing sinks under a load
  (the loads are let go each fixed update, `SandBed.DropLoads`), but the bed's colliders still
  follow the machines a sand step at a time (`SandField.FrozenStep`), so machines and people drive
  and walk on it as on rock, and wheels still raise their dust. **Clear all sand** asks first: the
  button darkens to "Clear sand?" with a tick and a cross beside it (drawn as bars, as no font is
  sure to have the glyphs); the tick clears, and the cross, the button again or a press anywhere
  else puts it back as it was.
* **Kept between runs of the game.** Everything the panel sets but the paint switch -- medium, colour,
  brush, size, rate, removing, the advanced numbers, the switches, where the panel is and whether it is
  folded -- is written to `settings.txt` in the mod's data folder (`SandSettings`, through `ModIO`, a second
  after a change) and put back when the game next starts. Painting itself starts off.
* **Save** and **Load** (greyed out, and not growing under the pointer, while a machine runs) keep the sand on its own, as a file in Besiege's own save and load screens, in
  `SavedMachines/Sand_mod` (made the first time). See *Sand files*, below.
* **The sprayer's settings** are a UI Factory panel docked under the block mapper
  (`SprayerPanel`), on the SpecialEffects mod's panel framework (`Panel`, `Chooser`,
  `ValueField`, `UIF`): the medium, its colour, the spray's rate, speed, spread and
  lifetime, and the advanced section of the medium's own numbers, all in one scrolling
  panel. The spray key stays on the stock mapper. UI Factory is a soft dependency: every
  mention of its types is in `UIF.cs`, so without it the mod still loads and the stock
  mapper shows everything.
* **Keep changes to sand** (on to begin with). When a run starts, the sand as it is -- including anything brushed or
  sprayed while building -- is captured; when it stops, that is put back, the way Besiege
  puts the machine back. With *Keep changes to sand* on, the sand stays as the run left it. The captured copy is
  taken whole, which is quick, and packed (below) a millisecond's worth a frame after.
* **Allow others to load sand** follows Save and Load, for the host in a game with others, off to
  begin with: on, the other players' Load works, and their sand file is sent to the host in 32 KB
  pieces and loaded there for everybody.
* **Other players place sand** is the last row, and only there with somebody else in the game:
  the level editor runs as a multiplayer session of one, so being in a session is not enough.

**The folded icon.** One pile of sand with grains still falling on it, on the same background as the
open panel's top bar -- its colour and its transparency. Under UI Factory the icon is a window of its own
(the window prefab with its title bar and scroll view off, the bar's dark strip over it), so it is the
same translucent, blurred plate the open bar is; on the plain panel it is the panel's opaque plate. The
picture itself is clear round the sand. It is **a file, not a drawing**: `Sand/Resources/Icon.png`, made by
`tools/make-icon.sh` out of `SandIcon.cs`, declared in `Mod.xml` as a `<Texture>` and loaded
back by `SandHud.PileSprite` through `ModResource.GetTexture`. That is the arrangement the Node
Editor mod uses for its own artwork, and it is right here for two reasons: the game cannot open
an image from disk (the mod loader refuses `System.IO`), so a picture has to arrive as a
declared resource; and a file can be looked at, replaced and diffed, where code cannot. If the
texture cannot be loaded -- a mod folder without it -- the pile is drawn instead, so the button
is never missing.

**It is not the Workshop thumbnail, and is not made to be.** That is `Sand/Resources/Thumbnail.png`,
a copy of `Thumbnail_2.png`, the artwork at the top of the repository, declared as the mod's `<Icon>`. Two pictures
on purpose: one is a button that has to read at 52 units, the other is a picture with the mod's
name on it for a page. They were briefly one file and it served neither -- the artwork does not
read at 52 units, and a square icon is not a thumbnail anybody can tell the mod by.

The copy is 768px and not the artwork's 1625 because Steam caps a Workshop preview at 1024 KiB
and refuses an item over it with nothing said about which file, which would break every upload
of the mod while the game loads it perfectly well. The artwork as it stands is over that, so
copying it verbatim would do exactly that. `tools/make-thumbnail.sh` checks what it wrote.

The art is laid out on a square of 64 units and drawn at whatever size is asked for, so nothing
in it is a pixel of any one texture: the shipped icon is 1024 pixels square and the panel shows
it at 52 units. The texture is sampled with `FilterMode.Point`, since smoothing between the
pixels is exactly what must not happen to pixel art.

**The plate.** A square with its corners rounded to 12 units, left clear: the sand is cut to its
corners, the one soft edge in the picture, and the background is the panel's (above).

**The sand in it is pixels.** The pile is a grid of `Pixel`-unit squares and every one of them is
a single flat colour, with no anti-aliasing anywhere on it, so the silhouette steps rather than
smoothing. Four colours, each with a version of itself a shade lighter and a shade darker
(`Nudge`, towards white and towards black rather than by scaling, so brightening never clips a
channel flat): `Main`, `Shadow`, `GrainDark`, `GrainLight`. The lit sand is drawn from a palette
weighted hard towards `Main` and the shadowed sand from one weighted towards `Shadow`, with the
grains mixed sparingly through both -- a pile that is a third dark grains is not sand. Which of
the twelve a pixel is comes from a hash of where it is, so the pile is speckled the same way
every time it is drawn.

**One pile, standing on the bottom of the frame.** A single half-cosine crest with no skirt and
no second dune, and both flanks wider than the frame, so the sand reaches the bottom edge of the
image at the left and right rather than standing on a shelf above it.

**The sun is low.** The sand below `ShadowFrac` of a column's height is in shadow and the sand
above it is lit, so about half the pile is shadowed. The line between the two colours is a curve,
because it follows the pile's own outline up over the crest and back down, tilted a little by
`Tilt` so the pile reads as lit from one side. It is measured against the pile that is actually
visible rather than against any one part of it, so every column gets its half.

**The grains are all one size.** Each is `GrainPixels` sand pixels square, every one the same,
and each is a single flat colour rather than speckled: at that size a grain with two shades in it
reads as a smudge rather than as a grain.

The art is laid out on a square of 64 units and drawn at whatever size is asked for, the whole
drawing scaled to it, so nothing in it is a pixel of any one texture. That is what lets the same
code serve two sizes an order of magnitude apart -- the shipped icon is 1024 pixels square and
the panel shows it at 52 units -- and the icon is not an upscale of anything: the pixels and the
speckle are the drawing's own.

The PNG is written by hand in that tool rather than through Unity, which has no `EncodeToPNG`
without the engine running and none of the compression available on the Mono Besiege ships loads
its native helper.

## Keeping sand small, and saving it with the level

`BedCodec` packs a chunk of bed for everything that keeps or moves one: the copy a run goes
back to, a level file, and the chunks the host sends. Heights are whole millimetres, each
stored as its difference from what its neighbours predict (the median edge predictor of
lossless image coders: exact on a slope and on a step), so smooth sand is nearly all zeros and
ones. Colours go into the chunk's own palette and each column's is an index of only as many
bits as the palette needs. The result goes through a small LZ compressor (`Lz`, written here
because System.IO.Compression is one of the namespaces the loader refuses), which folds the
runs and repeats. A 20 m mound of varied sand is 15 KB packed against 338 KB as columns
(`BedPressTest`), and a chunk of flat sand is a few dozen bytes.

In the level editor the sand is saved into the level file, as the level's own custom data
(`Events.OnLevelSave`, `Level.CustomData`): the bed packed. While a run is going, what is
saved is the sand the run will go back to, unless the run is being kept. Loading a level
(`Events.OnLevelLoaded`) puts its sand in place of what there was; a level with none of its own leaves the sand as it is. The
sand is the player's, like the machine, and stays when the level changes: a new scene drops only what
belonged to the old one -- the colliders the solver knew, the ground under the sand (cast again where the
sand now is), and the copy a run would go back to. Like the machine it is not in the menus: on the
title screen and the islands' level selects (by scene name, `MainMenu` and `LevelSelect...`, or
`StatMaster.isMainMenu`) nothing is simulated or drawn and its colliders are off, until a level or the
builder is back (`SandField.InMenu`). Besiege keeps a
mod's custom data in the level and hands it back on load; a game without this mod ignores
it, and the level loads with no sand.

The level editor puts its level back when a run starts or stops, and announces that as a
load. Taken at its word, that replaced a kept run's sand -- everything brushed or dug during
it -- with what the file held. A load within two seconds of a run starting or stopping, or
during one, is now left to the run.

## Sand files

The sand placed can be saved and loaded on its own, apart from any machine or level (`SandFiles`). A mod
cannot write a machine file -- `XmlSaver.Save` is on the loader's blacklist and `ModIO` will not reach
`SavedMachines` -- so the game writes it. The panel's **Save** opens Besiege's save screen and steers it
into `SavedMachines/Sand_mod` (`FileBrowserView.Open`, then the screen's own controller: `CreateFolder`,
`OpenFolder`); when the game then saves there, `XmlSaver.OnSave` -- which runs before anything is written
-- puts the sand into the file's machine data, packed as a level's is, and takes out every block but the
starting block, marking the file as saved without it. A sand file is a machine of one block carrying the
sand, with the game's own name, overwrite question and thumbnail.

The screen is opened as the game's own buttons open it, in the folder it last saved or loaded in, and
held in the sand folder for its first second and a half or until the player clicks in it: the screen
fills itself over a few frames, and with a machine loaded something in that took it back to the
machine's folder. The game remembers the folder of the last save or load (`SetRestoreFolder`, from
`OnFileSaved` and `OnFileLoaded`) and its own Save and Load reopen there, so after a sand file the folder
it would have opened before is given back: the screen is opened, taken there, told that is the folder to
come back to, and closed again in one frame. The thumbnail is of the sand: the game renders it with its
own camera right after writing the file, and just before that camera culls (`Camera.onPreCull`) it is
aimed down at the sand from above, sees the whole scene, and is handed the sand to draw; the game puts
the camera back itself.

The screen opens in the sand folder as the game's own buttons open in theirs: the game reopens where it
last saved or loaded, with that file selected and its name in the box (`restoreFolder`, `restoreObject`),
so the sand's buttons set those two -- the sand folder, and the last sand file -- and then open it as the
game's buttons do, all in one frame. The collection's root has to be opened (read from disk) before it is
looked through: `GetRoot` makes a new, empty folder object each time it is asked, and looked through as it
came it never held the sand folder, so the screen was only ever taken there the first time, when making the
folder opened it (opened to learn the folder to give back, opened to go to the sand
folder, pick the file and say "come back here", and opened for real). Steering it there after it had opened
did not hold. The last sand file's name, read from the screen's name box as it is saved or loaded, is kept
in the settings file, so it comes up in the box the next time, this session or the next. The thumbnail is taken a moment after the file is written, after the screen has closed, so the camera is
re-aimed whenever that is in the next five seconds (it was given up when the screen closed, and the picture
was of the starting block); the camera's bloom and glow are off for it. It is
framed to the sand: looking down on it at 55 degrees, from just far enough back that every corner of the
box round the columns with sand in them is inside the picture.

A sand file's sand is only taken when the player is loading from a load screen. The game reads machine
files for itself as well -- saving over a file first reads the old one to keep it as a version
(`MachineAutosaveController.VersionMachine`) -- and the old file's sand replaced the sand being saved: the
level lost its tracks the moment it was saved, and the file had none (its thumbnail did, the meshes not
being redrawn yet). The screen's memory of where to reopen is given back whenever a sand session ends,
saved or not, and the sand folder is not read from disk while the screen is already in it (it was, every
frame for a second and a half, and the screen took that long to fill).

Loading a sand file is not undone by the level: putting the machine back can make the level editor say the
level loaded, and taken at its word the level's own saved sand replaced the file's -- tracks and all gone.
A level load within three seconds of a sand file's is left alone.

**Load** opens the load screen in the same folder. Reading a sand file, from there or from anywhere
(`XmlLoader.OnLoad`, as the file is read), puts its sand in place of the sand there is. The game then
loads the file as a machine. "Add to machine" adds nothing, the file saying it has no starting block; a
plain load replaces the machine with the one block, and the machine as it was before (taken as the file
was read) is put back as soon as that load has finished (`Machine.IsLoadingMachine`). Only the panel's own
save, into that folder, makes a sand file; every other save is left alone. Neither works while a machine
is running, or on a multiplayer client.

## Multiplayer

The host simulates the sand, the way Besiege simulates a multiplayer level's physics there:
the grains, the bed, every machine's sprayers and what sand does to every
machine. Clients simulate none of it; they draw what the host sends (`SandNet`):

* **The bed**, a chunk at a time as chunks change (`SandBed.Encode`, which is `BedCodec`: a
  full chunk is a few hundred bytes, where it was 2.3 KB), at
  sixty chunks a second, so a big level reaches someone who has just joined over a few
  seconds rather than in one burst. A client that arrives says hello and is sent everything.
* **The moving grains**, five times a second: an origin, a palette, and per grain its offset,
  velocity, row and colour -- eleven bytes, at most three thousand grains a message. A client
  carries them along their velocities in between, so they do not jump.
* **The brush** on a client asks the host to pour or erase, and the host does, unless it has
  turned *Other players place sand* off -- a switch the panel shows only with other players
  in the game.
* **The host's switches** -- other players placing sand, loading sand, sprayers paused, the sand
  paused, changes kept, the sand limit -- when one changes, and to everyone when a player arrives
  (its hello); nothing on a clock. A client's own settings are never written with the host's. On a
  client the panel's level-wide controls show the host's and are greyed out and do not grow under
  the pointer: *Pause sprayers*, *Pause sand simulations*, *Sand limit*, *Keep changes to sand*,
  *Clear all sand*, *Other players place sand*, and *Load* unless the host allows it. *Save*
  stays: a client saves the sand it is shown.

A client in a local practice run of its own (`Player.InLocalSim`) is on its own and simulates
its own sand, as in single player.

## Doing nothing where nothing is happening

With no grains moving, nothing slumping and no machine near the sand, the bed only looks for
a machine arriving, five times a second; the scan for colliders runs four times less often;
and the full scan of the level's fixed colliders runs at the start of a run and once a minute
rather than every ten seconds. A grain more than three metres from every machine is taken
into the bed after half the usual wait: nothing there will disturb it, and it costs a solver
slot for as long as it is a grain.

## Media stay what they are

A bed column holds up to six media, one on another (`SandBed.MaxLayers`): the surface layer, the
one under it, and as many more below as the column has had laid on it (`Chunk.deep`, kept only for
columns that have any). Sand of the surface's medium thickens it; another medium lies on top as a layer
of its own and pushes the rest down; only a seventh pushes the lowest into the one above it. Slumping,
releasing, blasting and melting take from the top, each part as its own medium, and what a column was
cut down through is left with the layer it is in. Painted one after another over the same disc, five
media come out as five layers in the order they went on, each with all that was poured; painted round
and onto one another and slumped, each keeps its volume to 0.1%; the layers survive being packed for
the other players, a copy kept for a run, and being dug out (`BedLayerTest`). Chunks packed before there
were more than two layers read as they were: the extra layers are a trailer at the end. A column used
to hold one medium and take whichever there was more of, so wet sand poured on a dry pile quietly
turned dry, and the other way round. (A pile a brush grows in place and settles at the end is settled
as one medium, so a stroke in another one starts a new pile; what the settling cuts down is put back
as the stroke's medium, which is what it was except where a stroke's cut goes through an older layer.)

**Explosions throw the sand.** A bomb blows a bowl out of the bed -- a share of its radius across
(`SandField.CraterShare`), a third as deep as wide and never over two metres -- and throws what it
was: 40% of it as grains, as coarse as it takes to keep them under 1500 (`SandBed.Blast`), going
outward and up at the speed that lifts them one and a half blast radii (22 m/s for a bomb's five metres under Besiege's
gravity), times the square root of the power over a bomb's usual 20, faster the nearer they were; the rest is
laid as a rim in a ring round the hole, so that every medium is all there afterwards. Grains in the
air are knocked the same way. The game has no explosion event, but bombs (`ExplodeOnCollideBlock`,
`ControllableBomb`), level explosives (`ExplodeOnCollide`, `ExplodeOnCollideSmall`) and cannonballs
and small bombs (`CannonBallExplosion`, `SmallExplosion`) all have public `radius`, `power`, `upPower`
and `hasExploded`, so those are looked at twenty times a second for the blocks of the machine and five
for the rest of the scene, and each is noted once when it goes off (`SandField.ExplosionScan`).
`SandField.ExplosionStrength` scales how hard the sand is thrown. A medium the air holds back is thrown
harder, by 1 + drag x speed / (4 x gravity) (`SandField.BlastBoost`): dust's drag of 6 a second stopped a
grain thrown at a bomb's 22 m/s within three metres, so a bomb in dust threw almost none of it; now dust
goes out about twice as fast, rises some metres as a plume and drifts down, and sand and mud a few per
cent faster than before.

The effects every explosion starts (`ExplosionEffect`, `ExplosionEffectAdv`) are watched too: one that no
source above accounted for (a bomb of a kind with no such fields) is an explosion of a bomb's usual numbers
(radius 5, power 20), so nothing that goes off is missed. How far the blast is from the sand is taken in
three dimensions, so a bomb high over the ground digs nothing and one on a pile digs the pile.

**Explosion marks.** The dust and scorch an explosion lays on the ground is a flat quad
(`ExplosionEffect.dustCraterQuad`) a little above it. Under sand it is hidden by the sand's depth up close,
but the depth buffer cannot tell a quad a few centimetres under a surface from the surface at a distance,
and it showed through. A mark is now shown only where nothing is over it (the bed's surface there is not more
than 4 cm above it): in a crater a blast has dug, on bare ground, or on sand it was laid on
(`SandField.UpdateDecals`).

**Digging has its own room.** What a machine digs out went into the same room as what a sprayer makes,
which is the governor's ceiling (6000 grains at the start) less the grains there are, and the grains a
wheel throws lie about its own pile until the bed takes them back (they are not taken within 9 cm of a shape).
Once enough were lying there, a wheel found no room, and neither dug nor threw anything. Digging may now use
twice the ceiling, and never less than four thousand, of the array (`SandField.DigSink`). Any part of the
bed's step that throws is caught and logged, so one failure no longer stops the digging, the colliders and the slumping
at once and silently.

What does change a medium on purpose, each cheap and each costing nothing while it is not
happening:

* **Water.** A grain under the sea's surface takes its row's wet medium (sand becomes wet
  sand), as before. A water cannon that is firing wets the grains in a cone along its
  stream, twelve metres long, and the surface of the bed where the stream lands.
* **Fire.** A cannon's steam, a lit flamethrower's flame (seven metres) and any block that is
  burning dry wet sand back to sand, grains and bed surface alike.

The cannons, flamethrowers and burning blocks are found by the same rescans as the colliders.

## Water

Besiege's water is CodeAnimo *Surface Waves*, and it exposes exactly what is needed
without a single readback:

- `BuoyancyManager` is findable in the scene, and its `waveData` and `terrainData`
  are `SimulationOutput`s whose `outputData` is a live `RenderTexture`;
- `simPosition` is a `Dimensions` carrying `firstCorner`, `localSize`,
  `resolutionX`/`resolutionZ` — the mapping from world XZ to texel.

So the wave height texture is copied down once a sand step (`WaterLink.CacheForCpu`) and
sampled per grain. A grain below the surface gets buoyancy scaled by the density ratio,
heavy drag, and the wet flag.

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

The CPU solver's cost is per grain, and most grains in a level are at rest
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
* **Releasing.** For every shape on a rigidbody that digs (see *Only dug sand becomes
  grains* below), the sand in each column it reaches into -- from just below its lowest
  point to the top of the pile -- becomes grains again, on a lattice so they are not born
  overlapped, and those grains push the shape like any others. Bounded by a budget of
  distance tests per step, by 600 grains a step and by the field's free room.
* **Slumping.** A pile steeper than its material's angle of repose (the friction
  coefficient times 1.15, a little generous, as a slope over eight neighbours: dust about
  13 degrees, sand 30, gravel 46) sheds sand to the lower column. Only columns that changed are looked at, so a settled bed costs nothing.
* **Drawing.** A chunk's vertices, normals and colours are worked out when its columns change
  (`ChunkMesher`, on every core) and kept, and put together in tiles of four by four chunks,
  one mesh and one draw a tile; at least eight chunks and four tiles a frame, and more while
  3 ms last, its edge dipping just under the ground. Lit and opaque (`SandLook`:
  `Legacy Shaders/Diffuse`, else `Mobile/Diffuse`, else `Standard`), colour from a
  mipmapped texture per tile (17 texels a chunk) since neither reads vertex colours, and
  grains the same way from a palette texture with their normals up, so loose sand matches the
  pile. The log says which shader was found.
  Being opaque and lit gives the sand a shadow-caster pass, which is what puts it in the
  camera's depth texture: drawn unlit it was missing there, and Besiege's depth-based screen
  effects saw anything standing in sand against the floor below it -- a bright halo round
  the wheels and the machine seeming to show through. The particle shader before that did
  not write depth at all, so a heap's far slope was painted over its near one. With no lit
  shader the old unlit vertex-colour material is used.

Volume is conserved exactly through absorb, slump and release, and the tests check it:
4000 grains dropped on flat ground end as bed with the total volume unchanged to better
than a thousandth of a percent, and a sphere pushed into the finished pile wakes a patch
that settles back without losing or gaining any.

* **Solid to machines.** Near anything on a rigidbody, each chunk of bed has a static
  mesh collider of its surface (`SandBedColliders`), cooked a few milliseconds' worth a sand
  step, nearest a body first, and dropped again when nothing has been near it for two
  seconds. PhysX holds a machine up on it as on the ground. What PhysX pushes back with is
  reported by the collider (`BedContact`, `Collision.impulse`) and the bed sinks under it
  (`SandBed.Press`): a column under a pressure sinks towards pressure over 150 kPa/m and stays
  there, at most a fifth of its depth, half the sand it loses pushed out to the unloaded
  columns beside it as a lip. A wheel bearing 200 N leaves a rut of about a centimetre.
* **Only dug sand becomes grains.** A shape releases a column only where a surface
  *touching* the sand (within 3 cm) moves faster than 0.8 m/s -- a spinning wheel, a block
  landing -- or where the shape is more than 6 cm *inside* the sand, which a body standing on
  the collider never is; and the colliders report contacts on a steep face (normal under 60
  degrees from level) as digs, so a blade pushed into a wall of sand turns it to grains and
  shoves them. A wheel rolling without slipping has a still contact point and digs nothing.
  The first version of this rule counted any shape within 9 cm of a column below its top:
  on a slope the column above a wheel always passes beside it, and the front of a rolling
  wheel moves fast without touching, so a machine dug away the slope in front of it and
  ploughed through the pile instead of climbing it.

Before those two, the bed had no collider and released every column any shape touched. A
machine was held up only by the grains it woke, which it shoved aside, so it settled through
a pile to the floor and drove along the bottom with the sand standing round its wheels: no
ruts, and the machine visible inside the sand. `tools/tests/BedPressTest.cs` holds the rules.

What it does not do. Nothing about the colliders can be tested headlessly --
they are PhysX's -- so the pressing and digging are tested and the rest has to be seen in game.

## Making the CPU solver carry more

The CPU solver's cost is per
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
  of sand it makes does not change. This is the adaptive-sampling idea from
  the granular-simulation literature -- simulate a smaller set of particles accurately and
  get the volume from them -- done at emission time. The bed's volume per grain follows each
  row's own radius, so coarse and fine sand are interchangeable in it.

**The governor** (`SandGovernor.cs`) decides when to use them. Judging by step time reacts
only after the damage is done, which for a fast pour is thousands of grains too late, so it
measures instead: each step reports its time and its grain count, which gives a cost per
grain, and the budget over that is how many grains are affordable. That is the ceiling on
how many are admitted, set at once. When the live count fills the ceiling, quality is spent
in order of how little it shows -- fewer solver iterations, then coarser grains, then fewer
iterations again -- and returned, more slowly, when the count falls well below it.

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

## Stalls: when a run starts, and when a machine drives into sand

Both were work done all in one frame, and none of it was the solver:

* **Ground.** The level's floor was one raycast grid over the whole area the sand covered,
  rebuilt from scratch -- up to 9,216 level-wide raycasts -- every time sand left it, and again whenever a machine
  scattered sand past the edge. It is now kept in 4 m tiles of 0.25 m samples, each cast once,
  the first time sand needs it (`GroundField`). The tiles under grains are cast at once; the
  rest, at most 3 ms of raycasts a fixed update.
* **The bed.** It gave back as many grains as the field had room for in one step -- thousands,
  when a machine drove onto a deep pile -- and now gives back at most 600 a step; slumping
  checks 300 columns a step instead of 1,500. The bed's distance tests no longer call into
  the engine for a quaternion inverse.
* **Colliders.** The solver's feed and the bed's each scanned the scene every 10 sand steps;
  they now share one scan, every 25.

* **Level geometry woke the bed.** The one the log found, once it existed: `[Sand] slow
  fixed update 650 ms: bed 650` on a pile of under two hundred columns. Besiege's levels are
  built of box colliders, floors included, and the bed counted a floor box as a shape
  touching the sand. Every step it handed the pile lying on the box back as grains, refused
  to take them back while they lay beside it, and tested each grain against every collider of
  the level. The live grains then filled the field's room, so sprayers stopped spraying. The
  bed now considers only shapes on a rigidbody (`BedShapes`); fixed geometry is its floor
  already, by the ground raycasts.

* **On a big level** (the log: 150,000 bed columns) three more. The bed was drawn a chunk a
  draw, six hundred draws and 25 ms a frame; it is drawn in tiles of four by four chunks now.
  The collider manager looked each candidate chunk up in a list, for every one of a machine's
  hundreds of shapes, 26 ms a step; a stamp on the chunk does it now. And the scan for
  colliders, every second, looked at every collider in the level, 26 ms; the full scan runs at
  the start of a run and every ten seconds, and the ones between look only at rigidbodies,
  which is all that comes and goes. The bed's feed keeps only those as well.
* **Walls of sand were cliffs.** A steep wall slumped at the bed's ordinary pace, a few
  hundred columns a step: for seconds a machine meeting one hit a vertical face of sand, which a
  side contact took as a blade pushed into it, and dug through. Slumping now runs every
  fixed update for as long as there is a backlog, harder the bigger it is (up to 2 ms; it was
  10, which with a big backlog was all of a fixed update).
  And a side contact only digs for a real blow or shove -- over 0.5 m/s into the face, or 300 N (`SandBed.SideDigSpeed`, `SideDigForce`; they were 1 m/s and 3 kN).
* **Wheels sank through brushed piles.** Slumping refilled the rut under a wheel standing in
  it, which buried the wheel, and the bed then dug a buried wheel out as grains -- so a machine
  slowly went through a pile it should have stood on. Columns under and around a load (five by
  five, for ten steps after the last press) are now held: they do not slump, and being buried
  in them is not a reason to dig. The dig speeds went up with it (sand 0.8 m/s, dust 0.4, snow
  0.6, mud 0.7), so only a wheel actually spinning in the medium digs it.
* **Mud splashed sand-coloured.** Grains are drawn through a palette texture of their colours,
  and it held 1024 of them and was never emptied: sand that mixes blends its colours, every
  blend is a new one, and once it was full every new colour was drawn as the first ever added
  -- sand's. It now holds 4096, keeps colours to five bits a channel, and empties at the start
  of a frame once half full (the grains' mesh is rebuilt every frame).
* **Dug mud stood in pillars.** A dug column is given back as a stack of grains in its own
  footprint, and the stack stood: grains here slide but cannot roll, so friction held one
  balanced on another however it was nudged, and mud's cohesion bound the stack into a block.
  Three changes, measured on a 60 cm stack (a scratch bench; grains still above 25 cm after
  1.5 s, of 90): friction now falls away at contacts within about 30 degrees of vertical, where
  a real grain would roll off, and is full at the slant of grains nested in a heap
  (`CpuSolver.RollSin2`); a grain's cohesion fades in over its first second (`Knit`), so a dug
  stack falls in before it sticks; and every grain given back starts with a little sideways
  speed, more the higher it is, as a wall falling in. Mud went from 53 grains standing to 12,
  dry sand from 44 to none; heaps still stand at their angles.
* **Wheels went through brushed piles.** The ground raycasts took the highest
  static or kinematic collider under them as the floor, and a machine being built is
  kinematic: brushing near it measured the machine's top as the ground and set the floor's
  layer to the machine's -- "OnlyCollideSelf", which the running machine does not collide
  with. The bed's colliders go on the floor's layer, so a brushed pile was one wheels drove
  through. The raycasts now skip every block, and the layer the colliders go on is checked
  against the machine's at the start of every run (`SandField.SolidLayer`): one it does not
  collide with is replaced by one it does, and colliders already made are moved to it.
* **Machines went into piles instead of up them.** Three things together. A wheel that
  found itself inside the sand -- through a surface between two physics steps, or because the
  surface had just risen around it -- was dug out as grains, and grains cannot hold a machine
  up, so it sank on through. Now a body inside the sand without sliding pushes it aside, to
  the first column outward that it is clear of, as a wheel settling into sand does; only
  slip or a hard landing digs. The bed's colliders are surfaces with nothing behind them, so
  every body over the sand is given continuous collision detection against static colliders,
  once, and cannot pass through one in a step. And the sand a pressed rut pushes aside went to
  the next columns over, still under the tyre, which rose into it: it now goes past the
  footprint, three columns out, as a lip beside the rut.
* **Machines sank as they drove.** Digging was decided by how fast any surface touching the
  sand moved, and a rolling wheel's rim meets the front of its rut at a good fraction of the
  machine's speed -- moving into the sand, pressing it, not along it. So every rut was dug
  deeper the faster the machine went, until it had sunk. Digging is now slip: how fast the
  shape's underside slides over the ground, which is nothing for a wheel rolling at any speed
  and all of it for a wheel spinning in place or a block dragged along; or a landing faster
  than 6 m/s. And only the part of a contact's impulse that pushes into the sand bears on it:
  the impulse included the friction that drives a powered wheel along, which pressed ruts as
  though the machine were many times heavier than it was.
* **Dust was seventy times the grains.** Its grain was 8 mm across, so a scoop of dust was
  about seventy times as many grains as the same scoop of sand: 0.125 m^3 was 24389 grains and
  95 ms a solver step, against sand's 343 and 2.3 ms. A dust grain is now a 25 mm clump --
  729 grains, 6.7 ms -- and what makes it dust is what it always was: light, slippery and slow
  to fall.
* **Patches of bare ground while brushing.** The bed's queue of chunks to redraw, and the
  renderer's queue of tiles, were served newest first at a fixed few a frame. While the brush
  was held the chunks under it went back on every frame and always went first, so a chunk that
  had just been given sand waited -- drawn as bare ground -- until the button was let go. Both
  are oldest first now, and rebuild for up to 3 ms a frame past the minimum.
* **Garbage.** Packing a chunk allocated about forty kilobytes of scratch (a 32 KB hash table
  among it), and after every run started hundreds of chunks were packed: enough garbage to
  bring on Mono's collector, whose pauses of tens of milliseconds landed in whatever the sand
  was doing -- a solver slice of 39 grains taking 22 ms. The codec's buffers are kept now. The
  full scan at the start of a run also walked the scene five times; it walks it once, takes
  the loose bodies from the colliders it found, and reads the rest from the machines.
* **A machine on sand slid on as though on ice.** A bed collider was recooked whenever any
  column of its chunk changed, and under a wheel pressing soft sand that was every step; a
  recooked collider loses the contacts PhysX was holding against it, its static friction with
  them. Now a collider is recooked only once its surface has moved a centimetre, or two
  millimetres and half a second (`SandBedColliders.CookTolerance`) -- less cooking as well.
* **Sticky grains shook and pushed light blocks.** On a scratch bench, a free 2 kg block under
  glue was pushed 58 m in 300 steps, and with a block held still the glue pushed it sideways
  with 87 N -- grains that should have weighed on it and no more. Three things: the pull that
  draws a sticky grain onto a face was handed back to the block as a force, and with the
  overlap pushing the grain off again the next substep, capped separately, they did not cancel;
  a grain only near a face was given the face's velocity for nothing, and passed it on through
  the grains round it; and glue reached for faces from so far off that whole layers counted as
  stuck. Now only a grain touching a face rides with it, the pull is not handed back, and a
  sticky grain's force on a block is the part of its weight pressing into the face plus a drag
  along it no larger than friction under its weight; glue reaches out at most 1.2 grain radii.
  The block held still now feels under 12 N sideways at any stickiness. Free, mud-sticky sand
  moves it 2 m where it moved 15; glue 21 m where it moved 58, the rest being the solver's own
  noise under 80 times the block's weight of glue. Glue carried round a spinning wheel fell from
  349 grains to 157 with it.
* **Big machines starved the bed of attention.** Releasing sand under shapes has a budget of
  distance tests a step, spent shape by shape from the top of the list: on a machine of hundreds
  of parts it ran out before the end, every step, and the parts at the end -- a plough's blade,
  say -- never dug, ploughed or picked anything up. Each step now starts where the last one
  stopped, columns are sampled from the shape's bottom rather than the sand's, and the budget is
  twice what it was. And ploughing asked where the blade met the column, which for a layer
  thinner than the blade was its foot, so a thin layer was never ploughed: it asks whether the
  sand stands above the blade's underside.
* **A 30 ms stall on every rescan.** The rescan between full ones still walked the whole
  scene, four times over: for the rigidbodies, the water cannons, the flamethrowers and the
  fire tags (every block has one). On a built level that was 30 ms each rescan, the largest
  thing in every `[Sand] slow` line. The machines' own block lists (`Machine.SimulationBlocks`,
  each player's in multiplayer) give all four without walking anything; bodies that are not
  blocks come from the full scan at the start of a run.

The in-game timing report (F9) that measured all this has been taken out of the released mod;
`./tools/test-bed.sh --bench` measures the simulation's share headless.

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
  or two. Scaled up threefold (`FrictionBoost`; it was lowered to 1.5 when the contacts were reworked, below), a slow pour settled at about 17 degrees
  against 9 before; much more saturates, because friction is then limited by the geometry of
  spheres, which roll, and starts to make a pile creep. Real sand is nearer 30; piles that
  rest in the bed are slumped to their angle of repose (34 for sand then; sand's friction is lower now, and it is 30).
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
  moment, at any friction. That is the compression wave described above and is why settled sand
  is kept as bed. It remains for the fallback paths and for anything that wakes a deep pile; the
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
  off: a block laid out in columns stood two seconds with vertical walls. The sand the bed
  gives back now shifts every other layer half a step into the pockets
  of the one below; the block slumps at once into a heap.
* **Fast sand went through things.** The floor and the shapes pushed a grain out by at most
  a radius a pass, so sand landing at 15 m/s sank a quarter of a metre into the ground, and
  sand falling at 12 m/s passed through a 2 cm plate, being further past it in one substep
  than it is thick. The floor now pushes all the way out; for a shape, a grain that moved
  more than its radius relative to it is walked back along its path and stopped where it
  first touched.
* **Restitution was applied on every solve**, three bounces a contact; it is once a substep.

## Why there is no GPU solver

A GPU solver was begun: `Sand.compute`, a compute-shader port of the grain solver as it was
before the bed, the floor and the later contact fixes. It was never run, and it was removed
(it is in the history, before the commit that removed it).

A `.compute` has to be compiled per graphics API (GLSL through HLSLcc for OpenGL, DXBC from
`fxc` for Direct3D 11) and serialised into a `ComputeShader` object whose layout is the
editor's, so it takes an asset bundle built by a licensed Unity 5.4.0f3 editor; Unity 5.4
will not run, batch mode included, until it has been activated with an account. There is
nothing in the game to copy: of Besiege's 202 data files not one holds a `ComputeShader`
(counted; see FINDINGS.md). `Material(string)`, a shader from source at run time, stopped
working in 5.4, and calling OpenGL directly is out, `System.Runtime.InteropServices` being on
the loader's blacklist. So the cores are what there is to spend: the solver, the bed's meshes
and the brush's columns all run on every core (`Workers`).
