# Project Eden — Feature Guide

A mod that scales the late game of Dyson Sphere Program up across the board. Every number lives in
`ProjectEden/data/*.json` and can be edited; rebuild after changing one, or **drop a file of the same name into
`BepInEx\config\ProjectEden\` in your profile to override it without rebuilding at all** (see section XIV).

It also ships six cheat switches, **all on by default** (instant build / build without condition / no build collision /
collider pool off /
no power spacing / pump anywhere), likewise in section XIV. Alloy ammo's "one product, different raw materials" technique is section XV.

> Section XI is the longest: it carries the whole chemistry chain end to end — C1, the nitrogen
> chain, three phases of organic chemistry, and the refining line that cuts a barrel of crude into
> four fractions. The contents give it four second-level entries; every other section is flat.

**Everything this mod adds is fully translated into English** — just switch language in game. See section XVI.

> Game version 0.10.34.28529 ／ BepInEx 5.4.17 ／ requires LDBTool, CommonAPI, DSPModSave

*This document is the English version of `mod特性.md`. Both are kept in step; if they ever disagree, the Chinese one is the original.*

<!-- toc -->
**Contents**

- [I. Mega Structures](#i-mega-structures)
- [II. Advanced Mining Machine](#ii-advanced-mining-machine)
- [III. Water Pumps and Oil Extractors](#iii-water-pumps-and-oil-extractors)
- [IV. Logistics](#iv-logistics)
- [V. Orbital Collectors](#v-orbital-collectors)
- [VI. Matrix Labs](#vi-matrix-labs)
- [VII. Power](#vii-power)
- [VIII. Extra Recipes](#viii-extra-recipes)
- [IX. Custom Ores and Gases](#ix-custom-ores-and-gases)
- [X. New Buildings](#x-new-buildings)
- [XI. The Chemistry Chain: C1, nitrogen, organics and refining](#xi-the-chemistry-chain-c1-nitrogen-organics-and-refining)
  - [Organic chemistry, phase one: a real destination for formaldehyde and carbon dioxide](#organic-chemistry-phase-one-a-real-destination-for-formaldehyde-and-carbon-dioxide)
  - [Organic chemistry, phase two: propylene + ammonia → a third route to carbon nanotubes](#organic-chemistry-phase-two-propylene--ammonia--a-third-route-to-carbon-nanotubes)
  - [Organic chemistry, phase three: the aromatic trunk, cut down to the cumene process alone](#organic-chemistry-phase-three-the-aromatic-trunk-cut-down-to-the-cumene-process-alone)
  - [Linking up with petroleum chemistry: three refinery units](#linking-up-with-petroleum-chemistry-three-refinery-units)
- [XII. The standard for new items: follow real chemistry and physics](#xii-the-standard-for-new-items-follow-real-chemistry-and-physics)
- [XIII. Interface Changes](#xiii-interface-changes)
- [XIV. Cheat Switches (all on by default)](#xiv-cheat-switches-all-on-by-default)
- [XV. Alloy Ammo: one product, different raw materials](#xv-alloy-ammo-one-product-different-raw-materials)
- [XVI. English Localization](#xvi-english-localization)
- [XVII. Known Trade-offs](#xvii-known-trade-offs)
- [XVIII. Compatibility](#xviii-compatibility)
- [XIX. The Biodome: a light-bound biological chain](#xix-the-biodome-a-light-bound-biological-chain)
- [XX. Living Composite: hyphae growing metal into a solid](#xx-living-composite-hyphae-growing-metal-into-a-solid)
- [XXI. Combustible Liquid Power Plant: what you burn decides how much you get out](#xxi-combustible-liquid-power-plant-what-you-burn-decides-how-much-you-get-out)
- [XXII. Living Proliferators: two tiers above vanilla, each in two characters](#xxii-living-proliferators-two-tiers-above-vanilla-each-in-two-characters)
- [XXIII. Alien Veins: mining them consumes drill bits](#xxiii-alien-veins-mining-them-consumes-drill-bits)
- [XXIV. Silicon Carbide: what moissanite is for, and it buys throughput](#xxiv-silicon-carbide-what-moissanite-is-for-and-it-buys-throughput)
- [XXV. Bio Matrix: the seventh matrix, and it is grown](#xxv-bio-matrix-the-seventh-matrix-and-it-is-grown)
- [XXVI. Magma: putting a water pump on a lava planet](#xxvi-magma-putting-a-water-pump-on-a-lava-planet)
- [XXVII. Catalytic Reactor: a factory that remembers its own state](#xxvii-catalytic-reactor-a-factory-that-remembers-its-own-state)
- [XXVIII. The Integrated Chemical Plant: the first machine here that eats several recipe types](#xxviii-the-integrated-chemical-plant-the-first-machine-here-that-eats-several-recipe-types)
- [Config Quick Reference](#config-quick-reference)

> Each section stands on its own — no need to read in order. For config file names, jump to the last section.
<!-- /toc -->
---

## I. Mega Structures

A new **"Mega Structures" tab** (category 12) appears in the build bar, holding eight 10000x facilities:

| Building | Recipe type | Working power |
|---|---|---|
| Heavenworks Assembler | Assemble | 22.5 MW |
| Foundry Smelter | Smelt | 22.5 MW |
| Calciner Chemical Plant | Chemical | 22.5 MW |
| Forgeworks Fabricator | Assemble | 22.5 MW |
| Deep Probe Collider | Particle | 45 MW |
| Biodome | **Bioculture** (this mod only) | 18 MW |

- **No prerequisite tech.** The recipe is 1 Iron Ingot + 1 Copper Ingot, hand-crafted in 1 second
- **Belts connect directly**: 12 ports, no sorters needed
- **Built-in planetary logistics station**: 80 drones, 100% delivery rate, 3.6 GJ of storage kept full. It fetches
  materials from logistics stations and advanced mining machines by itself, and ships products out by itself
- **30 storage slots**, 10,000,000 per slot

The first five borrow **vanilla recipe types**, so a vanilla machine can run those recipes too — the mega versions are
merely much faster. The Biodome is different: its recipe type is this mod's own number 11 (Bioculture), and its three
recipes **can be run by nothing else**. See section XIX.

There is a seventh thing on this tab (slot 7) — the **Wind Turbine Cluster** — which is not an assembler. See section X.

### About "10000x"

The engine's hard ceiling is **one recipe cycle settled per tick**, i.e. 60 cycles/second; past a certain point,
raising speed buys nothing at all. So the mega buildings use multi-cycle settlement: each tick they run vanilla's
own settlement logic several times over. Every pass is complete vanilla logic, so **nothing is conjured out of
nothing** — they still stall when material runs out.

### How to feed them (important)

At full speed a mega building eats **3600 recipes' worth of input per second**. The two feed paths differ wildly in
what they can actually deliver:

| Path | Delivered per tick | Enough? |
|---|---|---|
| **Built-in logistics station** | Tops the internal buffer up to 200 recipes | **Yes** (consumption is 60/tick) |
| Direct belt connection | One stack per input port — at most 50 items with 50-level stacking | No |

**So feed them with logistics stations.** The building's own 80 drones fetch from logistics stations and advanced
mining machines automatically, and that path keeps up on its own. Direct belts are better suited to "patch in a
nearby production line"; do not expect them to fill a 10000x machine.

If a mega building seems slow, check whether it is fed only by belts.

### Virtual logistics (saves rendering)

Goods move **directly** between a mega building's storage slots and the other logistics stations on the planet —
**no drone actually takes off**.

At full speed the stations dispatch continuously to keep up, and a handful of mega buildings means hundreds of
drones in the air at once. With virtual transport, supply and demand settle within the same tick, so stations no
longer need to dispatch and the drones stay parked and unrendered.

It works in **both directions**: inbound only would still leave other stations sending *their* drones to collect the
mega building's products, and the sky would be just as busy.

> The cost: transfers are instantaneous, with no delay for distance. If you would rather not have that, turn
> `virtualLogistics` off in `megabuildings.json` and it reverts to vanilla drone behaviour.

> **Proliferator points travel with the goods.** A station slot records the *total* points for the whole slot, so
> moving part of the goods has to move a proportional share of the points with them.
> An early version moved the count but not the points — the source was left holding the whole slot's points on
> fewer items (free proliferator on every transfer), while the receiving end got unsprayed goods.
> Both sides balance now.

---

## II. Advanced Mining Machine

### Ore is smelted on the spot

| Ore mined | Actual output |
|---|---|
| Copper Ore | Copper Ingot |
| Silicon Ore | High-Purity Silicon |
| Titanium Ore | Titanium Ingot |
| Fire Ice | Graphene |
| Fractal Silicon | Crystal Silicon |
| Optical Grating Crystal | Carbon Nanotube |
| **Iron Ore** | **Iron Ore** (left alone) |
| Stone | Stone (it splits into Stone Brick or Glass, so no mapping) |
| **Coal** | **Coal** (it is both a fuel and the feedstock for graphite, matrices and the whole chemistry chain) |

> For the three rare ores the config records **only the vein type, never an item ID**: the ore is looked up from
> `VeinProto.MiningItem` at runtime, and the product is derived from the one vanilla recipe that takes just that ore.
> Rare-ore item IDs are easy to get wrong; this way they cannot be. A failed derivation logs a WARNING rather than
> silently doing nothing.

### Numbers

- **Mining speed maxed**: the panel reads 100100%
- **Veins are not consumed**: resource utilisation counts as fully researched, so reserves never drop
- **Internal buffer 10,000,000** (vanilla 50)
- **Station storage 10,000,000** (vanilla 5000)
- **Power fixed at 22.5 MW**

> Vanilla's power formula is `speedDamper × speed² / 1e8`, which at maxed speed produces absurdities like 30 TW.
> It is pinned to a fixed value here instead of scaling with the square of speed.

### Build restrictions lifted

- **Multiple miners can be stacked on the same spot.** What actually blocks you in vanilla is the collision check
  (`EBuildCondition.Collide`), not a spacing rule. The game's own data structures natively support
  **up to 4 miners per vein**
- **They can be placed on oil seeps** and will mine crude oil
- The spacing rule between a miner and other logistics stations is lifted as well

### Statistics panels

A miner whose product has been remapped still shows up in **reference rate** and **theoretical capacity** — vanilla
matches both of those against the vein's original product, so Copper Ingot never matched and the miner vanished from
the statistics entirely.

---

## III. Water Pumps and Oil Extractors

Both share the same speed-up logic as the advanced mining machine:

- Maxed collection speed, no reserve consumption, 10,000,000 internal buffer
- The **stack level onto belts follows the stacking techs** (vanilla hardcodes 4)

> An oil extractor's output multiplier is "well reserve × global rate" rather than a vein count, which makes
> zero-consumption especially important for it: the reserve is simultaneously the yield multiplier and the thing
> mining eats, so without it the rate decays as you extract.

---

## IV. Logistics

### Planetary / Interstellar Logistics Stations

- **30 storage slots** (vanilla 4 / 5), with paging and a scrollbar added to the panel
- **10,000,000 per slot**
- **Interstellar station max charging power 30 GW** (vanilla 0.06 GW)

> The charging slider's range on the panel is derived from that value: minimum half of it, maximum five times it, so
> it now drags between **15 and 150 GW** — and the setting sticks. The mod raises a station only once, on load, and
> only if it is still sitting at vanilla's value; after that it never interferes.

### Carry capacity and stacking

- Drones carry **10,000** per trip
- Vessels carry **1,000,000** per trip
- **Stack level 5000** (logistics tower stacking and the automatic piler follow)
- **Sorter stacking counts *stacks per swing*, not items** — setting 5000 means "as high as allowed"; the effective
  value is derived. Items carried per swing are bounded by `itemCount` (Int16), i.e. 8191, and one "stack" is a whole
  belt pile. At belt stacking 5000 that works out to **1 stack**, which is already 5000 items per swing. A larger
  number is not faster; it wraps the counter negative and items vanish

> ⚠️ **Set this back to 63 if you want full-tier proliferator.** The proliferator points of a stack on a belt are
> kept in a field that only holds 0–255, and it records the **total for the whole stack**. Proliferator Mk.III is
> 4 points per item, so 63 levels is exactly 252 and the top tier survives intact; 64 levels is 256, which a single
> byte cannot hold. 255 levels would need 1020, which it certainly cannot hold.
>
> **That limit has been lifted.** This mod ships a preloader that widens the field from one byte to two before the
> game's assemblies are loaded, so 255 levels x 4 points = 1020 now fits: **255-level stacking and full-tier
> proliferator can be used together**, no trade-off.
>
> The stack field was widened too, so the **theoretical ceiling rose from 255 to 8191 levels** (proliferator points
> become the limit again: 8191 x 4 points = 32764, which just fills two bytes). **The config still ships 255** —
> The config now ships **5000 levels**, i.e. 5000 x 120/s = 600,000 items/s per lane. To go higher, raise
> `stationPilerLevel` in `stations.json`; the ceiling is 8191. Cargo on belts looks exactly the same: the game's cargo shader was measured and never reads this
> value at all.
>
> WARNING: **your saves now depend on this mod.** Saves made before installing it still load (they are read in the
> old format automatically), but a save written afterwards **cannot be opened without the mod**. Back up your save
> first if that matters to you. If the preloader ever declines to run (say, after a game update), the mod still
> works and says so in the log, and proliferator points fall back to a clamped-at-255 downgrade.

### Belt speed

All three belt tiers are raised to **4x** vanilla, configured in `belts.json`:

| Belt | Vanilla | Now |
|---|---|---|
| Conveyor Belt MK.I | 6/s | **24/s** |
| Conveyor Belt MK.II | 12/s | **48/s** |
| Conveyor Belt MK.III | 30/s | **120/s** |

The 1:2:5 ratio between tiers is kept. Together with 255-level stacking the theoretical ceiling of a single lane in
one direction is **5000 × 120 = 600,000 items/second**.

Stacking is "how much per trip", belt speed is "how often" — throughput is the product of the two, and unlike
stacking, belt speed has no byte-width wall in its way.

> **Belts that are already built get updated too.** Speed is baked into the save, and **stored twice**: once on the
> belt component (what the panel shows and sorters read) and once in the cargo path's segments (**what the cargo
> actually moves at**). Changing only the first gives you "the number went up but nothing moves faster". Both are
> fixed on load.

> ⚠️ **Going past vanilla speed needs a patch to backstop it.** When vanilla moves cargo it runs a loop that counts
> backwards from the current position looking for free slots, **one step per unit of speed, with no lower bound on
> the array index**. Vanilla's MK.III only walks back 5 slots, and the margin before a path's start covers that;
> at 20 it walks off the head of the array on short belts and the game throws outright — after ten-odd minutes,
> when some stack happens to reach that exact spot.
>
> This mod adds the missing bound check there (out of range reports "occupied", which is precisely what
> "there are no more free slots ahead" means — it supplies vanilla's missing termination condition). The startup log
> prints `传送带回扫越界保护已生效` (belt back-scan bound guard active) — **without that line, do not raise belt speed**.
>
> Only the site that actually crashed in testing is fixed. Pushing past 120 still means testing it yourself: raise
> one tier, run for ten-odd minutes, include short belts, and watch the log for IndexOutOfRange.

### One thing deliberately left alone: dispatch cadence

What makes interstellar shipping slow is **neither vessel speed nor carry capacity — it is dispatch frequency**. One
"dispatch evaluation" launches at most one vessel, and a station at default priority **is evaluated only once per
second**.

**You can fix that in game**: give the *fetching* station an interstellar route priority and its supply/demand pairs
move into the high-frequency band, from 1 evaluation per second to 6 — **12x**, with no code change at all. So this
mod does not touch it.

---

## V. Orbital Collectors

- **Collection speed maxed**: roughly 600 million/second (one slot filled per tick)
- **10,000,000 per storage slot**
- The slot count follows the planet's gas species **on demand**: this mod adds nitrogen to gas giants, and at
  registration it raises the collector's slot count to exactly what is needed (see section IX). Without that, a new
  gas gets no slot and can never be collected

---

## VI. Matrix Labs

Only **production mode** (the side that makes matrices) is changed. **Research speed is untouched.**

- **Matrix production 10000x** (covers both the Matrix Lab and the Self-evolution Lab)
- **10,000,000 each for input and output slots**
- **250,000 of each matrix in research mode** (vanilla 10)

### Automatic supply and shipping

Labs and the planet's logistics stations are **wired both ways**, with no belts or sorters needed:

- **Fetching**: production mode fetches recipe inputs, research mode fetches matrices. It only takes from slots
  marked **Supply**; slots someone marked "Demand" are left alone
- **Shipping**: matrices made in production mode are **delivered into logistics stations automatically**. It only
  fills slots marked **Demand** — those are the ones where you explicitly said "put this here", symmetrical with
  fetching only from Supply

> So you need a slot on the station for that matrix, set to **local Demand**, before labs will ship into it. To
> export the matrices, use the vanilla idiom: local Demand + remote Supply. With no Demand slot at all the matrices
> pile up inside the lab and production stops at 10,000,000 — exactly as it would with no belt attached in vanilla.

Stacking needs no thought either: every level sends and receives directly, without relaying up from the bottom.
Research mode has no products and takes no part in shipping.

> If you want sorters to be able to take from labs as well, set `outputReserveItems` in `lab.json` to a few hundred —
> that many items are held back in each output slot, so virtual shipping and physical extraction can coexist.

> This is *virtual* supply: **you will not see drones flying**. Labs were not given a real logistics station because
> labs stack — every level would grow its own station and its own fleet, and it would only affect newly built labs,
> leaving existing saves to be rebuilt by hand. Virtual supply works on labs that already exist, immediately.

> Research-mode matrix storage **cannot hold 10,000,000**: `matrixServed` is a 32-bit integer holding
> "count × 3600", and 10 million overflows it 16 times over. 250,000 is the safe ceiling with margin.
>
> Incidentally, the "research speed" field in the building's properties is **purely decorative** in this game —
> no logic reads it. Actual research throughput comes from the research-speed techs.

---

## VII. Power

**Satellite Substations cover the whole planet** (2000 m, enough for any buildable world).

The **connection distance between nodes is untouched** — lifting that too would make every node connect to every
other, and the wiring computation grows quadratically, which is a real frame-rate cost once you have many nodes.
Power coverage itself does not need it.

Substations that are already built have their coverage rebuilt on load, and consumers reconnect.

---

## VIII. Extra Recipes

**Crude Oil X-Ray Cracking** (Chemical): identical in content to plasma refining — Crude Oil ×2 → Refined Oil ×2 +
Hydrogen ×1.

It exists so the mega chemical plant can make refined oil: the game's recipe picker filters on a **single recipe
type** and a machine only ever accepts one, so chemical facilities could not do a refining job.

---

## IX. Custom Ores and Gases

**Eight** new ore chains. **Every vein asset is reused from iron and recoloured at runtime**, so no art assets are needed:

| Ore | Placement | Yields | Processing |
|---|---|---|---|
| **Cobalt** (vein 15) | Rare slot; lava / volcanic ash; never in the home system | Cobalt Ore | Five reduction routes, below |
| **Aluminium** (vein 16) | Regular vein spot | Alumina Ore | Three routes, below |
| **Gypsum** (vein 17) | Regular vein spot | Gypsum Ore | No ingot; goes straight to a chemical plant for sulfuric acid, below |
| **Lithium** (vein 18) | Regular vein spot | Spodumene | Three-step extraction chain, below |
| **Manganese** (vein 19) | Regular vein spot, desert themes | Pyrolusite | Ethylene carbothermic reduction |
| **Chromium** (vein 20) | Regular vein spot, lava / volcanic ash | Chromite | Ethylene carbothermic reduction |
| **Vanadium** (vein 21) | Rare slot, very low chance, never in the home system | V-Ti Magnetite | **Aluminothermic** (carbon cannot touch it), plus a synthetic fallback via residue extraction |
| **Tungsten** (vein 22) | Rare slot, never in the home system | Scheelite | **Three-step chain** ending in tungsten carbide, below |

> **There are two placement modes.** A "regular vein spot" adds another vein to the planet and is laid down by
> density, just like iron and copper. A "rare slot" uses vanilla's kimberlite mechanic — a whole planet either has
> it or does not, and you have to go looking across systems. Cobalt, vanadium and tungsten are all the latter.

Appearance comes from three sources — **vanilla assets recoloured at runtime**, **hand-drawn icons**, and the
mega buildings' **code-generated 3D meshes and textures**.

> **This repository contains no third-party art**: all 41 icons are drawn from scratch, and the buildings'
> geometry and textures are generated procedurally at runtime.

| | Source | Look |
|---|---|---|
| Veins on the ground | Iron vein model, recoloured | Same mesh and texture, material tinted by `veinTint`: cobalt deep blue, aluminium cold white, gypsum warm cream, lithium pale violet |
| Ore icons | Iron ore icon, recoloured | Cobalt hue 226° / sat ×1.3; aluminium saturation crushed to ×0.12 (near cold white); gypsum hue 42° / sat ×0.38; spodumene hue 285° / sat ×0.5, value ×1.15 |
| The small icon on vein labels | Iron vein icon, recoloured | Same palette as each ore (it tints the *vein* icon, not the ore icon) |
| Cobalt Ingot icon | Iron Ingot icon, recoloured | Same as cobalt ore |
| New building icons | Each source building's icon, recoloured | Electrochemical Plant cold blue, Redox Chemical Plant violet, Integrated Logistics Hub amber, lithium accumulator and exchanger purple, Wind Turbine Cluster teal |

**41 icons are drawn from scratch** (`tools/make_icons.py`, `drawsvg` → SVG → `resvg` → PNG):

| Category | Icons | How they are drawn |
|---|---|---|
| Metal ingots | Aluminium, lithium, cobalt, manganese, chromium, vanadium, tungsten, tungsten carbide | An isometric ingot, with top/left/right value steps carrying the volume. **The palette is chosen so the icons stay apart at 80px, not to match a textbook**: manganese is pink-grey rather than purple (lavender is already lithium's), vanadium is cold grey so blue stays cobalt's, chromium is pushed to the brightest mirror white, tungsten is the darkest in the set (it is pressed powder metallurgy and really is dull), and tungsten carbide is near-black with one very narrow highlight — "black plus a sharp specular" is the visual signature of a hard material |
| Ores | Cobalt ore, pyrolusite, chromite, V-Ti magnetite, scheelite | Three chunks piled up. Three things make them read as ore rather than gemstone: **each vertex is jittered** (a regular polyhedron always looks cut no matter how you tune it), **each chunk is rotated independently** (same orientation reads as an array, not a pile), and **the front chunk overlaps the back**. Scheelite is the only pale ore, so value alone separates it from the other four |
| Alloys | Manganese steel, stainless steel, chrome-plated copper, chrome-vanadium tool steel, cobalt-chrome, vanadium-titanium | Same ingot shape as the pure metals, but with an **inlay stripe** across the top face in the colour of the secondary constituent — you can see at a glance that something has been added, and the stripe colour tells you what (chrome-plated copper's copper body plus silver stripe is the plainest). Chrome-vanadium tool steel is ternary and gets **two** stripes |
| Alloys (cont.) | Cemented carbide | Same shape as the pure metals, near-black with one very narrow highlight — the visual signature of a hard material. **No pips on the top face**: grade tiers were removed and there is one item per alloy (the pip-stamping code is still in `make_icons.py` in case tiers ever return) |
| Powders | Sulfur powder, tungsten trioxide | A few thousand grains piled up, with the ragged silhouette formed by the grains themselves. Tungsten trioxide has two **broken cakes** on top — it is calcined from ammonium paratungstate as a friable cake, not a fine powder, and colour alone cannot separate them: both are yellow, and ten-odd degrees of hue is unreadable at 80px |
| Molecules (ball-and-stick) | CO₂ O=C=O, O₂ O=O, CO C≡O, CH₃OH, HCHO H₂C=O, C₂H₄ H₂C=CH₂, N₂ N≡N | One shared helper (`_molecule` auto-centres and auto-fits), one consistent palette |
| Ionic crystals | Lithium sulfate, lithium hydroxide | Drawn as ionic formulas: a sulfate group (sulfur plus four oxygens) with two lithiums; Li–O–H as a chain |
| Layered crystals | Lithium cobalt oxide | The **crystal structure**, not a formula: three CoO₂ sheets sandwiching two rows of lithium ions — lithium moving in and out between the layers *is* how charging and discharging works |
| Recipe icons | Water electrolysis | An electrolysis cell: blue liquid surface, two electrodes, a stream of bubbles on each side, an arc across the top |
| Mega buildings | Heavenworks Assembler, Foundry Smelter, Calciner Chemical Plant, Forgeworks Fabricator, Deep Probe Collider | Isometric silhouettes that **match each building's procedural 3D mesh one-for-one** — what you pick out of the build bar is the thing standing on the ground. Each keeps one motif of its own: the tower's cooling rings, the chemical plant's tank cluster, the fabricator's gantry, the collider's ring — at 80px colour barely separates them, **the silhouette is the only thing that reads** |
| Build-menu tab | Mega structures | A **monochrome, detail-free** megastructure silhouette. A tab icon is not an item icon: it sits on the build bar permanently at a smaller size, so colour would fight the neighbouring tabs and detail would dissolve at a dozen pixels |

> The source SVGs and both output sizes (80×80 and 480×480) are in `tools/out/`. To revert one to "recoloured
> vanilla icon", delete that entry's `icon` / `ingotIcon` from `ores.json`.

### Cobalt: five reduction routes

Cobalt ore (counted as CoO) reduces easily, and **coal plus all four organics on the C1 chain work as reductants**.
The only difference is how many units of ore's oxygen one unit of reductant can take — and that number is not
invented, it is how many electrons the carbon (and hydrogen) in the reductant can give up:

| Reductant | Electrons from the half-reaction | Recipe | Time |
|---|---|---|---|
| **Coal** (carbon 0 → +4) | 4 | Cobalt Ore ×2 + Coal ×1 → **Cobalt Ingot ×2** | 1.5 s |
| **Carbon monoxide** (+2 → +4) | 2 | Cobalt Ore ×2 + CO ×2 → **Cobalt Ingot ×2 + CO₂ ×2** | 1.5 s |
| **Formaldehyde** (0 → +4) | 4 | Cobalt Ore ×2 + HCHO ×1 → **Cobalt Ingot ×2 + CO₂ ×1 + Water ×1** | 1.5 s |
| **Methanol** (−2 → +4) | 6 | Cobalt Ore ×3 + CH₃OH ×1 → **Cobalt Ingot ×3 + CO₂ ×1 + Water ×2** | 2 s |
| **Ethylene** (two carbons, −2 → +4) | 12 | Cobalt Ore ×6 + C₂H₄ ×1 → **Cobalt Ingot ×6 + CO₂ ×2 + Water ×2** | 4 s |

Every equation balances:

```
2 CoO + 2 CO    → 2 Co + 2 CO₂
2 CoO + HCHO    → 2 Co + CO₂ + H₂O
3 CoO + CH₃OH   → 3 Co + CO₂ + 2 H₂O
6 CoO + C₂H₄    → 6 Co + 2 CO₂ + 2 H₂O
```

So **the further down the chain the organic, the more cobalt one unit of it smelts** — that gradient is computed
from chemistry, not fitted to a progression curve. The cost, of course, is that those organics get progressively
more expensive; which route to use depends on what you happen to have spare.

### Aluminium: three smelting routes

Aluminium can be won carbothermically or electrolytically, and every ratio follows the chemical equation:

| Route | Machine | Recipe | Time |
|---|---|---|---|
| **Carbothermic** (2 Al₂O₃ + 3 C → 4 Al + 3 CO₂) | **Redox Chemical Plant** | Alumina Ore ×2 + Coal ×3 → **Aluminium Ingot ×4 + CO₂ ×3** | 3 s |
| **Electrolysis** (Al₂O₃ + 3 H₂ → 2 Al + 3 H₂O) | **Electrochemical Plant** | Alumina Ore ×1 + Hydrogen ×3 → **Aluminium Ingot ×2 + Water ×3** | 2 s |
| **Hydrocarbothermic** (2 Al₂O₃ + 3 C₂H₄ → 4 Al + 6 CO + 6 H₂) | **Redox Chemical Plant** | Alumina Ore ×2 + Ethylene ×3 → **Aluminium Ingot ×4 + CO ×6 + Hydrogen ×6** | 3 s |

Carbothermic eats coal and emits CO₂, but its inputs are easy to come by. Electrolysis emits no carbon but eats
hydrogen, which suits a mid-to-late game that already has gas giant collection. **All three need their dedicated
machine built first.**

In the hydrocarbothermic route ethylene cracks into carbon and hydrogen first, and it is that carbon which takes the
oxygen. Because the carbon loses only one oxygen, what comes out is **carbon monoxide rather than carbon dioxide**,
and together with the hydrogen that is syngas — the entire skeleton of the reductant returns to the C1 chain, having
taken a detour to turn ore into aluminium.

#### Why ethylene is the only organic that reduces aluminium

All four reductants reduce cobalt, but **only ethylene reduces aluminium**. That is not a balance decision, it is
chemistry refusing:

Al₂O₃ sits very low on the Ellingham diagram and is one of the hardest oxides to reduce — which is exactly why
aluminium was more expensive than gold before electrolysis was invented.

| Reductant | What cracking / oxidation gives | Can it reduce Al₂O₃? |
|---|---|---|
| Carbon monoxide | Already partially oxidised | **No.** CO is precisely the *product* of carbothermic aluminium; using it as the reductant runs the reaction backwards |
| Formaldehyde, methanol | The carbon already carries oxygen; cracking gives **CO** | **No.** What comes out is still CO, as above |
| **Ethylene** | Neither carbon has been oxidised; cracking gives **elemental carbon + H₂** | **Yes.** Elemental carbon takes the carbothermic route |

The test is blunt: **does cracking give you elemental carbon?** CO will not move aluminium; C will.

> **The three routes use both new recipe types.** Carbothermic and hydrocarbothermic are "Redox" (type 10),
> electrolysis is "Electrochemical" (type 9), and only the matching machine can run each. See section X.
>
> Carbothermic reduction (aluminium and cobalt) originally sat in the smelter and was moved: carbon reduces
> aluminium from Al³⁺ to the metal and is itself oxidised to CO₂, which is a redox reaction by definition. The new
> organic-reductant routes follow the same reasoning and all live in the Redox Chemical Plant.
>
> **Carbon dioxide now has a downstream**: hydrogenating it makes methanol directly, plugging the off-gas of
> carbothermic metallurgy back into the production line. See section XI.

### Lithium: the real hard-rock extraction process, in three steps

The lithium in spodumene (LiAlSi₂O₆) is locked inside a silicate framework and cannot simply be taken out. Real
hard-rock lithium extraction is a three-step process, reproduced here as-is:

| Step | Machine | Recipe | Time |
|---|---|---|---|
| **Acid roasting** (2 LiAlSi₂O₆ + H₂SO₄ → Li₂SO₄ + 2 HAlSi₂O₆) | Chemical Plant | Spodumene ×2 + Sulfuric Acid ×1 → **Lithium Sulfate ×1 + Stone ×2** | 4 s |
| **Electrodialysis** (Li₂SO₄ + 2 H₂O → 2 LiOH + H₂SO₄) | **Electrochemical Plant** | Lithium Sulfate ×1 + Water ×2 → **Lithium Hydroxide ×2 + Sulfuric Acid ×1** | 3 s |
| **Molten salt electrolysis** (4 LiOH → 4 Li + 2 H₂O + O₂) | **Electrochemical Plant** | Lithium Hydroxide ×4 → **Lithium ×4 + Water ×2 + Oxygen ×1** | 4 s |

All three equations balance. The tailings (aluminosilicate) are represented by stone, the same treatment used for
gypsum-to-sulfuric-acid.

> **The sulfuric acid circulates.** Step one consumes a unit and step two hands the same unit back. That is not a
> convenient fudge: step two is modelled on real **bipolar membrane electrodialysis**, and producing acid on the
> acid side is exactly its selling point. So the whole chain needs only one batch of acid to start, and hooking it
> up to the gypsum line is enough.

> **Why the last step must be electrolysis.** Lithium, like aluminium, is far too electropositive for **any**
> carbon-based reductant — the same Ellingham argument as in the aluminium section. Industry electrolyses molten
> LiCl, but the game has no chlorine source, so this electrolyses molten lithium hydroxide instead: still molten
> salt electrolysis, lithium at the cathode and oxygen at the anode, unchanged in essence. It is the one place where
> the electrolyte was swapped to fit the game; everything else follows the real process.

> **Lithium and cobalt meet at lithium cobalt oxide** (LiCoO₂, the classic lithium-ion cathode), which goes on to
> become the Lithium-Ion Accumulator. See section X.
>
> The synthesis uses lithium hydroxide rather than lithium metal: **4 LiOH + 4 Co + 3 O₂ → 4 LiCoO₂ + 2 H₂O**.
> Reality does the same — hydroxide (or carbonate) plus a cobalt source, sintered in the solid state at high
> temperature. Nobody makes cathodes out of lithium metal; it is too expensive and too dangerous.
> So **lithium metal itself still has no downstream**; it is the display piece at the end of this chain.

### Gypsum → sulfuric acid

| Machine | Recipe | Time |
|---|---|---|
| Chemical Plant | Gypsum Ore ×6 + Coal ×2 + Water ×4 → **Sulfuric Acid ×4 + Stone ×4** | 6 s |

The Müller-Kühne process: gypsum (CaSO₄·2H₂O) decomposed at high temperature with coal as the reductant; the sulfur
leaves as sulfur dioxide and becomes sulfuric acid after catalytic oxidation and absorption, while the calcium stays
in the slag as stone.

> This is **a second sulfuric acid route alongside vanilla's "refined oil + stone + water"**, which is untouched.
> Gypsum has no ingot; mine it and feed it straight to a chemical plant.

Every one of these recipes has **no prerequisite tech**, but **cannot be hand-crafted**.

**Mining**: both manual right-click and the advanced mining machine work — the cloned model carries its colliders
across, and without them the vein cannot be hit by a ray and cannot even be clicked.

### Tungsten: three steps, ending in tungsten carbide

| Step | Machine | Recipe | Time |
|---|---|---|---|
| 1 | **Chemical Plant** (vanilla) | Scheelite ×2 + Sulfuric Acid ×2 → **Tungsten Trioxide ×2 + Gypsum Ore ×2 + Water ×2** | 3 s |
| 2 | **Redox Chemical Plant** | Tungsten Trioxide ×2 + Hydrogen ×6 → **Tungsten Ingot ×2 + Water ×6** | 4 s |
| 3 | **Smelter** | Tungsten Ingot ×1 + Coal ×1 → **Tungsten Carbide ×1** | 2 s |

**Why does tungsten take such a detour? Because carbon cannot smelt it.**

Carbon behaves here exactly as it does with vanadium: it will not reduce tungsten from the oxide to the metal, it
combines with the tungsten into a carbide instead. For vanadium that is a dead end, which is why vanadium switched
to the aluminothermic route. For tungsten, the carbide is precisely what we wanted in the end. So:

* **Tungsten metal can only be won with hydrogen** (`WO₃ + 3H₂ → W + 3H₂O`). Real tungsten powder is reduced from
  the trioxide in a hydrogen stream at 700–900°C, and there is no second route.
* **Tungsten carbide is one step further** (`W + C → WC`), and it goes in the smelter — it is solid-state
  carburisation at 1400–1600°C, a furnace reaction.

**Why does step one stay in the vanilla chemical plant?** Because it is not a redox reaction at all: tungsten is +6
throughout, calcium does not change state either, and the sulfuric acid merely displaces the tungsten out of calcium
tungstate. The machine follows the reaction class, not whichever is convenient.

**Why scheelite rather than wolframite?** Both tungsten ores are common in reality; scheelite (CaWO₄) was chosen
because its by-product lands on an item that already exists:

```
CaWO₄ + H₂SO₄ → H₂WO₄↓ + CaSO₄
```

CaSO₄ is **gypsum ore**, and roasting gypsum gives sulfuric acid back. But this is **partial recovery, not a closed
loop**: one pass yields 2 gypsum while replacing 2 sulfuric acid needs 3 gypsum (6 gypsum → 4 acid), so each pass is
still 1 gypsum short, plus coal and water. It saves two thirds of the acid, not all of it. Wolframite
((Fe,Mn)WO₄) would have needed two invented items for ferrous sulfate and manganese sulfate, for nothing.

> **Tungsten has no synthetic fallback.** Vanadium has residue extraction as a safety net; tungsten does not, so its
> rare-slot chance is set a little higher. If not a single tungsten planet rolls, the cemented carbide line simply
> does not exist.

### Alloys: nine of them

| Alloy | Machine | Recipe | Time | Good at |
|---|---|---|---|---|
| **Carbon steel** | — | **It is vanilla Steel**, nothing new made | — | Cheap, tough enough |
| **Manganese steel** | Smelter | Iron ×7 + Manganese ×1 + Coal ×1 → ×8 | 4 s | **Toughness 80, highest in the table** |
| **Stainless steel** | Smelter | Iron ×7 + Chromium ×2 + Manganese ×1 → ×10 | 5 s | Corrosion 90, the cheap all-rounder |
| **Chrome-plated copper** | **Electrochemical Plant** | Copper ×4 + Chromium ×1 → ×4 | 3 s | **The only high conductivity + high corrosion resistance** |
| **Chrome-vanadium tool steel** | Smelter | Iron ×6 + Chromium ×2 + Vanadium ×1 + Coal ×1 → ×8 | 6 s | Hardness 80, the hardest steel |
| **Titanium alloy** | — | **Already in vanilla**, nothing new made | — | Decent at all three, weak at none |
| **Cobalt-chrome alloy** | Smelter | Cobalt ×6 + Chromium ×4 → ×10 | 7 s | Corrosion 95, and neither hard nor tough is weak |
| **Vanadium-titanium alloy** | Smelter | Vanadium ×4 + Titanium ×6 → ×10 | 8 s | **Hard and tough at once — the only one** |
| **Cemented carbide** | Smelter | Tungsten carbide + cobalt, 100 parts total → ×50, ratio adjustable | 30 s | Hardness at the top of the scale |

**Two of the nine add no new item**: the "carbon steel" you want *is* vanilla **Steel** (vanilla's Iron ×3 → Steel ×1
is carbon steel already), and Titanium Alloy already exists. They only needed the four property axes attached,
which incidentally pulls vanilla's materials into the same system.

> A second "iron + coal → steel" recipe was deliberately **not** added: it would bypass vanilla's 3 iron per steel
> and amount to a 3x buff.

**Ratios are by mass fraction, not by balanced equation** — an alloy is a solid solution, not a compound, and there
is nothing to balance. But every one of them is derived from a real grade: manganese steel is Hadfield steel,
chrome-vanadium tool steel is the AISI 6150 family, cobalt-chrome is Stellite/Vitallium, and vanadium-titanium is a
β-phase titanium alloy.

**Stainless steel works out especially neatly**: real **200-series stainless was invented by substituting manganese
for nickel**, and the game has no nickel — so "iron + chromium + manganese" lands directly on a real grade rather
than an invented one.

**Chrome-plated copper goes in the Electrochemical Plant**, not the smelter: electroplating is an electrochemical
process, not melting. The machine follows the reaction class here too.

### Ratios: seven of the alloys are adjustable

Select one of these recipes and a **ratio panel** appears automatically below the smelter window (the
Electrochemical Plant window for chrome-plated copper).

**Read it as "one balance plus N adjustable"**, exactly how real alloy grades are written ("18% Cr, 8% Mn, balance
Fe"): the first row is the balance (greyed out, not draggable), and every adjustable constituent below gets its own
slider. Drag any of them and the balance absorbs the change — so **there is no coupling rule to remember**.

When only one constituent is adjustable (a binary alloy) the balance row can be dragged too, because it is then the
unique complement.

| Alloy | Adjustable | Degrees of freedom |
|---|---|---|
| **Manganese steel** | Manganese 4–16 / Coal (carbon) 3–12 | **2** |
| **Stainless steel** | Chromium 5–18 / Manganese 2–12 | **2** |
| **Chrome-vanadium tool steel** | Chromium 3–14 / Vanadium 2–10 | **2** |
| Cemented carbide | Cobalt 5–45 | 1 |
| Chrome-plated copper | Chromium 2–12 | 1 |
| Cobalt-chrome alloy | Chromium 12–28 | 1 |
| Vanadium-titanium alloy | Vanadium 4–20 | 1 |

### What the ratio settles into: yield and time

**There is one item per alloy**, with no grade tiers. Dragging a slider changes the four properties of the mix
coming out of that furnace, and those four properties convert into two things: **how much this batch yields**, and
**how long it takes**.

```
【Smelter · Manganese Steel】
Manganese  ████████░░░░  11
Coal       █████░░░░░░░   7
balance Fe               32

H 52.4  T 98.0  C 18.0  E 8.8  →  Manganese Steel ×50   17.0 s   (base ×40 / 20.0 s)
```

The last line is live: the four numbers on the left are what this furnace is actually smelting right now, and the
right-hand side is what that buys.

End-to-end spread per alloy (the startup log prints this table from the real measurement):

| Alloy | Worst ratio | Best ratio | Throughput ratio |
|---|---|---|---|
| Manganese steel | Mn4/C12 → ×32, 24.0 s | Mn16/C3 → ×50, 17.0 s | **2.2x** |
| Chrome-vanadium tool steel | Cr3/V2 → ×32, 36.0 s | Cr14/V10 → ×50, 25.5 s | **2.2x** |
| Stainless steel | Cr5/Mn2 → ×40, 30.0 s | Cr18/Mn12 → ×62, 21.2 s | **2.2x** |
| Cemented carbide | Co45 → ×40, 36.0 s | Co5 → ×62, 25.5 s | **2.2x** |
| Cobalt-chrome alloy | Cr12 → ×40, 42.0 s | Cr28 → ×62, 29.8 s | **2.2x** |
| Chrome-plated copper | Cr12 → ×32, 36.0 s | Cr2 → ×50, 25.5 s | **2.2x** |
| Vanadium-titanium alloy | V20 → ×40, 48.0 s | V11 → ×62, 34.0 s | **2.2x** |

> **Why every one of them is 2.2x.** The four property values are on wildly different scales between alloys —
> dragging end to end moves manganese steel's weighted quality by 23% and vanadium-titanium's by 1%. So before
> settling, quality is **normalised against each alloy's own reachable range, enumerated at registration**, which is
> what puts every slider's travel on a comparable scale. Without it, vanadium-titanium's slider would do nothing.

**"Best" is not the same as "most economical".** Six of the seven have their maximum-quality ratio sitting at a
slider endpoint (enumerated, not guessed) — but **the adjustable slots are the scarce metals**: chromium, vanadium,
cobalt, tungsten carbide. Dragging up means every batch eats more of them. So where to set it depends on whether
rare metal or throughput is your bottleneck, and **the mod deliberately does not decide that for you**.
Vanadium-titanium is the one with an interior optimum (vanadium 11; both ends are worse).

> **How the four properties are computed.** Cemented carbide and chrome-plated copper use the **mixture rule**
> (hardness by power mean, toughness by harmonic mean); the other five are **solid solutions**, where the alloy can
> beat every constituent (manganese steel is tougher than both iron and manganese). An interpolation model cannot
> produce that number at all, so those five anchor on the nominal composition and offset by slope.
>
> The weighted quality is a **geometric** mean, not a weighted sum: a sum is blind to an axis going to zero, and the
> "high score" you get by dumping every part into one axis is not a usable alloy. Weights list only the axes that
> alloy cares about (manganese steel toughness, stainless corrosion, cemented carbide hardness), editable in
> `alloys.json`.

**Two things that will trip you up:**

* **A newly built furnace inherits the last ratio you set**, and so does one pasted from a blueprint.
* **The four property rows in the item tooltip are fixed**, showing the nominal ratio's values. That is an engine
  limit, not laziness: properties live on `ItemProto`, so one item per alloy means one row, and DSP has no
  per-stack or per-item metadata slot anywhere (the same wall as the cargo stacking section).
  **Each furnace's real values are visible only in the ratio panel.**


### The four property axes on metals

Metals get four extra lines in their item tooltip: **Hardness / Toughness / Corrosion Res. / Conductivity**.
Sorted by hardness:

| | Hardness | Toughness | Corrosion | Conductivity |
|---|---|---|---|---|
| Copper Ingot | 12 | 70 | 40 | **95** |
| Iron Ingot | 20 | 60 | 10 | 20 |
| Manganese Ingot | 30 | 45 | 20 | 15 |
| Titanium Ingot | 35 | 55 | 85 | 15 |
| Cobalt Ingot | 40 | 50 | 60 | 25 |
| Vanadium Ingot | 45 | 30 | 65 | 10 |
| Tungsten Ingot | 50 | 20 | 80 | 30 |
| Chromium Ingot | 55 | 15 | **95** | 10 |
| Tungsten Carbide | **95** | 10 | 85 | 12 |
| Cemented Carbide | **90** | 11 | 82 | 14 |
| Steel (carbon steel) | 45 | 55 | 10 | 15 |
| Manganese Steel | 50 | **80** | 15 | 10 |
| Stainless Steel | 50 | 50 | 90 | 10 |
| Chrome-Plated Copper | 25 | 60 | 75 | **80** |
| Chrome-Vanadium Tool Steel | 80 | 30 | 60 | 10 |
| Titanium Alloy | 55 | 60 | 90 | 12 |
| Cobalt-Chrome Alloy | 65 | 55 | **95** | 15 |
| Vanadium-Titanium Alloy | 70 | 50 | 88 | 10 |

The numbers are not invented; each axis has a basis:

* **Hardness** follows Vickers. **The hardest pure metal is chromium** (~1060 HV), not tungsten (~350–450 HV) —
  "tungsten is hard" is an impression cemented carbide gives; pure tungsten actually sits below chromium.
* **Toughness** reflects how brittle a metal is at room temperature. Tungsten and chromium both have a
  ductile-brittle transition above room temperature, so they are at the bottom; tungsten carbide's fracture
  toughness is around 5 MPa·√m, brittle enough that it has to be metal-bound to become a tool — which is the entire
  reason cemented carbide exists.
* **Corrosion resistance** reflects resistance to acid and to oxidation.
* **Conductivity** is scaled from IACS: copper 100% → 95, tungsten 31% → 30, cobalt 27% → 25.

Tungsten carbide is not a metal, but it uses the same axes — an alloy's properties are computed from its
constituents, so it needs a row.

**The alloy rows list the values computed at the nominal ratio**, not invented ones: cemented carbide and
chrome-plated copper are computed from their constituents with the composite mixture rule (hardness by power mean,
toughness by harmonic mean, corrosion and conductivity linear), and the other five use the solid-solution anchor
model.

**That row is fixed; what you actually smelt is not.** Properties live on `ItemProto`, so one item per alloy means
one row — dragging the ratio moves the real values away from it (cemented carbide's hardness varies continuously
between 75 and 93), and the item tooltip cannot show that. **Each furnace's real values are live in the ratio
panel.**

> **For now this is display only and drives no gameplay.** It is the data foundation for the alloy recipes: only
> once every metal has four values can an alloy's properties be "computed from composition" rather than guessed.

### Gas giant gases: nitrogen

Gas giants gain one more collectable gas, **nitrogen**. It works exactly like a vein: both are written into
`ThemeProto`, veins through `VeinSpot / VeinCount / VeinOpacity` and gases through `GasItems / GasSpeeds`.
The config is the `gases` section of `ores.json`, and adding a gas is pure data work.

| | |
|---|---|
| Rate | **0.6x** the theme's **highest** existing gas rate |
| Heat value | **None** (inert gas) |
| Icon | Hand-drawn N≡N triple bond, CPK blue brightened one step |

> **Nitrogen's downstream is nitric acid** — see the nitrogen chemistry chain at the end of section XI.

> **The heat value is deliberately blank.** N≡N is one of the most stable bonds in chemistry and nitrogen does not
> burn — which is the entire value of it as a protective atmosphere. That follows the standard set in section XII.
> The side effect is that it contributes nothing to the collector's power-discount calculation (`gasTotalHeat` is
> that formula's denominator), but it does not matter: gas giants still carry hydrogen and other gases with heat
> values to hold up the denominator, and vanilla guards against a zero denominator anyway.

#### How gases are generated

There is **exactly one** generation site: `PlanetGen.SetPlanetTheme` (called from `PlanetGen.CreatePlanet`), gated
on `planet.type == EPlanetType.Gas`, and then:

```csharp
items[i]  = theme.GasItems[i];                        // species copied verbatim, no randomness at all
speeds[i] = theme.GasSpeeds[i]
          * (rand.NextDouble() * 0.19090915f + 0.9090909f)   // about ±10%, determined by the planet seed
          * PlanetGen.gasCoef                                 // global coefficient
          * Mathf.Pow(planet.star.resourceCoef, 0.3f);        // system resource coefficient to the 0.3
heats[i]  = LDB.items.Select(items[i]).HeatValue;     // ← taken straight off the item
gasTotalHeat += heats[i] * speeds[i];
```

Three things to know:

- **The two arrays must be the same length.** The loop runs to `GasSpeeds.Length` but indexes `items[i]`, so a
  longer `GasSpeeds` throws on the spot. Injection aligns both.
- **The rate is configured as a multiple, not an absolute.** Each gas giant theme's base rates live in
  `resources.assets`, which cannot be read offline or by decompiling, so writing an absolute would be guessing.
  The startup log prints the computed real value.
- **It only affects planets not yet generated**, same as veins — a planet that already exists has its `gasItems`
  baked into the save.

#### The gate that wastes your afternoon

Adding a gas is not the same as being able to collect it. The full chain is:

```
ThemeProto.GasItems
  → PlanetGen.SetPlanetTheme            → planet.gasItems
  → PlanetTransport.NewStationComponent → station.collectionIds (all gases, untruncated)
  → StationComponent.Init               → storage[i].itemId     ← the gate is here
  → UpdateCollection                    → output
```

The second-to-last step looks like this:

```csharp
for (int i = 0; i < collectionIds.Length; i++) {
    if (i > _desc.stationMaxItemKinds - 1) break;   // ← gases past the limit get no storage slot
    storage[i].itemId = collectionIds[i];
}
```

Nitrogen *is* in `collectionIds`, and **without a slot it is never collected**, with no error of any kind.
So after registration the collector's `stationMaxItemKinds` is raised to **exactly the largest gas count across all
themes**.

> **Exactly that, not more.** A collector's slots are laid out one-for-one from `collectionIds`, so raising it to 30
> would only add a pile of empty slots — and drag the 30-slot station interface onto the collector.
> That is why `StationCapacityPatches` deliberately skips collectors instead of applying its uniform slot count.

> Only **newly built** collectors are affected: the `storage` array is baked in at build time.

### Displayed as common minerals, not "rare signals"

Vanilla splits vein types by number: **1–6 are common** (iron, copper, silicon, titanium, stone, coal) and
**7 and up are treated as rare** (crude oil, fire ice, fractal silicon …). The new types are **15–22**, so by
default they would be filed as rare — highlighted in the planet panel, shown as "unknown rare signal" while
unexplored.

This mod puts them back on the common-mineral display path. **Only this mod's vein types are affected**; vanilla's
rare veins are untouched.

### Where the veins are

The veins are laid onto every planet theme that produces iron (themes that produce no ore, like gas giants, are
skipped), at a multiple of the iron vein density: cobalt **0.35x**, aluminium **0.3x**, gypsum **0.25x**,
lithium **0.18x**.

> **Only for planets not yet generated.** A planet you have already visited has its vein data in the save and will
> not grow new veins; planets in an existing save that you have **not** visited will carry them the next time they
> generate. To get these ores near home, you have to look outward at unexplored planets.

Density and reserves are per-ore adjustable in `ores.json` (`veinRarity` / `veinAmountScale`).

### Using it together with GalacticScale

With GS2 installed the new veins still work, but two things are held up by a compatibility layer:

- **Vein generation**: GS2 replaces vein generation entirely, so this mod re-types a share of the iron vein groups
  into the new ore types at GS2's own exit points. Which type is chosen is banded by each ore's `veinRarity` and
  decided by a deterministic hash of (planet ID, group index) — so the same planet gives the same result on every
  load

> ⚠️ **The two paths cost iron differently.** The vanilla path **adds** vein spots to the planet theme and takes
> nothing from iron; the GS2 path **re-types** iron groups. The four ores' rarities sum to 1.08, which means that
> with GS2 about **52% of iron vein groups become something else**
> (iron : cobalt : aluminium : gypsum : lithium = 1 : 0.35 : 0.3 : 0.25 : 0.18) — **and every ore you add pushes
> that ratio further up**. Before adding a fifth, consider scaling the `veinRarity` values down together.
- **Vein distribution labels**: GS2 replaces vanilla's label logic and adds a per-type display switch; a type that
  is not in its dictionary gets **no label node created at all**. The compatibility layer inserts the new types into
  that switch

> With GS2 uninstalled the vanilla path runs and both compatibility layers disable themselves.

### Adding another ore

`ores.json` is a table; append an entry to the `ores` array and **no code changes are needed**: continue the vein
numbering, take an unused item ID, fill in the tint parameters. `hasIngot: false` means "ore only, no ingot".

Recipes are a table too: each ore's `recipes` array takes any number of entries, each with its own machine type,
time and materials on both sides. `{ "id": 1006 }` is a vanilla item, `{ "ref": "ore" }` / `{ "ref": "ingot" }` are
this ore's own ore and ingot, and `{ "ref": "co2" }` is an item added in the `items` section — **use a reference
name, never a hardcoded ID**, because a new item's ID shifts automatically on collision. By-products and other new
items that belong to no vein go in the top-level `items` section, and their icons are likewise recoloured from some
vanilla item.

At startup the mod checks ID occupancy, grid cell occupancy and vein numbering continuity, shifting on collision and
telling you in the log which value to pin.

### A technical note

Adding a new vein type normally requires a preloader (a BepInEx bytecode patch) to extend the `EVeinType` enum.
**This mod does not need one**, and installs like any other mod: `EVeinType` is byte-backed, types 15–22 are valid
without names, and the arrays around it size themselves from the largest ID in `LDB.veins`.

Registering is not enough on its own, though — vanilla's vein-type loops have their upper bound hardcoded to 15
(seven places: five planet generation algorithms, the planet detail panel and the star detail panel), and the new
types sit exactly outside it, so **not a single vein would generate**. Only once that bound is computed from the
actual vein count do the new veins really appear.

Also **vein numbers must be contiguous, with no gaps**. Skipping the sentinel value 15 and starting at 16 looks
tidier and does not work: in vanilla's planet detail panel the vein loop reads `VeinProto.MiningItem` **before** it
null-checks the proto. Vanilla never trips over that because 1–14 are dense; leave 15 empty and the loop walks into
the hole, and the panel throws the moment it opens. Verified by doing it — which is why aluminium is 16 and
something has to occupy 15.

---

## X. New Buildings

Six of them, each a **whole-building clone of a vanilla one** with a few fields changed. Configured in
`machines.json`.

### Electrochemical Plant

One production facility plus one new class of recipe.

| | |
|---|---|
| Appearance | The Chemical Plant's model and icon, **tinted cold blue** (material and icon tinted separately) |
| Build | Chemical Plant ×1 + Electric Motor ×8 + Circuit Board ×8, 3 s, **hand-craftable**, no prerequisite tech |
| Location | Next to the Chemical Plant in the build bar (category 5, finds a free slot at startup) |
| What it does | **Electrochemical** recipes, four in total: aluminium electrolysis, water electrolysis, lithium hydroxide electrodialysis, lithium molten-salt electrolysis |
| Speed / power | Identical to the Chemical Plant — it is a whole-building clone with only the recipe type changed |

#### Electrochemical recipes

| Recipe | Contents | Time |
|---|---|---|
| **Aluminium electrolysis** (Al₂O₃ + 3 H₂ → 2 Al + 3 H₂O) | Alumina Ore ×1 + Hydrogen ×3 → Aluminium Ingot ×2 + Water ×3 | 2 s |
| **Water electrolysis** (2 H₂O → 2 H₂ + O₂) | Water ×2 → **Hydrogen ×2 + Oxygen ×1** | 2 s |
| **Lithium hydroxide electrodialysis** (Li₂SO₄ + 2 H₂O → 2 LiOH + H₂SO₄) | Lithium Sulfate ×1 + Water ×2 → **Lithium Hydroxide ×2 + Sulfuric Acid ×1** | 3 s |
| **Lithium molten-salt electrolysis** (4 LiOH → 4 Li + 2 H₂O + O₂) | Lithium Hydroxide ×4 → **Lithium ×4 + Water ×2 + Oxygen ×1** | 4 s |

The last two are steps 2 and 3 of the lithium chain; see section IX.

Water electrolysis is a second source of hydrogen that does not require gas giant collection. **Oxygen has two
downstreams**: as the oxidant for formaldehyde (section XI) and as the oxidant for lithium cobalt oxide synthesis.
Lithium molten-salt electrolysis releases another unit of oxygen as well.

#### Why "a new machine" equals "a new recipe type"

Vanilla **filters the recipe picker on a single recipe type**: `UIRecipePicker.RefreshIcons` compares `filter`
against `recipe.Type` directly, and `filter` comes from the machine's `prefabDesc.assemblerRecipeType`.
**One machine accepts exactly one type**; there is no such thing as "this machine can do A and B".

So "a class of recipes only the new machine can run" *is* "allocate a new type number and point a machine at it",
and that is the whole implementation of the Electrochemical Plant.

The good news is that this needs no preloader: `ERecipeType` is int-backed and `(ERecipeType)9` is valid without a
name (the same argument as `EVeinType` for the custom veins). Vanilla occupies 1–8 and 15, so **9–14 are free**
(this mod has taken 9 for Electrochemical and 10 for Redox, leaving 11–14). And `AssemblerComponent.SetRecipe`
**never validates the type** — the type only matters at the UI layer, and not one line of production logic changed.

Only two pieces of text need filling in: the recipe tooltip's "Made in" line and the item tooltip's "Type" line,
both of which are vanilla `switch` statements over known types with a fallback branch.

### Redox Chemical Plant

Same approach and same source (the Chemical Plant) as the Electrochemical Plant, with only colour and recipe type
changed.

| | |
|---|---|
| Appearance | The Chemical Plant's model and icon, **tinted violet** (hue 285°) — vanilla's is teal and the Electrochemical Plant is cold blue, so all three are distinguishable at a glance |
| Build | Chemical Plant ×1 + Electric Motor ×10 + Circuit Board ×10, 3 s, **hand-craftable**, no prerequisite tech |
| Location | Follows the Chemical Plant's category, finds a free slot at startup |
| What it does | **Redox** recipes, 33 in total — the most of any machine in this mod; see the table below |
| Speed / power | Identical to the Chemical Plant — a whole-building clone |

#### Redox recipes

This is the machine with the most recipes in the mod; the **33 fall into five groups** (detailed tables live in
their own sections and are not repeated here):

| Group | Count | Recipes | See |
|---|---|---|---|
| **Metal reduction** | 16 | Cobalt ×6 (coal / CO / formaldehyde / methanol / ethylene / ammonia), aluminium ×3, manganese ×2, chromium ×2, vanadium ×2, tungsten ×1 | Section IX |
| **C1 and organic chemistry** | 8 | Water gas, methanol synthesis, methanol via CO₂ hydrogenation, formaldehyde, Fischer-Tropsch, propylene ammoxidation, steam cracking, cumene cleavage | Section XI |
| **Nitrogen chain** | 3 | Haber-Bosch ammonia, ammonia catalytic oxidation, nitric acid absorption | Section XI |
| **Refining and sulfur** | 3 | Residue hydrodesulfurisation, contact-process sulfuric acid, hydrocracking | Section XI |
| **Material synthesis** | 3 | Lithium cobalt oxide, aluminium nitride by direct nitridation, zeolite catalyst regeneration | Sections IX, XXIV, XXVII |

What they have in common is **electron transfer**: a metal is reduced out of its oxide, or carbon/hydrogen changes
oxidation state in the reaction. The one thing kept out is methanol-to-olefins — that is a dehydration, the carbon's
oxidation state never changes, so it stays in the vanilla Chemical Plant.

Carbothermic reduction (aluminium and cobalt) used to be in the smelter and now belongs to this machine.

> **Note this gates cobalt later than before.** Cobalt is the earliest of the new ores to be useful, and now you
> have to hand-craft a Redox Chemical Plant first (Chemical Plant ×1 + Electric Motor ×10 + Circuit Board ×10).
> If that feels too late, change that recipe's `"type"` back to `1` in `ores.json` — it is one field.

> **Smelters in existing saves that already have one of these recipes set keep running** (`AssemblerComponent` does
> not validate recipe type while working; the type only matters in the recipe picker), but once dismantled and
> rebuilt you will not be able to select it again.

### Lithium-Ion Accumulator

The lithium-ion version of vanilla's Accumulator: **charges fast, holds more**.

| | |
|---|---|
| Appearance | The Accumulator's model and icon, **tinted the lithium family's purple**, matching the lithium vein and ingot |
| Build | Accumulator ×1 + **Lithium Cobalt Oxide ×8** + Energetic Graphite ×8, 5 s |
| Capacity | **6x** vanilla (energy density) |
| Charging power | **10x** vanilla (the point: it takes power fast) |
| Discharge power | 4x vanilla |

The recipe is a real lithium-ion cell: **lithium cobalt oxide cathode, graphite anode** (energetic graphite), put
into an accumulator shell.

> **The values are configured as multipliers, not absolutes.** Capacity and charge/discharge power live in the
> source building's `PrefabDesc`, which is read from a prefab inside `resources.assets` — **not readable offline,
> not recoverable by decompiling** — so writing an absolute would be guessing from memory. A multiplier is applied
> to the runtime value and follows whatever vanilla does. The startup log prints the resolved absolutes, something
> like "capacity 270 MJ → 1.62 GJ (×6), charging 900 kW → 9 MW (×10)", so you can pin numbers off the log.

> **It is built empty**, exactly like vanilla (`curEnergy` always starts at 0 in
> `PowerSystem.NewAccumulatorComponent`).

#### Lithium-Ion Accumulator (full)

There is a "full" variant, as in vanilla:

| | |
|---|---|
| Heat value | **16.2 GJ** (6x vanilla's full Accumulator at 2.7 GJ, following the capacity multiplier) |
| Mecha power bonus | **+150% (×2.5)** |
| Fuel type | 8, the same class as vanilla's full Accumulator |

The mecha power bonus comes from `ItemProto.ReactorInc` — `Mecha.GenerateEnergy` computes
`ratio = ReactorInc + 1` and multiplies `reactorPowerGen` by it, so 1.5 means +150%. A pure data field, no patch
required.

Vanilla reference: Accumulator (full) ×2.0, Hydrogen Fuel Rod ×2.0, Deuteron Fuel Rod ×3.0, Antimatter Fuel Rod
×8.0, Crude Oil ×0.5. The lithium cell's ×2.5 sits between the hydrogen and deuteron rods.

> **It changes power, not total energy.** A higher bonus means the same cell **discharges faster**, but because the
> total is fixed at 16.2 GJ it also discharges fewer times. For "lasts longer" adjust the heat value; adjust this
> one only for "hits harder".

> **The shell is returned when it burns out**, as in vanilla — but this one is **done by a patch**, not for free.
> The "return the shell" code in `Mecha.GenerateEnergy` hardcodes 2206 / 2207:
> ```csharp
> if (this.reactorItemId == 2207) { player.TryAddItemToPackage(2206, 1, ...); }
> ```
> A cloned full accumulator is neither of those numbers, so it would simply vanish when burned — throwing away an
> entire accumulator's build cost every time. `MechaFuelShellPatches` filters the value as it is read (this mod's
> full variants all report as 2207, and the empty shell is looked up from the current fuel), touching no real data —
> the same technique as filtering `storageId` for the logistics hub.

> **It is not a new building, it is another face of the same one.** Vanilla's 2206 and 2207 **share one ModelIndex
> (46)** and differ in only three things: `BuildIndex = 0` (no build-bar slot, placed from the inventory), their own
> grid cell, and `FuelType` + `HeatValue`. That structure is reproduced here, so the full and empty variants share a
> PrefabDesc and their capacity and power are automatically consistent — there is no way for "the full version's
> numbers to disagree".
>
> The heat value is not hand-written; it is **derived from the capacity multiplier** off vanilla's full Accumulator:
> six times the capacity means six times the energy one full cell can release.

### Lithium-Ion Energy Exchanger

Fills empty Lithium-Ion Accumulators, or discharges full ones back into the grid, at **6x** vanilla's throughput.

| | |
|---|---|
| Appearance | The Energy Exchanger's model and icon, likewise tinted the lithium family's purple |
| Build | Energy Exchanger ×1 + Lithium Cobalt Oxide ×20 + Super-Magnetic Ring ×20, 8 s |
| Power / energy pool | 6x vanilla |

> **Why a separate exchanger instead of making vanilla's compatible.**
> The empty↔full pairing is **pure data**, written on the exchanger's `PrefabDesc.emptyId` / `fullId`;
> `PowerExchangerComponent`'s belt I/O, state machine and energy settlement know only those two IDs from beginning
> to end — **one exchanger inherently serves one pair**. Supporting several would mean taking over every site in
> `InternalUpdate`, which is not worth it. Cloning a second exchanger pointed at the new pair is exactly how vanilla
> models this, and needs no patch at all.
>
> The config's `pairMachineKey` points at the accumulator. **Registration follows the order of the `machines`
> array, so the accumulator entry must come before the exchanger**, or it logs an ERROR and falls back to serving
> vanilla's pair.

### Integrated Logistics Hub

Puts **all three kinds of logistics drone** into one building:

| | |
|---|---|
| Appearance | The Interstellar Logistics Station's model and icon, **tinted amber** |
| Build | Interstellar Logistics Station ×1 + Electromagnetic Turbine ×20 + Circuit Board ×20, 5 s |
| Location | Right next to the two logistics stations in the build bar |
| Berths | **200 Logistics Drones + 50 Logistics Vessels + 20 Logistics Bots** |
| Automation | The bots resupply and recover from the mecha based on what the hub holds — no manual request list needed |
| Storage / slots / charging | Same as this mod's enlarged logistics stations (30 slots × 10,000,000, 30 GW) |

Vanilla's planetary station has only drones; the interstellar station has both but a small hangar. This one enlarges
the berths as well, so one hub does the work of several.

> **Why this needs almost no code**: "planetary drone" and "interstellar vessel" are already two halves of one
> `StationComponent` — the same component holds both `idleDroneCount` and `idleShipCount`, and
> `isStellarStation` decides whether vessels are allocated. Cloning an interstellar station carries both switches
> across unchanged, so both drone kinds are simply there; only the counts were tuned.
>
> **The vessel cap is 64**, a hard limit: `idleShipIndices` is a `UInt64` bitmask indexed `1L << (index & 63)`, and
> a larger number would have entries overwrite each other. The config clamps anything above 64 and logs a WARNING.

#### The third drone: Logistics Bots

Logistics Bots **deliver to the mecha (Icarus) and recover from it**. In vanilla they belong to the Logistics
Distributor (a different component, `DispenserComponent`), need an adjacent storage box as their source, and each
one serves exactly **one** item.

In this hub they **draw on the 30 slots directly, fully automatically**:

- Whatever sits in a non-empty hub slot is added to the mecha's delivery request list (one stack of each by
  default, configurable)
- Short on the mecha, it gets delivered; over the limit, it gets recovered, and the recovered goods go straight
  into the logistics network
- The panel gains a Logistics Bot slot you can add to or take from by hand; it also tops itself up like the drones

Configured in `machines.json`: `courierCount` (how many), `playerDeliveryMode` (send/receive mode),
`autoDeliveryList` (auto-fill the request list), `deliveryKeepStacks` (how many stacks to maintain).

##### Why the implementation takes such a detour

Vanilla assumes everywhere that "one distributor = one storage box = one item", and attaching that to a 30-slot
logistics station means working around every one of those assumptions:

| What vanilla does | How it is worked around |
|---|---|
| A distributor's source must be a `StorageComponent`, while a station's slots are `StationStore[]` — **different types, neither can read the other** | The hub carries a **hidden buffer box** as the distributor's source, aligned with the 30 slots in both directions every 10 ticks |
| `ConnectToDispenser` only links a storage box on an **adjacent entity** | The link is made manually at build time |
| When the player puts something into the building, the `storageId` check comes first | It is filtered out for hubs, so drones go into the logistics station rather than falling into the buffer |
| Three panels (storage / station / distributor) fight over one click, and the last match wins | Only the station panel is kept; distribution mode comes from the config instead |
| A distributor only works on items configured in the **delivery request list**, and an empty list means everything idles | The hub's contents are filled into the list automatically (empty rows only, never touching entries the player configured) |
| **One distributor serves exactly one item** (`filter` is both the pairing condition and the item ID used when picking up) | `filter` is rotated to the next item once per second, leaving pairing, pickup and dispatch entirely to vanilla |

> That last one is this building's only imperfection: **it serves one item at a time**. At one rotation per second
> with 20 bots you cannot tell in practice; making one distributor genuinely serve several would mean taking over
> every `filter` site inside an 8.9 KB `InternalTick`, which is not worth it.

> **For diagnosis**: turn on `courierDebugLog` in `machines.json` and it prints one status line every 10 seconds
> (slots / buffer / request list / bots / pairing / item currently served). That chain has five stages, and any one
> of them being empty looks identical from the outside — "the bots are just sitting there".

### Wind Turbine Cluster

A thousand wind turbines pressed into one tower array.

| | |
|---|---|
| Appearance | The Wind Turbine's model and icon, **tinted teal** (hue 158°) |
| Location | Slot 7 of the **Mega Structures** tab in the build bar |
| Output | **300 MW**, exactly a thousand wind turbines (300 kW each) |
| Build | Wind Turbine ×1000 + Energy Matrix ×1000, **10 s, hand-craft only** |

- Like a single turbine it **lives on planetary wind**: on an airless world it is just as motionless an ornament
- It is **one entity**: one collision box, one grid tie. The 1000x is in the power, not the footprint
- Verified in game: registration logs `发电 300 kW → 300 MW（×1000），无燃料`, and placing one next to another
  turbine raises "wind turbine too close" — a check that only fires on buildings carrying the wind flag, which
  proves the clone really did carry "this is wind power" across rather than becoming an inert shell

> **How "hand-craft only" is done.** The recipe's type is set to `None` (0).
> The replicator only looks at `RecipeProto.Handcraft`, so it still lists it;
> the recipe picker skips on "`filter != 0` and `filter != recipe.Type`", and no machine's
> `assemblerRecipeType` is ever 0, so no machine can select it.
> The one cost, which was checked: the product gets no `productionMask` bit, and the only reader of that bit in the
> whole game is the "reference rate" panel — which a hand-craft recipe has no business appearing in anyway.

> **Why no power logic had to change.** `PowerSystem.NewGeneratorComponent` copies `windForcedPower`,
> `genEnergyPerTick` and the rest of `PrefabDesc` **field by field** into `PowerGeneratorComponent`, and the wind
> branch's per-tick ceiling is simply `(long)(planetary wind × genEnergyPerTick)` — **with no clamp anywhere**.
> So multiplying the power is the entire implementation.
> Conversely, those "which kind of generator is this" booleans have to be copied explicitly: miss one and the clone
> is **a power plant that generates nothing**, silently.

---

## XI. The Chemistry Chain: C1, nitrogen, organics and refining

A one-carbon chemistry route that starts from **coal and water** and never touches petroleum. The entrance is water
gas, the junction is methanol, and there are three exits.

```
Coal + Water ──water gas──> CO + H₂
                             │
             CO₂ ────────────┼──> Methanol ──oxidation──> Formaldehyde
   (carbothermic off-gas)    │        └──MTO──> Ethylene
                             │
                             └──Fischer-Tropsch──> Refined Oil
```

### Recipe list

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Water gas** (C + H₂O → CO + H₂) | Redox Chemical Plant | Coal ×2 + Water ×2 → **CO ×2 + Hydrogen ×2** | 2 s |
| **Methanol synthesis** (CO + 2 H₂ → CH₃OH) | Redox Chemical Plant | CO ×2 + Hydrogen ×4 → **Methanol ×2** | 2 s |
| **Methanol via CO₂ hydrogenation** (CO₂ + 3 H₂ → CH₃OH + H₂O) | Redox Chemical Plant | CO₂ ×2 + Hydrogen ×6 → **Methanol ×2 + Water ×2** | 2.5 s |
| **Formaldehyde** (2 CH₃OH + O₂ → 2 HCHO + 2 H₂O) | Redox Chemical Plant | Methanol ×2 + Oxygen ×1 → **Formaldehyde ×2 + Water ×2** | 1.5 s |
| **Ethylene via methanol-to-olefins** (2 CH₃OH → C₂H₄ + 2 H₂O) | **Chemical Plant** | Methanol ×2 → **Ethylene ×1 + Water ×2** | 3 s |
| **Fischer-Tropsch** (4 CO + 8 H₂ → 4 -CH₂- + 4 H₂O) | Redox Chemical Plant | CO ×4 + Hydrogen ×8 → **Refined Oil ×4 + Water ×4** | 4 s |

Every ratio follows a balanced chemical equation.

> **Why MTO alone goes in the Chemical Plant.** Methanol dehydrating to an olefin leaves the carbon's oxidation
> state unchanged from start to finish — it is a dehydration, not a redox reaction, so it stays in the vanilla
> Chemical Plant (type 2). The other five involve electron transfer and go to the Redox Chemical Plant (type 10).

### This chain closes two dead ends

- **Carbon dioxide** used to be pure off-gas from carbothermic aluminium with nowhere to go; hydrogenating it now
  makes methanol, which plugs metallurgical off-gas back into the production line
- **Oxygen** used to be the other half of water electrolysis that nobody wanted; it is now the oxidant for
  formaldehyde

So water electrolysis now has a home for both products: hydrogen feeds methanol synthesis and Fischer-Tropsch,
oxygen feeds formaldehyde.

> **Fischer-Tropsch makes refined oil renewable.** In vanilla refined oil can only come from crude, and the sources
> are fixed; with this, coal and water are enough to make oil. The cost is a serious hydrogen appetite — 8 hydrogen
> for 4 oil — so water electrolysis has to be scaled up first.

### Icons

The four new items on this chain are all **hand-drawn ball-and-stick structural formulas**: C≡O, CH₃OH, H₂C=O,
H₂C=CH₂. Carbon dioxide (O=C=O), oxygen (O=O) and later nitrogen (N≡N) use the same shared helper and palette
(carbon grey, oxygen red, hydrogen white, nitrogen blue). The full list of 13 hand-drawn icons is in section IX.

> The palette borrows from CPK, but **carbon is lightened** — true CPK carbon is near black and smears into DSP's
> dark interface. Bonds also have to be lighter than the atoms at either end, or they disappear on a dark
> background and double bonds collapse into a blob. And the atoms need enough gap between them for the parallel
> lines of double and triple bonds to show; too close and the spheres cover them.

Recipe icons reuse their product's molecule diagram (vanilla does the same), and Fischer-Tropsch uses vanilla's
refined oil icon.

### Nitrogen chemistry: nitrogen → ammonia → nitric acid

The nitrogen collected from gas giants finally has a downstream, via two classic real processes: **Haber** for
ammonia and **Ostwald** for turning ammonia into nitric acid.

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Ammonia synthesis** (N₂ + 3 H₂ → 2 NH₃) | Redox Chemical Plant | Nitrogen ×1 + Hydrogen ×3 → **Ammonia ×2** | 3 s |
| **Nitrogen dioxide via Ostwald oxidation** (4 NH₃ + 7 O₂ → 4 NO₂ + 6 H₂O) | Redox Chemical Plant | Ammonia ×4 + Oxygen ×7 → **NO₂ ×4 + Water ×6** | 2 s |
| **Nitric acid** (4 NO₂ + O₂ + 2 H₂O → 4 HNO₃) | Redox Chemical Plant | NO₂ ×4 + Oxygen ×1 + Water ×2 → **Nitric Acid ×4** | 2.5 s |

All three equations balance, and nitrogen's oxidation state walks **0 → −3 → +4 → +5**, ending at nitric acid.

> **Step one is the hard part of the chain, not the end of it.** The N≡N triple bond is one of the most stable in
> chemistry; the real Haber process needs hundreds of atmospheres and an iron catalyst to break it, and at the start
> of the twentieth century the step was considered impossible. Hence ammonia synthesis has the longest time.

> **Step two merges the textbook's two steps into one.** The Ostwald process is properly "ammonia → NO" then
> "NO → NO₂", and industrially those happen back to back in the same vessel, so no separate nitric oxide item is
> added.
>
> Step three uses the **oxygen-supplemented form** (4 NO₂ + O₂ + 2 H₂O) rather than the textbook's
> 3 NO₂ + H₂O, which would leave a unit of NO behind — a real absorption tower feeds excess air precisely to convert
> the intermediates as well.

> **This chain is hungry for oxygen** (8 units per batch of nitric acid), so the water electrolysis line has to be
> scaled up. Conveniently, electrolysis gives hydrogen and oxygen at 2:1 and ammonia synthesis wants hydrogen — both
> products have a home, and ammonia oxidation returns a net 4 water.

**Heat values follow the standard**: ammonia **has** one (4 NH₃ + 3 O₂ → 2 N₂ + 6 H₂O, 383 kJ/mol, giving
**2.6 MJ** — ammonia genuinely burns, it is just hard to ignite); nitrogen dioxide and nitric acid **do not** —
they are **oxidants**, the side that feeds combustion, not fuel.

> **Nitric acid has no downstream yet.** The natural next step is nitration (explosives, fertiliser) — say the word.

### What burns and what does not

Carbon monoxide, methanol and ethylene all carry heat values and can be burned in thermal power plants and the mecha
reactor as chemical fuel.

| Item | Combustion | Molar enthalpy | In-game heat value |
|---|---|---|---|
| Carbon monoxide | CO + ½O₂ → CO₂ | 283 kJ/mol | **1.95 MJ** |
| Methanol | CH₃OH + 1.5 O₂ → CO₂ + 2 H₂O | 726 kJ/mol | **5.0 MJ** |
| Ethylene | C₂H₄ + 3 O₂ → 2 CO₂ + 2 H₂O | 1411 kJ/mol | **9.7 MJ** |

These are not invented: everything is scaled proportionally against **coal at 2.7 MJ** (vanilla's value,
corresponding to carbon's 393.5 kJ/mol). For comparison with vanilla — wood 1.5, coal 2.7, crude oil 4.05,
fire ice 4.8, hydrogen 8.0 — carbon monoxide is weak, methanol upper-middle, and ethylene beats hydrogen, matching
the real energy density ordering of the three.

> Note that **burning them is not economical**. Methanol costs 2 hydrogen to make 1 unit, and hydrogen is 8 MJ by
> itself; the point of these recipes is chemical feedstock and by-product disposal, and being burnable is just one
> more exit.

**Carbon dioxide and oxygen deliberately have no heat value**: the former is already fully burned, the latter is an
oxidant, not a fuel. Formaldehyde is not configured yet either (say the word; the same standard gives 3.9 MJ).

### All of these gases/liquids fit in a storage tank

Carbon monoxide, methanol, formaldehyde and ethylene, plus carbon dioxide, oxygen and the nitrogen from gas giants —
**seven** in all can be stored in tanks and moved by pipe.

> Lithium sulfate, lithium hydroxide and lithium cobalt oxide on the lithium chain are **solids** (crystals at room
> temperature), so they travel on belts and in storage, not in tanks — that is a phase judgement under the standard
> in section XII, not an oversight.

> **This nearly did not work.** Vanilla keeps a fluid whitelist (`ItemProto.fluids`) built by scanning the item
> table on the game's preload thread, and LDBTool only inserts mod items into that table at **the very last step of
> the same method**. The whitelist is therefore built before any mod item exists, so writing `isFluid: true` in the
> config accomplishes nothing at all.
>
> And it fails completely: when an empty tank picks up from a belt, that whitelist is passed into the pickup
> function **as the filter array** — not in the table means never picked up, never picked up means the tank never
> learns its fluid ID, so an empty tank stays empty forever, and inserting by hand is blocked by the same table.
>
> The fix is to re-run vanilla's own whitelist builder once the item table is complete (it rebuilds wholesale, so
> it is idempotent and keeps every vanilla fluid). The startup log prints
> "流体白名单已重建：N → M 种，新增……" (fluid whitelist rebuilt); if that line is missing, it did not take effect.
>
> **Fuel has a table of the same shape, but it is not our problem**: power buildings picking fuel off a belt also
> use a preload-built table as a filter array (`ItemProto.fuelNeeds`), and **LDBTool re-runs that one itself**
> (along with item ID indices, the recipe item table and the icon set) — it just missed the fluid table. There is a
> whole family of these preload-time static tables, and when adding something new it is worth checking first
> whether LDBTool already re-runs it for you.

---

### Organic chemistry, phase one: a real destination for formaldehyde and carbon dioxide

Once the C1 chain was laid out, two points on it were **blocked**:

- **Formaldehyde had exactly one downstream** (Cobalt Ingot · Formaldehyde Reduction), which made it nothing
  but a mid-strength reductant here — while in reality formaldehyde is where resins, curing agents and cage
  amines start
- **Carbon dioxide had exactly one sink** (Methanol · CO₂ Hydrogenation), carrying the exhaust of the entire
  carbothermic reduction family on its own

These three recipes are aimed at those two points.

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Urea · Ammonia and CO₂ Synthesis** (2 NH₃ + CO₂ → CO(NH₂)₂ + H₂O) | **Chemical Plant** | Ammonia ×2 + Carbon Dioxide ×1 → **Urea ×1 + Water ×1** | 2 s |
| **Hexamine · Formaldehyde and Ammonia Condensation** (6 HCHO + 4 NH₃ → C₆H₁₂N₄ + 6 H₂O) | **Chemical Plant** | Formaldehyde ×6 + Ammonia ×4 → **Hexamine ×1 + Water ×6** | 3 s |
| **Plastic · Urea-Formaldehyde Resin** | **Chemical Plant** | Urea ×2 + Formaldehyde ×3 → **Plastic ×2 + Water ×2** | 2.5 s |

The first two balance exactly. All three are **condensation-dehydration**: neither carbon nor nitrogen changes
oxidation state anywhere, so by reaction class they all go to the vanilla Chemical Plant (type 2) — the same
reasoning that keeps MTO there.

**The result: formaldehyde goes from one downstream to three, ammonia from two to three, and carbon dioxide
from one sink to two.**

### Hexamine: the one you can burn

It is the white solid fuel tablet camping stoves burn (sold as hexamethylenetetramine fuel tablets) —
smokeless, ashless, non-hygroscopic.

| Item | Combustion | Molar enthalpy | In-game heat value |
|---|---|---|---|
| Hexamine | C₆H₁₂N₄ + 9 O₂ → 6 CO₂ + 6 H₂O + 2 N₂ | 4200 kJ/mol | **28.8 MJ** |

> **28.8 MJ looks outrageous next to coal's 2.7 MJ, but that is molecular weight talking, not energy density.**
> By mass hexamine is about 30 kJ/g against carbon's 33 kJ/g — essentially the same. This mod's heat-value
> anchor has always been **per mole** (ethylene 9.7 and propylene 14.1 come from exactly that), and switching
> to per-mass would overturn the whole existing family, so the anchor stays put.
>
> It is also **not economical to burn**: one hexamine costs six formaldehyde and four ammonia, which means the
> whole C1 chain and the nitrogen chain have to be running first. Its point is that formaldehyde finally has a
> terminal outlet — burn it and it is gone.

**Neither of the other two gets a heat value**: urea is a fertiliser and a feedstock, and nobody burns urea for
warmth; plastic is a vanilla item and its properties are not ours to set.

### Why the urea-formaldehyde resin produces vanilla Plastic directly

Because **the expensive part of a new item is not drawing its icon — it is finding it a downstream.**

This mod already has a cautionary example: **nitric acid**. The nitrogen chain builds all the way up to it and
stops; not one recipe in the whole repo consumes it. Adding a "urea-formaldehyde resin" item here that likewise
had no downstream would simply be a second nitric acid.

Urea-formaldehyde resin *is* a plastic in reality (an amino plastic, among the first thermosets — a century of
plywood glue and electrical fittings), so it lands straight on **vanilla Plastic**, on the same precedent as
mapping polyethylene onto vanilla Plastic. Plastic is a heavy vanilla consumable, so this line will never back up.

> **It is not cheaper than vanilla's own recipe** (Refined Oil ×2 + Coal ×1 → Plastic ×1). Two urea cost four
> ammonia and two carbon dioxide; three formaldehyde cost three methanol plus oxygen — it works out far more
> expensive. What it buys is not cost, it is **a plastic route that never touches a drop of crude oil**: coal and
> water in, plastic out.

> **It is the only one of the three that does not balance.** Urea-formaldehyde resin is a crosslinked network
> with no fixed formula (the same property as Vanadium Ingot · Residue Extraction). The ratio is anchored on the
> real **molar F/U = 1.5** (industry uses 1.2–2.0, and 1.5 is the classic value), which is where urea ×2 +
> formaldehyde ×3 comes from.


### Organic chemistry, phase two: propylene + ammonia → a third route to carbon nanotubes

Phase one gave formaldehyde and carbon dioxide somewhere to go. Phase two builds **a second trunk
line**: it brings the olefin chain and the nitrogen chain together, and ends on vanilla's own
carbon nanotube.

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Acrylonitrile · Propylene Ammoxidation** (2 C₃H₆ + 2 NH₃ + 3 O₂ → 2 C₃H₃N + 6 H₂O) | Redox Chemical Plant | Propylene ×2 + Ammonia ×2 + Oxygen ×3 → **Acrylonitrile ×2 + Water ×6** | 3 s |
| **Polyacrylonitrile · Addition Polymerisation** | **Chemical Plant** | Acrylonitrile ×2 → **Polyacrylonitrile ×2** | 4 s |
| **Carbon Nanotube · PAN Carbonisation** (2 C₃H₃N → 6 C + N₂ + 3 H₂) | **Smelter** | Polyacrylonitrile ×2 → **Carbon Nanotube ×1 + Nitrogen ×1 + Hydrogen ×3** | 5 s |

The first and third balance atom for atom. The second is addition polymerisation, which **has no
byproduct and conserves mass by construction**, so both sides carry the same count — that is not
laziness, it is what an addition-polymerisation recipe looks like (compare the urea-formaldehyde
resin: condensation polymerisation, two monomers in and water out).

> **Why the entry point must be propylene and ethylene will not do.** Ammoxidation starts by
> abstracting an α-hydrogen from the olefin, and all four of ethylene's hydrogens sit on the
> double-bonded carbons — there is no α position. Only propylene's extra methyl group has one.
> That gives propylene a second irreplaceable role here (the first being the more concentrated
> rung of the hydrocarbothermic reduction family).

### Nitrogen is only a carrier on this line — you get all of it back

Two ammonia cost one nitrogen and three hydrogen, and the carbonisation step gives back exactly
one nitrogen and three hydrogen.

```
Nitrogen ──Haber──> Ammonia ──ammoxidation──> Acrylonitrile ──polymerise──> PAN ──carbonise──> Carbon Nanotube
   ↑                                                                                   │
   └───────────────────── nitrogen + hydrogen returned in full ────────────────────────┘
```

**This was not arranged for elegance — it is simply what happens**: nitrogen's job is to carry the
nitrile group in and make the chain cyclise into a ladder under heat. Once carbonisation is done
its job is over and it is driven off. So the **net consumption of this line is propylene and oxygen
only** — the nitrogen taken from a gas giant circulates.

### Carbon fibre was deliberately not made a new item

In reality this route ends at carbon fibre. But **vanilla already has the carbon nanotube** as its
"late-game pure-carbon structural material", and this mod additionally remaps the spiniform
stalagmite crystal's mining output onto it. Adding a second item in the same position would run
straight into this repo's own test: **no better on the four axes and no cheaper = dead content.**

So carbonisation produces **vanilla Carbon Nanotube** directly, with no new item. It is the same
move as phase one's urea-formaldehyde resin landing on vanilla Plastic, and it buys the same two
things: a terminal that cannot back up, and a carbon-nanotube route that **does not depend on the
spiniform stalagmite vein** — much as Fischer-Tropsch freed refined oil from crude deposits.

> **One approximation to state plainly**: carbonising PAN in reality yields carbon fibre or carbon
> nanofibre, which is **the same family but a different form** from a nanotube (tubes are grown by
> vapour deposition). Landing it on vanilla's nanotube follows the "polyethylene → vanilla Plastic"
> mapping convention; it is not a claim that the two are the same thing.

> **The output count of 1 is a balance knob**, not a stoichiometric result — vanilla's nanotube has
> no defined carbon content, so how many tubes six carbons make is our decision. It is anchored on
> the real mass yield: PAN to carbon fibre runs about 50% (68% theoretical, the rest leaving as HCN
> and CO), so two in and one out.


### Organic chemistry, phase three: the aromatic trunk, cut down to the cumene process alone

Aromatics are the largest family on the whole tree — benzene, toluene, xylenes, ethylbenzene,
styrene, cyclohexane, terephthalic acid, PET… Building all of them would burst the item grid
(vanilla's page 1 is already 111/112 full, so new items land past column 14, and the loot filter
and signal pickers cannot draw anything out there). So this phase is **cut down to one line**:
benzene → cumene → phenol + acetone, four new items net.

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Benzene · Steam Cracking** | Redox Chemical Plant | Refined Oil ×6 → **Ethylene ×6 + Propylene ×2 + Benzene ×1 + Hydrogen ×3** | 4 s |
| **Benzene · Methanol to Aromatics** | **Catalytic Reactor** | Methanol ×6 → **Benzene ×1 + Water ×6 + Hydrogen ×3** | 5 s |
| **Cumene · Benzene Alkylation** | Chemical Plant | Benzene ×1 + Propylene ×1 → **Cumene ×1** | 2.5 s |
| **Phenol · Cumene Oxidation and Cleavage** | Redox Chemical Plant | Cumene ×2 + Oxygen ×2 → **Phenol ×2 + Acetone ×2** | 3 s |
| **Plastic · Phenolic Resin** | Chemical Plant | Phenol ×3 + Formaldehyde ×4 → **Plastic ×4 + Water ×4** | 3 s |

The first four balance exactly.

### Benzene has two entrances: one needs no oil, the other no gas giant

```
Refined Oil ──steam cracking──┐
                               ├──> Benzene ──+propylene──> Cumene ──+oxygen──> Phenol + Acetone
Methanol ──methanol to aromatics──┘
```

**Steam cracking** does not dominate the existing Propylene · Catalytic Cracking, nor the reverse:
the same six refined oil either crack all the way to olefins (propylene ×4 + ethylene ×6) or trade
two propylene for one benzene and three hydrogen. Different product sets — take whichever covers
what you are short of.

**Methanol to aromatics sits on the Catalytic Reactor for a reason.** MTA runs on ZSM-5 just as MTO
does, and **aromatisation cokes harder than olefin production**: an aromatic ring is the step before
coke, and the same acid sites that close a ring will let it keep growing. Running it continuously
means changing catalyst continuously, which is precisely what that building exists for. What it buys
is **aromatics without petroleum** — methanol needs nothing upstream but coal and water.

### The cumene process: one in, two out, with the ratio fixed by chemistry

This is the famous two-birds-one-stone of industrial chemistry. Oxygen takes the lone hydrogen off
the tertiary carbon and hangs a peroxide chain there; add acid and the skeleton rearranges and
splits along it, the ring half leaving with a hydroxyl as phenol and the remaining three carbons as
acetone. Nearly all the world's phenol comes this way.

> **Phenol and acetone come out 1:1, and that is a stoichiometric result, not a balance knob.**
> So "too much acetone" is this line's built-in trade-off rather than a tuning miss — acetone has a
> heat value (12.3 MJ), and burning the surplus is a legitimate outlet.

> **Why it has to be an isopropyl group.** Ethyl or methyl will not do: the oxidative cleavage needs
> a **tertiary carbon with exactly one hydrogen on it**, and only the branch point of an isopropyl
> group provides one. This is propylene's third irreplaceable role in this mod (the first two being
> the more concentrated rung of hydrocarbothermic reduction, and the ammoxidation to acrylonitrile).

### Why two routes to vanilla Plastic are allowed to coexist

The urea-formaldehyde resin (phase one) and the phenolic resin (this phase) both produce vanilla
Plastic. That **looks** redundant and is not:

| | Urea-formaldehyde | Phenolic |
|---|---|---|
| Eats | Urea + formaldehyde | Phenol + formaldehyde |
| Needs upstream | **Ammonia → nitrogen → a gas giant and orbital collectors** | **Benzene → refined oil or methanol → coal and water** |
| Resin charged per plastic | 105 g | 101 g |

**One needs no oil, the other no gas giant, and neither is more efficient** — the charged mass per
plastic is deliberately aligned. It is the same design as "three different routes all reach
ethylene": take the one that fits what you have, rather than the later one simply being better.

### Two counter-intuitive phases

- **Benzene melts at only 5.5 °C** and will freeze in the pipes in cold weather — a real nuisance in
  a real chemical plant. It is still handled as a fluid in game; the detail lives in the description
  rather than in `isFluid`.
- **Phenol is a solid at room temperature** (melting at 40.9 °C) — in a laboratory it comes as lumps
  of white crystal, not a liquid. So it travels by belt and storage box and **does not go into a
  tank**.

### Still not connected: nitric acid has no downstream

Nitric acid's real outlet is **nitration** (benzene → nitrobenzene → aniline), and benzene now
exists, so it could in principle be built. It was not, because **aniline has no downstream of its
own**: its destinations are MDI and polyurethane, which is another two or three items, and the chain
would still end on something nothing wants. Rather than add two items and one more dead end, nitric
acid is left hanging until its downstream has been thought through properly.


### Linking up with petroleum chemistry: three refinery units

With organic synthesis built out to phase three, three things were still missing between it and
the oil side. Filling them in turns **refined oil from an ingredient into a real hub**, and finally
gives the barrel-bottom residue three places to go.

#### 1. Vanadium extraction now eats the residue — it used to be a dead connection

The `Vanadium Residue Oil` item's own comment has always said "**Vanadium Ingot · Residue
Extraction is extracting from this**", while that recipe actually consumed **Refined Oil ×40** and
had nothing to do with the residue at all. The link the comment described did not exist in the data.

It is now **Vanadium Residue Oil ×12 + Sulfuric Acid ×4 → Vanadium Ingot ×1 + Carbon Dioxide ×8**.
The basis is solid: vacuum residue carries 100–1000 ppm vanadium and light distillate carries almost
none — **the vanadium was concentrated into the bottom of the column all along**.

> **This is not a back door for vanadium.** Twelve residue costs 48 crude (vacuum distillation is
> four crude in, one residue out), the same order as the roughly 53 crude that 40 refined oil worked
> back to. The difference is that those 40 distillate were destroyed before; now only the bottom
> fraction is spent and the distillate is kept.

#### 2. Real catalytic reforming (the trunk source of aromatics, and of hydrogen)

**Benzene · Catalytic Reforming: Refined Oil ×3 → Benzene ×2 + Hydrogen ×6** (exactly balanced),
on the Catalytic Reactor.

Before this, benzene could only come from steam cracking (where it is one of four byproducts) or
from methanol to aromatics (which never touches oil). In reality the trunk source of aromatics has
always been reforming — a refinery runs it for octane, a chemical plant wants the rings it throws
off. **The same unit, two entirely different reasons for owning it**, which is precisely where
petroleum chemistry and organic synthesis meet.

It is also the refinery's **hydrogen source**: most industrial hydrotreating hydrogen is reformer
byproduct, and it lines up exactly with the desulfurisation recipe below.

> **Putting it on the Catalytic Reactor is not a borrowed excuse.** Platinum-rhenium catalyst cokes,
> and the industrial unit is literally called **CCR — continuous catalyst regeneration**, with
> catalyst circulating between reactor and regenerator. That is the same thing this building is.

> One rename came with it: the old `Hydrogen · Catalytic Reforming` actually performs **steam
> reforming** (hydrocarbon + water → syngas), while "catalytic reforming" means something else
> entirely in a refinery. It is now **`Hydrogen · Steam Reforming`**. The recipe ID did not move;
> saves reference IDs, not names.

#### 3. The sulfur line is connected

```
Residue Oil ──hydrodesulfurisation──> Sulfur + Refined Oil
                          Sulfur ──contact process──> Sulfuric Acid
```

| Recipe | Machine | Contents | Time |
|---|---|---|---|
| **Sulfur · Residue Hydrodesulfurisation** | Redox Chemical Plant | Residue Oil ×4 + Hydrogen ×2 → **Sulfur ×1 + Refined Oil ×3** | 4 s |
| **Sulfuric Acid · Contact Process** (2 S + 3 O₂ + 2 H₂O → 2 H₂SO₄) | Redox Chemical Plant | Sulfur ×2 + Oxygen ×3 + Water ×2 → **Sulfuric Acid ×2** | 2.5 s |

**Almost all the world's sulfur really is made this way** — not mined, but taken out of oil and gas
as a byproduct. Nor is it taken out in order to sell it: sulfur corrodes equipment and poisons
downstream catalysts, so removing it is mandatory and selling it is incidental.

Three things this buys:

- **Sulfuric acid gains a second route.** It had only the gypsum process (which needs a gypsum vein
  and yields glass as well); now there is an oil route (which needs crude and hydrogen). Neither
  dominates: take the oil route if you have no vein, the vein route if you have no oil.
- **The residue now has three destinations**: vanadium, sulfur, or low-temperature fuel. What to do
  with a given barrel-bottom ought to be a choice.
- The hydrodesulfurisation recipe **does not balance, and the reason was already on file** — vacuum
  residue is a fraction rather than a compound, with no fixed formula, and its sulfur runs 2–5 wt%
  depending on the crude. Exactly how the residue oil itself and Vanadium Ingot · Residue Extraction
  are already handled.

> **A real coincidence worth noting**: the contact process's catalyst is **vanadium pentoxide** — and
> V₂O₅ is also precisely why residue oil cannot be burned hot (it melts below hot gas path
> temperatures and attacks turbine blades). The same substance is a poison in one place and the
> catalyst in another. That is not invented.

#### 4. One barrel of crude, cut four ways — each downstream eats its own

With the first three in place, one question remained: **every one of these recipes was eating the same
"refined oil", while in reality they do not eat the same oil at all.**

```
Atmospheric and Vacuum Distillation
    Crude Oil ×10 → Naphtha ×2 + Refined Oil ×3 + Vacuum Gas Oil ×3 + Residue Oil ×2
```

The 2 : 3 : 3 : 2 yield is anchored on the real cut fractions of a medium crude (naphtha ~22%, middle
distillate ~30%, vacuum gas oil ~27%, vacuum residue ~21%). **Both columns are one recipe**: a
Chinese refinery calls the atmospheric and vacuum columns a single unit, not two machines. Four
products is exactly this mod's proven ceiling.

| Cut | Carbon / boiling range | What it really is | Who eats it |
|---|---|---|---|
| **Naphtha** | C5–C10 / 30–200 °C | Light distillate | **Steam cracking, catalytic reforming, steam reforming** |
| Refined Oil | C10–C20 / 200–350 °C | Kerosene and diesel | Vanilla graphene / plastic / organic crystal, and power |
| **Vacuum Gas Oil** | C20–C40 / 350–550 °C | VGO | **Catalytic cracking, hydrocracking** |
| Residue Oil | C40+ | Vacuum residue | Vanadium, sulfur, low-temperature fuel |

**Four existing recipes therefore changed feedstock**, each for a hard reason:

- **Benzene · Steam Cracking ← naphtha**: an ethylene plant's formal name is a *naphtha cracker*
- **Benzene · Catalytic Reforming ← naphtha**: a refinery calls this cut *reformer naphtha*
- **Hydrogen · Steam Reforming ← naphtha**: light ends reform, heavy ends just coke
- **Propylene · Catalytic Cracking ← vacuum gas oil**: an FCC exists to turn heavy into light; cracking
  something already as light as diesel is pointless

> **The line between gas oil and residue at an FCC is exactly this building's mechanic.** Vanadium and
> nickel from the residue deposit on the molecular sieve and kill the catalyst **permanently**; gas oil
> carries far less metal. Residue catalytic cracking (RFCC) does exist in reality, at the price of several
> times the catalyst consumption — and "the catalyst dies" is precisely what the Catalytic Reactor is about.

#### Hydrocracking: turning the heavy into the wanted

```
Naphtha · Hydrocracking    Vacuum Gas Oil ×4 + Hydrogen ×3 → Naphtha ×3 + Refined Oil ×2
```

The core conversion unit of a refinery, and its purpose fits in one line: **turn what is heavy and
plentiful into what is light and wanted**. ⚠️ It does not balance (a fraction has no formula), but
**four oil in and five oil out is the right direction** — hydrocracking's liquid yield exceeds 100%
(the industry calls it volume swell), and the extra volume is the hydrogen that went in.

> **It pairs with catalytic reforming.** Reforming makes hydrogen, hydrocracking consumes it, and the
> naphtha hydrocracking produces goes back to feed the reformer — a real refinery's hydrogen balance
> is those two units offsetting one another. That loop is now closed here.

#### One abstraction, stated out loud: one oil item = 4 CH₂ units

Every oil item in this mod is accounted for in those units, which makes the four cuts
**interchangeable stoichiometrically**. That is what let the three already-balanced equations (steam
cracking, catalytic reforming, steam reforming) change feedstock **without a single edit**.

The justification is that a real refinery accounts in barrels, and a barrel of naphtha and a barrel of
gas oil genuinely carry the same order of carbon. What separates the four cuts is **not their carbon
content but which reaction each can feed** — which is what this whole section is about.

#### What was not built: desulfurised refined oil

"Oil with the sulfur taken out should burn hotter" is physically correct — that temperature table is
sorted by sulfur in the first place. But expressing it would require **a new item**, and that item
would **strictly dominate vanilla refined oil** as a fuel while every recipe that eats refined oil
would have to decide whether it accepts it too. That is exactly the dead-content and dominance
problem the previous three phases kept dodging, so desulfurisation returns vanilla refined oil and
adds no new item.


## XII. The standard for new items: follow real chemistry and physics

**This is a hard rule, not flavour text.** Every property of every new item in this mod is **derived**, not picked;
once set, the basis goes into a `//` comment at the matching place in `ores.json`, so that the next person retunes
**the anchor** rather than guessing at the number again.

| Property | Basis | Example |
|---|---|---|
| Recipe ratios | A **balanced** chemical equation | 2 Al₂O₃ + 3 C → 4 Al + 3 CO₂ ⇒ ore ×2 + coal ×3 → aluminium ×4 + CO₂ ×3 |
| Whether it is a fluid | Phase at ambient conditions | Methanol is liquid; CO / ethylene / formaldehyde are gases → all fluids. Ores are solid |
| Whether it burns | Whether it actually burns in reality | CO₂ is fully burned and oxygen is an oxidant → **neither gets a heat value** |
| Heat value | Molar enthalpy of combustion, scaled against one vanilla anchor | Coal 2.7 MJ ↔ 393.5 kJ/mol; CO 283 → 1.95, methanol 726 → 5.0, ethylene 1411 → 9.7 MJ |
| Which machine | The **reaction class**, not convenience | MTO is a dehydration, not a redox → stays in the vanilla Chemical Plant; the other five go to the Redox Chemical Plant |
| Icon | Structural formula for molecules; what the real material looks like otherwise | Ball-and-stick models of C≡O, CH₃OH, H₂C=O, H₂C=CH₂ |
| Description | The actual industrial process | Water gas, Fischer-Tropsch and MTO are all real names |

This rule has already bought three concrete things:

- **Balance stays arguable.** Every heat value traces back to one anchor, so retuning a whole family means editing
  that one anchor rather than maintaining a table of numbers invented by feel.
  Note in passing that **vanilla is not internally consistent** (its hydrogen is about 4x more generous per mole
  than its coal), so pick one anchor and say in the comment which one.
- **It catches design errors early.** Asking "is this actually a redox reaction?" is what moved carbothermic
  aluminium and carbothermic cobalt out of the smelter, and what kept MTO out of the new machine.
- **By-products stop being dead ends.** Carbon dioxide (carbothermic off-gas) and oxygen (the other half of water
  electrolysis) had no takers until real chemistry said where they go: CO₂ hydrogenated to methanol, oxygen as the
  oxidant for formaldehyde.

> **The one property that is not physical is stack size.** It is a pure balance knob; this mod uses a flat 300
> everywhere (vanilla is 100 for solids, 20 for fluids). Do not try to justify it chemically.

## XIII. Interface Changes

The items and buildings this mod adds do not fit in vanilla's fixed grids, so a few places were widened.
**With only one page, or nothing overflowing, everything looks exactly like vanilla.**

### Replicator: horizontal paging

Each vanilla page is 8 rows × 14 columns, and page 1 (items) is essentially full. This mod's ores, ingots and
recipes can only land at column 15 and beyond — and vanilla clips everything from column 15 onward.

The replicator (Ctrl / the replace key) now has a horizontal scrollbar along the bottom, 3 pages × 14 columns by
default. The page count is set by `replicatorPages` in `megabuildings.json`.

> Extending onto more **rows** is not possible: vanilla skips outright at `row >= 8` and never draws row 9.

### Recipe picker: the same paging

The window you get from "select a recipe" on a machine is **a separate renderer**, and it clips column 15 just the
same. Without the fix, opening the recipe picker on a new machine shows a blank grid on every tab — which looks
exactly like "the recipes are not attached to the machine", when they are perfectly attached.

It now has a horizontal scrollbar too, and **jumps to the first usable recipe when opened** (vanilla's tab selection
is remembered across windows, so opening the picker for a machine that only accepts a custom recipe type would very
likely land on the previous tab and show nothing).

### The eight mega buildings now have distinct shapes

They used to clone one vanilla model (the logistics station) and differ only by colour — five of the same
building in five paints. Each one's geometry is now **generated in code**, with a silhouette of its own:

| Building | Shape |
|---|---|
| Sky Assembly Plant | three tapering tiers + a central column |
| Foundry Smelter | slim tower + six cooling rings (the only vertical silhouette) |
| Calciner Chemical Plant | three tanks + cross-piping + a thin stack |
| Precision Machining Centre | low body + gantry (two posts and a beam) + hanging spindle |
| "Azure" Particle Collider | a ring on four supports + central target chamber (the only round one) |

The colours are still there, but they are no longer the only thing telling the buildings apart.

The textures are generated too: plate seams, rivets, grating walkways, hazard striping, concrete bases, and
window bands that light up at night. Plus railings, ladders, pumps, valves and rail carriages — **the industrial
read comes from those small parts, not from the overall shape.**

> Only the **drawn mesh and its textures** changed: footprint, colliders and belt attachment points are all still
> the original ones, so belt connection and placement feel exactly as before. Roughly 800–2000 triangles per
> building, the same order as a vanilla one.

> Buildings too big or too small? Tune `modelScale` in `megabuildings.json` (default 0.6).

> Not to your taste? Set `proceduralModels` to `false` in `megabuildings.json` and it falls back to the
> one-model-five-colours look.

### Item picker: a search box

The station item slots, the inserter filter and the storage filter all go through one window, and it too draws only
14 columns. This mod's items sit in columns 15–42, so horizontal paging does reach them — but **a logistics station
has 30 slots to configure**, and hunting each one across three horizontal pages is not a workflow anybody finishes.

The window now has a toolbar along the bottom:

- **Just start typing when it opens** (the field is focused automatically) to filter by item name or item ID
- **Enter** takes the first match, **Esc** closes the window
- **◀ page ▶** on the right is horizontal paging; the **mouse wheel** over the grid pages too
- While searching, the right-hand label shows the match count; it turns red when nothing matches

**Search spans both tabs**: it does not matter whether something lives on the Items or the Buildings page — typing
finds it either way, because looking for Carbon Dioxide should not require knowing which tab owns it. Typing the
Chinese name works on the English client as well.

> While searching, matches are **laid out sequentially from the first cell** rather than by grid position — so
> "which column is it in" simply stops being a thing in search mode. Locked items are not returned, exactly as when
> not searching.

> Game hotkeys do not fire while you are typing.

### More than two products

Vanilla's product UI has **exactly two widget sets**, and this mod has **nine three-product recipes and one
four-product recipe** (the carbothermic family, molten-salt lithium electrolysis, the ferrochrome route, and
tungsten acid leaching).

This is not just a missing icon: the assembler window's horizontal layout is a set of constants chosen by product
count, and vanilla only distinguishes "one" from "more than one" — so a three-product recipe gets the
**two-product layout**, leaving the rate text and the ingredient box in the wrong place.

Both windows now size themselves to the real product count:

- **The assembler window** (Chemical Plant / Electrochemical Plant / Redox Chemical Plant): product cells, progress
  rings, counts and hover tips are all filled in, and clicking the icon takes the items out as usual
- **The replicator's crafting tree**: the main box widens and the icons re-centre across three

> **With three or more products the downstream branch is not drawn.** Vanilla lays those consumer icons out with
> *two*-product geometry, so once there are three product icons the connector lines and nodes no longer line up
> with anything — the whole branch is hidden instead. Recipes with two products or fewer are unaffected and still
> show their downstream as before.

> One vanilla defect fixed along the way: taking products out by clicking the icon used to clear the product buffer
> whether or not the inventory had room (so a full inventory destroyed them); the added cells deduct only what
> actually went in.

### Build bar: the child row scrolls horizontally

Vanilla allows at most 12 children per build category, and once a screen is full that is the end of it. There is now
a horizontal scrollbar above the child row, and **a category can hold up to 36**:

- **Drag the handle** or **click the track** to page
- **Scroll the wheel** over the child row to page as well

With only one page the scrollbar hides itself, so vanilla's categories look completely unchanged.

> The technique is **move the data, do not change the reads**: the mod keeps its own complete child table and copies
> the current window into vanilla's array each frame, before vanilla refreshes. Vanilla reads its own data from
> beginning to end, so clicks, F1–F12 and tooltips all line up automatically. It also makes the "a slot holds a
> building but the interface has no button there" crash structurally impossible.

> **The startup log line "slot N holds X but the interface has no button there" is normal, not a fault.** Vanilla
> fills slots from `BuildIndex` before the scrolling takes over, so anything beyond one screen's worth of buttons
> (10 here) briefly has no button; the fallback clears it and makes a note. The scrolling keeps its own complete
> copy, so the building is still reachable. Verified: the Electrochemical Plant sits in slot 11, logs this at
> startup, and is found normally in build category 5.

### Double-click in the build bar → jump to the replicator

Double-clicking a building in the build bar opens the replicator with its recipe selected. Vanilla's path only
handles **the first 14 columns of pages 1 and 2**, so double-clicking this mod's buildings either did nothing or
opened with nothing selected — both the mega buildings (page 3) and the Electrochemical Plant (extended columns)
were affected.

It now switches to the right tab and column page before selecting.

---

## XIV. Cheat Switches (all on by default)

Six switches in `cheats.json`, **all on by default**, behind a master `enabled` switch (set that to false and all six are disabled at once).

> **This default was flipped after 1.5.0.** All six used to be off, on the reasoning that something which bypasses the rules should not be on unasked. Turning them on by default was the owner’s call: this mod exists to take the late game off its leash, nearly everyone who installs it switches them on anyway, and the old default was one more step to nowhere.
> **To get the old behaviour back, override `cheats.json` and set them to false — no rebuild needed.**

> **Editing this file needs no rebuild.** Create or edit
> `BepInEx\config\ProjectEden\cheats.json` in your profile and it takes effect on the next launch — it overrides the
> copy embedded in the DLL. Delete the file to go back to the embedded defaults (all five off). The startup log
> always states the current status, including when everything is off.
>
> The other 11 config files work the same way; content configs simply track the version, so there is rarely a reason
> to do it.
> **Note**: an override file you forgot to delete will make every later change to the embedded JSON look like it
> "did not take effect", so every use of an override prints a WARNING with its path.

Unlike everything else in this document — which retunes content — these five **bypass the game's rules outright**.
That is why they live in their own file, and why turning on any of them puts a WARNING in the startup log naming
what is on: this mod is debugged by reading the log, and six months later "no collision is on" and "buildings can
overlap, that's a bug" look identical.

| Switch | Effect |
|---|---|
| `instantBuild` | **Instant build.** A prebuild is finished the moment it is placed, without waiting for construction drones. **Materials are still charged** (taken from the mecha inventory); anything you cannot afford is left for the drones — it saves time, not materials |
| `instantBuildPerTick` | How many to finish per settlement pass, 0 for the default of 100. Finishing an entire large blueprint at once costs a frame; spread over a few, it is invisible |
| `noConditionBuild` | **Build without condition.** Every build rejection is allowed through, and the cover/rebuild flags are cleared with it |
| `noCollision` | **No build collision.** Buildings can overlap freely; belt connection and mecha collision are unaffected |
| `noCollisionPhysics` | Additionally switches off the planetary collider pool so the mecha can walk through buildings. **Overlapping does not need it**, and it blinds the build tools' cursor picking |
| `powerNoSpacing` | **No spacing limit on power buildings.** Wind turbines, solar panels, thermal plants and geothermal plants can all be placed flush |
| `waterPumpAnywhere` | **Pump anywhere.** Water pumps can be built on land |

### Things to know first

- **Build without condition does not apply to mining machines.** A miner's build check is simultaneously the "is
  there actually ore in range" check; forcing it through produces a miner with an empty vein array — it builds fine
  and never produces anything. For overlapping miners and mining oil, use the two switches in `advancedminer.json`.
- **Build without condition ≠ free building.** Vanilla places a prebuild and the drones fetch the materials
  afterwards, so allowing "not enough items" only lets you place it; you still need the materials for it to be
  built.
- **What makes buildings overlap is letting the build check through**, in two places: the "collides with another
  object" verdict *together with* the cover/rebuild flags written by the same piece of code — the latter is a silent
  second gate, and clearing only the former gives you "no error message, but clicking still does nothing".
- **`noCollisionPhysics` is the one switch of the six with a real side effect, and it is now on by default too.** It additionally switches
  off the planetary collider pool, letting the mecha walk through buildings — at the cost of blinding the build
  tools' cursor picking: they use Unity physics raycasts to identify what is under the cursor (the belt tool alone
  has five), and with the pool off those hit nothing. Overlapping does not need it. (Selecting and dismantling
  buildings are unaffected — that path uses the game's own raycasting and never touches Unity physics.)
- **Belts failing to connect was a bug, now fixed.** No-collision was clearing the preview's "cover" flag — a gate
  worth clearing for buildings, but on a belt that flag *is* the "join this existing belt" intent. The belt tool no
  longer clears it.
- **No power spacing allows three rules through at once.** Vanilla's spacing has three tiers: 3.5 m between any two
  power generators (this is the one solar panels are subject to), 10.5 m for wind turbines, 12 m for geothermal.
  All three are computed in the same piece of code and cannot be separated.
- **Pump anywhere still needs a planet with water.** The pump's product comes from the planet's water type; on a
  waterless world it still produces nothing. This switch only lifts the "must be built on water" placement rule.
- **You can place what you cannot pay for, and it may not get built.** "Build without condition" allows "not enough
  items" through as well, so you can place with an empty inventory; "instant build" only builds what you can afford,
  and the rest stays as a blue ghost (vanilla's drones cannot build them either). It looks a lot like "instant build
  is flaky" when it is actually an empty inventory.

### The red cursor text is cleared too

Allowing build conditions through only makes the building **buildable**; the cursor still shows a red "cannot build"
message, because that text is written by `CheckBuildConditions` **itself at the end of the method**, earlier than
any postfix patch:

```
ok = true;
for each build preview:
    condition is Ok, or the one this tool tolerates → skip
    otherwise ok = false; cursor turns into a red X; cursor text = this rejection's explanation
if ok: cursor returns to normal, text becomes something like "Click to build (3)"
```

So this mod replaces **the one or two places that loop reads the condition** with its own filter function and leaves
everything else to vanilla — which puts the red X, the red text, the "(3)" count and the geothermal strength readout
all back to normal automatically, with nothing to reproduce by hand. With the cheats off the filter returns the
field unchanged, which is the same as not patching at all.

All five build tools are covered, with one log line each at startup:

```
BuildTool_Click.CheckBuildConditions：光标提示的条件判定已接管 2 处
BuildTool_BlueprintPaste.CheckBuildConditions：光标提示的条件判定已接管 3 处
BuildTool_Path / Addon / Inserter …：已接管 1 处
```

("cursor condition check taken over, N sites". A tool that fails to take over logs an ERROR rather than failing
silently.)

> **The blueprint paste error panel is deliberately left alone.** That panel goes through a different path
> (`AddErrorMessage`), and keeping it tells you which cells vanilla would have rejected, which is useful when
> debugging a blueprint.

### How to confirm they are actually working

Every build condition that is allowed through writes one line to the log, **once per kind**. With a cheat on, these
two lines should appear in `LogOutput.log` first:

```
cheats.json 读的是磁盘覆盖文件，内嵌的那份被忽略：...
以下作弊项已开启：建造秒完成（…）、无条件建造（采矿机除外）、无碰撞（…）、发电建筑无间距限制、平地抽水
```

(The log itself is written in Chinese — that is this repo's convention. The first line says the disk override was
used and the embedded copy ignored; the second lists which cheats are on.)

**If those two lines are missing, the config was not read** — stop looking anywhere else. Once they are there, go
build something and the matching lines appear in pairs:

```
建造被拒：WindTooClose（6），建筑「风力发电机集群」
作弊：已放行建造条件 WindTooClose（6）
作弊：建造秒完成已生效，本次结算建好 1 个（每次上限 100，材料从机甲背包扣）
```

(Build rejected → cheat allowed that condition through → instant build finished one this pass.)

Exercised in game so far: `PowerTooClose(5)`, `WindTooClose(6)`, `Collide(34)`, `NotEnoughItem(2)`, plus instant
build and the collider pool shutdown, with no exceptions throughout. `NeedWater(24)` (pump anywhere) only fires when
a pump is actually placed on land and has not been seen in the log yet.

> **Why this is not a copy of CheatEnabler.** The reference is soarqin's CheatEnabler (MIT), but the implementation
> took another route. Its "no spacing" replaces the constants `110.25f` / `144f` in the check with `1f` globally;
> in this game version the blueprint paste check contains `110.25f` **seven times, three of them turret spacing** —
> replacing all of them would take out the turret rule as well.
> This mod erases the build preview's rejection *verdict* afterwards instead, and the turret sites never write that
> field, so they separate for free.
>
> "Instant build" is the reverse: vanilla **already has** that path, merely locked behind sandbox mode
> (`ConstructionSystem.FastBuild`, with batch rendering and drone recall already built in).
> This mod reuses that machinery and only changes "ignore materials" into "charge first", which needs five fewer
> patches than CheatEnabler.

---

## XV. Alloy Ammo: one product, different raw materials

One recipe, **any two alloys you like**, and the combination decides the damage tier and the yield.
**The recipe is not public** — the replicator shows a single "Alloy Ammo" entry, and which pair gives which tier is
for you to find out.

| | |
|---|---|
| Machine | Assembler |
| Inputs | Any two alloys, 2 parts each (the same alloy twice is allowed) |
| Output | **Alloy Ammo I – V**, five tiers |
| Ammo type | Bullet — feeds gauss turrets and the mecha |

Select the recipe and two picker rows appear below the assembler window. **Click the left half to go back, the
right half to go forward**:

```
【Alloy Ammo · pick two alloys】
Alloy A   ◀  Cemented Carbide  ▶
Alloy B   ◀  Chrome-Vanadium Tool Steel  ▶

H 87.4  T 13.8  C 71.0  E 11.2  →  Alloy Ammo V ×8   DMG 2240
```

### The five tiers

Scaled against vanilla's **Magnum Ammo Box** as the anchor (700 damage / 30 rounds) — as with heat values, the
config holds **multipliers, not absolutes**, because vanilla's ammo numbers live in `resources.assets` and writing
an absolute would be guessing. The startup log prints the resolved values.

| Tier | Damage | Rounds per box |
|---|---|---|
| Alloy Ammo I | 700 | 30 |
| Alloy Ammo II | 945 | 33 |
| Alloy Ammo III | 1260 | 36 |
| Alloy Ammo IV | 1680 | 39 |
| Alloy Ammo V | **2240** | 44 |

### How the pair converts

The two alloys are blended 50:50 by the same mixture rule the alloys themselves use (hardness by power mean,
**toughness by harmonic mean**), and then:

- **Damage tier ← penetration score** (hardness × 0.85 + corrosion × 0.15). Hardness dominates, which is why real
  armour-piercing rounds use a tungsten carbide core
- **Yield ← toughness**, with diminishing returns. Brittle stock cracks during forming, so a batch yields less

The harmonic mean on toughness is the load-bearing part: **one brittle plus one tough still comes out brittle**, so
"hard *and* tough" is the rare solution — and it is exactly what no single pure metal can give you.

Measured combinations (the log records each one as you find it):

| Combination | Result |
|---|---|
| Cemented Carbide + Chrome-Vanadium Tool Steel | V, yield 8 |
| Cemented Carbide + Manganese Steel | IV, yield 9 |
| Chrome-Vanadium Tool Steel + Chrome-Plated Copper | III, yield 14 |
| Manganese Steel + Chrome-Plated Copper | I, yield **20** |

> **An honest note on balance.** Tier V is 2240 × 8 = 17920 damage per craft against tier I's 700 × 20 = 14000, so
> **the yield penalty does not offset the damage gain** — with materials free for the taking, V wins outright.
> The real brake is that cemented carbide is expensive (tungsten is a rare-slot ore with no synthetic fallback), so
> the decision is close to binary: do you have tungsten or not. For a gradient instead, raise
> `toughnessExponent` and lower `yieldMax` in `ammo.json` to steepen the yield curve.

### Two limitations to know

- **A turret's quick-fill has only three buttons**, and vanilla's three bullet ammos fill them, so none of these
  five tiers appears there. **Belt feeding and shift-click insertion both work normally**
- **"Discovered" is in memory only** right now, so it is forgotten on restart, and the `???` hiding in the item
  tooltip is not wired up yet. That waits until the numbers settle — bumping the save version for a feature still
  being tuned is not worth it

> **The technique here is worth recording on its own: one product can now have different raw materials.**
> A recipe's input slots (`requires[i]`) are `int`s too, and **can be overridden per building** — until now only
> amounts and the output item were. So 28 combinations of 7 alloys need **one recipe and five outcome items**
> instead of 28 recipes (which the replicator could not show anyway; it cannot add rows).
> It also makes "a secret recipe" possible at all: those combinations are not recipes, so no screen in the game
> lists them.

---

## XVI. English Localization

**Everything** this mod adds has English text: item names and descriptions, recipe names and descriptions, vein
names, building names, the "Made in" and "Type" tooltip lines, the four property axis headers, and the alloy ratio
panel's title. Just switch language in the game settings; nothing else is needed.

### Why it used to show Chinese

Vanilla's `ItemProto.name` is not a field but the result of `Name.Translate()`, and `Translate` **returns the key
unchanged when it cannot find it**. This mod's `Name` was Chinese text, which is not a key, so the Chinese displayed
normally and English displayed the Chinese too.

So the approach is **to register that Chinese string itself as the key**, with the original in the Chinese column
and the translation in the English one — `Name` does not change by a single character. Changing `Name` would also
work, but LDBTool **records proto IDs keyed by name**, and renaming would make all of those bindings miss.

> **It will not overwrite vanilla's translations.** Vanilla's keys are Chinese strings too, and writing over one
> would change that text globally. So every key is checked before registration, and any that already exists is
> skipped and listed in the log. That is why vanilla names like 铁块 or 钢材 need not be in the table.

### Editing the translations / adding a language

`data/i18n.json` is a flat "Chinese → English" table; edit it and rebuild, or drop a file of the same name into
`BepInEx\config\ProjectEden\` as in section XIV and skip the rebuild entirely.

Languages other than Chinese (French, German, Japanese, Korean …) all fall back to **English** rather than Chinese,
which is far more readable for those players.

### What happens if you forget the English

At startup every proto this mod registered is checked, and anything whose name or description contains Chinese with
no entry in the table gets **one WARNING each, printing the original**:

```
以下 2 处中文没有英文译文，切到英文时会原样显示中文（补进 data/i18n.json 即可）：
    钪块
    银白色稀土金属，熔点极高……
```

("N Chinese strings have no English translation and will display as Chinese; add them to data/i18n.json", followed
by the offending strings.)

When everything is covered it writes a "localization check passed" line instead, so it never goes silent just
because there was nothing to do.

---

## XVII. Known Trade-offs

These are **unavoidable side effects** of the changes above, not bugs:

- **Mining machines can also be stacked inside other buildings** — what is allowed through is the generic collision
  check, and the verdict does not say whether the other party was a miner
- **Ordinary mining machines can mine oil too** — the two pieces of code that exclude oil seeps at build time are
  instruction-for-instruction identical, so only one of them cannot be opened up
- **The vanilla Chemical Plant can also run "Crude Oil X-Ray Cracking"** — recipes work by type, not by building
- **Mining machines can no longer be rebuilt/replaced in place** — stacking requires clearing the "cover and
  rebuild" flag, and those are two sides of the same thing
- **A mega building's 30 storage slots are neither visible nor adjustable** — clicking one opens the recipe panel,
  not the station panel (the two panels displace each other and only one can stay). The layout is automatic from the
  current recipe (inputs as Demand, products as Supply), so there is nothing to configure anyway
- **Sorter speed (how fast the arm swings) is unchanged** — what changed is stack level and belt speed; a sorter
  carries more per swing, but a swing takes just as long as in vanilla
- **The small vein icons on the map (M) are still iron's** — that path copies icons into a shared atlas with
  requirements on the texture format, and was not risked; the icons on vein labels and in the planet panel are
  recoloured
- **A machine accepts only one recipe type** — vanilla filters the recipe picker on a *single* type, so the
  Electrochemical Plant cannot do ordinary chemistry and the Redox Chemical Plant cannot do electrochemistry. Making
  one machine take both would mean changing every reader of `assemblerRecipeType` into a set lookup, which is not
  cheap and was not done
- **The Electrochemical and Redox Chemical Plants are on page 2 of the "production" category** — vanilla has already
  filled that category's visible slots in the current version, so new machines queue behind them (reachable with the
  scrollbar above). To put them on page one, change `buildIndex` in `machines.json` to a different category (for
  example `1207` for the "Mega Structures" tab — `1206` is taken by the Wind Turbine Cluster)
- **Lithium metal, formaldehyde and nitric acid have no downstream yet** — lithium is the display piece at the end
  of its chain (battery cathodes use lithium hydroxide, as in reality); formaldehyde and nitric acid are storable,
  shippable products awaiting later chains (nitrogen already has a downstream — see the nitrogen chain)
- **Direct belts cannot keep a mega building fed** — each input port picks up one stack per tick, which is a limit
  on pickup frequency, not buffer size; feeding by logistics station has no such problem

## XVIII. Compatibility

- **GenesisBook**: detected automatically, and this mod's mega-assembler feature disables itself to avoid one
  building being driven by two sets of belt logic
- The mega buildings' icons and models are drawn from scratch: the icons come out of `tools/make_icons.py` and the meshes and textures are generated procedurally at runtime — **no GenesisBook art asset is used**

---

## XIX. The Biodome: a light-bound biological chain

The sixth building on the Mega Structures tab. Two things about it are structurally unlike the other five:

1. **Its recipe type is this mod's own number 11 (Bioculture)**, not a borrowed vanilla one. The other five run
   vanilla recipes that a vanilla machine can also run — they are merely ten thousand times faster. The Biodome's
   three recipes **can be run by nothing else**.
2. **The whole building depends on sunlight.** It is the only building in this mod whose output varies with the
   environment: **full sun = the full 10000x rate, no sun = a complete stop**, with all three recipes stopping
   together.

### The three recipes

| Recipe | Inputs | Products | Time |
|---|---|---|---|
| Log · Photosynthetic Forestry | **none** | Log x4 + Plant Fuel x4 | 3 s |
| Microbial Consortium · Algal-Bacterial Co-culture | Water x4 + Log x2 + Plant Fuel x2 + Oxygen x2 | Microbial Consortium x2 + Carbon Dioxide x2 | 4 s |
| Algal Oil · Solvent Extraction | Microbial Consortium x3 | Algal Oil x1 | 3 s |

The oxygen comes from the existing "Oxygen · Water Electrolysis" (Electrochemical Plant); the carbon dioxide feeds
back into the existing "Methanol · CO2 Hydrogenation", so the carbon goes round a loop and nothing is thrown away.

### Downstream of algal oil: Refined Oil · Transesterification (vanilla Chemical Plant)

Algal oil is not only a fuel — it connects to the existing petrochemical downstream:

| Recipe | Machine | Inputs | Products | Time |
|---|---|---|---|---|
| Refined Oil · Transesterification | **vanilla Chemical Plant** | Algal Oil x2 + Methanol x6 | Refined Oil x6 | 4 s |

`C57H104O6 + 3 CH3OH -> 3 C19H36O2 + C3H8O3`, taking triolein, **balanced**, with the recipe scaled by 2. Methanol
strips the triglyceride's three fatty-acid chains off one at a time, giving three fatty-acid methyl esters (biodiesel)
and one glycerol.

Three things worth stating:

- **Why the vanilla Chemical Plant and not the Redox Chemical Plant.** Transesterification is a substitution at the
  ester group; carbon's oxidation state never changes — the same test that keeps MTO's dehydration on the Chemical
  Plant (see section XII). Turning algal oil into actual alkanes would mean hydrodeoxygenation, which *is* a
  reduction and would belong on the Redox Chemical Plant. Choosing transesterification keeps machine and reaction
  class consistent.
- **The glycerol by-product is omitted.** There is no game item for it, and **water is not substituted in its
  place** — that would be inventing a false equation.
- **It is not an energy arbitrage.** Taking vanilla Refined Oil at 4.4 MJ: 2 Algal Oil (8.4 MJ) + 6 Methanol (30 MJ)
  = 38.4 MJ in, 6 Refined Oil = 26.4 MJ out. A net loss.

It also gives methanol a large new customer — the C1 chain's only downstream used to be formaldehyde and ethylene.

### Why photosynthetic forestry has no inputs

Photosynthesis itself balances: 6 CO2 + 6 H2O --light--> C6H12O6 + 6 O2. The problem is that **the game has no
"atmosphere"**. Writing carbon dioxide and water in as ingredients would force the player to pipe gas to every
greenhouse, when in reality a greenhouse takes both straight out of the air.

So on paper it creates matter out of nothing, and that is paid for with the light constraint: **no light, no output.**
The sun is this recipe's real raw material; it simply does not occupy a slot.

The constraint sits on the **whole building**, not on this one recipe: at night the co-culture and the extraction stop
too. Chemically the co-culture runs inside a tank and could keep going, but the owner's call was to make it uniform —
one building, one state, which is far easier to explain than "some recipes in this machine turn and some do not".

### How the light is computed: copied from the solar panel

`PowerGeneratorComponent.EnergyCap_PV` is one line, in full:

```
currentStrength = clamp01((sun direction . building position, normalised) * 2.5 + 0.8572445) * luminosity
```

This mod uses that formula verbatim, and takes its two inputs from the same places:
`PlanetData.runtimeLocalSunDirection` and `PlanetData.luminosity` (both checked against the call site in
`PowerSystem.GameTick`). The 2.5 and the 0.8572445 are what let the strength reach zero slightly after the sun drops
below the horizon — **there is a dusk, not a hard cutoff**.

The behaviour is therefore identical to a solar panel's, and it applies to **whatever recipe the building is
running**:

- The planet rotates and output rises and falls with it; **the night side stops**
- The dark side of a tidally locked planet is **permanently stopped**; the lit side runs at full rate forever
- Further from the star is slower (that is what `luminosity` is)
- A Dyson sphere or swarm **does not help** — those feed ray receivers, not daylight

### Implementation: it scales the cycle count, not the speed

A mega building's `speed` is 100,000,000 (10000x), far above any recipe's `timeSpend`, so **slowing it down does
nothing at all until it drops below `timeSpend`**. What actually decides throughput is how many recipe cycles settle
per tick (60 by default, i.e. 3600 cycles/s), so that is the number scaled by sunlight: strength 1.0 gives 60 cycles,
0.5 gives 30, 0 gives none.

Slowing it down would also have a worse side effect: a mega building is recognised by `speed >= threshold`, so pushing
the speed under that threshold means the building is never picked up again on the next tick — permanently dead.

**The cost, stated up front: the panel's "production speed" row always reads 10000x.** That row reads `speed`, while
what actually tracks the sunlight is how many cycles settle per tick. To see what it is producing right now, watch the
output, not that row.

On a zero-strength tick one extra thing is needed. This mod's hook is injected **before** the vanilla
`InternalUpdate` call, and that call cannot be cancelled — on its own it will settle a full cycle. So `time` is
pushed down in advance to a value that still cannot reach `timeSpend` after the increment; the increment's upper
bound is exactly `speedOverride`, so the value used is `-speedOverride - 1`. That is an exact figure, not a guess.

### Where the two new items' numbers come from

Following the standard in section XII, item by item:

- **Microbial Consortium** (not a fluid, no heat value). An algal-bacterial consortium is a real process: the
  microalgae fix carbon and make oil, the heterotrophic bacteria consume what the algae exude and hand the nitrogen
  and phosphorus back, and the pair out-produces either alone. It is called a "consortium" and contains algae for
  exactly that reason — it is mixed biomass, not one species. Harvested, it is a wet cake (what centrifugation or
  flocculation actually yields), hence not a fluid. **No heat value is given**: burning wet biomass directly is a net
  loss, because the latent heat of evaporating the water eats most of the combustion heat. Burning it would require a
  drying step the game does not have.

- **Algal Oil** (fluid, chemical fuel, 4.2 MJ). **This is the only heat value in the table derived by mass rather
  than by mole.** The table's anchor is coal at 2.7 MJ against carbon's 393.5 kJ/mol enthalpy of combustion. A
  triglyceride (taking triolein) burns at about 35,100 kJ/mol, which on the molar anchor would be **241 MJ, 89 times
  coal** — right off the end of vanilla's fuel ladder. The molar anchor works for small molecules because one "unit"
  of each is a comparable size; one unit of triolein carries 57 carbons and is simply not in the same league as one
  unit of carbon. So the anchor here is **mass**: biodiesel is about 37 MJ/kg and coal about 24 MJ/kg, a ratio of
  1.55, giving 2.7 x 1.55 = 4.2 MJ. That lands between Crude Oil (4.05) and Fire Ice (4.8).

- **The co-culture recipe is not a balanced equation**, for the same reason as "Vanadium Ingot · Residue Recovery":
  biomass is not a compound and has no fixed formula (the empirical CH1.8O0.5N0.2 is an average elemental ratio that
  moves with strain and culture conditions), so no strict coefficients exist. The ratios are taken at the order of
  magnitude a mass balance gives. The carbon dioxide is **an unavoidable product of aerobic respiration**, not a
  product added to round the recipe out.

- **Three consortium to one oil** corresponds to roughly 33% lipid content by dry weight — the real upper band for
  oleaginous algae under nitrogen limitation; ordinary culture gives about 20%. The real process also leaves defatted
  residue, and **no residue product is given here**: the most natural match for it is Plant Fuel, which is an input to
  the recipe above, and connecting the two would make a self-sustaining free-growth loop.

### One balance note

Zero inputs at 60 cycles per tick is **14,400 Logs/s + 14,400 Plant Fuel/s** in full sunlight. That is the same order
as the other mega buildings (they also run 3600 cycles/s), but all of those consume inputs and this one does not;
averaged over a day/night cycle it comes to roughly half. If that is too much, lower `cyclesPerTick` in
`megabuildings.json` (global) or cut the product counts on that recipe in `ores.json` — both are data, no code change
needed. To drop the light constraint entirely, set the Biodome's `lightDependent` to `false` in
`megabuildings.json`.

---

## XX. Living Composite: hyphae growing metal into a solid

The Biodome's second production line. **One recipe eats any alloy** — six of the nine finally
have somewhere to go, where before they could be made and nothing wanted them.

```
Consortium + Hydrogen (+ light) ──→ Mycelial Matrix ──+ alloy──→ Living Composite I–IV
```

### Two recipes, both in the Biodome

| Recipe | Inputs | Products |
|---|---|---|
| Mycelial Matrix · Photohydrogenotrophy | Consortium x4 + Hydrogen x9 | Matrix x2 + Water x6 |
| Living Composite · Mycelial Symbiosis | Matrix + **the alloy you pick** | Living Composite I–IV |

**The second one occupies a single replicator cell and makes 24 different things.** Select it
and a panel appears under the assembler window:

- **Row one**: click the left or right half to cycle the filler alloy — **six** to choose from:
  Manganese Steel, Stainless Steel, Chrome-plated Copper, Chrome-Vanadium Tool Steel,
  Cobalt-Chrome, Vanadium-Titanium
- **Row two**: drag to set how many parts of alloy go in (1–9 out of 10)

**You do not pick the grade; the ratio decides it:**

| Alloy parts | 1–2 | 3–4 | 5–6 | 7–9 |
|---|---|---|---|---|
| You get | I Dispersed | II Percolating | III Connected | IV Rigidized |

Six fillers x four grades = **24 combinations**. The panel shows the live four-axis values and
the yield for the current combination, so dragging the slider shows hardness climbing and
toughness falling as it happens.

The choice is stored per building in the save, and newly built or blueprint-pasted machines
inherit the last combination you picked.

> The whole Biodome is bound to sunlight, so all five recipes **stop at night**.

### The four fields in plain words

| Field | Plain meaning |
|---|---|
| Hardness | How much force before it deforms |
| Toughness | How much of a beating without breaking |
| Corrosion | How long it lasts in water |
| Conductivity | Whether it can be a wire |

(These apply to Iron Ingot, Manganese Steel and the rest just the same.)

### What each grade is

| Grade | Hard. | Tough. | Corr. | Cond. | In one image |
|---|---|---|---|---|---|
| **I Dispersed** | 15 | **88** | 68 | 4 | **Bread with walnuts** — however hard the walnuts, the bite is bread |
| **II Percolating** | 22 | 76 | 72 | **38** | **Sesame scattered until you can just step across** — connected, still soft |
| **III Connected** | 55 | 62 | **85** | 12 | **The walnuts glued into one block** — load finally transfers |
| **IV Rigidized** | **82** | 18 | 80 | 13 | **Locked into triangles** — as hard as a ceramic, and as brittle |

**I Dispersed**: the metal is still a scatter of islands. Soft, but its **toughness is the
highest in the whole table** — running-shoe midsole, helmet liner: not hard, but it eats the
impact. The metal is wrapped in organic matter and not connected to itself, so **rust in one
spot cannot reach another**.

**II Percolating**: the least intuitive grade — **it conducts, and it is still almost soft**
(hardness only 15 to 22). Conduction needs **one** spanning path; load bearing needs a path
in **every** direction. A wire conducts; you cannot use it as a beam. This is "rubber that
carries current", and nothing else in the game is that.

**III Connected**: the hyphae tie every grain into one network, load transfers in full, and
chromium's corrosion resistance takes over the whole block — **the most corrosion-resistant
of the four**.

**IV Rigidized**: a triangle cannot deform, and once everything is triangulated the material
can no longer move. Hardness 82, toughness collapsed to 18. **Hard and tough are opposites**
— glass is hard but shatters; rubber survives the drop but dents under a thumb.

### The one thing that matters most: no grade is a downgrade

I is not a worse IV — **I's toughness is nearly five times IV's**, and IV's hardness is over
five times I's. Every pair among the four has something it wins on.

So this is not a four-step upgrade ladder. They are **four different materials**: I to absorb
impact, IV for hardness, II if you want a wire that bends, III if it is going to sit in water.

### A nice consequence: a composite never beats its own filler

Except on toughness. Hardness and conductivity **cannot exceed the filler** — the matrix is
near zero on both, so mixing only pulls the value down — which is why every grade's numbers
sit under the ceiling of its own alloy. **The one axis it reliably wins is toughness**, which
is what the mycelial matrix contributes, and the entire reason the material exists.

IV against Cemented Carbide: hardness 82 vs 90, only 8 lower; toughness 18 vs 11, 64% higher.

### Icons

The four are **polished cross-sections** (the kind you mount for metallography), deliberately
a different form from the nine alloys' isometric ingots, so an ingot and a stock material are
told apart at a glance in the inventory. What is drawn inside the disc *is* the network:
islands, then one spanning path, then the full web, then triangulated and filled — plus one
to four grade notches on the mounting ring.

### Downstream: Directed Exsolution

Send the grown composite into a **Smelter** (or the mega Foundry Smelter). The mycelial matrix
chars away and the metal skeleton left behind exsolves along whatever way it was connected —
**which grade you put in decides what comes out**:

| Feed in | Get out | Why |
|---|---|---|
| **I Dispersed** | **Frame Material** | The highest-volume structural part for a Dyson sphere; light and tough is exactly what it wants |
| **II Percolating** | **Particle Broadband** | Broadband is transmission, and this is the only grade in the game that is a *soft* conductor |
| **III Connected** | **Titanium Crystal** | All-rounder for an all-rounder |
| **IV Rigidized** | **Diamond** | See below |

Another one-cell recipe with a panel: select it and click the left or right half of the row to
change which grade goes in.

**All four grades are wanted at once** — which is the point of their being mutually
non-dominated. This is not an upgrade ladder you save up for: you make grade I because you want
Frame Material, and grade IV because you want Diamond.

> **The IV → Diamond pairing has a real basis.** HPHT synthetic diamond is grown from a
> **iron/nickel/cobalt metal solvent-catalyst plus a carbon source**, and grade IV happens to be
> cobalt-chrome or carbide grains in a carbon-rich organic matrix — catalyst and carbon source in
> the same block. The other three pairings are **flavour, not mechanism**: DSP does not model
> material properties, and the engine only knows "this recipe wants that item ID".

The numbers are a first cut and still need tuning against the vanilla routes. The startup log
prints `平衡对照 · …` lines giving the ingredients and time of each target's existing route.

The full design, with the paper citations behind every number, is `活性复合材料V1.md` at the
repository root.

---

## XXI. Combustible Liquid Power Plant: what you burn decides how much you get out

A hundred thermal power plant combustors sharing one turbine set and one grid connection, **216 MW**,
burning combustible liquids only.

It sits in the **leftmost build category (Power Facilities), slot 10**, next to the Thermal Power Plant,
Solar Panel and Accumulator — *not* on this mod's own "Mega Structures" tab, where the Wind Turbine
Cluster lives. The inconsistency is deliberate: power buildings are easier to find among power buildings.

### A liquid has two properties, and they do different jobs

| Property | What it decides | Where you see it |
|---|---|---|
| **Working Temp.** | **Energy efficiency** — how much of a unit actually becomes electricity | a new row in the item tooltip |
| **Fuel Heat** | how long one unit lasts — i.e. units per second at full load | the tooltip's existing "Fuel Heat" row |

**Output is a flat 216 MW and does not change with the fuel.** What changes is how much fuel energy
it takes to hold that output. So switching liquids is invisible on the power grid and
**very visible on the belt**.

### The six liquids

| Temp. | Liquid | Heat | Efficiency | Per unit | At full load |
|---|---|---|---|---|---|
| 250 °C | Algal Oil | 4.2 MJ | 30% | 1.26 MJ | 170.8 /s |
| 500 °C | **Vanadium Residue Oil** | 12.0 MJ | 43% | 5.16 MJ | 41.9 /s |
| 600 °C | **Vacuum Gas Oil** | 8.0 MJ | 46% | 3.69 MJ | 58.6 /s |
| 700 °C | **Benzene** | 22.4 MJ | 49% | 10.88 MJ | 19.9 /s |
| 750 °C | Refined Oil | 4.5 MJ | 50% | 2.23 MJ | 96.8 /s |
| 800 °C | **Cumene** | 35.8 MJ | 51% | 18.10 MJ | 11.9 /s |
| 900 °C | **Naphtha** | 3.0 MJ | 52% | 1.57 MJ | 137.9 /s |
| 950 °C | **Propylene** | 14.1 MJ | 53% | 7.46 MJ | 28.9 /s |
| 1000 °C | Ethylene | 9.7 MJ | 54% | 5.20 MJ | 41.5 /s |
| 1300 °C | **Acetone** | 12.3 MJ | 57% | 6.98 MJ | 31.0 /s |
| 1500 °C | Methanol | 5.0 MJ | 58% | 2.91 MJ | 74.2 /s |
| 2000 °C | Ammonia | 2.6 MJ | 61% | 1.58 MJ | 136.6 /s |

> **The four added later — benzene, cumene, propylene and acetone — are all ash-free, sulfur-free and metal-free**, so their places on the ladder are decided by sooting alone. Benzene is the skeleton coke grows on (every turn of the HACA mechanism adds carbon to an aromatic ring, and benzene is the starting point rather than the product), so despite carrying five times the energy of refined oil it sits below it; oxygenated acetone is the cleanest of the four, second only to methanol.
>
> **The benzene row is the cleanest example of "the energy-dense one cannot burn hot"**: 22.4 MJ at 700 °C.
>
> **And the four cuts of a single barrel of crude demonstrate the whole rule by themselves**, with no need to reach for algal oil and ammonia:
>
> | Residue 12.0 MJ / 500 °C | Gas oil 8.0 / 600 | Refined oil 4.5 / 750 | Naphtha 3.0 / 900 |
> |---|---|---|---|
>
> The further down the column, the heavier and the denser in energy — and also the more sulfur and metal, and the less heat the hot gas path will tolerate. **The two ladders run strictly opposite**, and they are four things separated out of one barrel.

Of the twelve liquids, **naphtha, vacuum gas oil and vanadium residue oil** all come from one recipe —
**Atmospheric and Vacuum Distillation** (Refinery: crude oil ×10 → naphtha ×2 + refined oil ×3 + gas oil ×3 +
residue oil ×2), covered in section XI. It **does not dominate vanilla Plasma Refining**: if all you want is
refined oil, that route is strictly better (1.0 per barrel against 0.3, and it yields hydrogen too). This is
simply the only way to get the other three cuts.

**Gas oil and residue oil are the two this plant alone will accept** (heavy cuts need dedicated heating and
atomisation, which an ordinary boiler cannot provide). The other ten **still burn in the vanilla Thermal
Power Plant**: the combustible liquid fuel bit is OR-ed on, so nothing was taken away from them.

### Working temperature is not flame temperature

This needs saying, or the table above reads strangely: ammonia has the lowest heat value, so how does it
get the highest temperature?

**Because the temperature is how hot this fuel lets you run the hot gas path, not how hot it burns.**
In reality almost every hydrocarbon burns at 1800–2300 °C in air — they all bunch together. What actually
forces turbine temperature down is ash, alkali metals, sulfur and vanadium: they foul, slag and corrode,
and you turn the temperature down to keep the machine alive.

So the whole table is one cleanliness ladder:

> **alkali ash (algal oil) → vanadic hot corrosion (residue oil) → sulfur and aromatics (refined oil)
> → sooting (ethylene) → sootless (methanol) → carbon-free (ammonia)**

That is also why the two properties are independent of each other: **contaminant content and enthalpy of
combustion have no causal relationship**. So "the dirty ones are energy-dense and the clean ones are
energy-poor" is not a balance contrivance — it is what these fuels are actually like. Every heat value in the
table except the residue oil's is an existing enthalpy-of-combustion conversion; not one was retuned for
this chain.

> Ethylene sitting below methanol may look backwards. Ethylene has no ash and no sulfur, but it is a
> **textbook soot precursor** — C₂H₄/C₂H₂ are the standard fuels in soot formation research. Methanol has
> no C–C bond at all and therefore cannot form soot; its flame is so clean it is nearly invisible.

### It is less efficient than the vanilla Thermal Power Plant, on purpose

The vanilla Thermal Power Plant converts at **80%** (one coal is 2.7 MJ and yields 2.16 MJ). This plant's
best grade only reaches **61%**.

That is because vanilla's 80% is a flat game number, while this efficiency is computed as
**Carnot efficiency × the second-law efficiency of a real power plant**, which simply cannot get that high.
So efficiency is not what this building sells. Two other things are:

- **Power density**: one building replaces a hundred, saving floor space, grid connections and build cost
- **It burns what your factory is already venting**: when the feedstock's opportunity cost is near zero,
  how much is wasted stops mattering

### Full derivation

How temperature becomes efficiency, why the anchor sits on the most efficient grade, and the twenty papers
behind the numbers are all in `可燃液体发电V1.md` at the repository root. The values live in
`combustibles.json` and the plant itself in `machines.json`.

---

## XXII. Living Proliferators: two tiers above vanilla, each in two characters

One recipe, **any two Living Composites**, and the pair decides which proliferator comes out.
Like alloy ammo the **recipe is not documented** — the replicator shows a single
"Proliferator · Mycelial Coating", and which pair gives which is yours to find.

| | |
|---|---|
| Machine | Chemical Plant |
| Input | any two Living Composites, 4 each (the same one twice is allowed) |
| Output | **Proliferator Mk.IV / Mk.V**, each in an Extensive and a Concentrated form — four in all |

### Two properties doing two different jobs

Vanilla's three proliferators are a single upgrade line: each tier gives both a bigger per-item
bonus and more sprays per unit. These four split that into two directions:

| Property | What it decides |
|---|---|
| **Spray Level** | how much extra product / speed a sprayed item gets |
| **Sprays** | how many items one unit covers — i.e. how big your proliferator line has to be |

**Within a tier, the higher level always sprays fewer.** That is not a tuning choice: both
variants split the same total-charge budget, so it falls out.

### The numbers

Vanilla's top tier, Mk.III, is level 4, 60 sprays, +25% products / +100% speed, ×2.5 power.

| | Spray Level | Sprays | Products | Speed | Coater power |
|---|---|---|---|---|---|
| **Mk.IV Extensive** | 4 | **112** | +25% | +100% | ×2.5 |
| **Mk.IV Concentrated** | 5 | 90 | +27.5% | +125% | ×2.9 |
| **Mk.V Extensive** | 5 | **151** | +27.5% | +125% | ×2.9 |
| **Mk.V Concentrated** | **6** | 126 | **+30%** | **+150%** | ×3.3 |

Mk.IV Extensive gives **exactly** the vanilla top tier's per-item bonus; what it wins is 112
sprays per unit instead of 60. What it saves is not electricity — it is the proliferator line itself.

> **Higher levels are actually less power-efficient.** Per watt, level 6 is worse than level 4
> (1.30/3.3 against 1.25/2.5, and the same for speed). What it sells is **output per machine** —
> in this mod power is cheap and floor space and building count are the bottleneck, so the
> direction is right, but do not expect it to save electricity.

### The panel

Select the recipe and two picker rows appear below the Chemical Plant window.
**Click the left half to step back, the right half to step forward:**

```
【Living Proliferator】
Feedstock A   ◀  Living Composite II  ▶
Feedstock B   ◀  Living Composite II  ▶

Character 0.29  Grade 110.0  →  Proliferator Mk.V · Extensive ×2   Spray Level 5 / Sprays 151
```

The result line prints **both scores**, because the outcome is two-dimensional: report only the
result and you cannot tell whether that last click moved the grade or the character.

- **Character** = hardness / toughness. Hardness-dominant → Concentrated
- **Grade** = corrosion + conductivity. Higher → Mk.V

This is also the first time the Living Composite's four property axes are **actually read** by
anything — until now they were displayed in the tooltip and fed no calculation at all.

### A hint: the best material is not the answer

The instinct is to shovel in two of the top grade, **Living Composite IV Rigidized**.
**That gives the second-weakest of the four.**

The rigidized phase is absurdly hard, so its character score lands firmly on the Concentrated
side — but its corrosion + conductivity is only 93, **just short of the grade threshold**.
Conversely the humblest of them, **II Percolating**, doubled up gives Mk.V Extensive: it is the
only one of the four grades that conducts, and "electrical percolation precedes mechanical
percolation" finally has a consequence.

The rest is yours to find. Each pair you discover gets a line in the log.

### Why it stops at level 6

The engine's proliferator tables actually run to **level 10**; vanilla only ever uses 4. What
stops us is not the table — it is the **belt**:

```
spraying does      a cargo stack's proliferator charge = stack size × spray level
this mod's preloader widened that field to Int16 (max 32767)
belt stacking 5000  →  level 6 at most
```

**Belt stacking and proliferator level are two charges against the same budget.** Getting to
level 10 would mean dropping stacking below 3276, which is plainly a bad trade. The startup log
computes the ceiling for your current config and spells out the derivation.

---

## XXIII. Alien Veins: mining them consumes drill bits

**A vein found only outside your home system, harder than the drill bit itself — mining it consumes bits.**
"Interstellar", "extremely rare" and "eats drill bits" are not three design decisions; they are three
properties of one real mineral. The full derivation, with twenty citations, is in `外星矿脉V1.md`
at the repo root.

| | |
|---|---|
| Vein | Moissanite Vein (type 23), yields **Moissanite**; it is not smelted into a metal |
| Where | **None at all in the home system**; Barren Desert / Ashen Gelisol / Halite Flats, 12% per planet |
| How | **Advanced Mining Machine only**, with **storage slot 1 holding drill bits** |
| One bit | mines **47,970** ore |
| Bits made in | the **Forgeworks Fabricator** (this mod's `ERecipeType` 12, Forging) |

### A plain Mining Machine cannot be placed, and it says so up front

**Only the Advanced Mining Machine can work moissanite.** Placing a plain Mining Machine on a
moissanite vein is refused outright, with the same message as "no vein nearby" — which is exactly
how vanilla treats oil seeps: oil is filtered out of a plain miner's vein list, so as far as that
machine is concerned there is no usable resource there.

**This restriction was not added for balance; it was already true.** The drill-bit slot is a
**logistics station slot**, and a plain Mining Machine has no StationComponent at all, so it
structurally cannot have one — meaning it could never mine the vein anyway. What you got before
was a machine that built fine, drew power, produced nothing, and gave no hint why. All that
changed is that the refusal now happens at build time.

The test is "is this a station-style miner", not a proto id, because that flag and "does it have a
drill-bit slot" are the same fact; a proto id would only be accidentally right.

### What it solves

This mod's Advanced Mining Machine already runs at 200,000/s **with no running cost at all**. The obvious
way to add one is to make all mining consume bits — but that is a nerf to existing saves.

**So instead: leave every existing ore alone and add one new vein that is the only thing consuming bits.**
The new vein appears only on planets **not yet generated**, so no existing factory loses a single machine,
and no config switch is needed. And because nothing that was already running can stop, **halting outright
when bits run out** is an acceptable failure mode rather than something needing a slowdown compromise.

### Why moissanite — all three reasons are real

- **It really does come from other stars.** Silicon carbide in meteorites is a **presolar grain**, condensed
  in the stellar winds of stars that died before the Sun; ~90% comes from low-mass carbon stars. So "you
  cannot find it at home" is mineralogy, not balance.
- **It really is extremely rare.** It barely exists naturally on Earth; when Moissan first found it in a
  meteorite crater in 1893 he took it for diamond.
- **It really is harder than the bit.** Mohs ~9.5, Vickers ~2800 HV — **above tungsten carbide's ~2600**.
  Mining it is not "wears a bit faster"; the tool is softer than the rock.

### The drill bit: one item, four recipes

Qualifying materials are not listed, they are **selected by a predicate**: enough hardness (floor = ore
hardness − 12) and non-zero toughness. Run that over the four-axis table and however many materials
qualify is however many recipes you get.

| Material | Hardness | Toughness | Ore per bit from it | **Input → 1 bit** |
|---|---|---|---|---|
| Diamond | 100 | 5 | 47,970 | **×1** |
| Moissanite | 96 | 8 | 34,131 | ×2 |
| Tungsten Carbide | 95 | 10 | 32,065 | ×2 |
| Cemented Carbide | 90 | 11 | 10,006 | **×5** |

**The bit itself is uniform** — the four-axis difference moved to *how much material one costs*:
`inputs = ceil(capacity / that material's yield)`, where `capacity` is the best material's yield. So the
best material costing exactly 1 is **computed, not decreed**.

Two things worth saying:

- **Toughness is the axis that earns its place.** Diamond cleaves rather than deforming and has very low
  toughness; without that term diamond would run away with it and tungsten carbide and cemented carbide
  would be dead content instantly. With it the spread closes to under 5× and the lower tiers stay in use —
  because they are far cheaper. **The real decision on this chain is ore-per-bit versus cost-per-bit.**
- **Moissanite makes a good bit itself.** That falls out of the predicate rather than being a special case —
  silicon carbide is an abrasive in the first place. It is **not bootstrapping**: the entry-tier cemented
  carbide needs no moissanite, so the option only opens once you already have some.

### Why the Forgeworks Fabricator

A drill rod is an open-die chromium-steel forging — forging refines the grain, which is what lets it take
impact loading — and the carbide teeth on the crown are sintered, not assembled. **The machine follows the
process class**, the same test that keeps MTO dehydration on the vanilla Chemical Plant.

Worth noting: the **Forgeworks Fabricator used to be the same thing as the Heavenworks Assembler** — both
on vanilla Assemble type 4, with word-for-word identical descriptions, which by this repo's own dominance
test is duplicated content. This chain gave it a type of its own, and only then did it become a real forge.
The cost is that it no longer takes vanilla assembly recipes — and the Heavenworks Assembler picks up every
one of those.

### That slot on the miner

The Advanced Mining Machine goes from 1 storage slot to 2, and **slot 1 is laid out by the mod as a Demand
slot for drill bits**, stocking 3000 (about 12 minutes at full speed). If the logistics network has bits
they are delivered automatically; nothing to configure.

> **Only newly built miners.** `storage` is baked into the save at build time. This costs nothing here —
> moissanite veins only appear on planets not yet generated, so every miner you build on one is new.

**Three states mine nothing and therefore charge nothing**, each using vanilla's own test:

| State | Vanilla's test |
|---|---|
| No power | `power < 0.1` (first line of `InternalUpdate`) |
| Station slot full | `storage[0].localSupplyCount >= max` (first line of `UpdateVeinCollection`) |
| Internal buffer full | `productCount >= capacity` |

No ore out, no bit spent — both stop together. **Stopping only the charge would mean plugging the output
lets you mine a whole buffer for free.**

### Cannot find it? The log tells you where

It really is rare, so the mod works out the distribution at the start of every game:

```
Moissanite Vein: 18 candidate planets outside the home system / 0 inside, chance 0.12 -> ~2.2 expected
```

Turn on `prospectRareVeins` in `alienvein.json` and it will additionally **borrow the game's own star-map
scan** to sweep every candidate planet and name them outright:

```
Moissanite Vein: 3 planets
    Blue Pole Star - Blue Pole Star III (4 veins, 1,284,000 reserves)
```

Turn it back off once you have found one, to skip those dozens of background scans.

---

## XXIV. Silicon Carbide: what moissanite is for, and it buys throughput

What is moissanite for? **Power semiconductors.** The product is a
**Silicon Carbide Power Exchanger** — it stores not one joule more; what you replaced is the
converter, not the battery. The full derivation is in `碳化硅下游V1.md` at the repo root.

| | |
|---|---|
| Chain | Moissanite → High-Purity SiC → (+ Aluminium Nitride) → SiC Power Module → SiC Power Exchanger |
| Throughput | **30×** the vanilla exchanger (the lithium one is 6×) |
| Serves | **the same Lithium Accumulators**, the identical pair the lithium exchanger uses |

### It is not a tier above the lithium set

This is the pivot of the whole chain, and the reason it deserves to exist:

| | Buys | Basis |
|---|---|---|
| Lithium Accumulator | **how much you store** (capacity ×6, charge ×10) | electrochemistry: lithium cobalt oxide cathode, graphite anode |
| SiC Power Exchanger | **how fast you move it** (throughput ×30) | power electronics: silicon carbide switches |

**Silicon carbide stores nothing in the real world; it is a converter device** — SiC MOSFETs go in
inverters, and there is not one grain of it inside a battery. So the two answer **two different
bottlenecks**:

- Not enough storage → add Lithium Accumulators
- Storage is fine but you cannot get power in or out fast enough → add a SiC Power Exchanger

**Your lithium batteries are not obsoleted.** The new exchanger serves that same accumulator pair,
so upgrading **swaps the converter, not the battery** — exactly how a real SiC retrofit works.

> **This is also why there is no "silicon carbide accumulator".** That would write a converter device
> up as a storage medium, the same class of error as putting a recipe on the wrong machine.

### Three steps, every one a real process

| Step | Recipe | Machine |
|---|---|---|
| High-Purity SiC | Moissanite ×4 + Nitrogen ×2 → ×1 | Smelter |
| Aluminium Nitride | Aluminium Ingot ×2 + Nitrogen ×1 → ×2 | Redox Chemical Plant |
| SiC Power Module | High-Purity SiC ×2 + Aluminium Nitride ×4 + Copper Ingot ×8 → ×1 | Assembler |

- **High-purity SiC** uses **physical vapour transport**: silicon carbide powder sublimes at over two
  thousand degrees and regrows as a single crystal on a slightly cooler seed. Lely made the first
  batch in 1955; Tairov and Tsvetkov added the seed in 1978, and that is still the only route in
  volume production today. The **nitrogen is a dopant**: it substitutes onto carbon sites and turns
  intrinsically insulating silicon carbide into a conducting substrate — conductivity is precisely
  what this step buys.
- **Aluminium nitride** is the one balanced step: `2 Al + N₂ → 2 AlN`. It has to do two contradictory
  things at once — **conduct heat** (carry the die’s heat away) and **insulate** (hold off high
  voltage). AlN conducts heat almost like a metal while insulating completely, which is why it is the
  standard power-module substrate. **It is not filler; silicon carbide forces it**: SiC’s power
  density is far above silicon’s, so the substrate’s ability to shed heat becomes the tight
  constraint.
- **The power module** packages that sandwich: dies sintered onto copper-clad AlN, busbars brought
  out, the whole thing potted into a brick.

> **Nitrogen finds its second use here.** It was added as a collectable gas giant gas for the
> Haber process (ammonia); doping is an entirely separate use — and both are real.

### The conductivity axis finally leads

| Item | Hardness | Toughness | Corrosion | Conductivity |
|---|---|---|---|---|
| Moissanite (ore) | 96 | 8 | 92 | **18** |
| High-Purity SiC | 96 | 8 | 92 | **42** |
| Aluminium Nitride | 60 | 10 | 88 | **2** |
| SiC Power Module | 35 | 30 | 70 | **96** |

Conductivity is largely a spectator among the alloys; on this chain it tells the whole story:
**18 (insulating mineral) → 42 (nitrogen-doped substrate) → 96 (power device)**. Aluminium nitride’s
2 is the counter-example — **the same chain needs an insulator**, and that is real too.

### One recipe you will not see

High-purity SiC has **exactly the same four axes as moissanite ore** (both are silicon carbide), so
the drill bit predicate would have accepted it too, adding a "High-Purity SiC → Drill Bit" recipe.
That one is **excluded on purpose**: a wafer costs 4 moissanite, so making bits straight from the ore
is cheaper, and the recipe would be dead content the moment it appeared in the replicator.

**The predicate cannot answer this** — it asks "is this hard enough", while the question here is
"would anyone actually use it". The first is computable; the second is a judgement.

---

## XXV. Bio Matrix: the seventh matrix, and it is grown

All six vanilla matrices are synthesised in the **Matrix Lab**. The seventh is not.

| | |
|---|---|
| Made in | **the Biodome** (not the Matrix Lab) |
| Recipe | Hyphal Substrate ×2 + Colony ×2 → Bio Matrix ×1, 3 s |
| Used for | the **seventh ingredient** of the Universe Matrix |

### Why it is not synthesised in the lab

A Matrix Lab **crystallises** — it arranges matter into a lattice. A Bio Matrix is **grown**:
the hyphal network generates electrical pulses and passes them along its connections, so
growing it into an ordered array lets signals travel through it, while the colony feeds it
metabolically and keeps the whole thing alive. It is not manufactured; it is cultivated.

So it belongs to the Biodome. **You cannot select it under "Matrix Synthesis" in the Matrix
Lab — that is by design, not an omission.**

It also plugs the biological chain straight into the tech tree: hyphal substrate and colonies
already come out of the Biodome, so the algae line stops being a side branch and becomes part
of the main progression.

### How it becomes a hard end-game requirement

```
Biodome ──► Bio Matrix ──► Universe Matrix (7th ingredient) ──► end-game techs
```

**No tech requirement changed at all — they still ask only for Universe Matrices.** What changed
is the Universe Matrix itself: its recipe went from six ingredients to seven, and the new one is
Bio Matrix. So the rule still states in one line: *behind the white matrix, there is now something
living.*

That makes Bio Matrix a **hard** end-game requirement, but one that is **counted once** — you do
not pay twice for the same thing.

> Want the heavier version, where techs ask for Bio Matrix **on top of** their Universe Matrices?
> Set `bioMatrixInTechs` to true in `lab.json`. It is off by default.

> **Note for existing saves**: the Universe Matrix recipe went from six ingredients to seven.
> An already-built Universe Matrix line **needs one more input feeding Bio Matrix**, or the lab
> will sit at "missing raw materials". The lab's internal data fixes itself on load — you do not
> have to tear anything down and rebuild it.

### The Matrix Lab window changes with it

| Situation | Cells on the ring | Shape |
|---|---|---|
| Matrix Synthesis, nothing selected yet | 5 | regular pentagon (as in vanilla) |
| Matrix Synthesis, making a Universe Matrix | 6 | regular hexagon; the extra cell is Bio Matrix |
| Matrix Synthesis, making any other matrix | 5 | regular pentagon |
| Research Mode | 5 | regular pentagon (by default no tech asks for Bio Matrix directly, so that cell is not drawn) |

The seventh cell appears only where it is **actually usable**. By default that means only while
making a Universe Matrix, where it is the seventh ingredient slot. It is hidden otherwise — so the
"pick which matrix to make" screen never shows a cell that does nothing when clicked, and Research
Mode never shows a slot that would never be consumed. (Set `bioMatrixInTechs` to true and Research
Mode shows it as well.)

### The 3-D animation on the building

While researching a tech that needs Universe Matrices (that is, one that indirectly consumes Bio
Matrix), the animation ring on the Matrix Lab building includes it too (two of the five animation
positions are given to Bio Matrix).

This is `bioMatrixShaderDigit` in `lab.json`; **set it to 0 to get vanilla's appearance back**.
There is a switch because that animation is handed to the game's own compiled shader, and vanilla
never had a seventh matrix — whether the shader recognises a seventh colour can only be found out
by running it. The default pattern deliberately keeps three Universe Matrix positions, so if the
shader does not recognise it, only those two positions look wrong: never a blank ring, and never
any effect on throughput or research speed.

## XXVI. Magma: putting a water pump on a lava planet

That orange sea on a lava planet is scenery in vanilla. Now it is a resource.

| | |
|---|---|
| How to get it | a **Water Pump** — the vanilla one, not a new building — built on a lava shore |
| Product | Magma |
| Phase | **fluid** — it goes into storage tanks, onto belts, and through logistics stations |
| Can you burn it | **No**, see "Why it has no heat value" below |

No new tech and no new building: put a water pump at the edge of a lava sea exactly as you
would at the edge of water. This mod's pump speed-up and enlarged buffer apply to it as
well (`boostWaterPumps` in `advancedminer.json`).

### Why vanilla cannot pump it

Two **independent** gates block it, and neither reports anything:

1. **Building.** A pump carries a whitelist of which ocean kinds it accepts. Lava is not on
   it, so placing one there only ever gives you "must be built on water".
2. **Output.** Even once placed, a pump only produces when the ocean is a real item. Lava is
   not an item id in the data — it is a **negative marker**, sharing that slot with "ice" and
   "no ocean" — so the pump would run, draw power, and yield nothing.

The two gates being independent has a practical consequence: the **Pump Anywhere** cheat in
`cheats.json` clears only the first one. In older versions, with that switch on, a pump could
be placed on lava and would then spin uselessly — that was not a bug, it was the second gate.

This mod handles both, but **never edits the planet data itself**: that negative marker is
also what the ocean rendering and the **Geothermal Power Station** read, and changing it
would break geothermal power entirely.

### Why it has no heat value

By this mod's standing rule, whether something burns is decided by whether it can be
**oxidised**. Magma is molten silicate — silica, alumina, magnesia — and is **already fully
oxidised**, so like carbon dioxide it yields no energy at all. It therefore has no `fuelType`,
and neither a Thermal Power Plant nor the mecha reactor will take it.

What it carries is **sensible heat**, not chemical energy: it leaves the pump at around a
thousand degrees. That is what its eventual use should be built on — heat exchange, not
combustion.

### Where magma goes: the Lava Cooling Plant

The seventh mega building, and the only one that eats magma.

| | |
|---|---|
| Location | Slot 8 of the **Mega Structures** tab in the build bar |
| Recipe type | **13 (Lava Processing)**, this mod's own — only it can run the three recipes |
| Speed | 10000x, twelve belt ports connected directly, built-in planetary logistics station |
| Power | 6 MW idle / 30 MW working |

It is the only one of the eight that **shows a glowing surface on the outside**: an open
refractory pool in the middle, four cooling towers pulling the heat away, and a granulation
ring hanging above the pool. At a distance that is the whole silhouette cue.

### Three recipes: how far you cool it decides what crystallises

These are not three power tiers. They are **three real cumulate horizons from a layered mafic
intrusion** — which is where most of Earth's chromium, vanadium and magmatic cobalt actually
come from. And this mod's cobalt, chromium and vanadium veins were already placed on the
**Lava** and **Volcanic Ash** themes, from the same geology.

| Recipe | Input | Output | Time | Real basis |
|---|---|---|---|---|
| Chromite - Early Cumulate | Magma ×6000 | Chromite ×1 + Stone Ore ×4 | 4 s | Chromite crystallises first at ~1300 °C, is denser than the melt and settles into a seam (Bushveld LG6 / UG2) |
| Titanomagnetite - Late Cumulate | Magma ×6400 | Titanomagnetite ×1 + Stone Ore ×4 | 6 s | Vanadium is incompatible and concentrates in the residual melt until Fe-Ti oxides take it up at ~1050 °C (Bushveld Main Magnetite Layer) |
| Cobalt Ore - Sulfide Segregation | Magma ×40000 + Gypsum Ore ×600 + Coal ×400 | Cobalt Ore ×1 + Stone Ore ×26 | 8 s | Cobalt is chalcophile and needs an immiscible sulfide droplet to segregate first. Magma carries too little sulfur — Noril'sk got its sulfur from assimilated **anhydrite**, and coal reduces it: CaSO₄ + 4C → CaS + 4CO |

**The input ratios are not invented.** A basaltic melt carries Cr ≈ 300 ppm, V ≈ 280 ppm and
Co ≈ 45 ppm, so the magma needed per unit of ore stands as **1 : 1.07 : 6.7**. The ratio is
physics; **only the absolute scale is a balance knob** — the shipped tier is 6000 : 6400 : 40000,
i.e. the same ratio scaled up a hundredfold. Sulfur and carbon scale with it (the sulfur needed
to reach sulfide saturation goes with **melt volume**); the stone byproduct does not.

**The yields are deliberately tiny, and that is the design rather than caution.** Prospecting
for rare veins is what this whole line is worth; if magma bought chromium and vanadium cheaply,
that gameplay would be dead. The precedent is already in the mod: `Vanadium Ingot · Residue
Extraction` says outright that it exists so a bad galaxy seed cannot lock the line out, while
tungsten deliberately gets **no fallback at all**. These three sit in the same place — **they
fill a gap, they do not replace the veins.**

**The stone output is badly understated, and here is why.** The real silicate fraction is over
99.9%; the recipes give you a handful. Two solid reasons: written honestly, this building would
outclass every other source of stone by orders of magnitude and push mining out entirely; and a
byproduct nobody wants will back up its output slot, which stalls a multi-product machine
outright.

### Not done yet

**The heat in magma is still not actually used.** Right now it is only a feedstock for the three
recipes — the "thousand degrees" shows up in *what* crystallises and nowhere else. It has not
become electricity, and it does not supply heat to any other high-temperature step. The reason is
in the opening of section XXVI: DSP's component model **has no shape for "convert something and
generate power at the same time"**, so making it electricity means making it a fuel, and that
collides with the Geothermal Power Station already standing on those same lava planets.
---

## XXVII. Catalytic Reactor: a factory that remembers its own state

The eighth mega building. The other seven have no memory — feed them and they produce,
tear one down and rebuild it and you get the same machine back.
**This one remembers how much activity is left in the catalyst sitting in its bed.**

### How the loop runs

The catalyst is not a per-cycle ingredient. It is a **charge loaded into the bed**:

```
Zeolite Catalyst ──loaded──▶ runs ~10 minutes, coke builds up in the channels
                                │
                        activity spent, the whole bed is blown into the spent hopper
                                │
              Spent Zeolite ────▶ "Coke Burn-Off" in the Redox Chemical Plant
                                │
                          10 in, 9 out ──▶ back to Zeolite Catalyst
```

**The loop is closed, but it is not perpetual.** Regeneration returns nine tenths —
zeolite dealuminates, hydrothermally deactivates and sinters at regenerator temperature,
and real plants top up with 1–2% fresh catalyst every day. So you need a small,
permanent make-up line. It is not much throughput, but it cannot stop.

The regenerator's off-gas is carbon dioxide, and **"Methanol · CO₂ Hydrogenation" already
consumes it** — and methanol is in turn one of the reactor's feedstocks. That closed loop
was not designed; it appeared on its own once the real process was wired in.

### No sorters needed either way

Catalyst and spent catalyst travel through the building's **own planetary logistics
station**, not through the recipe's ingredient and product slots: drones bring the
catalyst and take the spent away, and all you have to do is keep some in the network.

The cost is that a mega building's storage slots are invisible to the player (the recipe
window takes the place of the station window), so a **read-only panel** sits under the
window:

```
In the bed       ████████████████████  200
Activity left    ███████████░░░░░░░░░  58.3%
Spent hopper                             0
350s of activity left    catalyst in stock 840
```

The three rows are how much is loaded, how long it can still run, and how much spent
catalyst has piled up. **With no catalyst the machine stops** and the panel says so —
it will not idle and burn feedstock at the same time.

### Activity only drops on ticks that actually produced

No power, missing ingredients, output backed up — on those ticks the machine produces
nothing in the first place, and **not a point of activity is spent**. You will never see
"the machine is stopped and the catalyst is burning anyway".

### Three recipes, and only this building can run them

| Recipe | In → Out |
|---|---|
| Ethylene · Methanol to Olefins (Fluidised Bed) | Methanol ×7 → Ethylene ×2 + Propylene ×1 + Water ×7 |
| Propylene · Catalytic Cracking | Refined Oil ×6 → Propylene ×4 + Ethylene ×6 |
| Hydrogen · Catalytic Reforming | Refined Oil ×1 + Water ×4 → Carbon Monoxide ×4 + Hydrogen ×8 |

All three are real molecular-sieve processes, and **the carbon and hydrogen balance
exactly**: the first is `4 CH₃OH → 2 C₂H₄ + 4 H₂O` and `3 CH₃OH → C₃H₆ + 3 H₂O` merged;
the second conserves everything without a single filler term, because refined oil,
ethylene and propylene are all (CH₂)ₙ in this mod.

**The first one does not make vanilla's MTO dead content.** Ethylene per unit of methanol
is identical in both (of the 7 methanol, 4 go to ethylene and 3 to propylene). What the
fluidised bed buys is **propylene**, not a better ethylene yield.

Real FCC also lays down coke, and coke is exactly what deactivates the catalyst. **This mod
does not make coke a product; it expresses it as the consumption of catalyst activity** —
the line missing from the recipe table is the building's mechanic itself.

### Propylene is not a dead end

Propylene C₃H₆, like ethylene, cracks to **elemental carbon** rather than to carbon
monoxide, so it drops straight into the existing hydrocarbothermic family. That family's
gradient was already set by how many electrons the carbon can give up (CO 2, formaldehyde 4,
methanol 6, ethylene 12) — propylene is 18. **Two propylene do the work of three ethylene**,
because two propylene and three ethylene both supply exactly six carbons:
`2 Al₂O₃ + 2 C₃H₆ → 4 Al + 6 CO + 6 H₂`, balanced exactly. The ratio was not picked.

It burns too: 14.1 MJ, scaled from a heat of combustion of 2058 kJ/mol against the coal
anchor — 1.45× ethylene's 9.7 MJ, matching the carbon-count ratio.

### What you can tune

`catalyst.json`: how many units a bed holds, how many ticks it lasts, the catalyst slot's
capacity, and a debug switch that prints all five stages on one line every 10 seconds.
**The catalyst slot's capacity is its own setting and does not follow the logistics
station's** — if it did, the first reactor built would ask the network for ten million
units of catalyst and starve every later one.

## XXVIII. The Integrated Chemical Plant: the first machine here that eats several recipe types

The ninth mega building, and the first machine in this mod that can run **more than one
`ERecipeType`**: chemical (2), electrochemical (9) and redox (10), all three, still at 10000×.

### It fills an actual vacuum

This mod's chemistry recipes are spread across three types, and there was exactly one mega
building for any of them — the Calcining Chemical Plant, type 2. **Types 9 and 10 had no mega
building at all.** Which means the whole C1 chain, the nitrogen chain, all three phases of organic
chemistry and most of the refining line were stuck on 1× cloned buildings.

| | |
|---|---|
| Build | **Energy Matrix ×1000 + Calcining Chemical Plant ×1000**, 10 s |
| Speed | 10000×, same as the other eight |
| Power | **360 MW** working / 60 MW idle. For comparison: Calcining Chemical Plant 22.5 MW, Miniature Particle Collider 45 MW, Catalytic Reactor 43.2 MW — it draws an order of magnitude more than the next hungriest building in the mod |
| Location | Slot 10 of this mod's own build-menu page |

> **This number is a balance knob, not a derivation.** "It does the work of three Calcining Chemical
> Plants" gives 72 MW, and that was the earlier value; what ships is **five times** that, for reasons of
> balance rather than physics: it is the **only** 10000× path for three classes of chemistry recipe, and
> the build cost (1000 Calcining Chemical Plants) is paid once — the lasting brake can only be the
> power bill. This repo’s rule is that a knob gets called a knob instead of being dressed in a derivation.

### It does not turn the Calcining Chemical Plant into dead content — because it eats them

A 10000× machine that runs every chemistry recipe **would** make the Calcining Chemical Plant dead
content by this repo's own dominance test: same speed, and that one only runs a single type.

**The build recipe is the answer: an Integrated Chemical Plant consumes 1000 of them.** That makes
the older building its prerequisite rather than its competitor — the same move as the silicon
carbide energy exchanger, which still serves the very same lithium accumulators instead of
replacing them.

The Electrochemical Plant and Redox Chemical Plant are 1× clones, early-game versions, and are not
in the same league to begin with.

### Vanilla says "one machine, one type", and it means it

This is the limitation recorded in section X all along:

> `UIRecipePicker.RefreshIcons` filters with `filter != recipe.Type → skip`, and `filter` is the
> machine's `prefabDesc.assemblerRecipeType`. **One machine, one type** — vanilla cannot express
> "this machine runs A and B".

Breaking it needs code. **But the real cost is far below the earlier estimate: of the 15 reads of
`assemblerRecipeType` in the whole assembly, only 8 are actual gates — and all 8 are the same shape.**

| Gate | Sites | What breaks without it |
|---|---|---|
| `UIRecipePicker.RefreshIcons` | 1 | The recipe picker shows nothing at all |
| `BuildingParameters.CanPasteToFactoryObject` | 2 | Copy-paste refused |
| `BuildingParameters.PasteToFactoryObject` | 3 | Blueprint paste drops the recipe |
| `BuildingParameters.ApplyPrebuildParametersToEntity` | 1 | Blueprint-built machines come out empty |
| `CopyFromFactoryObject` / `CopyFromBuildPreview` | 2 | The recipe is cleared at copy time |

The other 7 are false alarms: `PrefabDesc.ReadPrefab` writes rather than gates,
`FactorySystem.Import` compares against `== 4` only to pick an animation length, and
`ItemProto.typeString` and `UIInserterBuildTip` are pure display.

**All eight become one table lookup** (`RecipeTypeCompatPatches`), and the `acceptsRecipeTypes`
field in the config is that table. When no building declares it, the patch is not applied at all
and vanilla IL is untouched.

### Three things come free, and they are why this approach works

1. **`AssemblerComponent.SetRecipe` does not validate the type at all.** Its IL holds a single
   `recipeType = recipe.Type` store and no check — so the production logic needed no change.
2. **`AssemblerComponent.recipeType` holds the *recipe's* type, not the machine's.** The six reads
   in `InternalUpdate` pick sound and animation by 1/2/3/4/5, so a chemical recipe running in this
   machine **automatically keeps the chemical plant's sound and animation**.
3. **That field is serialised, but not one of the 18 read/write sites in the assembly ever compares
   it against the prefab.** So there is no "load the save, find a type mismatch, clear the recipe".

> All three were counted by reading the IL one site at a time. Had any single place compared the
> saved type against the prefab, the whole feature would need a different design — and that failure
> mode is a save quietly losing recipes on load, the hardest kind to diagnose.

### "Made in" has to append, not replace

A chemical recipe's `madeFromString` says "Made in Chemical Plant" forever and **never mentions the
Integrated Chemical Plant** — so nothing on the recipe tells the player that machine can make it,
and that machine is the only 10000× option there is. So the line appends instead:

```
Made in   Chemical Plant / Integrated Chemical Plant
```

**A gate opened that nobody knows about is a gate not opened**; this half matters as much as the
transpiler.

### One stale predicate fixed on the way past

`MachineRegistry`'s test for "is this one of our own recipe types" hardcoded `9 <= t <= 14`.
`ERecipeType` **never had a ceiling** (the derivation is in CLAUDE.md), and the Integrated Chemical
Plant took 16 — which that range silently excluded, so both the item tooltip's type row and the
recipe's "made in" line fell back to vanilla's placeholder. The test is now `t >= 9 && t != 15`:
**vanilla owns 1–8 and 15, everything else is ours.**


## Config Quick Reference

| File | What it controls |
|---|---|
| `megabuildings.json` | The eight mega buildings, the tab, speed, built-in logistics station, replicator page count |
| `catalyst.json` | Catalyst bed: charge size, how long it lasts, catalyst slot capacity, debug switch |
| `advancedminer.json` | Speed, buffers, product mapping and build restrictions for miners / water pumps / oil extractors, plus whether pumps can draw magma on lava planets |
| `stations.json` | Station slot count and capacity, charging power, carry capacity, stack level, orbital collectors |
| `lab.json` | Matrix lab production speed, storage, automatic exchange with logistics stations, and how Bio Matrix shows in the lab 3-D animation |
| `recipes.json` | Extra recipes |
| `power.json` | Power node coverage radius |
| `belts.json` | Speed of the three belt tiers |
| `ores.json` | The custom vein table: per-ore IDs, vein density, recolour parameters, recipe lists; extra items (phase, heat value, icon); and the gases injected into gas giants |
| `machines.json` | The seven new buildings: which vanilla building to clone from, parameters for the five `kind`s (assembler / station / accumulator / exchanger / generator), tint, build recipe |
| `metals.json` | The four-axis property table (hardness / toughness / corrosion / conductivity) |
| `alloys.json` | Per-building alloy ratios: adjustable slots, total parts, property weights, yield and time multiplier bands |
| `cheats.json` | **Cheat switches**, all on by default: instant build / build without condition / no build collision / collider pool off / no power spacing / pump anywhere |
| `i18n.json` | The English localization table, Chinese → English. Forgetting the English for a new item raises a WARNING at startup |
| `composite.json` | Living Composite: the filler shortlist, the ratio bands for the four grades, yield and percolation parameters |
| `ammo.json` | Alloy ammo: the five tiers' damage/rounds multipliers, the pair-conversion weights, the yield curve |
| `combustibles.json` | Combustible liquid power: each liquid's working temperature, the Carnot cold side and second-law efficiency, the fuel type bit, the property row's field id |
| `proliferator.json` | Living proliferators: the candidate list for both feedstock slots, the thresholds for the character and grade scores, and each outcome's level / sprays / yield |
| `alienvein.json` | Alien vein: which vein consumes drill bits, the bit predicate's hardness margin and yield formula, and the miner's bit slot |
| `cargoprobe.json` | One developer switch: the shader `inc` probe. Off by default, and a file of its own so flipping one bool does not shadow all of `stations.json` |

> Before adding an item or recipe to `ores.json`, read the standard in section XII — **properties are derived from
> real chemistry and physics, and the basis goes into a `//` comment**.

`tools/make_icons.py` generates the hand-drawn icons as vectors (`drawsvg` + `resvg`); output lands in `tools/out/`,
and moving a file into `ProjectEden/assets/icons/` makes it referenceable by filename from `ores.json` /
`machines.json`.

After editing, `dotnet build` is all that is needed — it deploys to the r2modman profile automatically. Every change
has a confirmation line in the game log; a failed match logs an ERROR rather than failing silently.
