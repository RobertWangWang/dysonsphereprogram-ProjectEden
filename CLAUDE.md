# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

**Project Eden** — a Dyson Sphere Program mod on BepInEx + Harmony that scales up the late-game production chain: ten 巨型建筑 (mega buildings — 10000× assemblers that double as planetary logistics stations, one of which produces nothing at night), a maxed-out 大型采矿机 / 抽水站, 30-slot × 10,000,000-capacity logistics stations, and 5000-level cargo stacking (a preloader widens the belt cargo fields so full-tier proliferator survives it). It also adds custom ore veins (cobalt, aluminium, gypsum, lithium, manganese, chromium, vanadium and tungsten, vein types 15–22, placed per-theme as either regular spots or rare slots), a collectable gas giant gas (氮气), cloned buildings (电化学厂 and 氧化还原化工厂, each with its own recipe type; 综合物流枢纽 carrying both drone kinds; 锂电池蓄电器 with its own 能量枢纽; and 风力发电机集群, a 1000× wind turbine), a six-recipe C1 chemistry chain (合成气 → 甲醇 → 甲醛 / 乙烯, plus Fischer–Tropsch to 精炼油), a nitrogen chain (Haber–Bosch to 氨, Ostwald to 硝酸), a three-step tungsten chain ending in 碳化钨, a 硬质合金 whose WC:Co ratio is set **per building** by a slider and settles into yield and craft time, a four-axis property row (硬度/韧性/耐蚀/导电) on every metal, 五-tier 合金弹药 whose damage and yield come from **which two alloys** you feed one recipe, paging for the replicator, recipe picker, build menu and item-picker grids, a 生物温室 whose three recipes (zero-input photosynthesis → four-input algal-bacterial co-culture → lipid extraction) run on its own `ERecipeType` 11 and whose whole output is scaled by the **solar-panel light formula** — full sun gives the full 10000×, no sun gives nothing, an **alien vein** (莫桑石, type 23) that exists only outside the home system and **consumes drill bits to mine**, the bit being one item with one recipe per qualifying material (a predicate over the four-axis table, expanded at registration) forged in 锤锻精工厂 on this mod’s own `ERecipeType` 12, that vein’s downstream — **silicon carbide power electronics** (seeded-sublimation wafer → AlN substrate → power module → a 碳化硅能量枢纽 that serves the *same* lithium accumulators at 5× the throughput, because SiC is a converter and stores nothing), a **seventh research matrix** (生物矩阵) that is **grown in the 生物温室 rather than synthesised in a lab** and is the Universe Matrix’s seventh ingredient, a vanilla 抽水站 that draws **岩浆** off a lava planet’s ocean and a seventh mega building, the 熔岩冷却厂, that crystallises it back into 铬/钒/钴 ore at deliberately tiny yields on this mod’s own `ERecipeType` 13, a **催化反应器** that is the first stateful machine here — it holds a charge of zeolite catalyst, loses activity only on ticks that actually produced, ejects 待生沸石催化剂 into its own station slots when spent and blocks until refilled, with regeneration burning the coke back off at a 10 % loss, a **综合化学厂** that is the first machine here to run **more than one `ERecipeType`** (chemical 2, refine 3, electrochemical 9 and redox 10, all at 10000×) — the eight real gates on `assemblerRecipeType` become one table lookup, and its build recipe eats 1000 燔石化工厂 so the older plant is its prerequisite rather than its victim, a **氧化还原燃烧厂** that is the first entity here to carry **both an assembler and a generator** — it presses a reductant and an oxidiser into propellant grains on its own `ERecipeType` 17, feeds those grains straight into its own fuel bay without a belt, and burns them for 30 GW, with a three-row panel whose two picker rows choose the pair and whose slider sets the oxidiser ratio, a **活性透镜** — a *living* gravitational lens that takes **no new building**, goes into the stock 射线接收站 through the vanilla `powerCatalystId` mechanism, generates ×5 power and ×3 critical photons (two independent knobs) and **heals in the beam while ageing in the dark**, and six rule-bypass cheat switches that are **on** by default (flipped after 1.5.0 by owner decision; they were off before, and the whole point of keeping them in their own file behind one master switch is that this default is one config override away from being reversed). ~68,000 lines of C# in 178 files — 165 under `ProjectEden/src/` plus 13 in `ProjectEden.Preloader/`, the one BepInEx patcher this repo ships — driven by 25 JSON configs, and fully translated into English. *(Those counts were "~31,500 lines in 106 files, twenty-two configs" for a long stretch and were wrong by more than 2×; re-derive them with `find ProjectEden/src ProjectEden.Preloader -name '*.cs' | wc -l` rather than editing the number by hand.)*

`部署.md` is the deployment runbook — install instructions to forward to a tester in part one, the release flow (build → verify → `tools/pack_release.py`) in part two; **read it before cutting a package**, because the packaging target inside `ProjectEden.csproj` produces a layout that cannot carry the preloader. `mod特性.md` (Chinese) and `mod_feature.md` (English) are the player-facing feature guide, and are **one document in two languages — always edited together** (see the second content rule below). `ProjectEden/DSP-Mod-开发指南-Rider.md` is a 756-line Chinese guide to DSP modding — still a good primer on BepInEx/Harmony/LDBTool, but several build instructions are outdated for this install (see below). **Comments, log messages and docs are in Chinese; keep it that way.** Text the *player* reads is a separate surface and ships bilingually — see the second content rule below.

**GenesisBook** (`ProjectGenesis`, ~16k lines — clone it next to this repo) is the reference implementation. Most non-trivial mechanisms here were ported from it — when something doesn't work, check how it solved the same problem before inventing an approach. **Its code is used under permission from its author; none of its art is.** Every one of the 60 PNGs in `assets/icons/` is now generated by `tools/make_icons.py`, including the five mega-building icons and the build-menu tab icon, which were the last ones of uncertain provenance — so the repo ships no third-party art at all. The building icons deliberately mirror the procedural meshes in `src/Model/`, so the icon and the thing standing on the ground are the same shape.

## Content rule: new items and recipes must be derived from real chemistry and physics

**This is not flavour text — it is how every number in `ores.json` gets decided.** A new item's properties are *derived*, never picked. Whenever you add or retune one, state the real-world basis in the JSON's `//` comment next to the value, so the next person retunes the anchor rather than guessing at the number again.

| Property | Derived from | Worked example |
|---|---|---|
| Recipe stoichiometry | A **balanced** chemical equation | 2 Al₂O₃ + 3 C → 4 Al + 3 CO₂ → ore ×2 + 煤矿 ×3 → 铝块 ×4 + CO₂ ×3 |
| `isFluid` | Phase at ambient conditions | 甲醇 liquid, CO / C₂H₄ / HCHO gases → all fluid; ores solid |
| `fuelType` | Is it actually combustible? | CO₂ is fully oxidised and O₂ is an oxidiser → **neither gets a heat value** |
| `heatValue` | Molar enthalpy of combustion, scaled against one vanilla anchor | coal 2.7 MJ ↔ 393.5 kJ/mol; CO 283 → 1.95 MJ, CH₃OH 726 → 5.0 MJ, C₂H₄ 1411 → 9.7 MJ |
| Which machine (`type`) | The **reaction class**, not convenience | MTO is a dehydration, not a redox → it stays on vanilla 化工厂 while the other five C1 steps go to 氧化还原化工厂 |
| Icon | Structural formula for molecules; the real material's look otherwise | C≡O, CH₃OH, H₂C=O, H₂C=CH₂ as ball-and-stick |
| `description` | The actual industrial process | 水煊气, 费托合成, MTO named as such |

Three things this rule buys, all of which have already paid off here:

- **Balance stays arguable.** Every heat value traces to one anchor, so retuning the whole family is one edit, not a spreadsheet of invented numbers. Note vanilla is *not* internally consistent on this (its hydrogen is ~4× more generous per mole than its coal), so pick one anchor and say in the comment which one.
- **It catches design errors early.** Asking "is this a redox reaction?" is what moved 碳热还原铝 and 碳热还原钴 off the smelter and kept MTO off the new machine.
- **Byproducts stop being dead ends.** CO₂ (from carbothermic aluminium) and O₂ (from electrolysis) both sat unused until the real chemistry said where they go — CO₂ + H₂ → methanol, and O₂ as the oxidiser for formaldehyde.

**A comment can describe a link the data does not implement, and nothing checks that.** `钒渣油`’s own `//` said "「钒块 · 残渣提取」提的就是它" for as long as both existed, while that recipe consumed `精炼油 ×40` and never touched the residue — so the residue’s only use was as a fuel and the vacuum-distillation recipe had no reason to be run. It was found by listing producers and consumers of every oil item, not by reading either file. **When a `//` asserts a relationship between two entries, the relationship is a claim until the other entry’s `items`/`results` are checked** — same family as "a named constant with zero readers is a claim, not a guarantee".

**The recipe graph was leaking energy, and the audit that found it should have existed from the
start.** Scanning all 77 recipes for "burnable output worth more than burnable input" returned
**14 positives totalling +528.6 MJ**, the worst being 苯 · 蒸汽裂解 at **+114.8 MJ per 4-second
craft**. Since a mega building's electricity is spread across 10000 crafts, that is a working
perpetual motion machine: ~30× return in a 1× plant and ~800× in a mega one.

Three separate errors, and only the third is vanilla's fault:

1. **The accounting unit had two contradictory answers in the same config.** 丙烯 · 催化裂化's
   comment said "6 份 × 4 = 24 个 CH₂" (one oil item = 4 CH₂) while 精炼油 · 费托合成
   (`4 CO + 8 H₂ → 精炼油 ×4 + 水 ×4`) only balances carbon if one item = **1** CH₂. Two readings
   of the same item, a factor of 4 apart, so every hydrocarbon recipe's ratios were wrong.
2. **The four oil cuts' heat values were set on a per-volume intuition** (3.0 / 4.5 / 8.0 / 12.0,
   "heavier = denser") **while the accounting unit is per-CH₂** — equal carbon per item must mean
   equal heat value. That ladder is what let cracking turn a cheap light cut into expensive
   products and mint energy.
3. **Vanilla hydrogen is 4.6× over the mod's anchor** (286 kJ/mol → 1.96 MJ; vanilla says **9.0**, measured — this line and three others said 8.0 for a long time, which is why the override's log line prints the value it actually read).
   This file already recorded that vanilla is not self-consistent here; what was new is that a
   reforming recipe yielding **8 hydrogen per craft** turns that inconsistency into free energy.

**The decisive measurement is that vanilla itself picks one CH₂ per item**: the combustion enthalpy
of one CH₂ (679 kJ/mol) maps to **4.66 MJ** on the coal anchor, and vanilla's 精炼油 is **4.50** —
4% apart. So the fix is to align *to* vanilla, not to override it: all four cuts are now 4.5 MJ and
the six hydrocarbon recipes were re-derived at 1 CH₂ per item.

Hydrogen is the one place the mod does override a vanilla value, through `ores.json`'s
`vanillaHeat` (with a `Name` cross-check, because a wrong hardcoded id would silently retune a
different item). Residual after all three fixes: **+67.2 MJ across 11 recipes**, and most of what
remains is legitimate — genuine endothermicity (steam reforming +5.5, steam cracking +4.6, water
gas +2.4), sunlight (the greenhouse's zero-input recipes), or an artefact of a deliberately
unburnable intermediate (甲醛 and PAN carry no heat value by the `fuelType` rule, so recipes
consuming them look like they create energy while the full chain is net negative).

**It is now a startup self-check, not a one-off.** `EnergyAudit.Run` is registered last on `PostAddDataAction` beside `I18N.VerifyCoverage` and `ProtoArrayCheck`, reads the **final** `HeatValue` out of LDB (so it sees the `vanillaHeat` override rather than what the config claims), and warns for any recipe whose products outvalue its inputs by more than 1 MJ. The three legitimate shapes — real endothermicity, sunlight, and deliberately unburnable intermediates — are declared per recipe in `energyNote`, **whose value is the reason**; a hardcoded whitelist would rot silently and explain nothing. Current state: 77 recipes, 11 exempt with stated reasons, 0 unexplained. **If you cannot write the reason, it is a hole.**

**What no number can fix: a 10000× machine makes electricity free.** 电解水 still yields 3.9 MJ of
hydrogen for 1.2 kJ of power in the 综合化学厂. Any endothermic fuel-producing recipe is a generator
at that speed; the only real levers are which recipes a mega building may run, and whether the
product is burnable at all.

The one property that is **not** physical is `stackSize`: it is a pure balance knob. Don't try to justify it chemically. The per-item values in the configs (300 for most of this mod's own items) are now only the *registration-time* value — since 1.10.5 `ItemStackSizePatches` flattens **every** item in LDB to `stations.json`'s `inventoryStackSize` (10000) at the end of `PostAddDataAction`, so those per-item numbers are overwritten unless that knob is 0. Set it to 0 if a per-item ladder is ever wanted back.

## Content rule: everything player-facing ships in both Chinese and English

**Every new item, recipe, vein, building, recipe type and UI string needs a Chinese name *and* description and an English name *and* description, in the same commit that adds it.** Half-translated content is worse than untranslated content: a player on English sees an otherwise-English tooltip with one Chinese line in it and cannot tell whether the mod is broken.

This does **not** contradict the rule at the top of this file. Code comments, log messages and these two documents stay Chinese. This rule is about **text the player reads in game**, which is a different surface.

How to satisfy it:

1. Write the Chinese `name` / `description` in the config as usual (`ores.json`, `machines.json`, `megabuildings.json`, `metals.json`, `recipes.json`).
2. Add the matching `"中文": "English"` pair to `data/i18n.json`. The key is the Chinese string **verbatim** — see **English localization** below for why the Chinese string itself is the lookup key.
3. Do not add an entry for a name vanilla already owns (铁块, 钢材, 低速传送带 …). `I18N.Apply` skips those to avoid overwriting vanilla's own translation, and says so.

**It is enforced, not trusted.** `I18N.VerifyCoverage` runs last on `PostAddDataAction`, walks every proto this mod registered, and warns once per `Name`/`Description` that contains Chinese and has no entry in the table, printing the string so it can be pasted straight into `i18n.json`. It also logs a line when everything is covered, so "nothing reported" is distinguishable from "the check did not run". Treat those warnings as build breakage, not noise.

The English is a real translation, not a transliteration: match DSP's own vocabulary (块 → Ingot, 矿石 → Ore, 矿脉 → Vein, 化工厂 → Chemical Plant), and keep the description's register — these read as in-world encyclopedia entries, so the English should too.

**The feature guide is a pair: `mod特性.md` and `mod_feature.md` are updated together, in the same change.** Never edit one and leave the other. The Chinese one is the original — write there first, then carry the edit across — and `mod_feature.md` says so in its own header, so a reader who finds only the English knows which one to trust when they disagree. Section numbering is parallel (一/二/三 ↔ I/II/III) so a cross-reference like "see section IX" resolves in both.

This is the same failure mode as half-translated content, one level up: a stale English guide is worse than no English guide, because nothing about it announces that it is stale. If a change is too big to translate in the same sitting, it is still cheaper to do it now than to reconstruct later which of 1500 lines moved.

## Content rule: new content updates `README.md` and the Thunderstore description too

**Every change that adds or removes player-visible content must also update `README.md` and the
`description` in `ProjectEden/manifest.json`, in the same commit.** They are the first and often the
only thing anyone reads — the Thunderstore page shows the manifest description, and the README is
what a player opens before installing. A feature that is absent from both effectively does not
exist, and a stale count in either is worse than no count.

**Keep the manifest description short.** Thunderstore allows 250 characters; this mod's sits near
120 and should stay there. It is a shelf label, not a feature list — name the shape of the mod and
the two or three things that distinguish it, and let the README carry the rest. When adding a new
headline feature, prefer rewriting a clause to appending one.

**Why this is a rule and not a habit: the same fact lives in four hand-maintained copies.**
`README.md`, `mod特性.md`, `mod_feature.md` and `CHANGELOG.md` all state how many mega buildings
there are, and `manifest.json` says it again. **Nothing errors when three of the five are updated.**
The feature-guide pair at least has a structural check (parallel `##`/`###` counts and resolvable
TOC anchors); README and the manifest have none at all. Both have already gone stale in practice —
the README said "eight mega buildings" several releases after there were nine, and it kept quoting
the pre-audit per-volume heat ladder for the four oil cuts after that ladder had been retracted
everywhere else.

The practical checklist for any content change: `README.md` → `manifest.json` description →
`mod特性.md` → `mod_feature.md` → `CHANGELOG.md`. Numbers first (how many buildings, veins, recipe
types), then the new row or section.

**`CHANGELOG.md` holds the current version and nothing else (owner instruction).** Exactly one
`## <version>` section, and it must be the version in `manifest.json`. When you cut a release, the
previous version's section moves to **`ProjectEden/CHANGELOG-history.md`** (newest first) and
`CHANGELOG.md` is left with just the new one plus its pointer line. **Nothing is ever deleted** —
git has every version anyway, and the archive stays in the repo; it simply does not ship, which is
the point. Do not add `CHANGELOG-history.md` to `pack_release.py`'s file list.

A secondary ceiling of **100,000 characters** stays as a backstop, since one version's entry can
still run away. It is measured on **content characters with line endings normalised to `\n`** — the
repo checks out CRLF, and counting those raw adds one phantom character per line, which would also
give different answers on Windows and Linux.

`tools/pack_release.py`'s `check_changelog(version)` enforces both and **prints the section list and
the character usage on every pack whether or not they pass** — a check that is silent when it passes
cannot be told apart from a check that never ran. At 1.10.5: `1,687 字 … 版本段 1 个 ['1.10.5']`,
and the shipped `CHANGELOG.md` went from 211 KB to 3 KB.

The reason is the same one that makes the manifest description a shelf label: the changelog ships
inside the package and is the first thing a reader scrolls. A changelog nobody reaches the bottom of
documents nothing.

## Local setup (verified)

| | |
|---|---|
| Game | `G:\SteamLibrary\steamapps\common\Dyson Sphere Program`, v0.10.34.28529 |
| Unity | 2022.3.62f3c1, Mono |
| BepInEx | 5.4.17 — inside an **r2modman profile**, not the game folder |
| Profile | `%APPDATA%\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx` |
| Dependencies | LDBTool 3.0.3, CommonAPI 1.6.7, DSPModSave 1.2.2 — referenced as direct DLL paths into the profile, not NuGet |
| Preloader | `<profile>\BepInEx\patchers\ProjectEden\ProjectEden.Preloader.dll` — **a second install location.** Deleting only the plugin leaves the assembly rewrite in place; deleting only the patcher leaves the plugin expecting widened fields (it detects this and degrades, see `CargoWidening.Report`). |
| Git | A real repository, 57 commits from `伊甸园计划 v1.1.0：首次公开提交` (2026-09-11), remote `origin` = the GitHub URL in `manifest.json`. **All work happens on `main`** — the one other branch, `alien-vein-fixes-and-silicon-carbide`, is fully merged and carries nothing. `bin/ obj/ dist/`, `DefaultPath.props` and the IDE folders are gitignored. *(This line used to read "Not a git repository", which was wrong for ~50 commits — a stale claim in this file costs exactly as much as a stale claim about the IL.)* |

Machine paths live in `DefaultPath.props` at the solution root (copy `DefaultPath.props.example`); the csproj imports it and falls back to `Condition`-guarded defaults. `Newtonsoft.Json.dll` ships in `ProjectEden/lib/` and is deployed next to the plugin — Unity's `JsonUtility` **silently** returns null for nested custom-class arrays, which is why it isn't used.

## Commands

```bash
dotnet build                                   # builds + deploys to the profile's plugins/ProjectEden/
dotnet build -p:GameDir="D:\other\path"        # override paths

# The preloader is a SEPARATE project and is deliberately NOT deployed by a plain build.
dotnet build ProjectEden.Preloader/ProjectEden.Preloader.csproj
powershell -ExecutionPolicy Bypass -File tools\verify_preloader.ps1   # offline: rewrite a copy, re-read, assert
powershell -ExecutionPolicy Bypass -File tools\verify_preloader.ps1 -Config Release   # 打包前：校验真正要发出去的那个二进制
dotnet build ProjectEden.Preloader/ProjectEden.Preloader.csproj -p:DeployPreloader=true
```

```bash
powershell -ExecutionPolicy Bypass -File tools\verify_harmony.ps1   # 三类会让 PatchAll 抛异常的注解错误
python tools\check_slots.py         # 物品格位 / 配方格位 / 建造栏槽位 / 模型 ID 的占用冲突
python tools\check_guides.py        # 两份特性指南的 ##/### 条数与目录锚点是否对得上
python tools\sim_throttle.py        # 离线复现巨型建筑分频节流的时序（见 MegaThrottle 那一节）
python tools\sim_pairindex.py       # 物流配对表：增量维护和全量重建是否等价（见 LocalPairIndex 那一节）
python tools\sim_hubtray.py         # 枢纽「摆台→派送→退货」一圈：复现「多出一格同样的货」，并比对三种退货策略
powershell -ExecutionPolicy Bypass -File tools\check_bp_nest.ps1       # 蓝图 CheckBuildConditions 里那 6 个 O(预览²) 循环还在不在
powershell -ExecutionPolicy Bypass -File tools\check_bp_inner.ps1      # 那 6 个循环是不是仍然只写 condition（跳过它们的前提）
powershell -ExecutionPolicy Bypass -File tools\check_bp_writes.ps1     # 整个 CheckBuildConditions 写了哪些字段（为什么不能整体短路）
powershell -ExecutionPolicy Bypass -File tools\check_output_gate.ps1   # 产出闸的 7 处乘法站点还在不在（见 MegaOutputGatePatches）
powershell -ExecutionPolicy Bypass -File tools\check_bp_anchor.ps1     # 蓝图粘贴里「物流站邻距」那道外层闸的锚点判据是否仍然唯一
powershell -ExecutionPolicy Bypass -File tools\check_bp_coverbelt.ps1  # 「覆盖带重建」那六个条件和两条路径还在不在（见 BlueprintCoverBeltPatches）
```

**Run `verify_harmony.ps1` after adding or editing any patch class.** It catches the three mistakes that throw out of `PatchAll` — a `TargetMethods` selector sharing a class with individual annotations, a bare-name patch on an overloaded game method, and **a prefix/postfix parameter name the target does not declare**. All three are invisible to the compiler and none of them fails as "this patch did nothing".

**Always run `verify_preloader.ps1` before deploying the preloader, and again after any game update.** It drives the real patcher against a *copy* of `Assembly-CSharp.dll`, writes the result, then re-reads it and asserts ten invariants. A preloader mistake surfaces as the game failing to start with a CLR type-load error that names nothing of ours, so none of this repo's usual method (read the IL, report a match count, loud-fail on zero) is available at that point.

No tests, no linter. Verification is: launch DSP through r2modman and read `BepInEx/LogOutput.log`. Enable the console with `[Logging.Console] Enabled = true` in the profile's `BepInEx/config/BepInEx.cfg`.

**Rider needs its MSBuild pinned.** Because the project targets `net472`, Rider switches to a .NET Framework MSBuild and on auto picks VS BuildTools 18.0, which cannot resolve `Microsoft.NET.Sdk` against SDK 8.0.424 — the project then fails to load with `找不到指定的 SDK "Microsoft.NET.SDK.WorkloadAutoImportPropsLocator"`. Fix: Settings → Build, Execution, Deployment → **Toolset and Build** → *Use MSBuild version* → `C:\Program Files\JetBrains\JetBrains Rider 2026.2.1\tools\MSBuild\Current\Bin\amd64\MSBuild.exe`. Rider's bundled MSBuild is the only one on this machine that works; both VS MSBuilds fail on any SDK-style project. `dotnet build` is unaffected, so **a green CLI build does not prove Rider can open the solution** — verify both after touching the TFM or toolset. Rider's real error lands in `%LOCALAPPDATA%\JetBrains\Rider<ver>\log\MsBuildTask\<pid>.<solution>.msbuild-task.log`.

## Editing files: use the Write/Edit tools, not shell heredocs

**Write and edit files with the `Write` and `Edit` tools. Do not route edits through `bash`
heredocs, `python - <<'EOF'` one-liners, or `sed -i`.** This is an owner instruction, and it was
given after watching the shell route fail repeatedly in a single session.

Every failure below is real and happened here, none of them in the content being written — all of
them in the layer that was supposed to deliver it:

- **The heredoc itself gets eaten.** `python - <<'PY'` with a script containing quotes came back as
  `bash: unexpected EOF while looking for matching '''`, twice, on scripts that were perfectly valid
  Python. The delimiter is quoted, so this should not be possible; it is, so stop relying on it.
- **PowerShell scripts must be pure ASCII, and a heredoc makes that easy to forget.** Windows
  PowerShell reads `.ps1` as ANSI, so one Chinese comment turns the whole file into parser errors
  that look nothing like an encoding problem (`unexpected token 'case'`, `missing string
  terminator`). This trap is already documented further down and was re-triggered anyway, *because*
  the script was being written through a heredoc instead of a file.
- **Python's own escape warnings.** `\s` / `\S` inside a heredoc-delivered regex raises
  `SyntaxWarning: invalid escape sequence`, noise that hides real output.
- **Anchor mismatches cost a round trip each.** A replace-with-assert script fails on the first
  wrong character of indentation or a full-width comma, and each failure is one more round of "print
  the surrounding text, adjust, rerun". `Edit` matches against the file as read, so the mismatch is
  caught before anything runs.

**What the shell is still right for:** running builds, `git`, `dotnet`, the packager, the icon
generator, Cecil/IL inspection — anything that *executes* rather than *authors*. A script that is
genuinely a program (a numeric verification sweep, a one-off migration over many files) belongs in a
real file under the scratchpad, written with `Write` and then run — not inlined into a heredoc.

The underlying rule: **the delivery mechanism for an edit should not be able to fail in ways the
edit itself cannot.** Every failure above was invisible in the change being made and only appeared
in the shell's parsing of it.

## How to debug this mod

Reading IL beats guessing. Every mechanism documented below was found this way, and several rounds were wasted by not doing it first:

```powershell
Add-Type -Path "<profile>\BepInEx\core\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("<game>\DSPGAME_Data\Managed\Assembly-CSharp.dll")
$asm.MainModule.GetType("MinerComponent").Methods | ? { $_.Name -eq "InternalUpdate" } |
  % { $_.Body.Instructions } | % { "{0:X4}: {1} {2}" -f $_.Offset, $_.OpCode.Name, $_.Operand }
```

Use it to confirm a transpiler's target pattern **before** writing the transpiler, and to inspect the deployed `plugins/ProjectEden/ProjectEden.dll` when a patch appears to do nothing (that is how the null-`buildings` JSON bug was found — an `ldlen` on null).

**One-shot logs on parallel tick paths need `Interlocked`.** A plain `if (_done) return; _done = true;` guard is not enough when the method runs under `_miner_parallel` / `_assembler_parallel` — every thread sees the flag unset and logs. Claim with `Interlocked.Exchange(ref flag, 1) == 0` **before** building the message, so the string interpolation stays off the tick path too (`AdvancedMinerPatches.ClaimLog`).

Prefer log-driven diagnosis over screenshots. Every patch here reports what it matched and how many sites it rewrote, and loud-fails on a match count of 0; that is what makes "it silently did nothing" diagnosable. **The same rule applies to configuration, not just to patches**: report the state you read, including the boring state. A status line that only prints when something is enabled makes "disabled" and "not loaded at all" look identical, and both `AlloyRatioPatches.ReapplyAll` and `ReportCheats` cost a round trip by getting that wrong. Several bugs were closed in one round by adding a one-shot diagnostic line (`建造栏核对`, `巨型建筑物流站接管成功`, `大型采矿机产量`) instead of guessing.

## Traps that cost real time here

Check these first when a change "has no effect".

**1. Values baked into the save at build time.** `AssemblerComponent.speed`, `StationStore.max`, `PowerConsumerComponent.workEnergyPerTick`, `StationComponent.collectionIds` / `storage[].itemId`, slot counts — all copied out of `prefabDesc` when the entity is created, then serialized. Changing `prefabDesc` only affects *newly built* entities. Every such value needs a second, runtime fix-up path for already-built ones (`StationCapacityPatches.PlanetTransport_GameTick`, `AdvancedMinerPatches.ApplyFixedPower`, `MegaAssemblerPatches.ApplySpeed`).

**2. The same constant, two meanings.** Blanket-replacing a literal breaks things:
- `UIBuildMenu`: `9` is both a *validity guard* and a *loop bound over the category buttons*. Raising the loop made it manage indices 10/11 — which hold the **dismantle and blueprint buttons**, not spare category slots — and the entire build bar broke. Only index **12** is genuinely free; `BuildMenuCategoryPatches._OnCreate` errors out if it isn't.
- `UIBuildMenu` again: the child row's `12` and the replicator grid's `14` are *array-shaped* limits, not raisable bounds — see **UI grids** below.
- Ordering branches (`bgt`/`bge`/`blt`/`ble`) are limits and may be raised; equality (`beq`/`bne.un`) means "is this the foundation category" and must not be. `IsOrderingBranch` encodes this.
- `MinerComponent`: `4` is both the belt-output stack cap and a divisor in the proliferator-point conversion.

**3. UI and logic read different sources.** The station panel's 集装数量 slider is recomputed from the *tech's* `UnlockValues`, not from `GameHistoryData.stationPilerLevel`. The mining panel shows `MinerComponent.speed`, while throughput uses the `miningSpeed` parameter. Change only one of a pair and you get "the number moved but nothing happened", or the reverse.

**4. Multithreaded tick paths.** `GameLogic._assembler_parallel` / `_miner_parallel` / `_station_output_parallel` call the components directly, bypassing `FactorySystem.GameTick`. A patch on `FactorySystem.GameTick` alone never runs — belt I/O for the mega buildings was silently dead for a long stretch because of this. Patch the component method, or transpile **both** call sites (`MegaAssemblerPatches`).

**And the parallelism is across planets, not just call sites.** `PlanetTransport.GameTick` runs under `GameLogic.FactoryTransportGameTick_Parallel` with ~31 worker threads, each on a *different planet*. Per-planet game data is therefore safe, but any **static scratch collection is shared by all of them** — a plain `Dictionary` written from a postfix there gets corrupted within minutes (`Operations that change non-concurrent collections must have exclusive access`). This crashed `MegaVirtualLogisticsPatches` in the wild and was latent in `LabLogisticSupplyPatches` and `GasCollectorPatches`. Per-tick scratch containers must be `[ThreadStatic]` (lazily created — a `[ThreadStatic]` initializer only runs on the first thread); shared caches must be `ConcurrentDictionary`. Read-only lookups built once at load (`ProductMap`, `TargetProtoIds`, `ChargePowerByProto`) are fine as plain collections.

**4b. A static cache built during preload, before LDBTool exists.** `ItemProto.fluids` — the whitelist that decides what may enter a 储液罐 — is built by `ItemProto.InitFluids()` at **IL offset 0x08B0** of `VFPreload.PreloadThread`, which scans `LDB.items.dataArray` for `IsFluid`. LDBTool patches `VFPreload.InvokeOnLoadWorkEnded`, reached at **0x0E11** of that same method — the last call in it. **The whitelist is therefore built before any mod proto is in LDB**, so setting `ItemProto.IsFluid = true` on a mod item accomplishes nothing on its own.

It fails silently and totally: an empty tank pulls from each of its four belts with `TryPickItemAtRear(beltN, 0, ItemProto.fluids, …)` — that array is passed **as the filter**. Not in it → never picked up → `fluidId` stays 0 → the tank stays empty forever. Hand-insertion goes through `PlanetFactory.EntityFastFillIn` → `ItemProto.isFluid(id)`, which reads the same array. Those two are the only gates (`ItemProto` carries no fluid colour or similar field; the tank's look is generic).

**There is more than one table in this family, and three of them are ours to fix.** `InitFluids` (0x08B0), `InitTurretNeeds` (0x08AB) and **`InitPowerFacilityIndices` (0x08D8)** sit within 0x30 bytes of each other in `PreloadThread` and LDBTool re-runs none of them; `RefreshFluidList`, `RefreshTurretNeeds` and `RefreshPowerStatIndices` do. Check this list before assuming a proto flag is enough.

**The power one is the mildest and the most instructive, because it does not fail — it misfiles.**
The power statistics panel does not compute its per-building rows; it looks them up:
`ProductionStatistics.RefreshPowerConsumptionDemandsWithFactory` @0054–006D is
`conDemands[powerConId2Index[protoId]] += requiredEnergy`, and the generation side is identical.
Both tables are a fixed `new int[12000]`, and this repo's item ids sit at 6500-odd — **inside the
array**, so there is no exception, just the default **0**. Index 0 is the placeholder the builder
seeds with `Add(0)` (`powerConIndex2Id[0] = 0`, resolving to no proto), so every mod power building's
draw pools into one nameless row. **The totals stay correct the whole time** — `totalConDemand`
(@00A0–00B5) accumulates outside the lookup — which is why the symptom reads as "the breakdown is
off" rather than "the panel is broken", and why nobody noticed for many versions.

**The timing is the only real design question here, and it does not generalise from the other
three.** `ProductionStatistics.Init` @0084/@0089 sizes `genCapacities` / `conDemands` / `genCount` /
`conCount` from `powerGenIndex2Id.Length` / `powerConIndex2Id.Length`, and its callers are
`GameStatData.Init` / `Import` — **once per game start or load**, i.e. after `PostAddDataAction`.
So rebuilding on `PostAddDataAction` is safe (the tables grow first, the stat arrays are allocated
against the new length afterwards) and rebuilding any *later* is a crash: a new index written into an
old-length array. **Before re-running a preload-time builder, find what else is sized from its
output and when that sizing happens** — "it is idempotent" says nothing about whether its consumers
have already measured it.

**LDBTool re-runs some of that family but not this one.** Its `VFPreload_Patch.VFPreloadPostPatch` calls `InitFuelNeeds`, `InitConstructableItems`, `InitItemIds`, `InitItemIndices`, `InitRecipeItems`, `InitSignalKeyIdPairs` and `IconSet.Create` after `PostAddDataAction` — but never `InitFluids`. So a mod fuel needs nothing extra (just `FuelType` + `HeatValue`, and `ItemProto.fuelNeeds` — the filter array `CargoTraffic.TryPickFuel` hands the belt — is rebuilt for you), while a mod fluid does. **Check LDBTool's post-patch list before writing your own rebuild.**

**CommonAPI already does this rebuild, and runs before us.** `CommonAPI.ProtoRegistry.OnPostAdd` calls `InitFluids()` too, so in this profile the whitelist is already correct by the time `RefreshFluidList` runs. That made an earlier version of the check log "**流体白名单重建后没有新增任何物品**" on every launch — a false alarm claiming a working feature was broken, because it measured *the delta* ("did I add any?") instead of *the outcome*. It now scans `LDB.items` for anything with `IsFluid` that is missing from `ItemProto.fluids` and warns only on that. **Verify the end state, not your own contribution to it** — otherwise the check breaks the moment someone else fixes the same thing first.

The fix is to re-run vanilla's own builder after LDB is complete — `ProjectEdenPlugin.RefreshFluidList`, registered **last** on `PostAddDataAction`. It is kept even though CommonAPI currently makes it redundant: `InitFluids` rebuilds wholesale so running it twice costs nothing, and it stops the feature depending on another mod's internals. `InitFluids` rebuilds from `dataArray` wholesale, so it is idempotent and keeps every vanilla fluid, and all readers do a fresh `ldsfld ItemProto::fluids`, so swapping the static field is picked up immediately. No transpiler. **Any other preload-time static cache has the same shape** — check the offset of its builder against 0x0E11 before assuming a proto flag is enough.

**4c. A third member of that family, triggered by a constructor rather than by preload:
`StorageComponent.itemStackCount`.** It is `new int[12000]` filled from `ItemProto.StackSize`
(`LoadStatic` @00DC–00F1), and *every* storage write path reads it — `AddItem`, `AddItemStacked`,
`AddItemFiltered`, `AddCargo`, `Sort`, `DeliveryPackage.SetDeliveryItem`, `Mecha.AutoReplenish*`,
`BuildingParameters.PasteToFactoryObject` — before writing the result into `GRID.stackSize`.
`LoadStatic` opens with `if (staticLoaded) return;` and its **only** caller is
`StorageComponent..ctor`, so the table freezes the moment the first storage component anywhere is
constructed. Changing `ItemProto.StackSize` afterwards reaches nothing. Same fix as `InitFluids`:
clear `staticLoaded` and re-run vanilla's own builder, which rebuilds wholesale from `dataArray`
and is therefore idempotent (`ItemStackSizePatches`). **The trigger is what is new here** — this
one is not in `PreloadThread`, so "check the offset against 0x0E11" would have missed it. The
general test is *what guards the rebuild and who calls it*, not *where it is scheduled*.

**And a counter-example to trap 1 that is worth recording, because the habit points the wrong way.**
`GRID.stackSize` is a genuine save field (`Export` @00C9 / `Import` @00DB), which by trap 1 would
demand a runtime fix-up for already-built storages. It does not: `StorageComponent.Import`
@012C–015D immediately **re-derives it from `LDB.items.Select(grid.itemId).StackSize`** and
overwrites what it just read. So existing saves self-heal, in both directions, and a fix-up pass
would be pure dead code. Trap 1 is about values vanilla copies *once*; **before writing the fix-up,
read the `Import` for a re-derivation** — `LabComponent.Import` and `AssemblerComponent.Import`
have the same shape and this file already relies on both.

**5. Fresh games differ from loaded saves.** `GameHistoryData.SetForNewGame` starts with `recipeUnlocked.Clear()`, wiping anything added in `Init`. And `ItemUnlocked` reads the `recipeUnlocked` HashSet **directly** instead of calling `RecipeUnlocked()`, so patching that method doesn't make an item buildable — `UnlockKey = -1` does.

**6. Units.** `MinerComponent.speed` is in hundredths (100 = 1%). `Cargo.inc` holds the *whole stack's* proliferator points, not the per-item rate — vanilla stores it in a byte, which capped fully-proliferated stacking at **63**. This repo's preloader widens both `inc` and `stack` to `Int16`, moving that ceiling to **8191**; see **Cargo.inc widening** and **Game internals: the cargo stacking ceiling**. When the preloader does not apply, the byte limit is back and `CargoIncClampPatches` makes the overflow a deterministic downgrade instead of silent corruption.

## Architecture

`Plugin.cs` (`ProjectEdenPlugin`, GUID `com.wangyu.projecteden`) loads the three JSON configs, registers the CommonAPI tab **before** the build bar is created, runs `PatchAll(Assembly)`, then hooks `LDBTool.PreAddDataAction` / `PostAddDataAction`. It implements `IModCanSave`; that stream is positional, so `Export`/`Import`/`IntoOtherSave` must stay in lockstep.

**Every Harmony patch class needs a class-level `[HarmonyPatch]`** — `PatchAll(Assembly)` silently skips unannotated classes, unlike the `PatchAll(Type)` overload the guide uses.

### Registration — `src/MegaBuildingRegistry.cs`

`PreAddDataAction` clones vanilla ModelProto 49 (物流运输站 — picked because it carries belt ports — `PrefabDesc.portPoses`, **not** `slotPoses`, which is the inserter array; see the exchanger section), tints its materials, overrides `prefabDesc` into assembler + station, and registers item/model/recipe via `LDBTool.PreAddProto`.

`PostAddDataAction` re-runs the static caches the new protos invalidate. **`ProtoPreload()` is the critical one**: `ItemProto.Preload(index)` loads `_iconSprite`, links `prefabDesc` from the ModelProto and rebuilds the item↔recipe association. Skip it and you get a blank icon, `-` for 制造速度 and 制造于, and an empty replicator entry.

Model IDs must be **< `LDB.models.dataArray.Length + 64`** — `ModelProtoSet.OnAfterDeserialize` allocates an array of that size but indexes it by model ID. `ResolveModelId` picks a valid one and logs it; the results are pinned in the JSON because model IDs go into saves.

### Mega buildings — `src/Patches/MegaAssembler/`

Identified at runtime by `AssemblerComponent.speed >= megaSpeedThreshold` — deliberately decoupled from `assemblerSpeed` so retuning speed doesn't break identification (GenesisBook uses the same discriminator, hence the SoftDependency guard).

- `MegaAssemblerPatches` — belt-direct I/O through `SlotDataStore` (12 slots per building, keyed by `(planetId, entityId)`, persisted via DSPModSave), plus `RunExtraCycles`, which re-enters vanilla `InternalUpdate` N−1 extra times per tick. That is how the engine's hard ceiling of **one recipe cycle per tick** (60/s) is exceeded without hand-writing settlement logic — each extra call is complete vanilla logic, so nothing can be conjured out of nothing.

  **A 10000× machine is almost immune to a brown-out, and that is a side effect rather than a design.** Vanilla throughput is linear in supply — `InternalUpdate` IL 0576 is `time += (int)(power * speedOverride)` — but at `speedOverride = 1e8` a single call adds one to two orders of magnitude more `time` than any recipe's `timeSpend`, so **a supply ratio of 0.11 settles exactly as much as 1.00**; then IL 0000's `if (power < 0.1f) return 0;` kills the building outright. The player-visible shape is "no slowdown at all, then suddenly dead". `powerScalesCycles` (megabuildings.json, default on) scales **the cycle count** by supply — the same lever `MegaLightPatches` uses, because `speed` must never move — **copying vanilla's own linearity rather than inventing a curve**, with a floor of 1 cycle/tick (a 1× machine at full speed) and vanilla's 10% gate left to do the actual stopping. Note power draw is unaffected either way: `SetPCState` computes from `workEnergyPerTick`, not `speed`, so there is no feedback loop between throttling and demand.
- `MegaStationPatches` — lays storage out as requires→Demand / products→Supply, shuttles items between slots and the assembler, keeps drones and energy topped up. Calls `RefreshStationTraffic()` only when the layout actually changes.

  **The slot count is gated by `UIEntityBriefInfo.icons`, not by the station logic.** That is a fixed-length array in the prefab which the hover panel walks by slot count, so it used to pin mega buildings at 5. `StationExpandPatches` now resizes it in an `_OnCreate` prefix, and `MegaStationPatches.MaxSafeStorageKinds` follows that resize (`StationExpandPatches.ExpandedIconKinds`) instead of hardcoding 5 — mega buildings ship 30. The constraint that remains is **`megabuildings.json`'s count must not exceed `stations.json`'s**, since the latter is what sizes the icons array; `MegaBuildingRegistry.SafeStorageKinds` clamps with a warning, because the two settings live in different files and the symptom of getting it wrong ("the game crashes when I hover a mega building") points nowhere near the cause.
- `MegaVirtualLogisticsPatches` — moves items straight between the mega building's storage slots and other planetary stations so drones never launch (the point is render cost: at 3600 recipes/s the stations dispatch continuously). Must be **bidirectional** — inbound only still leaves other stations sending *their* drones to collect the products. Slot direction needs no guessing: `SyncStorageLayout` already lays requires out as Demand and products as Supply. Six passes total (per direction: gather → walk stations → write back) rather than scanning stations per mega station, which would be mega × stations × slots².
- `MegaStationWindowPatches` — reports `stationId` as 0 for these buildings so the recipe window opens instead of the station window; otherwise both fight over `ShutAllFunctionWindow`.
- `RecipeUnlockPatches` — the fresh-game unlock problem from trap 5.

### Light-dependent recipes — `src/Patches/MegaAssembler/MegaLightPatches.cs`

生物温室 (item 6505, model 708, `ERecipeType` **11**) is the sixth mega building and the only one that
does not borrow a vanilla recipe type. Three things about it are worth keeping.

**1. The light formula is copied, not invented.** `PowerGeneratorComponent.EnergyCap_PV(sx, sy, sz, lumino)` is one
line: `currentStrength = clamp01((sun·pos) * 2.5f + 0.8572445f) * lumino`, where `pos` is the generator's
**normalized position** (written from `pos.normalized` in `PowerSystem.NewGeneratorComponent`, IL 02AD–02F7) and
the two arguments come, at the only call site (`PowerSystem.GameTick`), from `PlanetData.runtimeLocalSunDirection`
normalized (V_14) and `PlanetData.luminosity` (V_11). Those two fields are the whole input set; there is no third
channel and nothing to guess. The 2.5/0.8572445 pair is what gives a dusk instead of a hard cutoff.

**2. Scale the cycle count, never `speed`.** A mega building's `speed` is 1e8, two orders above any recipe's
`timeSpend`, so lowering it does nothing until it crosses that line — and crossing it is fatal, because
`MegaAssemblerPatches.MegaTick` identifies mega buildings by `speed >= megaSpeedThreshold` and a building that drops
under it is never picked up again. The throughput lever is `RunExtraCycles`' loop bound.

**The visible cost of that choice: the assembler panel's 制造速度 row always reads 10000×.** It reads `speed`, which is
deliberately never touched. There is no lever that both tracks the sun and shows up in that row — scaling `speed` down
to the threshold bottoms out at 30×, not 0, and going below it kills the building. Say so in the docs rather than
letting a player read the panel as a bug.

**3. A pre-hook cannot cancel the call it precedes.** The transpiler injects `MegaTick` *before* each
`AssemblerComponent.InternalUpdate` call site, so on a zero-light tick vanilla's own call still runs and would settle
a full cycle on its own (`time += power * speedOverride`, and speedOverride ≫ timeSpend). The suppression is
therefore to pre-load `time` with a value that still cannot reach `timeSpend` after the increment. The increment's
upper bound is exactly `speedOverride` (power ≤ 1), so `-speedOverride - 1` is derived, not picked. **Both
timers need it**: `time` (main product, IL 0101) and `extraTime` (proliferator extra output, IL 0022) are independent
`if`s in `InternalUpdate`, and suppressing only one leaks production.

**Zero-input recipes work, and the check is a loop over a zero-length array.** `InternalUpdate` IL 03A8–03F7 is
`for (i = 0; i < requireCounts.Length; i++) { if (served[i] < requireCounts[i] || served[i] == 0) { time = 0; return
false; } }` — with length 0 the body never runs, so the insufficient-input return is unreachable. `OreRegistry`
accordingly allows an empty `items` side **and logs an INFO line saying so**: a zero-input recipe and a typo that
dropped the ingredients look identical in the config, and the difference is a machine that creates matter from
nothing.

**Four-input recipes work too, and this is not symmetric with the product side.**
`UIAssemblerWindow.SyncServingStorage` loops `served.Length` via `ldlen` with no cap, unlike the product side, which
vanilla fully unrolled at 2 (see `MultiProductUIPatches`).

**And `Suppress` is only safe when suppression is RARE — `MegaThrottle`'s tick divider made it a
zero-output bug.** `AssemblerComponent.InternalUpdate` is a two-stage pipeline: **the settle at
IL 0101 cashes in the `time` that the PREVIOUS call accumulated at IL 056F.** So "filled" and
"settled" always land on adjacent calls. The greenhouse gets away with wiping `time` because a
sunset is minutes long — one pending cycle is lost at the boundary and nobody notices. A
`tickDivider` building is the opposite: **the tick after every fill is a suppressed tick**, so the
pending cycle is destroyed every single time. Net effect: the first tick deducts one full set of
inputs and sets `replicating = true`, and from then on nothing ever settles. Offline replay over
7000 ticks at divider 70: **0 cycles, 1 batch of inputs consumed** — exactly the shipped symptom
(「给了原料没办法产出产物」), on all four antimatter buildings.

The fix is that **suppression needs a matching release**: see `MegaThrottle.Hold` / `Release` below.
The general rule: *a "stop the machine" primitive is not a "slow the machine down" primitive*, and
the difference only shows up when the two alternate at tick granularity.

`lightDependent` is a per-**building** flag in `megabuildings.json`, collected into `MegaLightPatches._protoIds` at
registration and matched against `EntityData.protoId` on the tick path. The building stops at night whatever recipe it
is running.

**It was per-recipe first, and that is worth recording because the reasoning was sound and still lost.** The chemical
argument is that only 光合育林 sees the sun — the co-culture and the extraction happen inside tanks — so a per-recipe
flag in `ores.json` was the first implementation. **The owner's decision was per-building**, and the deciding argument
is legibility, not chemistry: one building with one state is far easier to explain than one machine where some recipes
turn and some do not. The `ores.json` flag was **deleted rather than left in place** — two switches with the same name,
both effective only on mega buildings, is a configuration trap where someone sets one and believes it took.

**A custom `ERecipeType` on a mega building needed two display fixes.** `MachineRegistry.RecipeTypeMachineName` and
`MachineTypeName` previously scanned only `machines.json`; they now fall back to `megabuildings.json`, **restricted to
types 9–14**. Without that restriction the five buildings holding vanilla types would rewrite vanilla's own
制造于 text (every Assemble recipe would read 制造于 天工装配厂) —
that is overwriting working vanilla copy, not filling in a gap. `MegaBuildingEntry.machineTypeName` supplies the item
tooltip's 类型 row, since returning the building's own name there reads oddly.


### A mega building that is deliberately slow — `src/Patches/MegaAssembler/MegaThrottle.cs`

The four antimatter buildings (视界蒸发炉 / 磁分离塔 / 对产生室 / 彭宁阱复合室, `ERecipeType` 19–22)
are mega buildings that must *not* run at 10000×. `speed` cannot be lowered — `MegaTick` identifies a
mega building by `speed >= megaSpeedThreshold` and one that drops below is never picked up again — and
lowering `cyclesPerTick` bottoms out at 1, which at `speedOverride = 1e8` is still 60 crafts/s. So the
only lever is **skipping ticks** (`tickDivider`), phase-offset by `entityId` the way vanilla staggers
station drone dispatch.

**Hold and Release are a pair, and shipping only Hold means zero output.** The reason is the
settle/fill pipeline recorded under `MegaLightPatches.Suppress` above. `Release` runs on the assigned
tick and forces the pending cycle to cash in:

- **The guard is `replicating`, and that is not a proxy — it is the fact itself.** Vanilla sets it
  true at IL 054E, immediately after the sufficiency check and the deduction, and false at IL 0127
  when the products are emitted. So `replicating == true` ⟺ *one full set of inputs has been paid for
  and no product has been emitted for it*. Forcing `time = timeSpend` under that guard can only cash a
  cycle that was already paid for; **matter creation is structurally impossible**. Without the guard it
  would be free production, because IL 0101 does *not* check `replicating`.
- **Offline replay before shipping**, the repo's rule and it paid: 7000 ticks at divider 70 gives
  **100 cycles** (= ticks/divider) for recipe lengths 8/10/35/60 s, with consumption = cycles + 1 (the
  one batch always in flight — vanilla's own pipeline shape, not a leak). That replay is
  **`tools/sim_throttle.py`**, kept in the repo so the numbers above stay checkable: it models
  `InternalUpdate` instruction by instruction (offsets in the comments) plus the two hooks, and it
  still reproduces the old zero-output behaviour on demand. **Change it before changing the C#** —
  when a transform grows a new case, grow its checker first.

**The proliferator half was wrong on the first pass and only the replay caught it.** Pushing
`extraTime` straight to `extraTimeSpend` on the release tick looks obviously right and yields **one
extra batch per cycle** — measured **1.00 against vanilla's 0.25**, a 4× buff. The correct per-cycle
accrual is derived, not picked: vanilla adds `speedOverride` to `time` and `extraSpeed` to `extraTime`
in the *same* statement (IL 056F/0586), and one cycle only spends `timeSpend` of `time`, so one cycle
is worth `extraSpeed × timeSpend / speedOverride` of extra progress. Substituting vanilla's own
definitions (`extraSpeed = speed × incTableMilli × 10`, `extraTimeSpend = timeSpend × 10`) collapses
that to **`incTableMilli` extra batches per cycle** — i.e. exactly the percentage printed on the
proliferator. Replay: **0.24 against 0.2503.**

**Both hooks must first rewind `extraTime` by `extraSpeed`.** IL 0586 adds it unconditionally at the
bottom of every call, for the *next* call's benefit; leaving that in place makes `Release` see
"progress + one whole `extraSpeed`" and cross the threshold immediately — that is where the 1.00 came
from. And `Hold` must **not** zero `extraTime` (which is what `Suppress` does): at one hold per tick,
zeroing means the extra timer never reaches its threshold and spraying these buildings is worthless.

**The reference-rate panels need the divider too**, for the same reason recorded under
*The reference-rate panels quote a number the engine forbids* — otherwise they quote the un-throttled
rate, which is 70× the truth here.

### Stateful production — `src/Patches/Catalyst/`

催化反应器 (item **6507**, model 703, `ERecipeType` **14**) is the eighth mega building and
**the first machine in this repo that remembers anything**. Every other machine here is
memoryless: feed it and it produces, tear it down and rebuild it and you get the same
machine. This one holds a *charge* of zeolite catalyst that loses activity as it runs,
ejects itself as 待生沸石催化剂 when spent, and blocks until a fresh charge arrives.
`data/catalyst.json` holds the knobs; state lives in `CatalystBedStore` and is persisted
through `IModCanSave` (**SaveVersion 3 → 4**, with the usual `if (version >= 4)` branch —
the stream is positional, so reading one extra int on an old save shifts everything after it).

**The state is `{charge, life}` and `life` *is* the remaining productive ticks** — not a
0–10000 scale that has to be converted for display. The design draft had the scale; the
implementation dropped it because the panel's "how much longer" is exactly that number.

**"Did this tick actually produce" is measured, not reproduced.** `RunExtraCycles` now
returns how many of its N−1 extra cycles actually settled, by watching `produced[0]` across
each `InternalUpdate` call. That is strictly better than reproducing vanilla's own gates
(power ≥ 0.1, inputs sufficient, output not full) because it cannot drift when vanilla adds
a fourth gate. `LooksProductive` is the fallback for `cyclesPerTick == 1`, where there are
no extra cycles to observe — and it **logs that it is being used**, because it provably
cannot see the output-full case and will slightly over-charge there. This is the drill-bit
lesson (*a hook that runs before vanilla decides whether to act must reproduce that
decision*) satisfied by measurement rather than by imitation.

#### Two slot traps, and the second one was found only after the code was written

Catalyst in and spent catalyst out go through the building's **station slots**, never
through the recipe arrays — `AssemblerComponent.Export` writes `produced`/`served` counts
from the **lengths** of `recipeExecuteData.products`/`requires`, so changing a length
corrupts the save, and "eject once every ten minutes" cannot be expressed as a product
without either changing the length or parking a permanent `count = 0` product on the UI.

**(1) `MegaStationPatches.SyncStorageLayout` erases any slot it did not lay out itself.**
After laying out requires→Demand and products→Supply it clears every slot past its cursor,
and the only exemption is `count > 0`. A catalyst *demand* slot **has to exist while empty**
— empty is precisely when it is asking the network for more. Filling it from the tick path
(the `AlienVeinPatches.EnsureBitSlot` approach) therefore gets it wiped once per tick, and
the symptom is "the reactor never receives catalyst" with the cause sitting in a method
whose name contains nothing about catalyst. The fix is to make it **part of that layout**
(`CatalystBedPatches.LayoutSlots`, called with the same cursor) rather than to fight it.

**(2) `StationCapacityPatches.PlanetTransport_GameTick` used to force `max` on *every* slot of
*every* target proto, *every tick*.** (It is now a **one-time bootstrap per station** — see the
capacity note below — but the trap it created is worth keeping, because the catalyst slot still has
to be skipped and the reasoning is the same.) This is the one that was missed at design time and found
by reading the code afterwards. The catalyst slot is local **Demand**, so it requests from
the network up to `max`; raised to 10,005,000 it would have the first reactor built vacuum
up every unit of catalyst in the network and starve every later one — presenting as
「我别的反应器全停了」, which points nowhere near the cause. `CatalystBedPatches.OwnsSlot`
is now consulted there to skip those two slots, and `LayoutSlots` re-asserts the capacity
every tick anyway (one int compare, against a silent failure of the whole line).

**Both of these are the same shape, and it is worth naming: a rule this repo added for one
building silently constrains another one added later — and by now the constraining rule is
usually also ours.** The drill-bit slot hit it with `StationExpandPatches.StorageCount`;
this feature hit it twice in one day. When a new building needs a slot, check every
per-tick pass that walks `station.storage`, not just the one that creates it.

#### The panel, and what the log has to carry instead

Mega buildings deliberately do not open the station window (`MegaStationWindowPatches`
reports `stationId` as 0 so the recipe window opens instead), so **those 30 slots are
invisible to the player**. `AlloySliderPatches` therefore gained a sixth mode —
`RefreshCatalyst`, the **first read-only one** (the catalyst is chosen by the recipe, so
there is no `Handle*Input` at all). It distinguishes **"the slot was never laid out" (−1)
from "the slot is there and empty"**, because the first is a layout defect and the second
is normal waiting, and they need opposite responses.

**`MegaStationPatches`' storage dump was a single global `bool`**, so of eight mega building
types only the first one to tick ever printed its slots — and that is exactly the question
this feature needed answered. It is now keyed by `protoId` in a `ConcurrentDictionary` and
names the building. *A "log it once" diagnostic should mean once per kind of thing, not once
per session.*


### An assembler that is also a generator — `src/Patches/Redox/`

氧化还原燃烧厂 (item **6509**, model 702, `ERecipeType` **17**) is the tenth mega building and the
first entity in this repo that carries **two production components at once**. It presses a
reductant and an oxidiser into a propellant grain (the assembler half) and burns that grain for
30 GW (the generator half), with the grain never touching a belt.

**A retracted claim, and the retraction is the most useful part.** This file said, under
熔岩冷却厂, that DSP "cannot express *turn X into Y while generating power*". That sentence was
half right and the wrong half had been load-bearing for two features. The correct statement is
that one **component** cannot; one **entity** can, and the evidence is three instructions apart:

- `PlanetFactory.CreateEntityLogicComponents` tests `isPowerGen` at **IL 059E** and `isAssembler`
  at **IL 1122** — two independent sequential `if`s with a dozen other component tests between
  them, exactly the shape that already lets 综合物流枢纽 carry both a `StationComponent` and a
  `DispenserComponent`.
- `EntityData` carries `assemblerId`, `stationId`, `powerGenId` and `powerConId` as **four separate
  fields**, so nothing has to be shared or faked.

This is the same lesson as *"only 14 `ERecipeType` values remain"* and
*`kMaxCargoFlowSpeedPerSecond`*: **a sentence in this file is a claim until the IL is re-read.**
The original claim was never measured — it was inferred from the fact that a recipe has no power
output field, which is true and does not imply what was drawn from it.

**Feeding the generator needs no transpiler, and that is worth stating because it looks like it
should.** `PowerGeneratorComponent.SetNewFuel(itemId, count, inc)` is public and fills `fuelHeat`
itself from `LDB.items.Select(itemId).HeatValue` (IL 0016–0030); `EnergyCap_Fuel` gates only on
`fuelCount > 0`. **`fuelMask` is not consulted at burn time** — it is the filter that
`CargoTraffic.TryPickFuel` and hand-insertion use, so a grain moved in from code bypasses it
entirely. So `RedoxBurnerPatches.Burn`, hooked into `MegaAssemblerPatches.MegaTick`, is one method
call. Two details it does have to get right: `fuelCount` is **Int16**, so the bay is topped up to a
3000 cap rather than to whatever was produced; and a **tier change is only applied when the bay is
empty**, because `SetNewFuel` replaces `fuelHeat` wholesale while `fuelEnergy` may still hold part
of the previous grain.

**Burn takes priority over export, and that falls out of the ordering rather than being enforced.**
`MegaTick` runs `UpdateSlots` (which is where `MegaStationPatches.UpdateStationStorage` drains
`produced[]` into the station slots) *before* the cycles settle, and `Burn` runs *after* them. So
each tick's fresh production goes to the fuel bay first and only the surplus — what is left once
the bay is at its cap — reaches the station slots on the next tick. That is the behaviour you want
(a power plant feeds itself first) and it needs no flag.

**The three grain tiers are enumerated items for the same reason ammo damage is.** A grain's energy
is `ItemProto.HeatValue`, stored per proto, and DSP has no per-stack or per-item metadata anywhere
(the `Cargo` struct is full; four-axis alloy properties hit the same wall). So energy density is
tiered — 双元推进剂 7 MJ / 金属浆料燃料 11 MJ / 固体复合推进剂 14 MJ — while **yield** rides
`productCounts[0]` and costs no item slot at all. One recipe, twelve combinations.

**The slider controls 配氧比, and the shape of its effect is half derived and half a stated knob.**

- The **lean** side is exact, not a penalty: at φ < 1 the oxidiser can only burn φ of the fuel, so
  yield is multiplied by φ. At φ > 1 the fuel is already fully burnt and yield stops rising — the
  extra oxidiser is simply wasted.
- That alone makes φ = 1 a single fixed optimum, i.e. **the slider would be decorative** — the
  exact failure the alloy section records for single-axis grade thresholds. So density additionally
  scales as `φ^0.5`, which can promote a pair into the next tier. The real effect it is shaped
  after (richer mixture → more complete combustion → hotter flame → denser grain) is genuine, but
  it deliberately outweighs the dilution term that would push the other way, so **it is labelled a
  balance knob in `redox.json` rather than dressed up as a derivation.**

Measured over the whole space — 6 reductants × 2 oxidisers × 61 slider positions = **732
combinations — not one produces more burnable energy than it consumes**, because yield is
`floor(parts × heat × min(1, φ) / tierHeat)`. The recipe is therefore constructively safe against
`EnergyAudit` and needs no `energyNote` exemption.

**Two candidate oxidisers were designed and then dropped by the repo's own dominance rule**, and
that is recorded in `redox.json` so they do not get re-added: 二氧化氮 releases the same 2.0 oxygen
atoms per item as 氧气 while costing two more steps of the nitrogen chain, and 硝酸铵 releases only
1.0 (its own four hydrogens claim two oxygens as water before anything else can have them) while
costing more than either. What survived — 氧气 2.0 cheap, 硝酸 2.5 expensive — is a real trade, and
it gives **硝酸 its first consumer**: it had zero, and this file names it as the repo's cautionary
example of an item with no downstream.

**Metals as fuel: `fuelType` 32, never 1.** 铝块 (5.75 MJ) and 高纯硅块 (6.25 MJ) both get heat
values, derived from the usual coal anchor. They are given **bit 32 only**, so a 火力发电厂 cannot
burn them: a solid ingot does not burn in a coal boiler — it has to be powdered and matched with an
oxidiser — and granting bit 1 would have been a free multiplier on vanilla thermal power. The
audit needs those heat values regardless: without them the grain recipe would look like it creates
energy from nothing. 高纯硅块 is vanilla, so it goes through `ores.json`'s `vanillaHeat` (which
gained an optional `fuelType` for exactly this case, plus a warning when a heat value lands on
something with no fuel bit — "half a pair burns for zero power"); 铝块 is ours, so `OreEntry` gained
`ingotFuelType` / `ingotHeatValue`, applied at `PostAddData` against the **final** LDB state rather
than at registration.

**Carnot is already saturated here, which is why the plant sells power density and not efficiency.**
A 3000 °C flame gives `0.7 × (1 − 298.15/3273.15) = 0.636` against the 可燃液体发电厂's 0.6082 at
2000 °C — three percentage points for another thousand degrees. What the premixed grain actually
buys is that the plant breathes no air and has no flue-gas volume, so one turbine handles an order
of magnitude more power. The 30 GW figure itself is a **stated balance knob** anchored on the build
recipe: it eats 100 可燃性液体发电厂 (100 × 216 MW = 21.6 GW), so it beats what it consumes without
beating it by an order of magnitude, and the real selling point is that it needs no fuel line.

**The panel is `AlloySliderPatches`' seventh mode and its first three-row one** — two picker rows
plus a slider row. `LayoutRow` already switches per row, so nothing in the panel needed changing.
It also reads the live output power off `generateCurrentTick` (**not** `capacityCurrentTick`: the
two diverge exactly when fuel runs short, which is the moment the player needs to see it), because
a mega building's 30 slots and its fuel bay are both invisible — `MegaStationWindowPatches` reports
`stationId` as 0 so the recipe window opens instead of the station window.


**Two things break the moment a building is also a generator, and both were found by launching,
not by reading.** They are recorded together because they have the same shape: a vanilla assumption
that no vanilla building violates.

**(1) A fourth window joins the fight over one click.** `UIGame.OnPlayerInspecteeChange` tests the
component ids as a run of independent sequential `if`s, each opening with `ShutAllFunctionWindow()`,
so **the last match wins** — and the order is `assemblerId` @00A7, then `powerGenId` @0182, then
`stationId` @01AA. Giving the plant a generator therefore made 燃料厂 open the *power generator*
window, which promptly threw `NullReferenceException at UIPowerGeneratorWindow._OnOpen`.
Reconstructing the DMD offset (that method has two short branches before 0x50, +3 bytes each) puts
the throw on `ldfld powerNetworkDesc` @0046 / `callvirt ManualBehaviour::_Open()` @004B — a child
widget of that window that is simply not wired up for this building. **The fix is not to make that
window work; it is to not open it**, exactly as `stationId` has been filtered since the first mega
building. `MegaStationWindowPatches` now filters that id too.

**And fixing it opened the same hole one door down, which is the part worth remembering.** Giving
the plant `isPowerNode` (see the next finding) also gives it a `powerNodeId`, and `OpenNodeWindow()`
@0640 likewise sits *after* `OpenAssemblerWindow()` @0361 — so the power-node window took over from
the generator window and the symptom merely changed from "clicking it crashes" to "it opens with no
recipe button". **The lesson is not "add another id", it is that this family has to be enumerated
once and counted.** That method holds 23 component-id tests; the test is *which components does
this entity actually have*, not *which windows do I want*. A mega building hits five —
`assemblerId`, `stationId`, `powerConId`, `powerGenId`, `powerNodeId` — of which the consumer opens
no window and the other three must all be suppressed. The transpiler now rewrites all three and
loud-fails on any count other than 3.

**(2) `NewGeneratorComponent` does not connect the generator to anything, and the asymmetry is the
proof.** Enumerating every write to `PowerNetwork.generators` across the assembly returns exactly
one adder — `PowerSystem.OnNodeAdded`, at IL 04DD, doing
`list_sorted_add(net.generators, node.genId)` — and `OnNodeAdded`'s only caller is
`NewNodeComponent`. Compare the neighbouring path: `NewConsumerComponent` **does** call
`OnConsumerAdded`. So a consumer built inside an existing supply area joins its network
immediately, while **a generator only ever reaches the grid through a `PowerNetworkStructures.Node`**
— which is to say `isPowerGen` means "it can generate" and **`isPowerNode` is what means "it is
attached to the grid"**. Every vanilla power plant carries both; that is the little connection line
under it. Our prefab is cloned from 物流运输站, a pure consumer, so the node half had to be added.

The symptom is the worst kind this repo keeps running into: **every step succeeds and the feature is
absent.** The assembler ran, the recipe produced, power was consumed, fuel was moved into the bay —
and `networkId` stayed 0, so not one joule reached the grid, with nothing logged anywhere.

`MegaBuildingRegistry.ApplyGridHookup` copies `isPowerNode` / `powerConnectDistance` /
`powerCoverRadius` off a **real vanilla power plant's prefab at runtime** (`connectFromItemId`,
default 2204) rather than hardcoding them — those live in `resources.assets` and cannot be read
offline, the same reason the accumulator and generator clones take multipliers instead of absolutes.
It copies the cover radius too, so the plant does not quietly become a substation.

**Note this needs a rebuild of any plant placed before the fix** — `prefabDesc` is trap 1, it only
affects newly built entities. `RedoxBurnerPatches.ReportOnce` prints the whole chain on one line
(recipe id vs expected, `powerGenId`, `networkId`, fuel bay, capacity and actual output) precisely
so the next "it produces but does not generate" costs one log line instead of five rounds — the
courier-chain lesson applied before it was needed a second time.

**Three smaller findings from the same building, each a "the data is right and the screen is
wrong" shape.**

- **`prefabDesc` holding a value is not the same as the tooltip showing it.** `ItemProto.GetPropValue`
  really does have a branch computing `prefabDesc.genEnergyPerTick * 60` (switch case 5, IL
  02F0–0309), but **it is only ever reached if that field id appears in `ItemProto.DescFields`** —
  a per-item list of which rows to draw. The plant's item proto is cloned from the assembler
  template, whose list naturally has no generator row, so a 30 GW power plant showed 工作功率 and
  待机功率 and no generation row at all. `MegaBuildingRegistry.MergeDescFields` unions in the field
  ids of a **real vanilla power plant** rather than hardcoding `5`: that also picks up the
  neighbouring fuel-consumption row, and it survives a game update renumbering the switch.
- **"Actual output" and "maximum output" are different numbers and only showing one of them reads
  as a bug.** The panel printed `generateCurrentTick` alone, so a plant configured for 30 GW read
  **0.03 GW** and looked like a units error. It was not: the grid only ever draws what it needs, so
  an idle plant generates almost nothing. But *low demand* and *out of fuel* produce the same
  reading and want opposite responses — the second also drops `capacityCurrentTick`. The panel now
  prints `actual / capacity`.
- **A recipe registered in `PreAddDataAction` cannot name this mod's own items yet**, so both input
  slots hold a placeholder vanilla solid. The machine is correct from the first tick (the pair is a
  per-building `recipeExecuteData` edit) — but **the replicator draws the `RecipeProto`**, so it
  advertised 石矿 ×8 + 石矿 ×8. `RedoxRegistry.ApplyDefaultToProto` rewrites `Items` / `ItemCounts`
  / `Results` / `ResultCounts` (values, never lengths) in `PostAddDataAction`, which is free:
  LDBTool calls `RecipeProto.InitRecipeItems` **after** that action, exactly as the Universe
  Matrix's seventh ingredient relies on. It must re-`Preload` the icon by **dataArray index**,
  because `MegaBuildingRegistry.ProtoPreload` has already run by then.

### The catalyst slot — `src/Patches/Lens/`

活性透镜 (item **6658**) is a **living gravitational lens**. It takes **no new building**: it goes
into the stock 射线接收站, because `PrefabDesc.powerCatalystId` already makes "which receiver eats
which lens" a per-prefab field. It generates ×5 power, ×3 critical photons, and **heals in the beam
while ageing in the dark**.

**The catalyst slot is the third and last of DSP's "a machine slowly eats an item" structures**, and
the only one this repo had not used: fuel (`fuelId`), proliferator spray (`Cargo.inc`), and the ray
receiver's `catalystId`. The whole vanilla model is three fields — `catalystId` (what it eats,
copied from `prefabDesc` at build time and **saved**), `catalystPoint` (**one lens = 3600 points**,
one point burnt per 10 ticks → exactly ten minutes), `catalystIncPoint` (the spray points those
lenses carry).

**The engine reads nothing from the lens item.** `catalystId` is a single equality compare and the
×2 is a literal. So *"add a new lens item and let it into the stock receiver"* is, by itself,
byte-identical to the gravitational lens — every difference has to come from either a new building's
`prefabDesc` or from our own patches. The owner chose no new building, so it is all patches.

**The multiplier lives in three methods and they must move together.** `cata = catalystPoint > 0 ?
2 * (1 + inc) : 1` appears verbatim in `EnergyCap_Gamma_Req` (@00A6), `MaxOutputCurrent_Gamma`
(@0030) and `RequiresCurrent_Gamma` (@0037) — respectively this tick's capacity, what is reported to
the grid as maximum output, and what is requested from it. Patch only the first and the power a
receiver actually emits disagrees with what it advertises; the grid then schedules against a wrong
number and **the symptom is a jittery power curve, not an error**. Same shape as the piler's four
`4`s: one constant serving as production, declaration and request at once.

**Power and photons are two independent knobs, and an earlier claim in this session that they were
not was wrong.** `GameTick_Gamma` @00D2 is `productCount += capacityCurrentTick / productHeat`, and
`productHeat` is `PrefabDesc.powerProductHeat` — fully decoupled from the numerator. So scaling
`cata` moves both, and scaling `productHeat` moves photons back independently. One IL site each.

**Photons have a hard output ceiling of one per tick (60/s), and the statistics register does not
respect it.** `GameTick_Gamma` holds six `InsertInto` sites (six belt ports), but each success is
`productCount -= 1` followed immediately by a `br` **past all the remaining ports** — so a receiver
emits at most one photon per tick, with `productCount` capped at 20. Minting has no such limit, and
worse, @0106 registers the minted amount **before** @0129 clamps `productCount` to 20. Over-minting
therefore inflates the production panel with photons that can never reach a belt. `LensPatches`
prints the computed rate beside the ceiling at registration and WARNs if it is exceeded (measured:
stock ≈ 0.2/s, so ×3 has two orders of magnitude of headroom — but `powerProductHeat` lives in
`resources.assets` and cannot be read offline, which is why it is measured rather than asserted).

**Do not touch `3600`.** It is simultaneously "how many points a lens is worth" and "how many points
count as a lens", spread over at least 13 sites in 4 classes (`GameTick_Gamma`'s two belt ports,
`UIPowerGeneratorWindow.OnCataButtonClick`'s insert *and* take-out, `PlanetFactory.EntityFastFillIn`).
Changing half of them does not error — it mints or eats items. **The self-healing design is the
right route precisely because it sidesteps that whole table**: it only ever adds to `catalystPoint`,
so a lens is still 3600 points and every division keeps its meaning.

**Healing must move `catalystIncPoint` with it, and the design draft missed this.** The vanilla
consume block moves both fields together — @002A `point -= 1` paired with @0038
`incPoint -= incLevel` — which is exactly what keeps the ratio `incPoint / point` (the slot's spray
level) constant. Add to `point` alone and that ratio falls monotonically: at `healRate 0.6`
`incPoint` reaches zero at step 3600 while `point` lasts to step 9000, so **the extra 15 minutes are
completely un-proliferated** and a ×5 lens decays to ×2. Nothing errors; the power curve just sags.

**`ref __instance` on `GameTick_Gamma` does write back, and this was settled offline rather than
in-game.** `PowerGeneratorComponent` is a struct, so a postfix taking it by value would silently
edit a copy. The proof is the *type of the local at the call site*: `PowerSystem.GameTick`'s V_107
(and V_89) are declared `PowerGeneratorComponent&`, obtained by `ldelema` on `genPool` — managed
references to the array element. **Read the local's declared type instead of guessing from the
opcode sequence.**

**All three insert paths name their item rather than testing a whitelist — so switching means
writing `catalystId` itself.** `PickFrom(belt, 0, catalystId, null, …)` passes it as the *filter*;
`EntityFastFillIn` @1223 reads it and hands it to `TakeItemFromPlayer(ref thatId, …)`, i.e. **it
fetches that id out of your inventory regardless of what you are holding**; `OnCataButtonClick`
@00C6 is one `beq` against the in-hand item. So the switch is implemented by assigning the field
when the bay is empty, after which statistics, dismantle refunds (`TakeBackItemsInEntity`) and the
consume register (`consumeRegister[catalystId]`) all follow it for free. **Widening the comparisons
instead would have you feed living lenses and get gravitational ones back on dismantle** — item
transmutation.

**And the belt path needs `needs`, which is a fixed six-slot whitelist.**
`CargoPath.TryPickItem` @010C–013A unrolls `needs[0]`…`needs[5]` as six unconditional `ldelem.i4`,
so **the array must have at least 6 elements** (zero-pad; a real item id is never 0). It is ANDed
with `filter` (@0104: `filter != 0 && item != filter` bails). Same family as `ItemProto.fuelNeeds`
and `turretNeeds`. The filter is kept at `catalystId` whenever the bay is non-empty, deliberately:
a mismatched pick is **dropped by vanilla with the cargo already off the belt**, so narrowing the
filter is what stops a mixed belt from eating items.

**Two process failures, both already documented rules, both re-committed.**

- **"Enumerate the family once" was applied to the *read* sites and not to the *insert* sites.**
  The three multiplier sites were enumerated and asserted; the three insertion paths were not, and
  `EntityFastFillIn` was missed. Shipped symptom, reported by the owner: 「活性透镜放不进射线接收站」
  — shift-click silently kept fetching gravitational lenses.
- **The feature shipped with no log line on its "did nothing" branches.** Registration, icon and all
  six transpiler counts logged green while the feature was unusable, so the log could not
  distinguish *the patch never ran* from *the patch ran and a guard refused*. This is the **fifth**
  time this exact shape has cost a round trip here (`AlloyRatioPatches.ReapplyAll`, `ReportCheats`,
  `CargoShaderIncProbe`, `MultiProductUIPatches`, this). `LensPatches.ReportInsert` now names the
  refusing guard on every insert attempt. **Treat "add the status line" as part of writing the
  guard, not as a follow-up.**

**Sixth time, and it changed what the status line has to say.** `QualityCraftPatches` shipped with
full per-branch tracing and **no startup line**, so "手搓没有品质" produced a log with *zero* 手搓
lines — which cannot distinguish **the patch was never applied** from **the patch is on and the
player did not craft this session**. Per-branch tracing does not close that gap: every trace sits
*downstream* of the hook, so all of them go silent together. The status line therefore must report
**whether the hooks are attached**, read out of `Harmony.GetAllPatchedMethods()` — the applied
state, not "I called PatchAll and no exception came back" — and it has to run **after** `PatchAll`,
which is why `QualityCraftPatches.Report()` sits next to `RecipeTypeCompatPatches.Report()` rather
than in the earlier report block. **The rule generalises: a feature's status line answers "is it
wired up", and the branch traces answer "what did it decide" — one cannot substitute for the other.**

The offline half of the same question is cheap and was done in the same round: enumerating callers
proved `UIReplicatorWindow::OnOkButtonClick → AddTask → AddTaskIterate` is the **only** UI path, and
`AddTask` IL 0019–0036 calls `AddTaskIterate` unconditionally once `TryAddTask` passes. So
"applied + crafted" implies the line must appear, and one launch now settles which half is false.

### Advanced miner & water pumps — `src/Patches/AdvancedMiner/`

`MinerComponent` is shared by 大型采矿机 (protoId 2316), water pumps (`type == EMinerType.Water`) and oil extractors (`type == EMinerType.Oil`); `IsBoosted` covers all three, and `GetCapacity` gives them the same 10M internal buffer via the already-transpiled `productCount >= 50` gates (three sites, one per branch).

**The buffer `50` and the throttle `50` are the same number with two meanings — and they live in *different methods*, which is why this one stayed invisible for so long.** The three `ldc.i4.s 50` in `MinerComponent.InternalUpdate` (@0142 vein, @04CB oil, @06F2 water) are only the *gate*. The throttle is written in `GameLogic._miner_parallel` @0580 and `FactorySystem.GameTick` @04D9 — both `speedDamper = min(1, -2.45 × min(1, productCount / 50f) + 2.47)`, i.e. taper from 30 items, 2% at 50 — and **this repo patches neither**: `overrideSpeedDamper` works by overwriting the already-computed value from the `InternalUpdate` prefix, so that divisor had never been exposed. Raising `smallMinerCapacity` to 10000 while leaving the divisor at 50 therefore makes the miner crawl from 50 to 10000 at **2% speed** — measurably worse than not changing it, with nothing logged. `RetuneSmallMinerDamper` recomputes vanilla's own formula with the new capacity as the divisor, so the back-pressure shape is preserved and only the scale moves. This is trap 2 one level up: **the two meanings of a constant need not be in the same method, so "I enumerated every site in this method" is not the same as "I enumerated every site".**

**And the discriminator between the two throttle branches is not the proto id — it is whether the miner has a station.** `_miner_parallel` @04C2–@0580: a miner with a `StationComponent` throttles on `storage[0].count / max(storage[0].max, 3000)`; one without throttles on `productCount / 50`. So 大型采矿机's throttle was *already* dead the moment `StationCapacityPatches` raised the slot max to 10,005,000 — nothing to do with `overrideSpeedDamper`. `IsSmallMiner` therefore tests `type == Vein && protoId != minerItemId && entityPool[entityId].stationId <= 0`, matching vanilla's own question rather than guessing at proto ids.

**The three mining branches multiply by different things**, and the anti-overflow speed derivation must match: vein by `veinCount`, oil by `veinPool[veins[0]].amount * VeinData.oilSpeedMultiplier` (the panel's 原油速率), water by nothing. `MiningMultiplier` encodes this — estimating oil as 1 overshoots `speed`, `time` blows past Int32 and the vanilla fallback clamps output to nothing. Forcing `miningRate` to 0 matters more for oil than for veins: an oil well's `amount` is simultaneously the yield multiplier and the thing depletion eats, so without it the rate decays as you mine. Speed is written into `MinerComponent.speed` (so the panel agrees), derived by working backwards from the int-overflow clamp. `speedDamper` is forced to 1 — vanilla throttles to ~2% once the buffer passes 50, which nullifies any speed increase. Power is pinned by prefixing `SetPCState`, whose vanilla formula is `speedDamper * speed² / 1e8`; without that, 100000% speed drew 30 TW. A transpiler remaps mined ore to smelted output and replaces the `productCount/50` capacity checks. **The remap needs three sites, not two.** `InternalUpdate` reads `VeinData.productId` exactly three times: twice into `stfld MinerComponent::productId` (the assignments) and once into a `bne.un` at IL @015E — the guard `if (productId != 0 && productId != vein.productId) skip this cycle`. Patch only the assignments and the miner compares a remapped `productId` against an un-remapped vein product, so **after the first remap the guard never passes and mining halts**; it resumes only when the buffer drains to zero, because `productId` is reset to 0 at @0867 (vein miners only, and only while `productCount == 0`). Same shape as the piler's four constants: **one value serving as both an assignment and a comparison, with half of it patched.** Enumerate every read of the field, not just the ones matching the shape you came for — the match count now asserts 2 assignments and 1 comparison and loud-fails otherwise. The map is **data, in `advancedminer.json`'s `productMap`** — three entries keyed by item id (铜矿→铜块, 硅石→高纯硅块, 钛石→钛块) plus three keyed by **vein type** (可燃冰 8 → 石墨烯, 分形硅石 10 → 晶格硅, 刺笋结晶 13 → 碳纳米管), whose product is derived at runtime from `VeinProto.MiningItem` and the one vanilla recipe taking only that ore — rare-ore item ids are easy to get wrong, and a failed derivation logs a WARNING instead of silently doing nothing. **铁矿, 石矿 and 煤矿 are deliberately not remapped** — iron ore feeds too much downstream as ore, 石矿 splits into 石材 or 玻璃, and coal is simultaneously a fuel and the feedstock for graphite, matrices and the whole chemistry chain, so smelting it at the source starves everything that wants the raw ore. **The test is whether the ore has exactly one obvious downstream**; don't assume every ore has an entry.

**A miner's output ceiling is `period`, and nothing else — `speed` has been saturated for
versions.** Asked "how fast is the gas collector, and can the miner match it", the measured gap was
**3600×** (collector 600,000,000/s, miner 166,667/s) and *none* of it was reachable through speed:
the same log line reports the miner's own ceiling at **178,957/s**, i.e. **7% of headroom**.

Vanilla's output is one integer division — `count = time / period` (`InternalUpdate` @017A vein /
@04E5 oil) — and `time` is Int32, so the per-tick increment is held under 2e9 by
`maxTimeIncrementPerTick`. Therefore `output/tick ≤ 2e9 / period`, the numerator is already pinned
by `maxOutMinerSpeed`, and **the denominator is the whole lever**. `advancedminer.json`'s
`minerPeriod` (`-1` = follow the collector, default) derives it from the collector's own clamp:
`GasCollectorPatches` pins the collector at "fill one storage slot per tick"
(`collectorMaxPerTick`, falling back to `slotCapacity`), so `period = ceil(2e9 / 1e7) = 200` puts
both at 1e7/tick — **equal by construction rather than two numbers tuned to agree**.

All 36 accesses were enumerated before anything moved, and the three answers decided the design:

| question | answer |
|---|---|
| who writes it | **only `Import` / `SetEmpty` / `NewMinerComponent`** (the last from `PrefabDesc.minerPeriod`). **No UI writes it**, so by the `energyMax` rule the runtime fix-up *aligns* both ways rather than raising — this is not the `storage[].max` "the configured number is the default" case |
| is it saved | **yes**, and `Import` does **not** re-derive from the proto → already-built miners need the per-tick alignment (trap 1) |
| the other 13 readers | all display/statistics (参考速率, 理论产能, planet/star panels, miner panel, both collector panels), and `period` sits in *their* denominators too — so the panels scale with it. That is **alignment, not breakage** |

**The knock-on is the interesting half, and it is trap 2 again.** The belt-output stack size is
`(36000000 / period × miningSpeed) / 1800 + 1` — `period` is in **that** denominator as well, so
shrinking it computes twenty-thousand-odd layers against `Cargo.stack`'s widened ceiling of 8191.
`PilerLevelPatches.RaiseStack` therefore went from "raise" to "raise **and cap**", at its single
exit rather than at each call site. Without it the Int16 wraps negative — the recorded
"自动集装机吃货、面板显示负数" failure, silent as ever. **When a constant sits in a denominator,
enumerate every expression that divides by it, not just the one you came for.**

**And the diagnostic that answers "did it take" was a single global bool**, so of three device kinds
(miner / pump / oil extractor) only the first to tick ever reported its ceiling — which is exactly
the number you need per kind when tuning this. Now keyed per kind in a `ConcurrentDictionary`.
**Fourth time this file records it**: a "log it once" diagnostic means once per kind of thing.

`MinerBuildRulePatches` lets advanced miners be placed overlapping. What actually blocks that is **`EBuildCondition.Collide`**, not any distance rule — established by logging the real condition rather than guessing (two rounds were wasted on `MK2MinerTooClose` / `TowerTooClose`, which never fire between two miners: the station-proximity loop opens with `if (desc.isVeinCollector && station.isVeinCollector) continue`, and the miner's vein path only checks `NeedResource` / `NeedSingleResource`). Vanilla shape:

```csharp
if (collided) condition = Collide;                                        // generic, 3 sites
else if (desc.veinMiner && Physics.CheckBox(cd.pos, cd.ext, cd.q, 2048, Ignore))
    condition = Collide;                                                  // miner-specific
```

That `Physics.CheckBox` appears **exactly once** in each of `BuildTool_Click` / `BuildTool_BlueprintPaste`'s `CheckBuildConditions` and always inside the `veinMiner` branch, so swapping the call disables only the miner rule — but log evidence shows the **generic** collision is what actually blocks two overlapping miners, so the postfix clearing `Collide` is what does the real work.

**Placement has a second, silent gate.** `CreatePrebuilds` opens with `if (bp.condition != Ok) continue; if (bp.coverObjId != 0) continue;` — and `coverObjId` is written by the *same* collision loop that sets `Collide`. When a preview sits on a "coverable" building the game treats it as an in-place rebuild: `condition` comes back Ok, yet the click builds nothing and shows no message. Clearing `coverObjId` / `willRemoveCover` / `willReconstructCover` is therefore required alongside the condition, and `VeinData` natively carries `minerCount` + `minerId0..3`, so up to four miners per vein is a shape the game already supports.

The postfix also recomputes the bool result — whose "not a failure" exemption differs per tool (`NeedConn` for click, `NotEnoughItem` for blueprint paste, none for path). There are five `CheckBuildConditions` implementations; `Addon` and `Inserter` return without a local, so their exemption can't be read the same way.

**Miners on oil seeps.** The *only* thing stopping an advanced miner from mining 原油涌泉 is `if (veinPool[id].type == EVeinType.Oil) continue;` in the two vein-gathering loops of each build tool's `CheckBuildConditions` — everything downstream is type-agnostic: `InitVeinArray` copies the prebuild's vein ids unfiltered, `CreateEntityLogicComponents` sets `station.collectionIds[0] = veinPool[miner.veins[0]].productId` (so the station slot and `UpdateVeinCollection`'s gate line up on their own), and the Vein branch of `InternalUpdate` just cycles `veins[]` reading `productId`. The transpiler swaps the comparison constant for a call returning `-1`, matching only the two `beq` sites — the third `EVeinType.Oil` comparison is a `bne.un` and is the oil extractor's "must be oil" check. The two `beq` sites are instruction-identical (one in the `isVeinCollector` block, one in `veinMiner`), so plain miners get oil too.

**Build-error text does not match its key.** `BuildPreview.GetConditionText` returns Chinese *keys* (`MK2MinerTooClose` → "距离大型采矿机太近") that `LDB.strings` then maps to display text with different wording — "无法与其他大型采矿站建造在同一个位置" is `Collide`. Searching the assembly for a quoted message finds nothing, and the string table is a serialized blob inside `resources.assets` that plain grep can't read either. **Log the real `EBuildCondition` instead of reasoning from the message.**

**`[HarmonyPatch(typeof(T), nameof(T.M))]` on an *overloaded* method throws at `PatchAll` time, and that takes the whole mod down — not just the patch.** `AccessTools.DeclaredMethod` ends in `Type.GetMethod(name, flags)`, which raises `AmbiguousMatchException`; it surfaces as a stack trace through `Harmony.PatchAll` / `ProjectEdenPlugin.Awake` with no hint of which attribute is at fault. `PlanetFactory.InsertInto` has two overloads (`Int32 entityId` and `UInt32 ioTargetTypedId`), **both carrying the matrix branch**, so both need patching anyway.

**And do not fix it by naming the parameter types either** — this repo’s preloader widens that very chain’s `byte itemCount / itemInc / out byte remainInc` to `Int16`, so a signature written with `typeof(byte)` resolves to nothing once the widening is active, and *that* failure is silent. Use a `TargetMethods()` that selects by **name** and yields every overload: correct under both widths, and it patches both overloads without listing either signature.

**A prefix/postfix parameter name the target does not declare is the THIRD whole-mod-down trap, and its blast radius is partial, which makes it worse.** Harmony injects by **name**, so `(EBuildCondition type, BuildPreview preview)` against the game's `AddErrorMessage(EBuildCondition _bdCondition, BuildPreview _bp)` throws `System.Exception: Parameter "type" not found` out of `HarmonyManipulator.EmitCallParameter`, rethrown as `HarmonyException` through `PatchAll`. Unlike the other two, **classes patched before it survive and every class after it is skipped** — so the symptom is not "my patch did nothing", it is **some unrelated feature silently missing**. Shipped here as: a blueprint probe with wrong parameter names stopped `OreVeinColorPatches` and `VeinProtoArrayPatches` from ever being applied, and the visible result was a *vanilla* `IndexOutOfRangeException` in `PlanetModelingManager.LoadingPlanetFactoryMain` coming back.

**The tell is in the stack trace, and it is worth reading before doing any offset arithmetic: there was no `(wrapper dynamic-method)` frame.** The frame was the plain method in `Assembly-CSharp`'s own module (`in <mvid>:IL_035D`), which means *that method was never patched* and the offset needs **no** reconstruction. Two rounds were spent replaying Harmony's short-branch expansion against a body that Harmony had never touched. **Check for the wrapper frame first; it decides whether the offset is ours to translate.**

`tools/verify_harmony.ps1` now checks this offline (check 3), comparing every non-`__` parameter of every prefix/postfix against the target's declared names.

**`CodeMatch(OpCodes.Call, null)` matches every call.** If `AccessTools.Method` fails overload resolution it returns null, and the matcher then silently rewrites the first arbitrary call in the method. Null-check the `MethodInfo` before building the matcher.

**The same null has a second way in, and it fails much later than it is made: emitting it as an *operand*.** `code[i].opcode = OpCodes.Call; code[i].operand = AccessTools.Method(...)` with a null result produces a `call null`, which survives the transpiler, survives the build, and throws `ArgumentNullException: Invalid argument for call NULL` from `ILManipulator.WriteTo` — a stack trace pointing at Harmony’s writer, nowhere near the line that was wrong. (It happened here by splitting a patch class: the helper moved to the new class while the `typeof(...)` still named the old one.) **Resolve the `MethodInfo` once, before the loop, and bail out loudly when it is null** — not transpiling is always better than emitting a null operand.

**Statistics panels derive the miner's item from the vein, not the miner.** Both `UIReferenceSpeedTip.AddEntryDataWithFactory` (参考速率) and `ProductionExtraInfoCalculator.CalculateFactory` (理论产能) gate on `veinPool[miner.veins[…]].productId == queriedItem`, so once the ore→ingot remap is on, the miner vanishes from those panels — it produces 铜块 but is filed under 铜矿. `MinerProductStat_Transpiler` appends a `MapMinerProduct` call after each `ldfld VeinData::productId`; the miner's local is found by walking back to `ldfld MinerComponent::veins` and taking the instruction before it, which holds for both methods. The `PlanetFactory` argument sits at a different index in each, so it's located by scanning `original.GetParameters()` rather than hardcoded.

`SyncStationStorage` rewrites **`station.collectionIds[]`** as well as `storage[].itemId` — `UpdateVeinCollection` gates on `miner.productId != station.collectionIds[0]`, and getting only the latter produced "产物堆积" at 2 items.

**The three drone kinds live in two components, and one entity may hold both.** 行星内物流运输机 and 星际物流运输船 are `StationComponent.idleDroneCount` / `idleShipCount` (flags `isStation` / `isStellarStation`); 配送运输机 is `DispenserComponent.idleCourierCount` (`isDispenser`). `PlanetFactory.CreateEntityLogicComponents` tests those flags in **independent sequential ifs**, and `PlanetTransport.GameTick` walks `stationPool` and `dispenserPool` separately — both are pool-driven, not entity-driven — so a single entity carrying both ids is ticked by both with no extra work. `HubCourierPatches` merges all three onto 综合物流枢纽. **Vanilla assumes one dispenser = one storage box = one item everywhere**, and every one of those assumptions had to be worked around. In order of discovery, because each only became visible after the previous was fixed:

(1) **`DispenserComponent.storage` is a `StorageComponent` while `StationComponent.storage` is `StationStore[]`** — different types, so the courier cannot see the station's slots. The hub carries a hidden buffer box (`isStorage`, `storageCol × storageRow`) that the couriers actually use, and a shuttle on `PlanetTransport.GameTick` keeps it aligned with the 30 slots (drain first, then top up — the other order re-collects what was just pushed). **`ConnectToDispenser` only links a *neighbouring* entity's storage** (`ReadObjectConn`), so a same-entity box must be linked manually in a `CreateEntityLogicComponents` postfix. Patching `PickFromStoragePrecalc`/`InsertIntoStoragePrecalc` instead is a trap: those only compute "how much" and leave a bookmark, while the real transfers are scattered through an 8.9 KB `InternalTick` that follows those bookmarks.

(2) **Three windows fight over one click.** `UIGame.OnPlayerInspecteeChange` checks `storageId`, then `stationId`, then `dispenserId`, each opening with `ShutAllFunctionWindow()` — the *last* match wins. Filtering both `storageId` and `dispenserId` to 0 for hub entities (the `MegaStationWindowPatches` technique — filter the value as it is read, never write `entityPool`) leaves the station window, so the dispenser's settings have to come from config.

(3) **`PlanetFactory.EntityFastFillIn` checks `storageId` first**, well before `stationId`. Everything the player put into the hub — drones included — landed in the hidden buffer, so the station could not be given drones at all. Same for `EntityFastTakeOut`. Both are filtered for hubs; `TakeBackItemsInEntity` / `ClearItemsInEntity` are deliberately **not**, or dismantling would eat the buffer's contents.

(4) **`DispenserComponent.Init` sets `courierAutoReplenish = false`** (drones and vessels default to true, which is why those filled themselves and couriers did not), and the courier slot lives on the dispenser window that the hub cannot open. Both fixed: auto-replenish is turned on at build time, and `HubCourierSlotPatches` clones the warper box into a courier slot on the station panel. `UIButton.onClick` is a C# `Action<int>` event, so a cloned GameObject carries no runtime subscription — bind straight onto the clone.

(5) **The couriers only serve items configured in `Player.deliveryPackage`** (the mecha's delivery list); an empty list means every courier idles no matter how full the building is. `SyncDeliveryList` fills empty grids from the hub's non-empty slots. It must call **`Player.SetDeliveryItem`, not `DeliveryPackage.SetDeliveryItem`** — only the former follows up with `RefreshDispenserTraffic`, and without that re-pairing the item shows 无匹配的配送器 forever. Note `SetDeliveryItem` fills `stackSize` but not `stackSizeMultiplier`, so `stackSizeModified` is 0 on a fresh grid — compute the target from `stackSize` directly.

(6) **A dispenser serves exactly one item, and `filter` *is* that item.** This is the one that cost the most: `filter` is not merely a pairing predicate in `RefreshDispenserTraffic`, it is also read inside `InternalTick` as the item to work on — `if (grids[demandIndex].itemId != this.filter) continue;` and then `PickFromStoragePrecalc(this.filter, …)`. Transpiling the pairing check to accept any item therefore *looks* like it works (pairs appear, the tooltip stops saying 无匹配的配送器) and still delivers nothing. **Reading only the pairing code cannot reveal this**; the tick has to be read too. The shipped answer is to rotate `filter` through the hub's items once a second via `PlanetTransport.SetDispenserFilter` (which re-pairs for you) and let vanilla do the rest — one item at a time, but with 20 couriers it is not noticeable.

**Chains like this need a state dump, not a hypothesis.** Five stages (station slots → buffer → delivery list → pairing → courier) all present the same symptom: the couriers sit still. Several rounds were wasted reasoning from startup logs, which say nothing because the player has not configured the building yet, and from one-shot warnings that were true only when they fired. `courierDebugLog` prints all five stages on one line every 10 s; turn it on before forming a theory.

(7) **Changing `filter` turns every in-flight courier around, empty.** `SetDispenserFilter` @0035
calls `RefreshDispenserTraffic` → `OnRematchPairs`, which holds **three** calls to
`CourierTurnbackFromPlayer`; that method's last four instructions are `endId = 0`,
`direction = -1`, `t = maxt` — fly home, carrying nothing. So (6)'s rotation, at one second per
step, aborts every trip before it arrives. **With one item in the hub this is unreachable**
(`chosen == current`, so it never rotates), which is why it stayed hidden until a player put a
second item in a slot; the symptom was 「飞机飞出来又跑回去了」. Rotation now only happens while
`workCourierCount == 0`. Do not "fix" it by forcing a rotation on a timer — that trades an
invisible delay for a visible fault.

**The hub's buffer is a per-tick transit tray, not a store, and the reason is a type wall.**
`DispenserComponent.storage` is a `StorageComponent` while a station's slots are `StationStore[]`,
and the dispenser **never calls a single `StorageComponent` method** — across all 3050 instructions
of `InternalTick` it reads `grids[]` fields directly, bookmarked by `pickStorageSearchStart` /
`insertStorageSearchStart`. So the source cannot be swapped; what can be done is to make the tray
hold nothing between ticks (load the served item before the dispenser's tick, drain everything back
after it). Keeping 1000 of each item there instead is what produced 「小飞机搬了货、物流站槽位没动」.

Two facts make that design safe, both measured:

- **Goods travel with the courier.** `CourierData` carries `itemId` / `itemCount` / `inc`, so a
  pick happens at **dispatch**, not on arrival — which is why the tray must be loaded *before*
  `InternalTick`, and why a filter change cannot strand goods mid-flight.
- **`ordered` is on `DeliveryPackage/GRID`, not on `StorageComponent/GRID`.** Those are two
  different nested types; the storage one has no such field at all, and the only storage-grid
  access in `InternalTick` is two `ldfld itemId` at @2281/@22B6. So emptying the tray every tick
  cannot destroy an in-flight reservation.

**`DispenserComponent.Init` sets `playerMode = 2`, not 0.** This file and two code comments claimed
the opposite ("Init 不给 playerMode 赋值，默认 0，配送运输机会一动不动"). `Init` @0046 is
`ldc.i4.2 ; stfld playerMode`. What *is* false by default is `courierAutoReplenish` (@006A) — that
is the field that leaves a hub with zero couriers forever.

**The hub does not write the player's delivery list, and that is an owner decision.** Auto-filling
it from non-empty slots made the two settings fight: a slot set to **local Demand** would have the
hub pull the item in from the network and then send couriers to hand it to the mecha. Since a
dispenser only works on items that are on that list, the cost is stated plainly — with an empty
list not one courier moves — and a log line says so rather than leaving it to look like a defect.
`autoDeliveryList` restores the old behaviour.

#### Alien veins and drill bits — `AlienVeinPatches` + `DrillBitRegistry`

One vein type (莫桑石, type 23) consumes a **drill bit** per N ore mined. Only that type, so
existing saves are untouched and "no bit → the miner simply stops" is an acceptable failure mode.
Three findings are worth keeping; the first two are about the same station slot.

**A plain 采矿机 is refused at build time, and that rule is a consequence rather than a choice.**
The bit slot is a *station* slot, so a miner with no `StationComponent` structurally cannot have
one — `AlienVeinPatches.Tick` already `Block`ed such a miner, which meant it built, powered up and
produced nothing with no visible cause. `AlienVeinMinerGatePatches` moves that refusal to
`CheckBuildConditions` (postfix on the same three tools `MinerBuildRulePatches` uses), reading the
vein ids vanilla already wrote into `BuildPreview.parameters` / `paramCount` rather than re-running
the geometry. It discriminates on **`PrefabDesc.isVeinCollector`, not a proto id** — that flag *is*
"has a station slot", i.e. the same fact as "can hold drill bits"; a proto id would only be
accidentally right. The condition it sets is `NeedResource`, which has a vanilla precedent: a plain
miner next to an oil seep gets exactly that, because oil is filtered out of its vein list.
**It sets `__result = false` itself** rather than relying on `MinerBuildRulePatches`' postfix, which
recomputes the return value only `if (cleared)` and whose relative order is not guaranteed.

**An array slot that exists is not a slot that works.** Raising the miner's
`prefabDesc.stationMaxItemKinds` from 1 to 2 does make `StationComponent.Init` allocate
`storage[2]` — and that is all it does. Both fill loops run only to `collectionIds.Length`
(a vein collector has one ore), so **slot 1 is never written and keeps its default `max` of 0**:
zero capacity, nothing can go in it, and the station window shows a dead cell. The measured log
line is the whole story, and it reads as success until you look at the last number:

```
storage 长度 2，collectionIds 长度 1，isVeinCollector=True
  储物格 0：铁矿(1001) 本地 Supply 远程 None 数量 546666/10005000
  储物格 1：（空） 本地 None 远程 None 数量 0/0
```

`EnsureBitSlot` therefore fills the slot in itself — capacity, item id, `localLogic = Demand` —
from the tick path, the standard trap-1 runtime fix-up. **It runs only for miners standing on the
alien vein**, so a miner on any other vein correctly has no bit slot; the first-miner survey prints
which vein it is standing on, because "why has this machine no bit slot" is almost always answered
there and cannot be read off a screenshot. **It must call
`RefreshStationTraffic()` when it changes something**, or the demand table never learns the slot
wants anything; and only when it changes something, since that walks every station on the planet.

**There were two independent gates with the identical symptom, and fixing the first changed
nothing visible.** After `EnsureBitSlot` was in and the data verified correct, the window still
showed one row — because **our own** `StationExpandPatches.StorageCount` returns
`collectionIds.Length` for a collector, and a miner collects one ore. The row was discarded a
layer above the slot. That line was right when written (it is the 气体采集器 lesson: do not draw a
collector's uninitialised spare slots, or the 30-slot paging UI lands on it) and stopped being
right the moment our miner deliberately owned a slot past `collectionIds`. The test is now
**"is this slot usable"** — scan back from the end for `max > 0` — which is provably identical for
vanilla collectors, since `TargetProtoIds` is `{2103, 2104}` plus mega buildings and `kind:
station` machines, so nothing ever writes `max` on a miner's or gas collector's spare slots.

**The general shape: when a fix that the data says is correct produces no visible change, look for
a second gate rather than re-checking the first.** Both gates here answer "how many slots does this
station have", one from `StationComponent.Init` and one from our own window patch, and neither
errors when it disagrees with the other. It is also worth noting the second gate was *ours* — a
rule this repo added for one building silently constrained another one added later.

**Consumption must be derived from vanilla’s own production expression, term for term.** The bit
cost was computed as `miningSpeed * perTick / period`, where `perTick` is `speed × veinCount`.
Vanilla’s actual per-tick production (IL 0032–0056) is
`time += (int)(power × speedDamper × speed × miningSpeed × veinCount)`, and it does not run at all
when `power < 0.1f` (IL 0000) or when the buffer is full (`productCount >= GetCapacity(...)`, the
transpiled 50-gates). Three terms were missing, each silently over-charging: a full buffer burned
bits while mining nothing (**what the player reported**), an unpowered miner did the same, and a
browned-out one paid double. None of it errors — a hook that runs *before* vanilla decides whether
to act must reproduce that decision, not the nominal rate. Both gates copy vanilla’s own threshold
rather than inventing one, so the two cannot diverge at the boundary.

**And "the buffer is full" is the wrong gate once you have raised the buffer.** The first fix used
`productCount >= GetCapacity(...)` — vanilla’s own back-pressure, and correct there, because
vanilla’s buffer is 50 and fills instantly. This mod sets it to **10,000,000**, which at 240k/min
takes ~42 minutes to fill, so that gate essentially never fires and the player still saw
「矿满了，钻头照烧」. What actually decides whether the ore has anywhere to go is
`StationComponent.UpdateVeinCollection`’s own first line (IL 001D–0035):
`if (storage[0].localSupplyCount >= storage[0].max) return;` — a full station slot accepts nothing
and the ore just piles into the miner. **Raising a vanilla limit silently disables every piece of
logic that used it as a signal**, and the replacement signal has to come from whatever still
reflects reality. It stops mining (`miningSpeed = 0`) rather than only stopping the charge: charging
nothing while still producing would turn a display complaint into an ore exploit.

The power check sits deliberately **after** `EnsureBitSlot`: putting it first means an unpowered
new miner never gets its Demand slot laid out, and "no power" then looks exactly like "needs no
drill bit" in the panel.

**A local Demand slot asks the network for `max`, so capacity is a starvation lever, not a
comfort setting.** The obvious implementation copies `storage[0].max` — which
`StationCapacityPatches` has already raised to **10,005,000**. That would have the first miner
built demand ten million drill bits and starve every later one, presenting as
「我别的采矿机全停了」— a symptom pointing nowhere near the cause. `bitSlotCapacity`
is its own config value (default 3000 ≈ 12 min at full speed), clamped against slot 0 as a floor-guard.

**"Extremely rare" and "statistically absent" look identical in the config, and this one shipped.**
`placement.chance` is the per-qualifying-planet probability, but *how many planets qualify* depends
on the theme table and this galaxy's roll — both of which live in `resources.assets` and a random
seed, so neither is readable offline. Moissanite was given `chance: 0.02` on three themes, argued
as "the thinnest floor in the table". Measured against its siblings (钨 0.09, 钒 0.06) that is an
expectation **below one planet per galaxy**: a player started a fresh save, swept everything
reachable, and reported 没找到 — while registration, theme matching and the vein table were all
correct and the log carried no warning at all. **Every step succeeded and the feature was absent**,
which is this repo's worst failure shape.

Two things came out of it. `RareVeinSurvey` now prints, once per session, how many planets carry
each rare ore's themes, the resulting expected count, and **what `chance` would be needed for a
target of 2** — so tuning does not cost a round trip of "change a number, restart, fly around,
still nothing". It reads the end state (scanning `ThemeProto.RareVeins` back out) rather than what
`ExtendRareSlots` thinks it wrote, so a write that silently failed still counts as zero.

And the rule the number now follows: **rarity is `chance` × candidate planets, and only the first
half is in the config.** Measured on one cluster: 钴 25 planets × 0.25 = 6.3, 钒 37 × 0.06 = 2.2,
钨 33 × 0.09 = 3.0, 莫桑石 **18** × 0.06 = 1.1. Moissanite’s three themes carry barely half the
candidate planets the others do, so the same probability buys half the ore — which is why matching
vanadium’s `chance` did not match its findability. It is now **0.12**, anchored on vanilla’s own
rarest rare vein (光栅石 0.1 on 熔岩/戈壁/冰原冻土; 金伯利 0.18), for ~2.2 expected. Its rarity is
carried by *where* it can be rather than by a small number: 18 planets in the cluster and none in the
birth system. The original reasoning ("tungsten's absence kills the whole cemented-carbide line, this
one is only a late-game nicety, so its floor can be thinner") was **wrong on its own terms**: when
moissanite is absent the drill bit, its four forging recipes, `ERecipeType` 12 and the miner's bit
slot all have nothing to act on. *The consequence of absence being mild* is not the same claim as
*near-absence being acceptable* — and here the first was false anyway.

**Finding where a rare vein actually is: borrow the game’s scan, never re-roll its RNG.**
Expectation ("about 3 planets this cluster") does not answer the player’s question ("*which* 3"), and
an unvisited planet has no vein data to read. Re-deriving the roll looked cheap — `GenerateVeins` uses
`DotNet35Random(planet.seed)` and the rare draws sit at IL 048C / 04B8 — but between the constructor and
those draws lies a **data-dependent** number of draws (the up-to-12, break-on-failure loop at IL
03A4–03CD). A simulation that slips by one draw does not throw; **it confidently names the wrong
planet**, which is worse than reporting nothing. `RareVeinProspector` therefore calls
`PlanetModelingManager.RequestScanPlanet` — the star map’s own path — and reads `veinGroups` back. The
scan thread works on `GetUnloadedCopy`, copies the result home with `CopyScannedDataFrom` and then
`ReleaseCopy`, so peak memory is one planet regardless of how many are queued, and the answer is
identical to what the player would see on arrival. **Prefer the engine’s own implementation over a
reimplementation whenever the reimplementation can be silently out of step.**

**The forge got its own `ERecipeType` (12), and what made that cheap is worth recording.**
The bit should be forged, not assembled, but 锤锻精工厂 was `recipeType 4` — the *same*
Assemble type as 天工装配厂, with an identical description, speed and port count. By this repo's
own dominance rule that is two copies of one building, i.e. dead content. Since
`UIRecipePicker` filters on a single `recipe.Type`, **"only in the forge" is expressible only as a
new type**; re-pointing it to 12 costs it the vanilla Assemble recipes, and that cost is zero
here because its twin still takes every one of them. A duplicate building became a real one.
(Existing forges running an Assemble recipe keep running it — `SetRecipe` does not validate the
type — they just cannot switch back to one.) **Do not generalise the trade**: it was paid for by
the duplication, and would not be worth it for a building that was actually distinct.

#### Pumping lava — `LavaPumpPatches`

A vanilla 抽水站 on a lava planet produces 岩浆 (item **6640**, a fluid). No new building, no
new tech, no preloader — but the reason it did not already work is worth keeping, because it
is a shape that recurs.

**`PlanetData.waterItemId` is not an item id. It is a tagged union**, and the whole table is
readable off `UIPlanetDetail.OnPlanetDataSet` IL 0684–06B5:

| Value | Meaning |
|---|---|
| `> 0` | a real item id (水 1000, 硫酸 1116) |
| `0` | no ocean |
| **`-1`** | **lava** |
| `-2` | ice |
| more negative | unknown |

`-1 = lava` has two independent confirmations, neither of them a guess:
`PlanetModelingManager.ModelingPlanetMain` IL 0778–07B9 picks the ocean mesh with
`oceanSpheres[-waterItemId]` — the negative value *is* a render index — and both
`PowerSystem.CalculateGeothermalStrength` and `SetGeothermalAffectStrength` open with
`waterItemId == -1` as their **first instruction**, geothermal plants being exactly what you
build on lava.

**So the field itself must never be rewritten.** Swapping a lava planet's `-1` for the magma
item id breaks two things at once: the ocean sphere index goes out of range or picks the wrong
mesh, and those two `== -1` tests silently kill **every geothermal power station**. This is
trap 2 ("the same constant, two meanings") at field level; the fix rewrites **the value as it
is read**, never the source.

**Two independent gates, both silent**, and that independence is the part that matters:

| Gate | Where | Vanilla shape |
|---|---|---|
| Output | `MinerComponent.InternalUpdate`, `type == Water` branch (IL 0690) | `productId = planet.waterItemId; if (productId > 0) produce; else productId = 0;` |
| Building | `CheckBuildConditions` IL 2896–28F8 | `desc.waterTypes` whitelist; miss → `desc.geothermal ? NeedGeothermalResource(25) : NeedWater(24)` |

`cheats.json`'s 平地抽水 clears **only the second**. So with that switch on, a pump could
already be placed on lava and would spin, draw power and yield nothing — which reads as a
broken cheat and is really the first gate. **When a feature has two gates with one symptom,
clearing one changes nothing visible** — the same shape as the drill-bit slot's two gates.

The fix is correspondingly two-sided: append `-1` to `prefabDesc.waterTypes` for every
`minerType == EMinerType.Water` prefab (a **fresh array**, never an in-place append — whether
`ReadPrefab` gives each prefab its own array is not readable offline), and insert a
`MapWaterProduct` call after each `ldfld PlanetData::waterItemId` in a **listed** set of
methods.

**The method list is enumerated, not pattern-matched, and that is the whole design.** There
are 37 reads of `waterItemId` in the assembly and they are instruction-identical; only
semantics separate the two groups. Eleven are mapped (production, the build-time sign icon,
and every display/statistics path — `UIMinerWindow`, both vein-collector panels,
`EntityBriefInfo`, `AstroResourceStatPlan.AddPlanetResources` ×3,
`ProductionExtraInfoCalculator`, `UIReferenceSpeedTip`, `UIPlanetDetail`, `UIStarDetail`).
The rest **must read the raw −1**: `PowerSystem` ×2, `ModelingPlanetMain`, every `BuildTool`
(the whitelist is handled by the array edit instead), `FlattenTerrainReform`, `PlayerAudio`,
`ACH_MechaInTheWarter` and the `PlanetAlgorithm` family. Per-method expected counts live in
`Expected` and loud-fail on a mismatch.

Only the production site is load-bearing; the other ten are the ore→ingot lesson again — get
them wrong and the pump has no product icon and the item is missing from 参考速率 / 理论产能,
which reads as a broken item rather than a missing patch.

Two things that were already handled by existing infrastructure and did not need code:
`UnlockKey = -1` (set by `OreRegistry` for every extra item) is what keeps a recipe-less item
visible in the item picker — `GameHistoryData.ItemUnlocked` returns false at
`maincraft == null` otherwise, and magma has no recipe at all; and `isFluid` + the existing
`RefreshFluidList` is the entire tank story.

**No heat value, deliberately.** Molten silicate is already fully oxidised, so by this repo's
own `fuelType` rule it burns for nothing — like CO₂. What it carries is sensible heat.

#### What consumes it — 熔岩冷却厂 (item 6506, `ERecipeType` 13)

The seventh mega building, and the only consumer of magma. Three recipes (6645–6647) that are
**three real cumulate horizons of a layered mafic intrusion**, not three power tiers: chromite
settles first at ~1300 °C (Bushveld LG6/UG2), vanadium stays incompatible until Fe–Ti oxides
take it up at ~1050 °C (the Main Magnetite Layer), and cobalt is chalcophile so it needs an
immiscible sulfide droplet — which magma alone cannot supply, so that recipe additionally
consumes 石膏矿 and 煤矿 (`CaSO₄ + 4C → CaS + 4CO`; Noril’sk got its sulfur from assimilated
anhydrite exactly this way). **This is the same geology that already put 钴/铬/钒 veins on the
熔岩 and 火山灰 themes** — it was not invented for the building.

**The input ratios are derived, the absolute scale is the balance knob.** A basaltic melt carries
Cr ≈ 300 ppm, V ≈ 280 ppm, Co ≈ 45 ppm → magma per unit of ore stands as 1 : 1.07 : 6.7, which is
the shipped 6000 : 6400 : 40000 — that ratio scaled up 100×, the scale being the balance knob and the ratio the physics. **Sulfur and carbon scale with the magma, the stone byproduct does not**: sulfide saturation needs sulfur proportional to *melt volume*, so pinning 石膏矿/煤矿 while raising magma would break that recipe’s stoichiometry, whereas 石矿 is already understated on purpose for reasons unrelated to input scale. Yields are **one ore per cycle** on purpose: rare-vein prospecting is
what this line is worth, and a cheap synthetic route would kill it. The precedent is
`钒块 · 残渣提取` (an explicit "bad seed" fallback) versus tungsten, which deliberately has none.

**The 石矿 byproduct is deliberately understated by orders of magnitude, and the second reason is
mechanical, not cosmetic.** The real silicate fraction is >99.9%; shipping that would make this the
dominant stone source in the game *and* — more importantly — a byproduct nobody drains backs up its
output slot, which stalls a multi-product assembler outright. Say the deviation in the `//`, do not
pretend the equation balances.

**Why it is a feedstock and not a fuel.** DSP’s component model can express "burn X for power"
(`PowerGeneratorComponent` + fuel) and "turn X into Y" (`AssemblerComponent`) — so magma had to be
one or the other. (**This paragraph used to add "but not both at once", and that half was wrong**:
one *component* cannot, but one *entity* carrying two components can, which is what the
氧化还原燃烧厂 does. The retraction is recorded in full in that building's own section. It does not
change the verdict for magma, which rests on the three counts below.) Fuel loses on three
counts: 地热发电站 already occupies "power from a lava planet" (both `PowerSystem` methods gate on
`waterItemId == -1`, i.e. geothermal *only* works there); the honest energy density is ~1.9 MJ/kg
against coal’s ~27, which by this repo’s own coal anchor puts a magma item near 0.19 MJ — an order
of magnitude below 氨, already the worst in `combustibles.json`; and its Carnot η at 1200 °C is
0.558, fourth of seven. **The heat is therefore still unused as energy**, and that is a stated gap.

### Logistics — `src/Patches/Station/`

- `StationCapacityPatches` — slot capacity/count and max charging power on prefabDesc + a **one-time bootstrap** for existing stations. **Neither value may be forced per tick**: `storage[].max` is what the station panel's per-slot capacity box writes, and `workEnergyPerTick` is what its charge slider writes, so pushing either back every tick makes the player's edit snap back the instant they let go. The same field-level mistake was made twice here — the charge slider first, then `max` — so the rule is now stated once for both: **the configured number is the default, not a lock.** New stations get it from `prefabDesc`; existing ones are raised once, only if still sitting at the vanilla value recorded before `prefabDesc` was edited, and only once per station (tracked in a `ConcurrentDictionary` because that tick is parallel across planets, and cleared on `IntoOtherSave` because station ids are reused). Also hosts `StationsConfig`. Charging power is a three-hop chain: `PrefabDesc.workEnergyPerTick` → `PowerConsumerComponent.workEnergyPerTick` (**saved**) → `SetPCState` scales it by `1.05 - energy/energyMax` into `requiredEnergy` → `StationComponent.energyPerTick`. Only the middle hop needs fixing at runtime, and that fix-up must be a **one-time bootstrap, not a per-tick force** — the station panel's 最大充能功率 slider (`UIStationWindow.OnMaxChargePowerSliderValueChange`) writes the very same field, so overwriting it every tick makes the slider snap back the instant you let go. The patch therefore only raises stations still sitting at the vanilla value it recorded before editing `prefabDesc`. That slider's range is derived from the prefab too: min `prefabDesc/2`, max `prefabDesc×5`, value `= 50000 × slider`.

  **`energyMax` sits right beside it and takes the opposite treatment, which is the point worth keeping.** `stations.json`'s `stationEnergy` (renamed from `chargePower` in 1.12.4, and now denominated in **watts and joules** rather than per-tick — the `/60` belongs in code, as `machines.json`'s `workEnergyWatt` already had it) carries both knobs per station. The capacity one is `PrefabDesc.stationMaxEnergyAcc` → `StationComponent.energyMax` (**saved**, trap 1) — but enumerating every access shows **exactly three writers in the whole assembly: `Init`, `Reset`, `Import`**, and every other site is an `ldfld`. **No UI can write it**, so the "configured number is the default, not a lock" rule does not apply and the fix-up **aligns, up or down**; raise-only would make the config value un-lowerable, the `droneCarries` mistake one door over. Charging power keeps the one-time raise, because its slider does exist. **So "which of the two rules applies" is answered by enumerating writers, not by which field it sits next to.** Two details the alignment needs: clamp `station.energy` down with it (`SetPCState` computes `1.05 - energy/energyMax`, and an out-of-range energy yields a negative demand — the station then never charges again), and the raise branch must test `current != target` as well as `current <= vanilla`, because when the two coincide it would "change" an already-correct station every pass, set `touched`, and **the planet would never settle** — bringing back the 0.5 ms/frame full scan that the `Settled` ledger exists to stop.
- `StationExpandPatches` — 30 slots. Vanilla unrolls `AddItem` and the four supply/demand queries over `storage[0..5]`, so **beyond 6 slots items vanish on delivery**; all five are replaced with full scans. Adds paging + a scrollbar to `UIStationWindow` (widgets rebound by index, layout untouched), resizes `UIEntityBriefInfo.icons` in `_OnCreate`, and grows existing stations' arrays on `GameData.Import`.

  **`UIStationStorage` widgets are SHARED across every station the player opens, and vanilla only
  ever rewrites row 0.** That combination shipped a regression: `LayoutStorageRows` (added so the
  miner's second row would not sit on top of its first — see the drill-bit section) moved rows 1..n
  to coordinates derived from the *miner's* row 0, and those coordinates then stayed on the widgets.
  Open a 大型采矿机, then open any of the three logistics stations, and `OnStationIdChange` (IL 08A4)
  rewrites `storageUIs[0]` and nothing else — so **all three station windows drew rows 1..5 in the
  miner's layout**. Reported as 「三个物流站的 ui 不兼容了」.

  This is `MultiProductUIPatches.RestoreSlot1` again, and the method's own doc comment already
  stated the rule it broke — *whatever vanilla does not rewrite is what you must restore yourself*.
  Only the first half had been implemented. The complete rule is: **if you write it, you own
  restoring it, and the moment to restore is when someone else takes the widget over.** So the guard
  is not `if (!isVeinCollector) return;` but `if (!isVeinCollector) put it back`, against home
  positions recorded the first time the widgets are seen (safe in either order: vanilla skips
  positioning entirely for collectors and writes only row 0 for stations, so rows 1..n still hold
  their prefab values at that moment).

  **The same rule bit again on the energy bar, and then the fix itself created the mirror bug.**
  `HubCourierSlotPatches` pushes the energy bar's frame (`energyBar.parent`) right to make room for
  a courier slot. That frame is a `UIStationWindow` field, i.e. **shared by all three station kinds
  and the 大型采矿机**, and an enumeration of every rect write in that class — 41 of them across
  `_OnCreate` / `OnStationIdChange` / `_OnUpdate` / `RefreshTrans` / `RefreshTabs` — finds **not one**
  that touches it. So vanilla never puts it back: open a hub once and every ordinary station keeps a
  shortened bar for the rest of the session.
  **Then the restore broke the apply**: the push was hanging off `EnsureBox`, whose first line is
  `if (_box != null) return true` — a once-per-session event — while the restore ran every time a
  non-hub panel was shown. One restore and the push never came back, so the courier slot sat on top
  of a full-width bar. **Completing the rule: when you add a restore, the write it undoes must stop
  being a one-shot — the two have to run at the same cadence.**

  **And the readout does not follow the bar, because vanilla positions it from a hardcoded width.**
  `_OnUpdate` @0303/@0351 writes `energyText.anchoredPosition.x = round(W × ratio ∓ 30)` where `W` is
  the `isStellar` branch's literal 180 / 240 / 300 — it never reads the frame's rect. Measured: this
  hub's frame really is **240** wide, so `W` *is* the original width; after a 92 px push the frame is
  148 and vanilla still puts the readout at 207, i.e. **59 px past the right end**. The correction is
  a linear rescale by `(240 − push) / 240`, which needs no knowledge of which ∓30 branch vanilla took,
  and needs no restore because vanilla rewrites that field every frame.

  **Showing a row vanilla never shows costs two more fixes, and both are "vanilla only writes what it
  needs".** Giving the 大型采矿机 a second storage row (the drill-bit slot) exposed them in order.
  (1) **Position.** `UIStationWindow.OnStationIdChange` IL 08A4 reads
  `if (!station.isVeinCollector) { storageUIs[0].anchoredPosition = new Vector2(40f, -90f); … }` — a
  vein collector draws one row, so vanilla skips positioning entirely and row 1 stays wherever the
  prefab left it, **overlapping row 0**. It reads as a rendering glitch and is really "nobody owns
  that value". Same shape as `MultiProductUIPatches`’ `RestoreSlot1`: *read the vanilla path for its
  **writes**, not its reads.* (2) **Window height.** `RefreshTrans` recomputes it every frame as
  `100 + 76 * slots + 36`, where `slots` is `collectionIds.Length` for collectors and
  `storage.Length` otherwise (IL 00B2–00D4) — the very rule `StorageCount` overrides. The old postfix
  only ever **shrank** (`if (count <= visible) return;`), which is right for a 30-slot station
  (vanilla assumes 30, we draw 5) and silently wrong for the miner, which needs to **grow** by one row
  (vanilla assumes 1, we draw 2) and took that early return. It is now a signed delta between "rows we
  draw" and "rows vanilla assumed", which reproduces the 30-slot result exactly, is 0 for gas
  collectors, and is +1 for the miner. Row height 76 is vanilla’s own constant from that formula, not
  a measurement. (3) **The block below the rows.** The same branch ends with
  `panelDownTrans.anchoredPosition = new Vector2(x, 80f|60f)` — a **constant**, while the window height
  right above it scales with `slots`. So vanilla’s vein-collector layout is hardcoded for exactly one
  row: the window grows but 物流站设置 stays put and the second row lands on top of it. Both lines run
  every frame, so the correction has to run every frame too.

  **The panel did not need moving at all, and finding that out took three wrong fixes.** The
  measured rects settle it: `panelDownTrans` has `anchorMin/Max.y = 0` and `pivot.y = 0` — it is
  pinned to the window’s **bottom** edge, while the storage rows are pinned to the top. **Growing
  the window separates them by itself.** Worse, its rect top is ~110 units above where it actually
  draws (in vanilla’s 1-row layout the panel rect spans 80–230 above the bottom while row 0 ends at
  120, and it looks fine), so "measure the rect overlap" over-counts by that padding every time —
  which is exactly how attempt three pushed the whole block out through the bottom of the window.
  With the window grown by one row the panel’s *visible* top lands at 120 against a row bottom of
  121.6: already flush. The shipped fix is therefore to grow the window by `RowHeight * delta + 16`
  and touch nothing else; the 16 is read off those measurements, not tuned by eye.

  **The process lesson is the expensive one.** Three attempts, three different wrong models, all
  estimated from screenshots — which cannot distinguish a rect edge from a drawn edge and cannot
  show canvas scale, the two things that were actually wrong. This file already says not to
  diagnose layout from screenshots; **the one-shot rect dump should have been the second step, not
  the fifth.** When a UI fix fails twice, stop adjusting the formula and print the rects.
- `PilerLevelPatches` — rewrites the stacking techs' `UnlockValues` (trap 3) and replaces the hardcoded 4s in `PilerComponent` and `MinerComponent`.

  **`PilerComponent.InternalUpdate` has *four* hardcoded 4s and they must all move together** (verified by enumeration: 3 × `ldc.i4.4` + 1 × `ldc.r4 4f`, and no other `4.0` float in the method):

  ```
  @0312  if (stack1 + stack2 <= 4)      // does it make a whole stack
  float  b = inc / stack * 4f + 0.5f    // proliferator points that stack should carry
  @0359  AddCargo(item, 4, b)           // the stack size actually emitted
  @0371  cacheCargoStack1 = total - 4   // the amount deducted from the cache
  ```

  The first version replaced only the first two. At level 5000 the points were computed for a 5000-stack **and deducted** from the cache, while the stack itself emitted 4 and deducted 4 — points drained 1250× faster than items, `cacheCargoInc1` ran negative, and the player saw **"the piler eats items and shows negative numbers"**. **At the vanilla value the bug cannot exist** (4 == 4 everywhere), so it only appears once the level is retuned — which is exactly what this patch is for.

  Two process failures made it survive: the doc comment asserted "two places" without counting, and **the log line reported no count** — the repo's own rule is to log the replacement count so a wrong match is loud, and "2 of 4" would have been obvious. It now scans the whole method, replaces every one, reports both counts, and loud-fails if they are not 3 and 1.

  **This is the general shape to watch for when raising a hardcoded game constant: the same number is often used as a limit, an emitted amount, *and* a bookkeeping deduction. Partial replacement does not error — it silently creates or destroys items.** The miner's stack formula `(36000000/period*miningSpeed)/1800+1` ignores `speed` entirely, so raising the *cap* alone did nothing for water (2 < 50) — `RaiseStack` is injected after the computation and before the clamp.
- `LogisticsGlobalPatches` — drone/vessel carry capacity and stack levels, forced at the actual read sites rather than only on `GameHistoryData`. **"Only raise" silently makes a config value un-lowerable.** These fields live in `GameHistoryData` and are therefore *saved*, so a `Raise`-style fix-up sees the already-baked higher value and skips — lowering `droneCarries` from 100000 to 10000 did nothing on an existing save. The carry pair uses `Align` (writes whenever it differs, up or down). **The stack/piler values used to stay raise-only "because there is no reason to lower them" — that is no longer true and it caused a live bug.** `InserterAbsoluteMax` is now *derived* from the belt stack level, so it moves down as well as up: a save written with 5000 kept `history.inserterStackInput` at 5000, `Raise` saw `5000 >= 1` and skipped, and `OnInserterTechChange` then pushed **5000** into every sorter while the log cheerfully reported "capped at 1 stack". The symptom was unchanged item loss with a correct-looking log. Both now align.

  **1.12.8 added the courier and both base speeds, and the *speed* half is a different shape from
  everything else in this class — it must be written as an absolute, never as a multiply.**
  `logisticDroneSpeed` / `logisticCourierSpeed` are saved (`Export` @0345 / @03A5), so applying a
  ×10 to the current value compounds across sessions: 100× on the second load, 1000× on the third,
  **and nothing logs it**. Both are therefore computed as `vanilla base × multiplier`, with the base
  read live from `Configs.freeMode` — the same source `GameHistoryData.SetForNewGame` @01F6 uses —
  rather than hardcoding the numbers, which would go silently stale on a game update. That makes the
  write idempotent at any cadence.

  **And reading the base out of `ModeConfig..ctor` is not the same as reading the mode**, which the
  first draft of the docs got wrong. The ctor writes `logisticCourierSpeed = 10` (@01A1), and the
  live `Configs.freeMode` value is **6** — the ctor literals are *defaults* that the mode overrides.
  The drone happens to agree (8 in both), which is exactly what makes the mistake easy to keep. The
  code was right because it reads `Configs.freeMode` live; only the quoted numbers were wrong.
  **A constructor literal is the default, not the value** — read the accessor the game itself reads.

  Measured on the owner's save: carry went `运输机 200 → 10000`, `配送机 20 → 5000`, and base speeds
  `8 → 80` / `6 → 60`. That `20` is the number the owner had reported, which is what identified the
  courier as the right field in the first place.

  **Which layer to scale was decided by enumerating writers, and it is the reason this is safe.**
  Final speed is `base × scale` (`get_logisticDroneSpeedModified`). `UnlockTechFunction` writes
  **only** the scale (@0345 drone, @0512 courier); the base has exactly two writers, `SetForNewGame`
  and `Import`. So moving the base cannot collide with research — at the stated cost that every
  later speed tech now multiplies a bigger number. The same enumeration settled the carry side:
  `logisticCourierCarries` **is** tech-accumulated (@0525, read-add-write), so it takes `Align` like
  its two siblings, and `CourierData.itemCount` is **Int32** — checked, because this repo has been
  bitten four times by pushing a large value into a byte-wide field.

  **And "drone" is two different aircraft**, which cost a round: `droneCarries` is the planetary
  logistics drone that moves goods between stations, `courierCarries` is the courier that delivers
  to the mecha. A report of "carry is 20" against a configured 10000 is the tell that they are not
  the same thing — **when a reported number cannot be produced by the config you are looking at, you
  are looking at the wrong field**, not at a broken one.

**The general rule: a value that is *computed* rather than *configured* must never be synced with raise-only logic**, because the computation can legitimately produce a smaller answer than last session's. Overriding player research is not a concern at these magnitudes — vanilla's carry techs top out in the double digits.
- `GasCollectorPatches` — orbital collector speed. Capacity is handled by `StationCapacityPatches` (collectors are auto-included via `isCollectStation`), but their **slot count must not be raised to the station value**: `StationComponent.Init`'s collector branch lays out slots by `collectionIds.Length` capped at `stationMaxItemKinds`, so 30 slots would just add empty ones and drag the 30-slot station UI onto the collector. `OreRegistry.EnsureCollectorSlots` is the one sanctioned exception: it raises the count to the exact number of gas species a theme can hold, which is what makes an added gas collectable at all. `PrefabDesc.stationCollectSpeed` feeds a formula in `PlanetTransport.NewStationComponent` that bakes `StationComponent.collectionPerTick[]` into the save, so existing collectors need the same formula re-run at runtime. Ceilings: `UpdateCollection` does `(int)currentCollections[i]` (Int32) and `collectionPerTick` is a `float` (exact integers only below 2²⁴), hence the `collectorMaxPerTick` clamp.

### Matrix lab — `src/Patches/Lab/MatrixLabPatches.cs`

Only the **production** side (making matrices) is modified. The two sides of a lab are entirely separate code paths: `InternalUpdateAssemble` uses `speed` / `speedOverride`, while `InternalUpdateResearch` reads **only** `GameHistoryData.techSpeed` and never touches `speed` — so this cannot affect research rate.

Three things worth knowing before touching labs:

- **`PrefabDesc.labResearchSpeed` is cosmetic.** Its only two references in the whole assembly are `PrefabDesc.ReadPrefab` (write) and `ItemProto.GetPropValue` (the item tooltip). No logic reads it. The research lever is `techSpeed`, unlock function **22**, accumulative — structurally identical to `stationPilerLevel`'s function 29, so `PilerLevelPatches`' tech-rewriting approach ports directly.
- **Research mode has no parallel path.** `GameLogic` has `_lab_produce_parallel` and `_lab_output_to_next_parallel` but no research equivalent. Production and output-to-next *do* bypass `FactorySystem`, which is why the speed fix-up is a prefix on `LabComponent.InternalUpdateAssemble` rather than on `GameTickLabProduceMode`.
- **Three separate storages, none capped where you'd expect.** `PlanetFactory.InsertInto` checks no limit at all for labs — inserters stop because `needs[]` goes to 0. So production input is capped by `UpdateNeedsAssemble` (vanilla: hardcoded **6** for recipes over 9s, else `3×ceil(speedOverride/10000)+3`), research matrices by `UpdateNeedsResearch` (`< 36000`), and production output by an early `return` inside `InternalUpdateAssemble` (**two** copies — the unrolled single-product path and the multi-product loop). The first two are overridden by recomputing `needs[]` in a postfix; only the output gate needs a transpiler. `LabComponent.UpdateOutputToNext`'s twelve `36000`s are a per-tick *transfer rate* between stacked labs, not a storage cap.
- **`matrixServed[]` is scaled ×3600** (`InsertInto` does `matrixServed[i] += 3600 * itemCount`), so research-mode matrix storage cannot reach 10M — Int32 caps it near 298k items, and the stack transfer can double the peak, so 250k is the safe limit. Production's `served[]` / `produced[]` are plain counts with no such problem.
- **Same one-cycle-per-tick ceiling as assemblers.** `time += (int)(power * speedOverride)` runs once per tick and is guarded by `time < timeSpend`, so 60 cycles/s is the engine limit; `speed` only needs to be large enough to fill `timeSpend` in a single tick. Units: `timeSpend = RecipeProto.TimeSpend × 10000`, `extraTimeSpend = × 100000` (TimeSpend is in ticks), and `10000` is 1× speed. The binding Int32 constraints are `extraSpeed = speed × incTableMilli × 10` (**≤ ×4**) and `speedOverride = speed × (1 + accTableMilli)` (**≤ ×3.5**). **Those two ceilings used to read ×2.5 and ×2 here, and that was wrong** — they were computed from proliferator Mk.III, which is level **4** of an eleven-entry table (`+25% / +100%`), while the table itself runs to level **10** (`incTableMilli` 0.4, `accTableMilli` 2.5). Vanilla cannot reach past 4, but **this mod can**: 活性增产剂 sets its outcomes' spray level from `proliferator.json`. Measured by `MatrixSurvey`, which is why it takes `max()` over the live table rather than quoting a constant. Production mode also self-throttles on `produced[i] + productCounts[i] > 10 × ceil(speedOverride/10000)` — the same shape as the miner's `productCount/50` gate.

**Matrix craft times are measured every launch, never recalled — `MatrixSurvey`.** Vanilla recipe `TimeSpend` lives in `resources.assets`, so before this existed the repo's only source for those six numbers was two hand-written comments. The survey walks **`LabComponent.matrixIds`** (the engine's own answer to "what is a research matrix", already extended to seven by `BioMatrixPatches` — not an id range and not a name), prints each matrix's recipe, ticks/seconds, `timeSpend` and `extraTimeSpend`, flags the ones past `UpdateNeedsAssemble` @0014's `5400000` (= 9 s) gate, and then **checks `assembleSpeed` against the measured slowest recipe** — both tracks separately, taking the spray multiplier as `max(Cargo.incTableMilli)` rather than a quoted constant, plus an Int32 overflow check (`speedOverride`/`extraSpeed` going negative means the recipe can never fill and production stops, silently). Measured values: 电磁 180 / 能量 360 / 结构 480 / 信息 600 / 引力 **1440** / 宇宙 900 ticks, 生物矩阵 180. **引力矩阵 yields ×2 per craft**, so per *unit* the slowest is actually 宇宙矩阵. Its first run is what caught the ×2.5 erratum above.

`lab.json`'s `matrixTimeSpend` (ticks; 0 keeps vanilla, ships at 60) rewrites every one of them, swept over the same `matrixIds`. It must be registered on `PostAddDataAction` **after** `BioMatrixPatches` — that is what grows `matrixIds` — and being in that action is what makes it free: LDBTool calls `RecipeProto.InitRecipeItems` afterwards, and `timeSpend`/`extraTimeSpend` are derived there (@003C / @004A), so nothing has to refresh `recipeExecuteData`. It changes a **value**, not an array length, and `LabComponent.Import` @0381–0391 re-fetches from the static dictionary, so existing labs self-heal with no runtime fix-up. **It does not change lab throughput** — at `assembleSpeed = 1e8` every matrix recipe already filled `timeSpend` in one tick, so output was and remains 60/s; what moves is hand-craft time and the panel's displayed seconds. Say that wherever the knob is documented, because "I changed the time and nothing got faster" reads exactly like a bug.

**Labs pull from the logistics network without being stations** (`LabLogisticSupplyPatches`). Giving a lab `prefabDesc.isStation` does work — `CreateEntityLogicComponents` checks `isLab` and `isStation` in independent blocks, so one entity can carry both components — but labs *stack*, so every level would grow its own `StationComponent` and drone fleet, and `prefabDesc` only affects newly built labs. Instead a postfix on `PlanetTransport.GameTick` moves items straight from station storage into the lab buffers. Three passes (collect shortfall → take from stations → distribute) rather than scanning stations per lab, because the latter is labs × stations; containers are reused statics. Remember the two buffers differ: production `served[]` is a plain count, research `matrixServed[]` is ×3600.

**It has to be bidirectional**, for the same reason the mega buildings do: supply alone leaves `produced[]` piling up to `assembleOutputStorage` and the lab stalls, so the player still needs inserters. `ShipOut` mirrors the three passes (gather `produced[]` → push into stations → deduct what was actually accepted; nothing is deducted before a station takes it). Both directions key on the slot flag the player set, symmetrically: **taking** only drains `ELogisticStorage.Supply` (draining someone's Demand slot is theft), **pushing** only fills `Demand` (the explicit "deliver this here" marker; export is then the vanilla idiom, local Demand + remote Supply). Pushing first ignored `localLogic` and matched `itemId` alone, reasoned as "it stands in for a belt, and belt insertion ignores logistics settings" — wrong, because a belt has to physically reach the station and a virtual push does not: any new station with a matrix slot filled itself instantly and began supplying the planet. Research-mode labs have no `produced[]` and are skipped. Takes also deduct `StationStore.inc` proportionally — removing count while leaving `inc` alone makes the remaining items carry the whole stack's proliferator points, i.e. **free proliferation on every transfer**, while the receiving side gets un-sprayed goods. `MegaVirtualLogisticsPatches` had this bug and no longer does: all four `count` mutations now carry a paired `inc` one, the ratio always taken against the total *before* the deduction. Its outbound third pass settles against the **debt** recorded in pass two rather than each slot's own spray rate, so what the destinations received and what the sources pay are exactly equal and the books cannot drift. (`StationStore.inc` is Int32, unlike the belt's byte-wide `Cargo.inc` — no overflow concern at station scale.)

#### A seventh matrix — `src/Patches/Lab/BioMatrixPatches.cs`, `UniverseMatrixPatches.cs`, `LabSeventhSlotPatches.cs`

生物矩阵 (item **6007**) is the seventh research matrix. It is **grown in the 生物温室, not
synthesised in a lab** — so it never appears under 矩阵合成 — and it is the Universe Matrix's
seventh ingredient. No preloader: the data layer sizes itself, and the hardcoded parts are a
closed, enumerated list.

**The item id is not a style choice.** `PlanetFactory.InsertInto` IL 03F6 computes the matrix slot
as `itemId - 6001` and discards anything outside `[0, 6)`; `FactorySystem.GameTickLabResearchMode`
uses the same subtraction. So matrix ids **must stay dense from 6001**, and the first version's
6644 (`6644 - 6001 = 643`) made it silently un-insertable. `BioMatrixPatches` now refuses to extend
the table unless `LDB.items.Select(id).Name` really is 生物矩阵 — because **LDBTool's `CustomID.cfg`
re-pins ids by display name after registration**, and for a matrix a wrong id is not a misplaced
icon, it is a table pointing at an id with no proto while the real proto computes slot 643.

**Most of the lab is data-driven; exactly three methods unroll the six slots.** Enumerating every
method in the assembly that touches `LabComponent.served / needs / incServed / matrixServed`, and
classifying each access by whether its index is a literal or a loop variable, gives a closed list —
everything else (`PlanetFactory.InsertInto` both overloads, `InternalUpdateAssemble`,
`TakeBackItems_Lab`, `ThrowItems_Lab`, `EntityFastFillIn`, `UILabWindow._OnUpdate` /
`OnItemButtonClick`, the statistics panels) is `ldlen`-bounded and needs nothing:

| Method | Shape | Who covers it |
|---|---|---|
| `UpdateNeedsResearch` | `needs[0..5]` | `BioMatrixPatches` postfix |
| `InternalUpdateResearch` | 45 literal touches | prefix caps the rate, postfix deducts |
| `UpdateOutputToNext` | 42 literal touches (**both** `matrixServed` and produce-mode `served`) | two separate postfixes — the research one does **not** cover produce mode |
| `SetFunction` | `needs = new int[6]`, `Array.Clear(needs, 0, 6)` | `UniverseMatrixPatches` postfix |
| `UpdateNeedsAssemble` | `needs[0..5]`, 12 literal touches | `UniverseMatrixPatches` postfix |

The last two are what a **seven-ingredient produce recipe** needs, and getting them wrong fails the
way the 30-slot station did: `needs[]` is the only thing inserters consult, so a seventh ingredient
with no `needs` slot is **never requested and never errors** — the lab just waits forever for
something nothing will deliver. Prefix/postfix throughout; the only transpiler is the one constant
in `InsertInto`, anchored on the unique `ldc.i4 6001 ; sub`.

**Save compatibility is vanilla's own.** `LabComponent.Import` IL 03A5–03D2 resizes `served` /
`incServed` to `recipeExecuteData.requires.Length` and copies what fits, so an old six-ingredient
lab self-heals. It does **not** do the same for `needs`, so our postfixes check that length
themselves before using it.

**Modify the vanilla recipe during `PostAddDataAction`.** `RecipeProto.InitRecipeItems` rebuilds the
whole `recipeExecuteData` dictionary (IL 0000 `newobj`, 0005 `stsfld`) and LDBTool calls it *after*
`PostAddDataAction` — so editing `Items` / `ItemCounts` there is picked up for free, with no refresh
of our own. The recipe is found by scanning for a product of 6006, never by a hardcoded id.

**The lab window's ring is measured, then re-applied per mode.** Vanilla's six cells are a regular
pentagon of radius 100 centred on cell 5 at (0, −9.5), at `90° − 72°k`. A sixth ring cell does not
fit as an insertion (36° spacing → 61.8 px between centres against 96 px widgets, i.e. overlap); as
a hexagon it does (60° → 100 px, a 4 px gap). `LabSeventhSlotPatches` therefore measures the ring
once (centre, radius, clockwise order — nothing hardcoded) and re-lays it out whenever the count
changes: **5 ring cells → pentagon, exactly reproducing vanilla; 6 → hexagon.** It deliberately does
**not** follow unlock state, which vanilla uses to hide un-researched matrices — that gap is vanilla
behaviour and not ours to "fix".

Three things that cost time here, each an existing rule re-earned:

- **`_OnInit` runs at UI construction, `_OnUpdate` only while the window is open.** A log with the
  `_OnInit` lines and none of the `_OnUpdate` ones means the window was never opened, not that the
  code is broken — two independent probes on `_OnUpdate` both being silent is what settles it.
  `_OnRegEvent` runs after `_OnInit` (IL 001A then 0036) and its wiring loop is `ldlen`-bounded, so
  extending the arrays in an `_OnInit` postfix gets the seventh button wired **by vanilla** — wiring
  it again would double-subscribe.
- **Clone only the roots.** A slot owns eight widgets that nest in each other; cloning each one
  separately yields two copies, and the one you write to is not the one drawn on top
  (`MultiProductUIPatches`' "slot 3 shows slot 2's icon with slot 3's count"). Compute which of the
  eight is contained by no other, clone those, resolve the rest by relative path out of the clones.
  And `Instantiate(obj, parent)` defaults to `worldPositionStays = true`, which rewrites
  `anchoredPosition` — use the three-argument overload with `false`.
- **Roll back rather than half-extend.** If any widget fails to resolve, destroy the clones and stay
  at six: an array of length 7 with a null `itemButtons[6]` makes vanilla's own `_OnRegEvent` throw,
  and a null `itemIcons[6]` throws every frame in `_OnUpdate`. Both are far harder to diagnose than
  "there is no seventh cell".

**The decorative ring lines are a second, independent table.** Under the ring's parent sits a
`lines` node holding two groups of five: spokes (2 × 9.6, radius 50, aligned with the cells) and
dashes (28 × 2, radius 80.7 ≈ `100·cos36°`, on the edge midpoints), both satisfying
`rotation = angle − 90`. They are re-spaced from measured originals — `phase = ((angle − 90) / step)
mod 1`, `radius' = radius · cos(step'·phase) / cos(step·phase)` — and each group gets one spare
clone, hidden at five. **Take the phase as a circular mean over the group, not from one member**:
the first spoke sits at (0, 50) whose `Atan2` returns 89.99999, so a single sample fed through
`Mathf.Repeat(x, 1)` wraps a phase of 0 to ≈1 and the radius comes out 80.9 instead of 50. The
hidden spoke group is re-spaced too — it is hidden in one mode, not in all of them.

**The 3-D animation is one array element, because of a coincidence this mod created.**
`GameTickLabResearchMode` (IL 0111–0181) folds a tech's matrices into an index —
`state |= 1 << slot` for slots 0–4, and `slot == 5` short-circuits to **32** — then writes
`techShaderStates[state] + 0.2f` into `AnimData.working_length`. The table's values are **five
digits, each naming which matrix that animation position shows**: index 31 (all five basic) is
`23514`, index 32 is `66666`. Since index 32 means "this tech needs a Universe Matrix", and the
Universe Matrix recipe now contains 生物矩阵, entry 32 is exactly the techs that consume it — so
`techShaderStates[32] = 67676` is the whole change, with no transpiler on a per-frame method. The
code verifies that premise (either the techs list it directly, or the recipe contains it) and
refuses to touch the animation otherwise.

**Whether the shader accepts digit 7 cannot be answered offline** — the vanilla table never contains
a 7 and the shader is a compiled asset, the same wall as the vein recolour. So the default pattern
keeps three `6`s (failure degrades to two wrong positions, never a blank ring), the whole thing sits
behind `lab.json`'s `bioMatrixShaderDigit` (0 restores vanilla, no rebuild), and it logs in every
state.

**Whether techs require it directly is `lab.json`'s `bioMatrixInTechs`, default false.** The
Universe Matrix recipe already contains it, so appending it to the 32 techs as well is a double
requirement; the owner cut that. The three places that depend on the answer — the research-mode
`needs` slot, whether the seventh cell is drawn in research mode, and which premise the animation
rests on — all read `UsedByTechs`, **counted back out of `LDB.techs`** rather than inferred from the
switch, so flipping the switch moves all three with no second decision written anywhere.

### Bigger planets — `src/Patches/Planet/`

`planet.json` doubles every ordinary planet's radius (200 → 400), giving **4× the buildable area**
with every building occupying the same number of cells. **On by default since 1.12.1** (owner
decision), and **it invalidates any save made before that** — see the save note below.

**Why it is cheap: vanilla already keys the build grid on the radius.** `PlanetAuxData..ctor`
@0015 is `new PlanetGrid(type, (int)(radius / 4f + 0.1f) * 4, identity)`, and `PlanetGrid.SnapTo`
derives latitude as `lat/(2π) × segment`, longitude from `cos(latitude) × segment`, then subdivides
by five. So **cell count ∝ segment² ∝ radius² while each cell keeps a constant physical size**.
Terrain does not smear either: `PlanetAlgorithm*.GenerateTerrain` samples its noise at
`vertices[i].xyz * radius` — **world coordinates** — so landscape features keep their physical size
and a bigger planet simply has more of them.

**One hook, because none of it is saved.** `PlanetData.radius` has exactly three writes
(`CreatePlanet` @0751 gas giant / @094F everything else, plus the ctor default), and
`radius`/`scale`/`precision`/`segment` appear in no `Export`/`Import` — the galaxy is regenerated
from the seed on every load. So a postfix on `PlanetGen.CreatePlanet` replays deterministically.
Three numbers lock together: `precision = radius`, `segment = radius / 40`, which keeps
`precision/segment` at vanilla's 40 — hence **the radius must be a multiple of 40**.

**The guard tests the END STATE, not the planet type**: `radius==200 && precision==200 &&
segment==5`. That single condition excludes gas giants (80/64/2), `EPlanetType.None`, and anything
another mod already moved.

#### Four vanilla constants are hardcoded to radius 200, and finding them is the whole story

Every one was found by a player-visible symptom, not by a sweep. **Assume more exist** (see
*Known gaps*).

| What | Where | Symptom at radius 400 |
|---|---|---|
| `kMaxMeshCnt = 100` | `const`, inlined into four `newarr` in `PlanetData..ctor` | mesh count is `4 × segment²` = 400; writing the 101st tile crashes `ModelingPlanetMain` @107A |
| `20020` | `GetModPlane`: `return (short)(plane*133 + 20020)` | every foundation cell's rendered **and collided** height is pulled to 200.2 — the ground collapses 200 units inward |
| `210` / `800` / `600` | `TrashSystem.Gravity` | dropped items never query real terrain, gravity is 21× too weak → they drift over the surface |
| `AstroData.uRadius` | `CreatePlanet` @0AA9 sets it from `realRadius` **inside the method** | stale at 200, so the trash "ground" is 200.35 and **items can never land** |

**`kMaxMeshCnt` is the lesson about searching.** An earlier survey reported it as "zero
references, not a gate". That was exactly backwards: **zero field references means it is a `const`,
inlined as a literal at every allocation site** — the thing you cannot find by field name is the
thing most likely to bite.

**`20020` is `(200 + 0.2) × 100`, and only the READ side is wrong.** `FlattenTerrain` @0578
computes the level as `RoundToInt((pos.magnitude - 0.2 - realRadius) / 1.333333)` — correctly
radius-relative. Fix the three consumers (`ModelingPlanetMain` @0BA8, `UpdateDirtyMesh` @0116,
`QueryModifiedHeight` @00C6), **not `GetModPlane` itself**: it returns `Int16`, and the correct
value 40020 overflows 32767. All three treat the result as "height × 100", so one additive float
offset covers them; at radius 200 the offset is 0 and the value is bit-identical to vanilla.

**`uRadius` is the lesson about enumeration.** The survey that decided "one hook is enough" scanned
`ldfld PlanetData::radius` and **missed `get_realRadius()`, a property**. That single stale field
has **45 reader methods** — interstellar ship arrival times and dispatch, player navigation, Dyson
sphere rockets, ejectors, combat and enemy pathing all believed the planet was 200 across.
**Enumerate property getters alongside field reads, or the census is fiction.**

**The `TrashSystem` constants scale per body, keyed on an explicit id table.** `astrosData` holds
**stars and planets together**, and a star's `uRadius` is `StarData.radius × 1200` — a few hundred
to a couple of thousand, so it **can coincide with our radius**. Matching on the value would
silently catch a small star; a `bool[]` indexed by astro id cannot.

#### Seven rounds on one bug, and why

「出生点永远是个水洼，走进去掉下去出不来」 took seven launches. Three "independent" measurements —
`QueryHeight`, a ring scan, and a brute-force nearest-vertex scan — **all read the same
`heightData`**, which was correct the entire time. What had collapsed was the *geometry*. Two
things broke the deadlock, and only one of them was mine:

- **the player's own report** that walking in *dropped them through* — which reclassified the
  problem from "water" to "a hole";
- a probe that finally put `planet.meshes` vertices **side by side with** `heightData`.

**When several measurements agree that nothing is wrong, first ask whether they are measuring the
same thing.** Independence is about the data source, not about the code path.

And the probe's own verdict line was wrong in the same family: it judged "is there water" instead
of "do the two numbers disagree", so once both sides agreed on a real 0.2-deep puddle it still
printed 「QueryHeight 在撒谎」. **A verdict that does not compare the two sources cannot tell
disagreement from agreement.**

#### The save lock, and the ceiling

**Building positions are planet-local with magnitude ≈ radius**, so changing the radius — including
turning the switch off — puts every building in an existing save at the wrong altitude. There is
**no code-level fallback**: our `IModCanSave` block is read *after* galaxy generation, so a save
cannot record the radius it was built with. The warning is therefore the only thing between a
player and a ruined save, and it is written in four places (package README, CHANGELOG, both feature
guides, startup log). `InitModData` assigns the saved array by reference **without a length check**,
so a mismatched `modData` used to crash; it now reallocates and logs loudly.

**The hard wall is 655.35**: `heightData` is `UInt16` in units of `height × 0.01`, and
`GenerateTerrain` writes `(ushort)((radius + relief) * 100)` with **no clamp** — exceed it and the
terrain wraps. Relief is absolute, so headroom is `655.35 − tallest mountain` regardless of radius.
The config cap is 600. **Three softer limits arrive first and none is in the code**: memory
(~19 MB/planet at 2×, and *scanned* planets count too), **one planet is one work item** (so an
overbuilt planet is single-threaded), and terrain draw calls scale with tile count. **2× is the
only multiplier that has been measured.**

### Power coverage — `src/Patches/Power/PowerCoveragePatches.cs`

One more cache than the usual save-baked chain: `PrefabDesc.powerCoverRadius` → `PowerNodeComponent.coverRadius` (**saved**) → `PowerSystem.OnNodeAdded` squares it into `PowerNetworkStructures.Node.coverRadius2` **and links the covered consumers right there**. Writing `coverRadius` at runtime therefore changes nothing: the squared cache is stale and every `PowerConsumerComponent.networkId` is already fixed. The fix-up runs the vanilla teardown/rebuild pair (`OnNodeRemoving` → set → `OnNodeAdded`), which is what the game itself does when a substation is dismantled and rebuilt.

It hooks **`GameData.Import`**, not `PowerSystem.GameTick` — the latter has a `FactoryPowerSystemGameTick_Parallel` variant, and mutating network structures from a worker thread is not survivable. (`GameTick`'s own `coverRadius` read sits inside `if (node.isCharger)` and is the mecha charging range, not consumer coverage.)

Units are metres: `OnNodeAdded`/`OnConsumerAdded` project positions onto a sphere of radius `realRadius + 0.2` before comparing squared distances, so planet-wide coverage needs ≥ 2 × planet radius. `powerConnectDistance` is deliberately left alone — raising it makes every node connect to every other, and `line_arragement_for_add_node`'s work grows quadratically.

### Procedural building models — `src/Model/`

**The five mega buildings were one model in five colours.** `megabuildings.json`'s `copyFromModelId` is a **single global setting** (49, 物流运输站 — picked because it carries belt ports, i.e. `portPoses`), so all five cloned the same ModelProto and were distinguished only by `tintR/G/B`. Tinting cannot change a silhouette. `MeshKit` + `MegaBuildingMeshes` generate each building's geometry in C# instead — no Unity editor, no AssetBundle, and the geometry is **wholly original**, which matters because the art-asset licence question is still open (see `部署.md`).

**Why this is possible at all: the thing actually drawn in-world is `PrefabDesc.lodMeshes`.** `ObjectRenderer.Init` reads `lodMeshes[i]` *and* `lodVertas[i]` and hands both to `BatchRenderer(Mesh, Material[], …, VertaBuffer, …)`. The Mesh is the geometry; the **VertaBuffer is vertex-animation data** — its only use is `SetToAnimMaterial`, and the constructor null-checks it (IL 0139 and 01EE). So a static building needs nothing but a Mesh.

**Do not try to synthesise a VertaBuffer.** `ReadPrefab` builds `lodVertas` by `VertaBuffer.LoadFromFile(LODModelDesc.lodVertaPaths[i])` — a **pre-baked file** whose vertex count matches the vanilla mesh. Once the geometry is replaced that data is meaningless, so the **elements** are nulled. Nulling the whole array instead would crash: `ObjectRenderer.Init` indexes it.

**Only the mesh is swapped; everything else stays vanilla.** Footprint, colliders, belt attachment points (`SlotConfig.slotPoses` → `PrefabDesc.**portPoses**`; the names are swapped, see the exchanger section) and LOD distances all come from the vanilla prefab via `ReadPrefab`, and are already proven to work. Building a prefab from scratch would put all five back in play at once; this way the only surface that can break is the geometry itself. `mesh` and `meshes` are swapped alongside `lodMeshes` because build previews, blueprints and the dismantle highlight read those instead.

**Replacing array elements is safe, and that is worth checking rather than assuming.** `ReadPrefab` does `newarr UnityEngine.Mesh` for both `meshes` and `lodMeshes` (IL 00DA, 0940, 0D53), so every `new PrefabDesc(…)` owns its arrays — writing an element cannot reach the vanilla building. The **Mesh objects inside are shared**, so they must be replaced, never mutated. This is the same distinction the existing tint code relies on when it does `material = new Material(material)`: the array is ours, the objects in it are not. Get it backwards and you retexture 物流运输站 for the whole save.

**A bounding box is not a footprint, and the bottom of one is not the ground.** `MeshKit.Place` scales the generated shape to the vanilla mesh's *horizontal* bounds, caps it against their height, centres it on the local origin and sits its underside on **y = 0**. Both halves replaced a wrong assumption, each caught in game: (1) aligning the underside to `reference.min.y` assumed the vanilla mesh's box bottom is the ground plane — it is not, and the building **floated**; a DSP building's local origin is on the surface, so y = 0 needs no assumption at all. (2) Filling the box made the building look far larger than the station it clones, because **that station is a slim tower on thin legs and most of its box is empty air**. Solid geometry filling the same box reads as a much bigger structure. `modelScale` (default 0.6) is the correction and lives in `megabuildings.json` so it can be dialled without a rebuild. The log prints the reference box's size, centre, floor and ceiling — printing only its size is what made the first two guesses survive as long as they did.

**Collapsing the UVs to one texel was the wrong trade, and the fix was to stop borrowing the atlas.** The first version kept the vanilla material — whose texture atlas is laid out for the vanilla mesh's UVs — so any plausible UV would sample unrelated regions and come out speckled. Collapsing every UV to one texel avoided that and produced exactly what you would expect: **a building with no detail at all**, flat white with lighting as the only cue. The constraint was never the UVs; it was borrowing someone else's atlas. `BuildingTexture` generates a 4×4 atlas of surface kinds (plate, dark plate, accent, hazard, grating, glow, pipe, concrete, riveted plate, louvre) and assigns it to `_MainTex`, after which the UVs are ours to define.

**Detail lives in seam density, not in the silhouette.** The atlas' first cut drew plate cells as a 2×2 grid with faint seams and still read as flat — and those cells cover most of a building's surface. What made it read as industrial was five things together: 4×4 plates per cell, darkened seams, a second darker step along each plate edge to fake thickness, a per-plate brightness jitter, and corner rivets. `MeshKit` then **subdivides each face by panel size**, so a wall is a run of plates rather than one giant one; that subdivision is also what gives the lighting enough vertices to look right.

**A retracted claim: `_MS_Tex` must NOT be replaced, and the retraction is the lesson.** The argument for replacing it was that new UVs would sample the vanilla metallic/smoothness map at unrelated coordinates and go blotchy, so a constant — whatever its channel packing — could at worst be uniformly matte. **Shipped, the building rendered as nothing at all.** The shader is `VF Shaders/Forward/PBR Standard` and the constant `(70,150,0,150)` was an outright guess; a zero in whichever channel it reads as opacity is enough to erase the building. **"Blotchy" was a prediction, "invisible" was an observation** — a guarded guess about an unknown shader is still a guess, and "the worst case is mild" is part of the guess, not a bound on it. `_MS_Tex` now keeps vanilla's map by default; `overrideMetalSmoothTex` in `megabuildings.json` re-enables the experiment. `overrideMainTex` exists beside it for the same reason: if the building ever vanishes again, flipping the two switches separately isolates which texture did it in one launch instead of one launch per hypothesis.

**Every material write is guarded by `HasProperty`.** LODs need not share a shader, and Unity silently ignores a write to a property that does not exist — which would make "set it but saw nothing" indistinguishable from "there is no such property".

**Two reasons the buildings rendered dark, and the tint one is entirely self-inflicted.**
Reported as 「建模大多呈现为黑色而且用到的细节都差不多」, and both halves measured out rather than
guessed at.

*(a) `_Color` multiplies the generated atlas, and the tints were tuned for a world that no longer
exists.* The nine `tintR/G/B` triples date from when all the mega buildings shared **one** vanilla
mesh and tinting was the only way to tell them apart — so they were pushed to saturation 0.6–0.83.
Now each building has its own generated mesh **and** its own light-grey detail atlas (the dominant
`PlateLight` cell is 196/200/208), and `material.SetColor("_Color", …)` multiplies that. Computing
`atlas × tint` for all nine: **six of them land below luminance 110**, 熔岩冷却厂 at 60 and 观微对撞机
at 52. That is the reported black. The rule now is **hue carries the identity, the atlas carries the
light**: saturation capped at 0.45, value floored at 0.88, which lifts every building to ≥ 108 and
most above 130 without touching a single hue.

*(b) `_MS_Tex` still samples vanilla's map through our completely different UVs*, which is
arbitrary metalness on top of an already dark albedo. The switch stays **off** by default — that is
the retraction recorded above, and it stands — but `BuildingTexture.MeasuredMetalSmooth` no longer
fills the constant with an invented `(70,150,0,150)`. It **reads vanilla's own map and takes the
whole-image mean** (Blit → RenderTexture → ReadPixels, the `IconTinter` technique). The channel
packing is still unknown and **still does not need to be known**: a mean of the real texture lands
inside the range vanilla itself uses, so the worst case is "looks like an ordinary vanilla surface"
rather than "the building is invisible". If the read fails it returns null and the caller falls back
to vanilla's map with a WARNING.

**And the silhouettes were being normalised away by `MeshKit.Place` itself.** It scaled every
generated shape to *fill the reference footprint* (`min(refX/sx, refZ/sz)`) and only then capped
height against the reference — so a tall design was squashed **twice**: the height cap pulled the
scale down, and the footprint shrank with it. A distillation column and a particle ring came out as
the same squat block no matter how differently they were authored. **No amount of extra detail
vocabulary fixes that**, which is why "the details all look alike" was really a proportions problem.
`Place` now takes a `heightMul`, and each building declares its own `modelScale` /
`modelHeightScale` in `megabuildings.json` — 观微对撞机 0.74/0.78 (widest, flattest) through
综合化学厂 0.56/1.50 (narrowest, tallest).

**And the icon colour is now derived from the tint instead of being a second hand-kept copy.**
Reported as 「外观的主色调和 icon 的颜色不同步」. Measuring the two sets showed the **hues had
never drifted** (0–12° apart across all nine) — what had drifted was saturation and value, and it
drifted the moment the tints were re-tuned above: buildings went to sat ≤ 0.45 / value ≥ 0.88 while
`make_icons.py` still held nine hardcoded hexes at sat 0.30–0.83 / value 0.42–0.99. Two hand-kept
copies of the same fact will always separate; the only question is when.

`make_icons.py` now **reads `megabuildings.json` itself**. `building_color(itemId)` returns
`PlateLight × tint` — the colour the building actually renders as, since `_Color` multiplies the
atlas — and `building_pal` derives the light/dark ramp from it. The nine hex literals are gone, so
the build-bar icon is now a preview of the finished building rather than an approximation of it.
Making the icons punchier again is one `mul` argument in one function, not nine edits.

**Budget: 788–2036 triangles per building** (≈ 2.4k–6.1k vertices), the same order as a vanilla building. Face subdivision is capped at 6 per edge precisely so one large flat wall cannot explode.

### Stacked buildings blow out, and material tuning cannot fix it — `src/Patches/AdvancedMiner/StackedRenderPatches.cs`

**The reported symptom was "a few hundred 小型速采机 stacked on one vein go blinding white". It
took nine rounds, and the root cause was none of the eight things tried before it.** The whole
episode is recorded because every wrong turn was a rule this file already states, applied to the
wrong object.

**The mechanism: coincident copies of a transparent or additive layer accumulate.** Opaque
geometry stacked at the same position merely z-fights — one fragment per pixel survives, so N
copies look like one. But a `Blend One One` layer with ZWrite off and ZTest **LEqual** (equal depth
*passes*) adds N times, and an alpha-blended layer converges toward opaque as N grows. A 大型采矿机
carries **four** materials, two of which are exactly those cases (queue 3000 additive, queue 3001
glass). Vanilla never hits this because its build-spacing rule forbids stacking; **this repo lifts
that rule** (`MinerBuildRulePatches`) and ships a fixed-rate miner whose whole design is "stack
more of them", so several hundred coincident buildings is a normal state here.

**Therefore per-copy material tuning is structurally the wrong lever.** Whatever each copy's
contribution is reduced to, multiplying it by the stack count brings the total back. Eight rounds
were spent discovering this the expensive way. The fix is `stackedRenderLimit` in
`advancedminer.json`: draw at most N coincident same-proto buildings and withhold the rest from the
renderer.

**`EntityData.modelId == 0` is a vanilla-supported state, which is what makes withholding safe.**
`RemoveEntityWithComponents` IL 07DB is `ldfld EntityData::modelId ; brfalse` — a zero `modelId`
skips the whole `RemoveModel` block; and `CreateEntityDisplayComponents` opens with two `ret`s (null
`ModelProto`, null `prefabDesc`) whose surviving entities are exactly that state. So the engine
already copes.

**Withhold in a postfix, never by returning false from a prefix.**
`CreateEntityDisplayComponents` also builds the minimap block and computes inserter poses; skipping
the method loses those. Let vanilla run, then `RemoveModel` + zero the field — the same pattern the
vein-circle limiter uses. Colliders, clicking, the minimap and every logic component are untouched;
only the GPU side changes.

**Promote a replacement when the drawn one is dismantled.** Otherwise removing the visible building
makes the whole stack vanish, which reads as "I just dismantled all of them" — a worse symptom than
the one being fixed. Promotion calls `GPUInstancingManager.AddModel` directly rather than re-calling
`CreateEntityDisplayComponents`, which would add a *second* minimap block.

**Leaving a planet and returning re-creates every model, so the hide has to run again — and 1.9.4
shipped without that.** Reported as "I flew away and back and the reflection came back". The path is
`GameData.LeavePlanet` → `PlanetData.UnloadFactory` → `PlanetFactory.UnloadDisplay`, which zeroes
`modelId` / `mmblockId` / `colliderId` **per entity** (IL 0037/0049/005B) and tears the renderer down
wholesale (`CargoTraffic.DestroyRenderingBatches`); returning runs
`LoadingPlanetFactoryMain` @074D, which calls `CreateEntityDisplayComponents` for every entity again.
The bug was one line — `if (cell.Drawn.Contains(id) || cell.Hidden.Contains(id)) return;` — which
early-returned for an entity already marked hidden and therefore left the *freshly created* model in
place. The two cases are now separate: already-drawn stays drawn, already-hidden is **re-hidden**.

**The rule: a bookkeeping table records what you decided, not what is on screen now.** Whether to
remove a model has to be answered from the entity's current `modelId`, never from your own ledger —
the engine can recreate state behind you without telling you.

**And the diagnostic gap it exposed is worth more than the fix.** After the fix, the next session's
log had *no* re-hide line — which cannot distinguish "the player never took off" from "the fix does
not work". A status line ("is it wired up") and an event line ("what did it decide") were both
present and both insufficient, because the missing question was a **third** one: *did that scenario
occur at all?* A postfix on `UnloadDisplay` now logs the teardown with the table's counts, so the log
self-diagnoses: no teardown line → the path was never exercised; teardown but no re-hide → the fix is
broken. **When a fix targets an event you cannot trigger yourself, log the event, not just the fix.**

#### What the nine rounds actually cost, and the four rules they re-taught

**1. Material properties can only be measured, never recalled.** They live in `resources.assets`.
Four separate rounds were lost to guessed property names: `_EmissionColor` (**does not exist on any
of the four shaders**), `_Color` alone (the albedo chain continues into `_AlbedoMultiplier`),
and the PBR metallic/smooth/specular trio (useless against an **Unlit** layer, which by definition
ignores lighting). `MaterialProbe` now dumps a material's shader, queue, keywords and every declared
property with its value. **`materialScales` takes property names as data** precisely so the next
wrong guess costs a config edit rather than a build.

**2. "Dump once" must mean once per kind, not once per building — and this one hid the answer.**
The first probe printed only a building's *first* material. That table was incomplete **with no
sign that it was incomplete**, and the culprit sat in the third shader. The same lesson is already
recorded for `MegaStationPatches`' storage dump; it was re-earned here at a much higher price.

**3. A sentinel must not collide with a legitimate value.** The brightness knobs first used "0 or 1
means don't touch", so when the owner asked to turn a reflection off and wrote `0`, nothing
happened. The fields are now `float?` — **absent** means don't touch. Same family as "one constant
serving as both a limit and a sentinel".

**4. When four consecutive fixes each apply correctly and change nothing, the object is wrong, not
the dose.** That was the actual signal, and it was read as "press harder" four times. What ended it
was `ModelRenderCensus` — enumerate every model currently being drawn with its instance count, and
the offending object names itself (`模型 699 × 398`, with nothing else scaling with the building
count). **When a picture is impossible, enumerate the objects rather than re-reading the path you
already believe in** — the same move `MultiProductUIPatches.DumpBox` made.

Two smaller measured findings worth keeping:

- **`_VeinColorMultiplier` + `_VeinColorTex`** on the drill shaders paint *the mined vein's colour*
  onto the **miner's own mesh**. That is why the glow was ore-coloured (copper orange, iron cyan,
  coal not at all) — the colour is *taken from* the vein but *drawn on* the building, so turning off
  the vein-side display explained nothing. Colour is evidence about a value's **source**, not about
  where it is rendered.
- **`_ToggleVerta`** is the per-material vertex-animation switch. It is the material-side answer to
  "can the animation be turned off", which an earlier round had looked for only in `AnimData` (the
  driving *data*, not the switch).

**And one config-format trap: `Dictionary<string, float>` cannot carry `//` comment keys.** This
repo annotates JSON with sibling `"//name"` keys everywhere, which works for object fields and
**fails for a dictionary** — the string value will not convert and the whole config fails to
deserialize. Put the note outside the map.

### Extra recipes — `src/ExtraRecipeRegistry.cs`

DSP filters the recipe picker by a **single** `ERecipeType` (`UIRecipePicker.RefreshIcons` compares `recipe.Type`; `UIAssemblerWindow` passes `prefabDesc.assemblerRecipeType`), so a machine only ever accepts one type. To let a Chemical machine do a Refine job, cloning the recipe under the new type is far cheaper than teaching the picker and `AssemblerComponent.SetRecipe` about multiple types.

The clone finds its source by **type + product** rather than a hardcoded recipe ID, loud-fails when nothing matches, and copies icon/description/time/`NonProductive` from it — no new art needed. Two things that bite: the `Items`/`Results` arrays must be `Clone()`d or edits leak into the vanilla recipe, and `GridIndex` must be unique — sharing a replicator cell with the source hides one of the two. New recipes go into `MegaBuildingRegistry.RecipeIds` so `RecipeUnlockPatches` unlocks them without a tech.

**`recipes.json`'s second section, `vanillaEdits`, edits a vanilla recipe in place instead of cloning it** (`ExtraRecipeRegistry.OnPostAddData`). It only appends ingredients — deliberately no remove and no count change, since nothing has needed them.

- **Timing is `PostAddDataAction`, and that makes the refresh free.** `RecipeProto.InitRecipeItems` rebuilds the whole `recipeExecuteData` dictionary and LDBTool calls it *after* that action, so writing `Items` / `ItemCounts` there is absorbed on its own — the same timing the Universe Matrix's seventh ingredient relies on. Registered before `EnergyAudit.Run`, so the audit sees the edited recipe.
- **Identification is by product, never by recipe id** (vanilla ids live in `resources.assets`). When several recipes produce the item and the entry names no `type`, it **changes nothing** and lists the candidates — with another content mod installed, "which one got edited is down to luck" is worse than not applying. An ingredient already present is skipped, so the pass is idempotent across a hot reload.
- **Save-safe, measured**: see the correction under *Per-building recipes* — the stream carries each array's own length and `Import` resizes to the current recipe, so 2 → 3 ingredients grows an existing assembler's `served` with the new slot at 0.
- **Slot count is not a concern on the assembler side**: `UpdateNeeds`, `InternalUpdate` and `UIAssemblerWindow.SyncServingStorage` are all `ldlen`-bounded, and the greenhouse already runs a four-input recipe. The unrolled-6 limit is the **lab's** produce mode (`UniverseMatrixPatches`), which is a different component.

Shipped entry: **氢燃料棒's hydrogen ×10 → ×56**, which is not a balance knob but a hole this mod dug itself — see the `vanillaEdits` paragraph under **Cloned buildings** for the arithmetic.

**A retired entry, kept here because the save-safety asymmetry is the lesson.** 处理器 used to gain 电磁矩阵 ×2 — a stated balance knob (its downstream is wide enough that a matrix dependency pulls the research lab into the mid-game line). The owner removed it in 1.10.3. **Removal is save-safe by the same evidence that made the addition safe**, but not symmetrically: `Import` resizes `served` to the current recipe, so 2 → 3 grows with the new slot at 0 while 3 → 2 **drops whatever was in the third slot**. Adding an ingredient costs a player nothing; removing one quietly eats a few items per machine. Say so when retiring one.

### Custom ore veins — `src/OreRegistry.cs`

**Table-driven: `data/ores.json` is a list, and adding a vein type or a recipe is a data edit, not a code edit.**

**Lithium, like aluminium, can only be won by electrolysis** — same Ellingham argument, no carbon-based reductant touches it. Its chain is the real hard-rock route (硫酸焕烧 → 电渗析 → 熔盐电解), and the acid is regenerated by step 2, so it circulates rather than being consumed. The one deliberate deviation from industry is the electrolyte: real cells run molten LiCl, but the game has no chlorine source, so it electrolyses molten LiOH instead — noted in the recipe's `//` comment, per the content rule.

**Ore placement is per-theme and has two modes** (`ores.json`'s `placement`). `normal` adds a **regular vein spot** (`ThemeProto.VeinSpot`, density = `veinRarity` × that theme's iron); `rare` claims a **rare slot** (`ThemeProto.RareVeins`), which is the "a whole planet either has it or doesn't" mechanic kimberlite uses. `themes` filters by display name (substring; `熔岩` also matches `潮汐锁定熔岩`), and a filter that matches nothing loud-fails. **The theme table lives in `resources.assets` and cannot be read offline**, so `OreRegistry.DumpThemes` prints it at startup — that log is the only source for what to write in `themes`.

**`RareSettings` has stride 4**, read out of `PlanetAlgorithm.GenerateVeins` at IL 03F6: `ldfld StarData::index ; brfalse.s IL_0417` — so **`[i*4+0]` is the chance used when `star.index == 0` (the birth system) and `[i*4+1]` is the chance everywhere else**; `[i*4+2]` the per-extra-spot chance (rolled up to 11 more times), `[i*4+3]` richness. So **"none in the starting system" is a vanilla data slot, not something to patch** — put 0 in `[+0]`.

**This line said the opposite for a long time, and the inverted claim shipped.** `ExtendRareSlots` was written from it, so `birthSystem: false` wrote the *outside* chance to 0 and the birth-system chance to the configured value: **all four custom rare ores spawned only in the starting system and nowhere else** — the exact inverse of the design. It surfaced as a player starting a fresh save, sweeping everything outside the birth system, and reporting 「没找到」 while registration, theme matching, the vein table and the placement log line were all correct. Two habits would have caught it: **re-read the IL rather than trusting a prose summary of it** (the two slots are one `brfalse` apart and the summary had them backwards), and **check a vanilla theme’s own numbers** — vanilla rare veins do not spawn in the birth system, so whichever slot is 0 in vanilla data *is* the birth-system slot. `DumpThemes` now prints all four values per rare slot, before this mod writes any of its own, precisely so that evidence is in every log. Normal vein spots have no such slot; `birthSystem: false` on a `normal` ore warns and is ignored.

**GS2 changes what `veinRarity` costs, so the conversion must be capped.** The vanilla path *adds* vein spots and leaves iron alone; `OreGalacticScaleCompat` *converts* iron groups, so every point of rarity is taken from iron at `Σr / (1 + Σr)`. With seven ores tuned for vanilla semantics Σr reached 7.58 — **88% of iron gone**. `BuildBuckets` now weights `rare`-mode ores by their `chance` (the right order of magnitude) instead of `veinRarity`, and scales the whole set down to a 50% conversion cap, preserving relative ratios.

**Which reductant may smelt which ore is decided by chemistry, not by balance.** Cobalt oxide sits high on the Ellingham diagram, so coal, CO, 甲醛, 甲醇 and 乙烯 all reduce it — and the amount of ore one unit handles falls straight out of how many electrons its carbon can give up (CO 2, 甲醛 4, 甲醇 6, 乙烯 12), which is where that recipe family's gradient comes from. Al₂O₃ sits far lower, and **only 乙烯 works**: the test is whether cracking the reductant yields *elemental* carbon. 乙烯 cracks to C + H₂ and can therefore feed the carbothermic route; 甲醇 and 甲醛 already carry oxygen on the carbon and crack to CO; and CO itself is the *product* of carbothermic aluminium reduction, so using it as the reductant would be running that reaction backwards. Don't add the other three for symmetry. The same test extends to the later metals, and it is the *only* thing deciding which recipes exist:

| Oxide | CO / 甲醇 / 甲醛 (crack to CO) | 乙烯 (cracks to elemental C) | 氨 (decomposes to H₂) |
|---|---|---|---|
| CoO | ✓ | ✓ | ✓ |
| MnO₂ | ✗ — CO stalls at MnO | ✓ carbothermic | ✗ |
| Cr₂O₃ | ✗ — ferrochrome uses coke, not gas | ✓ | ✗ |
| Al₂O₃ | ✗ | ✓ | ✗ |
| V₂O₅ | ✗ | **✗ — carbon gives vanadium carbide** | ✗ |
| WO₃ | ✗ | **✗ — carbon gives tungsten carbide** | — (H₂ itself is the route) |

**Two metals break the carbon pattern, and they break it the same way** — carbon reaches the carbide and stops. Vanadium and tungsten are that case; the difference is only what you do about it.

Vanadium takes no carbon route at all, so it takes **aluminothermic** reduction (`3 V₂O₅ + 10 Al → 6 V + 5 Al₂O₃`), which conveniently returns alumina to the aluminium line. It also gets the only deliberately **unbalanced** recipe in the repo — vanadium recovery from petroleum residue is a real process but V is a trace impurity there with no fixed stoichiometry, so the `//` comment says so outright rather than inventing an equation. It exists because vanadium gates late materials and a bad galaxy seed must not lock the line out entirely.

**Tungsten turns the same failure into the product.** `WO₃ + 3 H₂ → W + 3 H₂O` on the redox machine is the only route to the metal (this is literally how tungsten powder is made industrially), and `W + C → WC` on the **smelter** is the carbide — the very reaction that disqualifies carbon as a reductant here. So the chain is three steps, not two, and each step's machine follows the reaction class: **酸解白钨矿 is not a redox at all** (W stays +6 throughout) and therefore stays on the vanilla 化工厂. The ore is **白钨矿 (CaWO₄)** rather than the commoner 黑钨矿 ((Fe,Mn)WO₄) for one reason: `CaWO₄ + H₂SO₄ → H₂WO₄ + CaSO₄` drops its byproduct onto **石膏矿, an item that already exists**, and the sulfuric acid then circulates the way it does on the lithium chain — wolframite would have needed two invented sulfate items instead. Tungsten deliberately has **no synthetic fallback** (unlike vanadium's 残渣提铒), so its rare-slot `chance` is set higher to compensate. Four ores: 钴矿脉 → 钴矿石 → 钴块 (vein 15); 铝矿脉 → 氧化铝矿石 → 铝块 (vein 16) with **two** routes — carbothermic (Smelt: ore ×2 + 煤矿 ×3 → 铝块 ×4 + 二氧化碳 ×3) and electrolysis (the custom 电化学 type 9, ore ×1 + 氢 ×3 → 铝块 ×2 + 水 ×3); and 石膏矿脉 → 石膏矿 (vein 17, `hasIngot: false`) feeding a second sulfuric-acid route (Chemical: ore ×6 + 煤矿 ×2 + 水 ×4 → 硫酸 ×4 + 石材 ×4). Fuel is a per-item pair: `fuelType` (bitmask, **1 = chemical**, what 火力发电厂 and the mecha reactor burn) plus `heatValue` in joules; `OreRegistry.ApplyFuel` warns if only one is set, since half a pair burns for zero power. The C1 gases are valued by scaling molar enthalpy of combustion against vanilla coal (2.7 MJ ↔ 393.5 kJ/mol) — vanilla is not internally consistent on this (its hydrogen is ~4× more generous per mole than its coal), so pick one anchor and say which. An ore with no ingot still carries recipes — every `ref` there is `ore` plus vanilla ids, and the recipe's `IconPath` falls back ingot → ore, since a null one leaves a blank replicator cell. Everything reuses iron's assets, recoloured at runtime: the vein's ModelProto is cloned from iron's and its materials tinted by `veinTint`, the item icons are HSV-recoloured from iron's by `IconTinter`.

The config has four levels: `items` (extra protos belonging to no vein — 二氧化碳, 氧气, plus the C1 chain's 一氧化碳 / 甲醇 / 甲醛 / 乙烯), `ores`, each ore's `recipes[]`, and a **top-level `recipes[]`** for recipes owned by no ore (电解水, plus the six-recipe C1 chain: 水煤气 → 甲醇 → 甲醛 / 乙烯, and 费托合成 → 精炼油). In that top-level list `ref: "ore"` / `"ingot"` are errors — there is no owning ore — and the recipe has no ingot icon to inherit, so it must set `icon` or `iconFrom` or its replicator cell renders blank. **Recipe ingredients reference mod items by name, not id** — `{"ref": "ore"}` / `{"ref": "ingot"}` for the owning ore, an `items` entry's `key`, or `<other ore key>.ore` / `.ingot`; `{"id": 1006}` means a vanilla item. Writing a raw id would break the moment `ResolveItemId` shifts a colliding id. `items` are registered first and recipes last, so every `ref` resolves regardless of declaration order.

**`ExtraItemEntry.iconFrom` is a misnomer: it is the proto *template*, not just the icon source,
and it is mandatory even when the entry brings its own `icon`.** `RegisterExtraItems` opens with
`ItemProto source = LDB.items.Select(entry.iconFrom)` and **skips the whole item** when that is
null; `source` is then what supplies `DescFields` and the fallback `StackSize`. Omit it and the
item never reaches LDB, so every `ref` pointing at it fails and its recipes, its `metals.json`
row and any build recipe naming it all collapse — a cascade of six errors whose single cause is
one missing field. The silicon-carbide chain shipped that way once.

**`iconFromName` is the name-keyed form of it, and exists for the same reason `ref: "vanilla:…"`
does.** Vanilla protos live in `resources.assets`, so the common ids are memorised and the rest are
not — 引力透镜 is 1209, but writing that number by hand is a guess, and a wrong one silently clones
*a different item* as the template, taking the icon and `DescFields` with it. Set `iconFromName` to
the proto's `Name` and registration resolves it, logs the resolved id, and ERRORs by name if it
misses. It is only consulted when `iconFrom` is unset, and it compares **`Proto.Name`, the raw key,
not `proto.name`** — matching the translated one fails on every non-Chinese client.

Note this is the **same fact as the `DescFields` crash, seen from the other side**: `OreRegistry`
items never had that bug precisely because they clone a vanilla proto, while `DrillBitRegistry`
builds one by hand and had to fill the arrays itself. The error message now says "proto 模板"
rather than "图标来源" and spells out that a custom `icon` does not excuse it — the old wording is
what made it look optional.

**`ref` also takes `vanilla:<Chinese name>`, and it exists because a wrong vanilla id is silent.**
Vanilla protos live in `resources.assets`, so **there is no way to enumerate their ids offline** —
the common ones (水 1000, 煤矿 1006, 精炼油 1114, 硫酸 1116, 氢 1120, 塑料 1115) are memorised, but
碳纳米管 / 石墨烯 / 晶格硅 are not, and writing the wrong number produces a recipe that makes a
different item with **no error anywhere**. `AdvancedMinerPatches` already refused to hardcode those
(it derives them from `VeinProto.MiningItem` at runtime); `OreRegistry.VanillaIdByName` gives the
config the same escape. It compares **`Proto.Name`, the raw key, not `proto.name`** — the latter is
translated, and matching on it would fail on every non-Chinese client (the mistake already recorded
under *English localization*). It resolves vanilla only: this mod's own protos are not in LDB during
`PreAddDataAction` and should be referenced by key anyway. A miss is an ERROR that skips the whole
recipe, and a hit logs the resolved id.

**Recipe names must lead with the product, because the replicator has no item cells.** `UIReplicatorWindow` draws recipes and only recipes — there is no cell for 钴块 itself, and vanilla papers over this by *naming the recipe after its product* (the cell that looks like "铁块" is the smelting recipe named 铁块; alternates get a suffix, e.g. 石墨烯 / 石墨烯（高效）). Naming a recipe after its **process** instead — 碳热还原钴, 氢还原钨 — is therefore not a cosmetic choice: the product becomes unfindable, and the panel reads as "a pile of formulas with the metal missing". That was reported twice as a missing-icon bug before the cause was found, and the log line that settles it is `配方「…」已注册`. All 33 `ores.json` recipes are now `<产物> · <工艺>` (钴块 · 碳热还原, 钨块 · 氢还原, 碳化钨 · 渗碳), which keeps the process visible without hiding the product. Renaming is safe: recipe IDs are pinned in the JSON and saves reference IDs, and LDBTool's name-keyed cfg entries hold 0 (no override).

**The replicator grid is drawn from `LDB.recipes` alone.** `UIReplicatorWindow.RefreshRecipeIcons` iterates `recipeProtoArray` and places each entry by `RecipeProto.GridIndex`; an item with no recipe never appears there, and `ItemProto.GridIndex` only drives the item-picker style panels. Two independent grids, so `ResolveGridIndex` must check **both** proto sets for occupancy (`GridTaken`), and item/recipe cells no longer need to coincide the way vanilla's do.

`OreRegistry.Ores` is the runtime list every patch iterates; nothing anywhere hardcodes a single vein id. The helpers worth knowing are `IsCustomVein(type)`, `Find(type)`, `MaxVeinId`, and the id-resolution pair (`ResolveItemId` / `ResolveRecipeId` / `ResolveGridIndex`), which scan `ProtoSet.dataArray` **directly** — `ProtoSet.Select` is not a reliable occupancy test (it reported 200 consecutive item ids as taken). `VerifyContiguous` loud-fails if the vein ids are not dense from 15.

**No preloader is needed to add a vein type, but the hardcoded loop bounds are.** The usual approach (ProjectGenesis) is a BepInEx patcher that deletes `EVeinType.Max` and adds named members; that part is avoidable, because `EVeinType` is byte-backed (`(EVeinType)15` is valid unnamed) and the arrays size themselves — `PlanetModelingManager.PrepareWorks` from the last `LDB.veins` proto's `ID + 1`, `PlanetAlgorithm.GenerateVeins` from `veinProtos.Length`.

**This paragraph used to end "`PrepareWorks` runs from `PlanetModelingManager.Start`, long after LDBTool's `PostAddDataAction`, so ordering works out." That is measured false and it shipped a crash** — see *A second vein-array crash* below. `PrepareWorks` runs **more than once, and the first time is before this mod's veins are in LDB.**

What is **not** avoidable is `for (int type = 1; type < 15; type++)` — vanilla walks vein types against a literal 15 in seven places (`GenerateVeins` on `PlanetAlgorithm` + `PlanetAlgorithm7/11/12/13`, `UIPlanetDetail.OnPlanetDataSet`, `UIStarDetail.OnStarDataSet`). Types 15+ sit exactly outside, so registering the proto alone yields **zero veins generated and nothing in the planet panel**. `OreVeinRangePatches` transpiles the bound to `PlanetModelingManager.veinProtos.Length`, matching only a `15` immediately followed by an ordering branch — `UIPlanetDetail`/`UIStarDetail.RefreshDynamicProperties` also contain a 15, but it is a rare-vein id comparison and must not be touched. Raising the UI bound is safe: those panels' `veinCounts`/`veinAmounts` are `new [64]`, the body null-checks `LDB.veins.Select(type)`, and rows come from a pooled `List`, not a fixed widget array.

**Raising that bound exposes vanilla assumptions that 14 vein types could never reach. Two of them, and the second is the one that actually crashed.**

*(a) A latent 512-spot overflow — real, fixed, but NOT the crash below.*
`PlanetAlgorithm..ctor` hardcodes `veinVectors = new Vector3[512]` (plus a parallel
`veinVectorTypes`). `GenerateVeins` fills it from two nested loops — outer over vein *type*
(that very `15` at IL 086C), inner over that type’s spots — and the capacity guard is
`if (++veinVectorCount == veinVectors.Length) goto <outer increment>` (IL 0853 → 0864).
**It breaks only the inner loop.** The outer loop then moves to the next vein type and writes
`veinVectors[512]` straight away. Vanilla’s 14 types never accumulate 512 spots, so the hole is
unreachable; at 23 types it opens, and the symptom is
`Scanning Thread Error: IndexOutOfRangeException at PlanetAlgorithm.GenerateVeins` with
`OreVeinRangePatches` named on the stack — accurate as the trigger, misleading as the cause.

`VeinVectorCapacityPatches` fixes it by **enlarging the two arrays in a constructor postfix**,
not by rewriting the branch: the guard compares against `veinVectors.Length` (a dynamic read, not
the literal 512), so a bigger array keeps vanilla’s own stop behaviour intact and no control flow
is touched. Capacity is derived rather than picked — vanilla budgets 512 spots for 14 types, so
the same per-type allowance times the current type count, rounded up to a multiple of 64 (23
types → 896). Cost is ~12 KB per `PlanetAlgorithm`, of which only a handful exist at once.

**That fix was shipped and the crash came back, byte-identical.** The 512 hole is genuine and the
guard is worth keeping, but it was never this crash — a reminder that *finding a real bug in the
right method is not the same as finding the one you are chasing*.

*(b) The actual crash: a type-indexed array sized from `veinProtos.Length`.*

**A transpiled method’s IL offset must be reconstructed, not looked up.** The report said
`IndexOutOfRangeException ... GenerateVeins (IL_04F9)`, and 0x04F9 in the *original* body lands in
the middle of an `ldfld` — no array in sight, which is what sent the first two rounds chasing the
wrong thing. The offset is from Harmony’s DMD, and two effects move it: Harmony’s
`MethodBodyReader` rewrites **every short branch to its long form** (2→5 bytes), and our own
transpiler swaps `ldc.i4.s 15` for a `call` (2→5). Replaying both offline over the real method
body maps patched `04F9` to original **`0496`** exactly:

```
0496: ldloc.s V_11        // veinSpots = new int[veinProtos.Length]   (IL 00AE)
0498: ldloc.s V_30        // vein type, straight out of theme.RareVeins[i]
049A: ldelema System.Int32
```

— the `veinSpots[veinType]++` that runs **only when a rare vein roll succeeds**, which is why it
appeared the moment the birth-system inversion was fixed and rare veins began rolling at all.
`veinSpots` / `veinCount` / `veinOpacity` are all `new [veinProtos.Length]` and then indexed by
vein **ID**, and `PrepareWorks` sizes `veinProtos` as `size = dataArray[i].ID + 1` — **assign, not
max** — so the capacity is the *last* element’s ID + 1, not the largest.

`OreRegistry` already sorts by ID and `VerifyVeinArrayOrder` already checks the last element — but
**at `PostAddDataAction`**, while `PrepareWorks` runs much later, at
`PlanetModelingManager.Start`, with LDBTool’s table build and every other mod in between.
*"My step was correct" is not "the end state is correct"*, and the gap between those two is
exactly where this landed. `VeinProtoArrayPatches` therefore checks the **end state** in a
`PrepareWorks` postfix: it logs the measured lengths unconditionally, and when they are short it
grows all four arrays and re-runs vanilla’s own fill loop verbatim
(`veinProducts[p.ID] = p.MiningItem`, …), which is idempotent.

*(c) A second vein-array crash whose cause I got wrong twice — the write-up below is kept because
the mis-diagnosis is the lesson.*

**Measured conclusion first: this crash was NOT a `PrepareWorks` timing problem. It was
[the parameter-name trap](#) aborting `PatchAll`.** `Harmony.PatchAll` threw partway through
`ProjectEdenPlugin.Awake`, so **every line of `Awake` after it never ran** — including
`LDBTool.PreAddDataAction += OreRegistry.OnPreAddData`. No ore registration meant `LDB.veins` held
vanilla's 14, `PrepareWorks` sized `veinProtos` to 15, and the **save already contained vein types
15–23 from earlier sessions** → `veinProtos[type]` off the end while loading the planet. Fixing the
parameter name fixed the crash; the vein-array work below never fired at all (the log shows
`够用` on both calls and zero grows).

**Two habits would have caught it, and both are already rules in this file.** *A mechanism that
could explain a symptom is not evidence that it did* — the `PrepareWorks`-runs-twice finding is
real and was allowed to masquerade as the cause. And **`PatchAll` throwing means the rest of `Awake`
is dead**, not merely "some patches missing"; when a crash looks like the mod is not installed,
check whether registration ran before theorising about anything downstream of it.

What survives as genuine: `PrepareWorks` **does** run twice and the first call **is** before our
veins reach LDB (logged: `LDB 里 14 条 … 按 15 定容 → 够用`, then `LDB 里 23 条 … 按 24 定容`).
Vanilla's own second call sizes it correctly, so nothing is broken today — but the check standing
guard there was measuring the wrong thing, and the point-of-use prefix on
`LoadingPlanetFactoryMain` is cheap insurance that cannot be ordered away. **Note the
`OreRegistry.MaxVeinId` half of that fix is inert on the first call — measured 0, because the
registry is empty then too.** Keep it for the later calls; do not believe it guards the first one.

The original (wrong) analysis follows.

Reported as `IndexOutOfRangeException at PlanetModelingManager.LoadingPlanetFactoryMain (IL_035D)` —
a different method from (b), and the stack names no mod at all. Offset reconstruction is easier here:
`OreVeinColorPatches` replaces `ldfld VeinData::type` with a `call`, **both 5 bytes**, so only
Harmony's short→long branch expansion shifts anything. The site is
`veinProtos[veinPool[i].type]` (original IL 0x035D `ldsfld veinProtos` → 0x0370 `ldelem.ref`) —
`veinProtos` indexed by **vein type**.

**What settled it was two log lines from the previous, healthy session:**

```
矿种数组核对：矿种表 14 条，最大编号 14 … 按 15 定容，需要 15 → 够用
…
矿种数组核对：矿种表 23 条，最大编号 23 … 按 24 定容，需要 24 → 够用
```

**`PrepareWorks` runs twice, and the first call happens before our veins reach LDB.** At that moment
`LDB.veins` holds vanilla's 14, so `veinProtos` is sized 15 — and `VeinProtoArrayPatches` measured
15 ≥ 15 and printed **"够用"**. The healthy session was rescued by the second call; the session that
crashed never got one, so the array stayed at 15 and vein type 15–23 went straight off the end.

**The failure is not "the check didn't run" — it ran, and it was right, and it was useless.** It
asked "is the array big enough *for what LDB currently holds*" when the question is "is it big
enough *for what it will hold*". Two fixes, and both are the same idea from opposite ends:

- **Required capacity must not be read from `LDB.veins` alone.** `OreRegistry.MaxVeinId` knows the
  answer before LDB does, so the requirement is `max(LDB max id, OreRegistry.MaxVeinId) + 1`. The
  first call now grows the array immediately instead of depending on a second call existing.
- **Ensure it again at the point of use** — a prefix on `LoadingPlanetFactoryMain` itself. **Any
  preparation step scheduled before the use site can be skipped, reordered or pre-empted by another
  mod; the use site cannot.** It also always re-runs the fill, because a grown-but-unfilled array
  leaves `veinProtos[15..23]` null and vanilla's `if (proto == null) continue;` then draws **no
  veins at all, silently** — the repo's worst shape, reached by "fixing" the crash.

**The general rule: a capacity check that measures the current contents of a table that is still
being built reports a true fact and a false verdict.** Measure against what you already know you
will need, and verify again where the value is actually consumed.

**Process notes.** The crash surfaced through a *diagnostic* (`RareVeinProspector`) driving the
scan thread over dozens of planets at once — work a player does slowly, one system at a time — so
the tool compressed the exposure rather than inventing the bug. That tool now also (1) refuses to
enqueue anything until `veinProtos.Length > MaxVeinId`, since it starts at
`UniverseGen.CreateGalaxy` and would otherwise beat `PrepareWorks` to the queue, and (2) gives up
after 30 s with no progress and prints `PlanetModelingManager.planetScanThreadError`, because a
dead scan thread otherwise reads exactly like a slow one.

**`ModuleDefinition.Types` does not include nested types.** A caller search that walks only top-level types silently reports "this method has no callers" whenever the call sits in a compiler-generated nested class — iterators/coroutines especially. `ItemProto.InitFluids` looked callerless for exactly this reason; its only caller is `VFPreload/<PreloadThread>d__51::MoveNext`. Recurse through `NestedTypes`.

**A Cecil scan for "hardcoded N" must not test `Operand -is [int]`.** `ldc.i4.s` carries an **sbyte** operand and `ldc.i4.0..8` carry none at all, so that test silently reports zero hits and "nothing hardcodes this" — which is how the seven sites above were missed on the first pass. Switch on the opcode name and convert.

**GalacticScale silently hides new vein types from the in-world 矿脉分布 labels.** `PatchOnUIVeinDetail.SetInspectPlanet` is a **prefix that returns false**, i.e. it replaces vanilla `UIVeinDetail.SetInspectPlanet` outright, and its replacement adds a per-type gate that vanilla does not have: `show = GS2.Config.VeinTips.ContainsKey(type) ? VeinTips[type] : false`, and only then `CreateOrOpenATip`. A type absent from that dictionary never gets a label node created at all. `OreVeinTipCompat` inserts the key from a `Priority.First` prefix on the same method (GS2 reads its config from disk after plugin Awake, so it cannot be done once at startup).

This one cost many rounds because every vanilla-side reading said the label should work — `DoVeinGroupsRecalculate`, `CreateOrOpenATip` and `UIVeinDetailNode.Refresh/_OnUpdate` contain no type filter whatsoever. Two things eventually cracked it: the user's observation that **hovering worked while the persistent label did not** (the hover path is `cursorNode`, which skips the gate), and then distinguishing the two in the diagnostic — until that point every "cobalt looks perfectly healthy" sample had actually been the cursor node. When a UI behaves impossibly, check whether another mod has replaced the method with a `return false` prefix before re-reading vanilla IL again.

**With GalacticScale installed, none of the vanilla generation path runs.** GS2 replaces it wholesale (`VeinAlgorithms.GenerateVeinsGS2` / `GenerateVeinsGS2W` / `GenerateVeinsVanilla` plus its own `GSTheme` vein tables), so both the `ThemeProto` entry and the raised loop bounds are dead code there — patched successfully, never executed. `OreGalacticScaleCompat` instead prefixes GS2's two funnels, `InitializeVeinGroup(i, veinType, …)` and `AddVeinToPlanet(amount, veinType, …, groupIndex, planet)`, and re-types a deterministic share of *iron* groups to the custom types, keyed on `(planet.id, index)`. The hash falls into one bucket per ore, sized by its `veinRarity` (`r_i / (1 + Σr)`), so several ores split the converted share stably across loads. GS2 never has to know they exist — rarity, distribution and collision are all computed as iron first. **Note the two paths differ in what they cost:** the vanilla `ThemeProto` route *adds* vein spots and leaves iron untouched, while this one *converts* iron groups — at the current rarities (0.35 + 0.3 + 0.25) that is ~47% of iron groups on a GS2 galaxy. Both methods increment their index argument before use, so the raw argument is a consistent key across the two hooks.

**Vein type ids must stay contiguous — you cannot skip past `EVeinType.Max`.** It is tempting to avoid occupying the sentinel value 15 itself, but `UIPlanetDetail.OnPlanetDataSet`'s vein loop reads `veinProto.MiningItem` **before** it null-checks the proto (in IL the `ldfld` precedes the `brfalse`). Vanilla never trips this because types 1–14 are dense. Register at 16 and leave a hole at 15, and the raised loop bound walks onto the hole: `LDB.veins.Select(15)` returns null and the planet panel dies with a `NullReferenceException` the moment it opens. Verified by doing it.

**Gas giants are the same `ThemeProto` story, one field pair over.** Veins live in `VeinSpot`/`VeinCount`/`VeinOpacity`; gases live in **`GasItems`/`GasSpeeds`**, and `ores.json`'s `gases[]` section injects into them (`OreRegistry.ExtendGasThemes`). The only generator is `PlanetGen.SetPlanetTheme`, called from `PlanetGen.CreatePlanet`, gated on `planet.type == EPlanetType.Gas` (5):

```csharp
items[i]  = theme.GasItems[i];                                   // species copied verbatim — no randomness at all
speeds[i] = theme.GasSpeeds[i]
          * (rand.NextDouble() * 0.19090915f + 0.9090909f)       // ±~10%, DotNet35Random(theme_seed)
          * PlanetGen.gasCoef                                     // global, set in PlanetGen..cctor / UniverseGen.CreateGalaxy
          * Mathf.Pow(planet.star.resourceCoef, 0.3f);
heats[i]  = LDB.items.Select(items[i]).HeatValue;                 // straight off the item
gasTotalHeat += heats[i] * speeds[i];
```

Three things that bite:

- **`GasItems` and `GasSpeeds` must stay the same length.** The loop runs to `GasSpeeds.Length` but indexes `items[i]`, so a longer `GasSpeeds` throws on generation. `ExtendGasThemes` aligns both.
- **A gas with no `HeatValue` contributes 0 to `gasTotalHeat`**, which is the denominator of the collector's power penalty (see `GasCollectorPatches`). That is correct for an inert gas like 氮气 and vanilla guards against a zero denominator, but don't expect an inert gas to speed collection up.
- Speeds are configured as a **ratio of the theme's highest existing gas speed**, never as absolutes — theme speeds live in `resources.assets` and cannot be read offline.

**Adding a gas is not enough to make it collectable.** The chain is `ThemeProto.GasItems` → `SetPlanetTheme` → `planet.gasItems` → `PlanetTransport.NewStationComponent` → `station.collectionIds` (all of them, uncapped) → `StationComponent.Init` → `storage[i].itemId`, and the last hop is the gate:

```csharp
for (int i = 0; i < collectionIds.Length; i++) {
    if (i > _desc.stationMaxItemKinds - 1) break;   // extras get no storage slot
    storage[i].itemId = collectionIds[i];
}
```

The gas is in `collectionIds` and still never produced, silently. `OreRegistry.EnsureCollectorSlots` therefore raises the collector's `stationMaxItemKinds` to exactly the largest `GasItems.Length` across all themes — **exactly that, not to 30**, for the reason recorded under `GasCollectorPatches`. Only newly built collectors are affected; `storage` is baked at build time. (`Init` is called *after* `collectionIds` is filled in `NewStationComponent` — verified, since the reverse order would lay out slots from an empty array.)

**Generation is driven by `ThemeProto`, indexed by vein type − 1.** `GenerateVeins` does `Array.Copy(theme.VeinSpot, 0, spots, 1, Math.Min(theme.VeinSpot.Length, spots.Length - 1))`, so entry *i* of the theme array is vein type *i+1*. Growing `VeinSpot` / `VeinCount` / `VeinOpacity` by one and filling the new slot from iron's values (× a rarity factor) is the whole of it — no transpiler. Only themes that already have iron are touched, so gas giants stay clean. This affects **planets not yet generated** (including unvisited ones in an existing save); planets already materialized keep their baked vein data.

**A new vein type cannot be recoloured without shipping a shader.** Vanilla stores `(uint)veinData.type` in `AnimData.state` and the stock shader reads it as a *type index*. ProjectGenesis packs an RGBA into that field instead (`a<<24 | b<<16 | g<<8 | r`) — but only because `SwapShaderPatches` swaps the vein material's shader for its own during `VFPreload.SaveMaterial`. **Copying only the packing half makes the veins invisible on the ground**: the stock shader gets a ~4-billion "type index", draws nothing, while `veinPool`, the planet panel and the resource totals all look perfectly correct. That combination — data present, statistics right, nothing on the surface — is the signature of this bug. The way out is that **`modelIndex > 2` veins take their look from their own material** instead of the type-indexed colour table — that is how vanilla draws 可燃冰 / 分形硅石. Each custom ore gets a cloned, tinted ModelProto, so `OreVeinColorPatches.Pack` passes the raw type straight through and the material does the colouring. The `EVeinType.Iron` substitution is only a fallback for `modelIndex <= 2` (the shared ore-pile model, i.e. model cloning failed), where the raw type would index past the stock colour table and draw nothing; it logs a warning when it fires.

The two transpiled sites are `PlanetModelingManager.LoadingPlanetFactoryMain` (`ldelema` → the helper takes `ref VeinData`) and `PlanetFactory.AddVeinData(VeinData)` (`ldarg.1` → by value); the differing stack shapes are why there are two helpers.

**`PlanetData.runtimeVeinGroups` means two different things**, and conflating them produces the very convincing illusion that generation is broken: it is `factory == null ? veinGroups : factory.veinGroups`. With no factory (a planet never landed on) it returns `PlanetData.veinGroups` — **a plain field read, not a generation**: the getter is ten instructions and calls nothing (verified). That field is filled by `SummarizeVeinGroups()`, which the **scan thread** runs, so it is populated only for planets that have been scanned or visited and is otherwise null. (An earlier version of this file said the getter returns "freshly generated" groups; it does not, and a prospecting tool written on that belief would have silently read nulls.) Once the planet has a factory it returns the **saved** ones, which for any already-visited planet predate the mod and contain none. The result is a planet whose orbital panel lists 钴矿石 86,145 while the ground at those exact coordinates is empty — and the coordinates themselves come from the generated preview, so they don't correspond to anything real. Any vein diagnostic must say which of the two it read. (`data.veinPool` is a third source, empty unless the planet is loaded in memory — reading that one produced a bogus "0 cobalt veins, this planet has none" report.)

`UIRecipePicker` also **remembers its tab across opens** (`currentType` is only defaulted when 0, in `_OnOpen`), so a machine whose recipes live on another page opens onto whatever tab was used last. The same patch adds an `_OnOpen` postfix: when the picker opens *with a type filter* and nothing is currently visible, it jumps to the tab and column page of the first matching unlocked recipe.

**Custom vein types are displayed as common minerals, not 珍奇.** Vanilla splits on `type >= 7` in `UIPlanetDetail`/`UIStarDetail` (`OnPlanetDataSet` + `RefreshDynamicProperties`), which would file a type-15 vein under rare signals. `OreCommonVeinPatches` inserts `NormalizeVeinType` before every `ldc.i4.7` that is immediately followed by a comparison, mapping custom types to 6 (煤矿). Vanilla rare veins are untouched.

**The 矿脉分布 chart colours its blocks by vein type through a prefab-sized palette.** `AstroResourceStatPlan.AddDetailedStatData(astroId, VeinData.type, …)` stores the **vein type** in `DetailedStatData.protoId`; `UIChartAstroResource.Refresh` copies that into `GraphData.colorIndex` and hands the shader `_ColorBuffer` = `graphColors`, a `Color[]` **serialized in the Unity prefab** covering only types 1–14. A new type indexes past the end and simply isn't drawn — while every data path feeding the chart is complete (the only type literal in `AddPlanetResources`/`AddPlanetDetailedResources` is the `== 7` oil special case). `OreChartColorPatches` grows `graphColors`/`barColors` in a `_OnCreate` postfix and rebuilds `colorBuffer` at the new length, since `_OnCreate` sized it from the old array.

**A cloned vein model needs its colliders copied, or the vein cannot be interacted with.** `new PrefabDesc(id, prefab)` alone yields a vein you can see but not raycast — right-click mining does nothing and mining machines cannot target it. Carry over `colliderPrefab` (falling back to `Resources.Load(source._colliderPath)`), `colliders`, `buildCollider`, `buildColliders`, `hasBuildCollider` and the rough extents, exactly as `MegaBuildingRegistry.CopyModelProto` does.

**A retracted finding, kept because the mistake is the lesson.** For several rounds this file claimed that assigning a runtime `Sprite.Create` result to `VeinProto._iconSprite` broke the in-world 矿脉分布 label. It does not. The label was missing because of GalacticScale's `VeinTips` gate (above), and the "A/B test" that seemed to prove the sprite theory was contaminated: every healthy-looking sample had come from the **hover cursor node**, which bypasses that gate, while the persistent `allTips` node never existed at all. Runtime sprites are fine on that path — ProjectGenesis's own veins use exactly the same `Resources.Load` prefix plus `Texture2D.LoadImage` + `Sprite.Create`. Two habits would have caught it far sooner: never conclude from an A/B whose two arms might be different code paths, and log *which* path a sample came from before reading anything into it.

(`HideFlags.HideAndDontSave` on runtime textures is still worth setting on its own merits — `Resources.UnloadUnusedAssets` destroys a texture referenced only from a static field, leaving a live `Sprite` whose `rect` still reads correctly.)

**Vein icons travel two independent paths.** The in-world group label (`UIVeinDetailNode`) assigns `veinProto.iconSprite` straight to `Image.sprite`, so a runtime-tinted `Sprite` works there. The map/minimap path is different: `IconSet.Create` (from `GameMain.CreateIconSet`, after LDBTool's post-add) keys a dictionary on `iconSprite` but blits **`iconSprite80px`** into a shared 625-slot atlas, then exposes `veinIconIndex[protoId]` to shaders as `_Global_VeinIconIndexBuffer`. `VeinProto.Preload` fills `_iconSprite80px` from `IconPath + "-80px"`, so a cloned proto inherits the source's 80px art. Don't assume one assignment covers both.

**Icons come from one of two sources, and the config picks per item.** A `ores.json` entry with `icon` / `ingotIcon` set gets `IconPath = "Assets/projecteden/<name>"`; `TextureResourcesPatches` prefixes `Resources.Load` for that prefix and serves the embedded `assets/icons/<name>.png`, so `ItemProto.Preload` loads `_iconSprite` on its own — **`TintIcons` must then leave that proto alone**, or it overwrites a perfectly good icon with a tinted iron one. Everything without a custom icon falls through to the recolor path below. Molecular species are drawn as ball-and-stick structural formulas through one shared helper (`_molecule` auto-centres and auto-fits, so moving an atom needs no transform retuning); the palette is CPK with **carbon lightened** — true CPK carbon is near-black and vanishes on DSP's dark UI — bonds lighter than either atom, and atoms spaced far enough apart that double/triple bonds are not hidden behind the spheres. `tools/make_icons.py` authors the custom ones as vector (`drawsvg` → SVG → `resvg-py` → PNG, 80×80 for items and 480×480 for veins, matching GenesisBook's own split); on Windows `cairosvg` and `reportlab.renderPM` are both dead ends because they need a `libcairo-2.dll` that pip cannot supply — pycairo statically links cairo into its `.pyd`, so cairocffi never finds it.

**Icons are otherwise recolored from iron's at runtime** (`Utils/IconTinter.cs`), so no art ships for those. Vanilla icon textures are not readable, so the pixels are fetched by `Graphics.Blit` into a temporary `RenderTexture` + `ReadPixels` rather than `GetPixels` (which throws on them). Recoloring is HSV — hue replaced, saturation/value scaled, **alpha preserved** — plus a saturation floor, because the near-grey pixels in the iron icon have no hue to rotate and would otherwise stay grey. The source sprite is taken from the *vanilla* item, which is always preloaded, so the tint doesn't depend on this mod's own `Preload` timing; the handler is still registered after `MegaBuildingRegistry`'s, whose `ProtoPreload` would otherwise overwrite `_iconSprite` with iron's original.

### Alloys — `ores.json`'s `items[]` + top-level `recipes[]`

Nine alloys, no new registry: they are ordinary extra items with ordinary recipes. Two of the nine **map onto vanilla items instead of adding new ones** — 碳钢 *is* vanilla 钢材 (1103) and 钛合金 already exists (1107), so those two get only a `metals.json` row. Deliberately **no** second 钢材 recipe was added for "iron + coal": it would bypass vanilla's 3 iron : 1 steel and amount to a 3× buff. `MetalPropertyPatches` now cross-checks the config's `name` against the resolved proto's name, because a wrong hardcoded vanilla id would otherwise attach the four axes to the wrong item in silence.

Machine follows reaction class as everywhere else: alloying is melting and mixing, so it is the smelter — except **镀铬铜, which is electroplating and therefore 电化学厂 (type 9)**. Stoichiometry is by **mass fraction, not a balanced equation** (a solid solution is not a compound); every recipe's `//` says so and names the real grade it is derived from — Hadfield steel for 锰钢, **200-series stainless for 不锈钢** (which substitutes manganese for nickel, and the game has no nickel, so the config's "iron + chromium + manganese" lands on a real grade rather than an invented one), AISI 6150 for 铬钒工具钢, Stellite/Vitallium for 钴铬合金, a β-titanium grade for 钒钛合金.

**The four-axis table only means something once `cost` is applied.** A bare four-axis dominance scan over the 21 materials reports **13** dominated pairs and is useless; with the cost column it reports **one** — 钒块 (5) fully dominated by 不锈钢 (5) at equal cost — and passes the other 12 as legitimate depth. That single survivor has an easy fix (不锈钢 5 → 6, which is also what its inputs sum to), and it is the whole argument for the column: without it every deliberate tier looks like a bug.

### Cloned buildings — `src/MachineRegistry.cs`

**A new machine and a new recipe type are the same feature.** `UIRecipePicker.RefreshIcons` filters on a single value (`filter != 0 && filter != recipe.Type → skip`), and `filter` comes from the machine's `prefabDesc.assemblerRecipeType` via `UIAssemblerWindow.OnSelectRecipeClick`. **One machine, one type** — there is no "this machine accepts types A and B". So "a class of recipes only the new machine can run" means allocating a type number and pointing a machine at it. (Making one machine accept *several* types — e.g. 量子化工厂 doing both 化学 and 电化学 — would require patching the picker and every `assemblerRecipeType` reader; GenesisBook does exactly that via a `ContainsRecipeType` helper transpiled into `BuildingParameters` and friends.)

**No preloader needed, same as `EVeinType`.** `ERecipeType` is int-backed and `(ERecipeType)9` is valid unnamed; vanilla names only 1–8 plus 15 (`Research`), so 9–14 sit in a gap. `AssemblerComponent.SetRecipe` **does not validate the type at all** (its IL only checks `recipeId > 0` and a null lookup), so the type is a pure UI concern and no production logic changes.

**There is no ceiling on `ERecipeType`, and an earlier version of this file wrongly said there
was.** It claimed "only 14 remains" — which was counting unused values *below* `Research = 15`,
not measuring a limit. Every candidate gate was then checked against the shipped assembly, and
all four are open:

- **The enum has no `Max` sentinel** (unlike `EVeinType.Max`): members are `None = 0`,
  `Smelt = 1` … `Fractionate = 8`, then `Research = 15`. There is nothing to delete, so nothing
  for a preloader to do — this is the same test the top of the preloader section prescribes,
  and it passes on its first question.
- **Nothing is indexed by it.** A whole-assembly scan for a `RecipeProto.Type` /
  `PrefabDesc.assemblerRecipeType` read followed within a few instructions by `ldelem`/`stelem`
  returns exactly one hit, `UIRecipePicker.RefreshIcons` @007B — and that `ldelem.ref` is
  `recipeProtos[i]`; the `Type` read at @007C feeds a plain `bne.un` against `filter`.
- **Both switches fall through.** `RecipeProto.get_madeFromString` is a 16-target jump table and
  `ItemProto.get_typeString` is `sub 1` + a 15-target table, so anything out of range takes the
  default — which `RecipeTypeNamePatches` already postfixes.
- **`productionMask` is not a per-type bit.** `ItemProto.InitProductionMask` is a three-way
  classification (`Type <= 5 → |= 1`, `== 8 → |= 8`, `== 15 → |= 2`), so types 9–14 already get
  no bit and **16+ loses nothing further**. Its only reader is `UIReferenceSpeedTip` (参考速率).

So 16, 17, 18 … are exactly as usable as 14: take the next free number and move on. What a custom
type still costs is unchanged — `RecipeTypeNamePatches` for the two display strings, and
`machineTypeName` on a mega building.

**The process note is the point.** "Only 14 remains" was never measured; it was inferred from the
shape of the enum, and it sat here long enough to be quoted as a design constraint in a feature
doc. Same family as the `kMaxCargoFlowSpeedPerSecond` trap recorded under **Belt speed**:
*a number that looks like a limit is a claim until you find the code that enforces it.*

Only two strings leak: `RecipeProto.madeFromString` (a recipe's 制造于) and `ItemProto.typeString` (an item's 类型 row) both `switch` over known types and fall through to a default. `RecipeTypeNamePatches` fixes both with getter postfixes — the same approach as GenesisBook's `DisplayTextPatches`.

**A retracted claim, and it cost a round: `UIReplicatorWindow` does NOT gate on `Handcraft`.**
This file used to say "the replicator gates only on `RecipeProto.Handcraft`", and the phrasing
was taken at face value when the organic phase-one recipes appeared to be missing.
`RefreshRecipeIcons` was then read, and its skip conditions are, in order:
`GridIndex < 1101` (@0062), `!RecipeUnlocked(ID) && !isInstantItem` (@0075), `row < 0 || row >= 8`,
`col < 0 || col >= 14`, and `GridIndex / 1000 != currentType` (@00FA). **`Handcraft` is read at
@00C1 and its only use is @0122**, where it picks `recipeStateArray[idx] = Handcraft ? 0 : 8` —
a *display state*, not a skip. So every one of this repo's 80 `ores.json` recipes **is** drawn on
the mod's own tab; they are simply dimmed and un-clickable, exactly as vanilla draws
塑料 / 硫酸 / 石墨烯. That is vanilla-consistent and not a defect.

The lesson is the one already recorded for `kMaxCargoFlowSpeedPerSecond` and for "only 14 remains":
**a sentence in this file is a claim until the IL is re-read.** A field being *read* a few
instructions before a `brfalse` does not make it the thing the `brfalse` tests.

**And that retraction was itself only half right — it cost a round of dead code.** It says
"`UIReplicatorWindow` does NOT gate on `Handcraft`", which is true of `RefreshRecipeIcons`
(the **drawing** path, where the flag only picks dimmed vs. not) and **false of
`OnOkButtonClick`** (the **acting** path), which gates on it hard:

```
0138: ldfld RecipeProto::Handcraft
013D: brtrue.s IL_016B            // true → carry on
013F: ldstr "该配方" … "生产"       // false → popup
016A: ret                         // ← returns BEFORE AddTask at IL 01DE
```

So a `Handcraft = false` recipe is drawn, is selectable, and **cannot be crafted** — the click
pops 「该配方 X 生产」 and returns. Since `OreRegistry` writes `Handcraft = false` for every
`ores.json` recipe, that covers all 80 of them.

The cost: a branch was added to `QualityCraftPatches` to give hand-crafted **refine** recipes
their tier's quality, on the reasoning that the machine path and the hand path would otherwise
disagree. The reasoning was sound and the premise was false — refine recipes are `Handcraft =
false`, so the branch could never execute. It was deleted, and the finding is recorded at the
site.

**The general shape, which this file already states one section down and this violated anyway:
an early guard that returns for one shape does not mean no later guard handles the others — and
a conclusion drawn from one method does not transfer to its sibling.** "Does this window gate on
X" has to be answered per method, by enumerating the exits of the method that actually acts.

**A third `kind`: `accumulator`.** Clones 蓄电器 (2206) and scales only `maxAcuEnergy` / `inputEnergyPerTick` / `outputEnergyPerTick`, carrying `isAccumulator` and `subId` across — `CreateEntityLogicComponents` gates on the former and `PowerSystem.NewAccumulatorComponent` reads all of them straight off `PrefabDesc`. **The config gives multipliers, never absolute joules**, because those values live in the prefab inside `resources.assets`: they cannot be read offline or by decompiling, so a hardcoded number would be a guess. `ApplyAccumulator` logs the resolved absolutes (GJ / MW) so they can be pinned if ever needed. A newly built accumulator always starts at `curEnergy = 0` — vanilla behaviour, not a bug.

**The "full" variant is an item, not a building, and the empty↔full pairing lives on the *exchanger*.** 蓄电器 (2206) and 蓄电器（满）(2207) share one `ModelIndex` (46) and therefore one `PrefabDesc`; they differ only in `BuildIndex` (2207 is **0** — it takes no build-bar slot and is placed from the inventory), their `GridIndex`, and `FuelType` + `HeatValue` (the full one is mecha fuel). `MachineFullVariantEntry` reproduces exactly that, and derives the heat value from the capacity multiplier rather than hardcoding it.

What converts one into the other is the 能量枢纽, and **`PowerExchangerComponent.emptyId` / `fullId` come straight from `PrefabDesc.emptyId` / `fullId`** — the whole component (belt I/O, state machine, energy settlement) knows only those two ids, so **one exchanger serves exactly one pair**. Hence `kind: "exchanger"`: cloning a second exchanger pointed at the new pair needs no patch at all, and is how vanilla itself models the concept. Its `pairMachineKey` resolves against machines registered earlier in the same pass, so the accumulator entry must precede the exchanger entry in `machines.json`.

**A fifth `kind`: `generator`.** Clones a power building and scales only `genEnergyPerTick` / `useFuelPerTick`; 风力发电机集群 is 风力涡轮机 (2203) at ×1000, i.e. 300 kW → 300 MW. `PowerSystem.NewGeneratorComponent` copies `photovoltaic` / `windForcedPower` / `gammaRayReceiver` / `geothermal` / `genEnergyPerTick` / `useFuelPerTick` / `fuelMask` / `powerCatalystId` out of `PrefabDesc` field by field (IL 0090–0195), so **`ApplyGenerator` re-copies every one of those explicitly** — miss one and you get a power plant that generates nothing, with no error anywhere. There is no clamp on the output: the wind branch is the whole of `EnergyCap_Wind` (`capacityCurrentTick = (long)(windStrength * genEnergyPerTick)`), and `genEnergyPerTick` is Int64. Multipliers, not absolutes, for the same `resources.assets` reason as the accumulator; the tooltip's 发电功率 row needs nothing — `ItemProto.GetPropValue` reads `prefabDesc.genEnergyPerTick × 60` directly. The source id is not a guess: `ACH_ThereIsNoWind.OnBuild` hardcodes 2203, and the registry additionally refuses a source whose `isPowerGen` is false. Since the cluster is one entity, it is also one grid connection and one collision box — the 1000× is in the power, not the footprint. **Verified in game**: it registers as `发电 300 kW → 300 MW（×1000），无燃料`, and placing one next to another turbine raises `WindTooClose(6)` — which only fires on `prefabDesc.windForcedPower`, so the clone provably carried the wind flag across rather than silently becoming an inert generator.

**`recipeHandcraftOnly` = the recipe's `Type` is `None`.** The replicator lists a recipe whatever its `Handcraft` is, while `UIRecipePicker.RefreshIcons` skips on `filter != 0 && filter != recipe.Type` — and no machine's `assemblerRecipeType` is ever 0, so a Type-0 recipe is listed for hand-crafting and selectable by nothing. The one measured cost: `ItemProto.InitProductionMask` opens with `if (recipe.Type == 0) continue;`, so the product gets no `productionMask` bit — and the **only** reader of that bit in the whole assembly is `UIReferenceSpeedTip` (参考速率), which a handcraft-only recipe has no business appearing in anyway. Every other `RecipeProto.Type` test in the UI is `== 8` (分馏), so 0 is inert there.

**Only 小型速采机 still sets it.** 风力发电机集群 and 可燃性液体发电厂 were flipped to `false` (type 4, Assemble) by owner decision — clicking out a thousand turbines one at a time is not what a factory game is for. **Hand-crafting is not lost by flipping it**: `Handcraft = true` is written unconditionally in `MachineRegistry`, so the switch only controls `Type`, i.e. whether any *machine* may also select it. The `productionMask` cost above reverses with it, which is the right way round — a recipe that can be automated belongs in 参考速率. 小型速采机 keeps it because it is the first miner of a run and must not wait on a production line existing.

Two things it did need: `madeFromString`'s 0 branch returns a bare `"-"` (`RecipeTypeNamePatches` now says 手动合成), and **`MachineRegistry.RecipeTypeMachineName` had to start rejecting 0** — `station` / `accumulator` / `exchanger` entries never set `recipeType`, so that field defaults to 0 and any Type-0 recipe would otherwise have reported 「制造于 综合物流枢纽」.

**`megaTab: true` puts a `machines.json` building on the mod's own tab.** Neither number it needs can live in the JSON: the replicator page index is handed out by CommonAPI at Awake (`MegaBuildingRegistry.TabIndex`) and the build category is configured in `megabuildings.json`. So the entry gives only `gridRow` / `gridCol` / `buildSlot`, and `MachineRegistry.WantedGrid` / `WantedBuildIndex` fetch the rest — which works only because `MachineRegistry.OnPreAddData` is registered after `MegaBuildingRegistry`'s.

**That makes the mega tab's slot and grid space shared across two config files, and nothing in either one says so.** 风力发电机集群 sits at build slot 7 / grid column 7 from `machines.json`; the seventh mega building was first written to the same pair in `megabuildings.json` and had to be moved to 8. The reservation ledger would have shifted one of them silently, and the visible result of a real collision is `UIBuildMenu.StaticLoad` overwriting `protos[category, slot]` — **one building simply missing from the build bar, with no error**. Check both files before picking a slot; `megabuildings.json`'s `//slot` on that entry says so at the point of use.

**And "check both files" is too weak, because the same fact is stored under a different KEY in each
file.** This shape has now cost three separate mistakes in one session, all of them the same move —
grep one spelling, read the result as the whole occupancy table, ship a collision:

| the fact | spellings that write it | what was missed |
|---|---|---|
| model id | `modelId` (megabuildings, machines) · **`veinModelId`** (ores) | four new buildings displaced 5 veins + 9 machines, a 14-deep cascade |
| build slot | `slot` (megabuildings) · `megaTab` + `buildSlot`, `buildIndex` (machines) | slot 13 collided with 小型速采机; the log's 建造栏核对 lists **only** `megabuildings.json` |
| replicator cell | `gridIndex` (ores, machines) · **`gridRow` + `gridCol`** (megabuildings, recipes) | cell 3201 collided with 原油X射线裂解 |

**None of the three errors, and no diagnostic that exists, announces itself as a collision.**
`ProtoSlots` shifts the loser and logs 「改用 X」 — which reads as a successful fallback and is
really an unstable id (the rule two sections up: **pin it the first time you see that line**).
The build-bar checker only knows one file. So the test cannot be "find the authoritative list",
because there isn't one; it has to be **enumerate every spelling that can write this number**.
`tools/check_slots.py` does that for all three, and the table above is the list to extend when a
fourth spelling appears. Note it deliberately keeps **item and recipe grids separate** — they are
two independent grids (`ProtoSlots.GridKind`) and may legally share a cell, so merging them
reports false positives, which is its own way of making a real collision invisible.

**And a fourth mistake followed immediately, from the fix for the third: pinning a number is
worthless if the pin is filed later than someone else's auto-assignment.** 小型速采机's item cell
was moved off a real collision onto a cell `check_slots.py` reported as free — and the next launch
printed the *same* 「已被占用，改用 3807」 as before. The cell had been taken in between:

```
288| 钒渣油的物品格位 3601 已被占用，改用 3207      ← ores.json, gridIndex 0 = "resolver picks"
419| 钴块 · 甲醇还原的配方格位 3601 已被占用，改用 3207
695| 小型速采机的物品格位 3207 已被占用，改用 3807   ← the pin, 400 lines too late
```

`PreAddDataAction` order is **mega buildings → `ores.json` → drill bits → `machines.json`**, and
`ores.json` has a batch of items whose `gridIndex` is **0**, meaning *"let the resolver pick"* — and
the resolver scans the visible band from the start. `MachineRegistry.OnPreAddData`'s `ReserveGrid`
call files **the resolved result**, i.e. it runs after the cell is already gone. So the ledger is
doing its job and the pin still loses, because the ledger only ever knew about it too late.

Fixed by `MachineRegistry.PreReserveGrids`, a handler that **registers nothing and only files
claims**, hooked between the mega buildings and `ores.json` in `Plugin.cs`. It files only cells the
config *explicitly* pins (a `megaTab` `gridRow`/`gridCol`, or a non-zero `gridIndex`) — never
`WantedGrid`'s fall-back to the source building's vanilla cell, which is occupied by vanilla anyway
and would just add a spurious duplicate warning. Filing does not move anything (`ReserveGrid` only
reports), so a genuine pin-vs-pin collision still surfaces as the loud warning it should be.

**And that fix made things strictly worse before it made them better, because a pre-reservation is
self-referential.** The very next launch printed both halves side by side:

```
279| machines.json 手工钉死的合成面板格位已提前登记 2 个
696| 小型速采机的物品格位 3207 已被占用，改用 3809
```

The thing occupying 3207 **was us**. `ResolveGridIndex` calls `GridTaken`, whose first line
consults the reservation set — so the pin filed on the claimant's behalf blocked the claimant. The
cell went to nobody: not to the auto-assignment it was fenced off from, and not to the machine it
was fenced off for. And the two wasted cells pushed every later auto-assignment along, taking the
「超过了 14 列」 warning from **1 to 3** — 双元推进剂 and 金属浆料燃料 moved past column 14, which
is to say they stopped existing in the loot filter and both signal pickers.

`ResolveGridIndex` now takes `mine`, which exempts **only the ledger half** for exactly that one
cell (a real proto sitting there in LDB still moves it), and `ReserveGrid` lets the same owner
re-file its own cell silently, since pre-reserve and register-time both file it by design.

**The general shape: a two-step "claim then acquire" flow is self-blocking unless the acquire step
says who it is.** Claiming early is the whole point, and the acquire step's occupancy test is the
claim table — so the earlier the claim, the harder it blocks its own owner, and **it does not
error**: it reads as an ordinary fallback. Same family as *a scrub is not coverage* — a mechanism
that does not mark what it itself wrote cannot tell "someone else left this" from "I just left
this".

**The general rule: a reservation ledger orders claims by when they are filed, not by whether they
were written in a config file.** When a registry pins a value, it has to file that value *before
every registry that auto-assigns into the same space* — which for this repo means a separate
claim-only pass, because the registry that pins runs last. Same family as the three above, one
level in: not "which key holds the number" but "when does the ledger learn about it".

**Mecha fuel has a power multiplier as well as an energy total.** `Mecha.GenerateEnergy` computes `ratio = ItemProto.ReactorInc + 1` (then folds in the proliferator table) and multiplies `reactorPowerGen` by it, so `ReactorInc = 1.5` means **+150% power**. It scales *rate*, not *total* — `HeatValue` is still what determines how long one unit lasts, so a high `ReactorInc` drains each unit faster.

**The vanilla spread that used to be quoted here was wrong in four of five entries, and it had been
quoted onward into `machines.json` and both feature guides.** It is now measured, by
`FuelSurvey.DumpFuelLadder`, which dumps every item with `FuelType != 0` — heat value, `ReactorInc`,
which generators accept it, and its own producing recipes:

| | claimed | **measured** |
|---|---|---|
| 原油 | −0.5 (×0.5) | **+0.2 (×1.2)** — even the sign was wrong |
| 蓄电器（满） | 1.0 | 1.0 ✓ (the only one that was right) |
| 氢燃料棒 | 1.0 | **2.0 (×3)** |
| 氘核燃料棒 | 2.0 | **3.0 (×4)** |
| 反物质燃料棒 / 金色燃料棒 | — / 9.0 | **5.0 (×6) / 11.0 (×12)** |

精炼油 is 0.3 (×1.3). The consequence was not academic: `锂电池蓄电器（满）` was tuned to ×2.5 and
both guides said that "sits between 氢燃料棒 and 氘核燃料棒" — against the real numbers (×3 and ×4)
it sits *below* 氢燃料棒, between it and the vanilla accumulator.

**Do not select fuels by name.** The survey's discriminator is `FuelType != 0`, deliberately: the
first draft was going to dump "the four fuel rods", which needs a name test, and this repo has paid
for "select by name, miss by name" five times (`_stack`, `itemInc`, `cacheCargoInc1`, auto-property
backing fields, …). `FuelType != 0` *is* the fact "can this be burnt", it cannot miss a fuel that
lacks 棒 in its name, and it prints the whole ladder so "which rung does a new fuel land on" is
answerable without a second launch.

**Two things that ladder made visible on its first run**, neither of which was being looked for:

- **`EnergyAudit` structurally cannot see a vanilla recipe**: it walks `ores.json`'s own recipe list,
  so every vanilla recipe is outside its scope — while this mod's 10000× mega assembler will happily
  run one. **The green line means "no mod recipe mints energy", not "nothing mints energy".**
  **Closed in 1.10.6 by `EnergyAudit.AuditMegaVanilla`**, a second pass that walks `LDB.recipes`,
  keeps the ones whose `Type` any mega building can run (each building's own `recipeType` plus its
  `acceptsRecipeTypes`), skips this mod's own (`ProtoSlots.OwnRecipeIds`, already covered by the
  first pass) and reports the positives — top 5 only, because a screenful of warnings is the same as
  none. It **reports and does not block**: vanilla's balance is not this mod's to enforce, and the
  real lever (which types a mega building may run) is an owner decision. The trigger was admitting
  refine to 综合化学厂 — **when you widen what a mega building may run, you widen exactly this
  blind spot**, so the edit had to carry the check.
- **And this mod's `vanillaHeat` override turned a mild vanilla surplus into a large one, by exactly
  the mechanism it was added to fix.** 氢燃料棒 is `钛块×1 + 氢×10 → ×2`, i.e. 108 MJ out. In vanilla
  that is 90 MJ in (hydrogen at 9.0) — **+18 MJ, a 1.2× surplus, unremarkable**. Re-anchoring
  hydrogen to 1.96 MJ to stop steam reforming minting energy drops the input to 19.6 MJ, so the same
  untouched vanilla recipe becomes **+88.4 MJ, a 5.5× multiplier**. Titanium gates the throughput, so
  it was a "titanium → electricity" converter at roughly 66 MJ per ingot rather than true perpetual
  motion — but the shape is the point: **re-anchoring one item re-prices every recipe that touches
  it, in both directions, and only the ones inside the audit's scope get re-checked.**
- **Fixed by `recipes.json`'s `vanillaEdits`, which gained `setCount` for it** (hydrogen ×10 → ×56,
  = `ceil(108 / 1.96)`, rounded up so the delta lands **negative** at −1.76 MJ rather than positive).
  `setCount` writes an **absolute** count, so it is idempotent across `PostAddDataAction` re-runs, and
  it changes a *value* not an array *length*, which is the save-safe half of that distinction. The
  cost is stated rather than hidden: **a hydrogen fuel rod now costs 5.6× the hydrogen** (5 → 28 per
  rod).
- **And the edit reports its own energy balance, because nothing else will.** `ReportEdit` prints
  burnable-in / burnable-out / delta for every vanilla recipe this mod touches, and WARNs on a
  positive delta over 1 MJ. That is the general rule this whole episode produced: **when you edit
  something outside a checker's scope, the edit site has to carry the check.**
- **The arithmetic above was wrong the first time it was written here, and the error is instructive:
  it used 8.0 MJ for vanilla hydrogen** — the figure this file, `ores.json` and `OreConfig.cs` had all
  been repeating — which made the vanilla surplus read as +28 MJ instead of +18. The measured value
  is **9.0**, and it was in the log the whole time, because `ApplyVanillaHeat` prints the value it
  actually read before overwriting it. *When a log line reports the number, do not restate it from a
  comment.*

**Burning a cloned "full" item does not return its shell without a patch.** The same method hardcodes the pair: `if (reactorItemId == 2207) player.TryAddItemToPackage(2206, 1, …)`. A cloned full accumulator is not 2207, so it is consumed outright — silently throwing away the whole build cost each time. `MechaFuelShellPatches` transpiles it the usual way: normalise the *read* of `reactorItemId` so any mod full variant reports as 2207, and replace the two hardcoded `2206` pushes with a lookup keyed on the current fuel (`reactorItemId` is still the full item at that point — it is only overwritten later, at IL 0x01F8). Values are filtered as they are read; nothing in `Mecha` is written.

Worth remembering as a search habit: the empty/full link is *not* discoverable by grepping IL for 2206/2207 — those literals appear only in `Mecha.GenerateEnergy` and the mecha window. The link is data in `resources.assets`, reachable only by finding the fields (`PrefabDesc.emptyId`/`fullId`) that read it.

**Build recipes in `machines.json` take `ref` as well as `id`.** `id` means a vanilla item; `ref` resolves through `OreRegistry.FindItemIdByRef` (an `items` key, or `<ore key>.ore` / `.ingot`). **Never write a raw id for a mod item here** — `ResolveItemId` shifts on collision and a hardcoded number then silently points at someone else's proto, producing a recipe with the wrong ingredient and no error.

**`machines.json` entries come in two kinds.** `assembler` (the default) is the above: a new `ERecipeType` plus a machine pointing at it. `station` clones a logistics station instead and only retunes it — **the planetary drone and the interstellar vessel are two halves of one `StationComponent`** (`idleDroneCount` / `idleShipCount`), gated by `prefabDesc.isStellarStation`, so cloning an interstellar station carries both across with no logic at all; 综合物流枢纽 is that. **`stationMaxShipCount` cannot exceed 64** — `idleShipIndices` is a `UInt64` bitmask indexed `1L << (index & 63)` — and the registry clamps it with a warning. Capacity, slot count and charging power are left at 0 and picked up by `StationCapacityPatches`, which consumes `MachineRegistry.StationItemIds` alongside the mega buildings.

The building itself is a whole-building clone of a vanilla one (化工厂 item 2309, 星际物流运输站 item 2104): ModelProto, materials (copied then tinted, or the vanilla building's own materials change), colliders, belt slots, speed and power all carry over untouched; only name, colour, icon (recoloured by `IconTinter`) and `assemblerRecipeType` differ. `BuildIndex` = category × 100 + slot — the resolver stays in the source building's category and finds a free slot, because changing category moves the building to a different build tab.

### UI grids: replicator, recipe picker, build menu — `src/Patches/UI/`

Four windows draw a grid addressed as `page × 1000 + row × 100 + col`, and **each one re-implements the clipping**. Vanilla wrote them all against two assumptions that stop holding the moment a mod adds protos: *the contents of a page never change*, and *nothing lives past column 14*. Every bug in this area has been one of those two, and none of them surfaces as an error.

| Window | What it draws | Patch |
|---|---|---|
| `UIReplicatorWindow` | recipes, by `RecipeProto.GridIndex` | `ReplicatorExpandPatches` |
| `UIRecipePicker` | recipes of one `ERecipeType` | `RecipePickerExpandPatches` |
| `UIBuildMenu` child row | buildings, by `ItemProto.BuildIndex` | `BuildMenuScrollPatches` |
| `UIItemPicker` | items, by `ItemProto.GridIndex` | `ItemPickerExpandPatches` + `ItemPickerSearchPatches` |
| `SetSelectedRecipe` (focus) | jumps the replicator to one recipe | `ReplicatorExpandPatches` |

**The item grid and the recipe grid are two different grids, and conflating them hides every mod item.** `ItemProto.GridIndex` and `RecipeProto.GridIndex` are drawn by disjoint sets of windows and may freely share a cell — vanilla itself puts 铁块's item and 铁块's recipe on the same number. `ProtoSlots.GridTaken` originally scanned **both** proto sets, which sounds safe and is not: replicator page 1's *recipe* grid is essentially full, so every mod **item** was pushed past column 14. The four windows that draw the item grid —

| Window | What opens it |
|---|---|
| `UIItemPicker` | inserter filter, station slot, storage filter |
| `UILootFilter` | loot filter |
| `UISignalPicker` / `UISignalTagPicker` | signal icon pickers |

— all clip `col >= 14` in `RefreshIcons`. The signature is *"the recipe shows up in the replicator but the item is nowhere"*, which reads like a missing icon and is not. `GridTaken`/`ResolveGridIndex`/`ReserveGrid` and both registries' `pending` predicates now all take a `ProtoSlots.GridKind`.

**Two more bugs sat in the same search loop, and only logging the outcome found them.** It was a single row-major pass to `maxCol + ExtraCols`, so row 1 was scanned all the way to column 42 before row 2 was tried at all — the first mod item went to column 15 while seven rows of visible cells sat empty. And the row/column bounds came from **measuring existing protos** rather than from what the renderer will draw: a page whose vanilla items stop at row 6 measures `maxRow = 6`, and rows 7–8 are never offered even though `RefreshIcons` clips only at `row >= 8`. Fixed by scanning the **visible band (cols 1–14 × rows 1–8) first** and only then spilling into the extended columns, with `VisibleRows`/`VisibleCols` as hard constants.

**And then the measurement said it does not matter: vanilla's item page 1 is 111/112 occupied.** After both fixes exactly one visible cell was free (row 7, col 7) and the remaining 33 mod items still overflowed. So paging is not a fallback for the item grid, it is the only answer — `ItemPickerExpandPatches` gives `UIItemPicker` the same one-instruction column offset as the replicator, plus wheel + `◀ ▶` buttons, and **goes completely inert when nothing overflows** (pages are computed from the real max column). **Paging alone turned out not to be enough in practice — see the search box below.** `UILootFilter` and the two signal pickers are still unpatched on purpose; `ResolveGridIndex` **warns by name** for every item past column 14, and that warning names them. (Checked while diagnosing: `LDBTool.CustomGridIndex.cfg` records **0** for all 85 mod protos, and 0 means *no override* — unlike `LDBTool.CustomID.cfg`, it was not what pinned these values.)

**Paging is reachable but not usable, and a 30-slot station is what proves it.** The mod's items land in columns 15–42 of item page 1 and 15–22 of page 2, i.e. three horizontal pages. Column paging makes every one of them *selectable*, and it still reads to the player as "I cannot pick the new items": a logistics station has 30 slots to configure, and hunting each one across three horizontal pages of a 14-column grid is not a workflow anybody completes. `ItemPickerSearchPatches` therefore adds a bottom toolbar to `UIItemPicker` — a search field plus the page controls — and it is the search field that actually answers the complaint. All three station kinds (行星内 / 星际 / 综合物流枢纽) share this one window: `UIStationStorage.OnSelectItemButtonClick` is a plain `UIItemPicker.Popup(pos, OnItemPickerReturn)`, so there is exactly one place to fix. The inserter filter and storage filter come along for free.

**Search works by abandoning the grid coordinate system entirely, and that is the whole trick.** A `RefreshIcons` **prefix** fills `indexArray` / `protoArray` itself and returns false, placing matches **sequentially from cell 0** instead of at `row × 14 + col`. `GridIndex` stops mattering, so "past column 14" stops being a category that exists — no offset arithmetic, no interaction with `ExtraCols`. Nothing downstream needs changing: `TestMouseIndex`, `hoveredIndex`, the hover tip and `OnBoxMouseDown` all address `protoArray` by that same sequential index. Search deliberately **ignores `currentType`** and spans both tabs — a player looking for 二氧化碳 should not have to know which tab owns it — and it reproduces vanilla's unlock gate (`showAll || history.ItemUnlocked(id)`) verbatim, or the two modes would disagree about what exists.

**Use a real `UnityEngine.UI.InputField`, because that is what suppresses the game's hotkeys.** `VFInput.UpdateGameStates` derives `VFInput.inputing` every frame from `EventSystem.current.currentSelectedGameObject.GetComponent<InputField>() != null` (IL 013F–0162) — nothing registers, nothing subscribes. So a stock `InputField` gets hotkey suppression for free, and a hand-drawn text box would have to maintain that flag itself. The cost of `inputing` being true is that the game's own Esc handling stops responding, so the patch handles Esc explicitly (deactivate, then `UIItemPicker.Close()`); Enter takes `protoArray[0]`, which is the fast path when the query matches one item.

**The toolbar is positioned from world corners, every frame, and both halves of that are deliberate.** Anything drawn below the grid gets clipped away entirely if an ancestor carries a `Mask`/`RectMask2D` — "the log says it was built and the screen shows nothing", and whether the prefab has one cannot be read offline — so `ResolveHost` walks up and re-parents above any clipping ancestor, naming it in the log. Position then comes from `GetWorldCorners` converted into the host's local space, anchored to the host's bottom-left so the host's own pivot cannot skew it; copying the grid's `anchoredPosition` instead is what produced the giant off-window rectangle when the replicator scrollbar was first built. It is recomputed every frame because this is a **popup that moves** (`UIStationStorage` places it 300px left of the station window) and because layout is not necessarily settled on the frame the toolbar is created.

**The old `Scrollbar` is gone.** It was a 12px strip whose `value` was rewritten every frame from `_page`, i.e. the third time this repo fought a Unity widget for ownership of a value (build-menu scrollbar, alloy slider). `_page` is now the single source of truth and the only way in is `SetPage`.

**Column paging (replicator, recipe picker).** `ResolveGridIndex` pushes mod protos past column 14 because replicator page 1 is essentially full, and every renderer clips `col >= 14` on its own. The fix is one injection: subtract a page offset right after `col = GridIndex % 100 - 1`, and vanilla's own guard clips what falls outside. Hit-testing (`TestMouseRecipeIndex`, `TestMouseIndex`) works in **visible** grid coordinates, the same space the write path now uses, so it needs no change. Patching only the replicator left the picker clipping the very same recipes — a custom machine then shows an empty picker on every tab, which reads exactly like *the recipe isn't attached to the machine*. If a fifth window ever draws this grid, it needs the same treatment.

**Page number is the tab.** `ResolveGridIndex` must never roll onto the next page: page 1 is the items tab, page 2 buildings, page 3 this mod's CommonAPI tab. It also cannot add **rows** — `if (row >= 8) continue;` — so it extends columns only (`ExtraCols = 28`).

**Focus (`SetSelectedRecipe`).** `UIBuildMenu.OnChildButtonClick` (double-click) then `UIGame.FocusOnReplicate(id)` then `SetSelectedRecipe(item.maincraft)`, which gates on `bool ok = (page == 1 || page == 2)` plus `col >= 14`. Both fail here: mega-building recipes sit on page 3, everything else in the extended columns. When `ok` is false it calls `SetSelectedRecipeIndex(-1)` and **does not even switch tabs**, so the replicator opens with nothing selected. The prefix takes over only what vanilla cannot express and returns true otherwise. (`item.maincraft` is fine — `ItemProto.FindRecipes` accepts any recipe listing the item in `Results`, no `Handcraft` needed.) Harmony injects by **parameter name**: the real signature is `SetSelectedRecipe(RecipeProto recipe, bool notify)`.

**Build-menu slots are backed by real buttons, and nothing checks that.** `UIBuildMenu.StaticLoad` fills `protos[BuildIndex / 100, BuildIndex % 100]` for any item (category <= 15, slot <= 12), but `_OnUpdate` then does `childButtons[j].tips.itemId = ...` with **no null check**. `childButtons` is a fixed set laid out in the prefab and **shared across categories**. Park an item on a slot with no button and the build bar throws `NullReferenceException` every frame it is shown, with a stack naming only `_OnUpdate` — it looks like a transpiler bug, not a data problem.

**Child-row scrolling moves data instead of re-indexing reads** (`BuildMenuScrollPatches`). The obvious approach — offset `protos.Get(currentCategory, j)` while leaving `childButtons[j]` alone — means finding *which* of `_OnUpdate`'s several `protos.Get` calls belong to the child loop (others scan categories, same shape, different meaning), then keeping `OnChildButtonClick`, the F1-F12 mapping and the icon pass in sync; miss one and the bar throws every frame. Instead this keeps its own `_full[category, slot]` table (slots to 36) and, in an `_OnUpdate` **prefix**, copies the current window into `protos[category, 1.._visible]` and nulls the rest. Vanilla reads its own array as always, so clicks, hotkeys, tips and double-click-to-replicator line up with no further patching — and since the writer never fills past the last real button, the null-button crash becomes structurally impossible. `_visible` is measured from `childButtons`, not configured. `ProtoSlots.ResolveBuildIndex` may therefore use any slot up to `BuildMenuScrollPatches.MaxSlot`; `BuildMenuSlotGuardPatches` stays as a second line, clearing any `protos` cell whose button is null and naming the culprit.

**That guard's warning is expected on every launch, and does not mean the building is missing.** `UIBuildMenu.StaticLoad` fills `protos[category, slot]` straight from `BuildIndex` before the scrolling prefix ever runs, so any slot beyond `_visible` (10 buttons here) briefly holds a proto with no button behind it — the guard clears that cell and logs `建造栏第 N 类第 M 槽放着「X」，但界面在那个位置没有按钮`. The scrolling patch keeps its own `_full[category, slot]` table, so the building is still reachable by scrolling the child row. **Verified in game: 电化学厂 sits at `BuildIndex 511` (slot 11 > 10 buttons), warns at load, and is findable in build category 5.** Treat the warning as a snapshot of the pre-scroll state; it is only a real defect if the building cannot be scrolled to, which is what the message's own last clause says to check.

**Then the two "fixed list" assumptions bite, in order.** Both were mistaken for input bugs:

- **A slot that empties is never cleaned up.** The child loop opens with `if (protos[currentCategory, j] == null) continue;` — the button keeps whatever it showed last frame. Only *proto exists but locked* clears tips and does `SetActive(false)`. Flipping from a full page to a sparse one therefore leaves surplus buttons on screen and reads as *paging did nothing*, while flipping back refreshes every slot and looks right. **That asymmetry is the tell that the fault is in the display, not the input.** `HideButton` replicates vanilla's hide branch.
- **The icon is initialised once.** `if (childIcons[j].sprite == null)` — correct for a fixed list, wrong the moment contents change. The tell is that name, count and click behaviour are all right (rewritten every frame) and only the picture is stale. `SyncIcon` assigns from the current proto.

**Do not fight a Unity widget for ownership of a value.** The child-row scrollbar was a `UnityEngine.UI.Scrollbar` whose `value` was rewritten every frame from `_page`; its internal drag/click state machine and that write fought, and the symptom was that it would page back but not forward. It is now two plain `Image`s with the handle positioned from `_page` and all input polled directly, so `_page` is the single source of truth. Screen-to-rect conversion uses the game's own **`UIRoot.ScreenPointIntoRect`** — it goes through `overlayCanvas.worldCamera`, and hand-rolled `RectTransformUtility` calls silently measure nothing because `GetComponentInParent<Canvas>()` can return a nested canvas whose `worldCamera` is null.

### The exchanger's fifth prefab field — `MegaExchangerDefaultPatches` + `ApplyExchangers`

Reported as 「奇点储能厂用传送带导入电浆储能柜，无法进行充电」. **A field that was never
assigned, and it is the `ApplyGenerator` lesson word for word.**

`PowerSystem.NewExchangerComponent` reads exactly **five** `PrefabDesc` fields — enumerated, not
recalled: `subId` @0080, `exchangeEnergyPerTick` @0097, `maxExcEnergy` @00C1, `emptyId` @00D8,
`fullId` @00EF. `MegaBuildingRegistry.ApplyExchangers` set three of them and **never touched
`maxExcEnergy`**, so it came from the cloned 物流运输站, which is not an exchanger.

**What that field means is read out of the IL rather than inferred from its name.**
`PowerExchangerComponent.InputUpdate` @0023 is `if (thisTick < maxPoolEnergy − currPoolEnergy)
accumulate only`, and @0055 is `currPoolEnergy −= maxPoolEnergy` paired with `emptyCount−−,
fullCount++`. So it is **the energy needed to fill one accumulator**, and the correct value is that
accumulator's own `maxAcuEnergy`. (Empty and full variants share one `PrefabDesc` — recorded under
*Cloned buildings* — so reading it off the **empty** id is the same number.)

**The symptom depends on which way it is wrong, and only one direction matches the report.** Too
large → the pool never fills → vaults go in and none ever comes out, which is exactly 「就是不充电」.
Zero would have been the opposite (a free conversion every tick), so *the report itself is evidence
about the inherited value* — worth noting, because the field's real value lives in
`resources.assets` and cannot be read offline.

**Three consequences, all of which had to be handled together:**

- **`maxPoolEnergy` is saved** (`Export` @009A / `Import` @00AE) and `Import` does **not** re-derive
  it from the proto — unlike `StorageComponent.Import`, which is this repo's standing
  counter-example to trap 1. So already-built plants do **not** self-heal and need a runtime repair
  pass; it hangs off `GameData.Import`.
- **The tier switch has to carry it.** 奇点储能厂 serves two vault tiers whose capacities differ by
  2×, and the switch previously rewrote `emptyId`/`fullId` only. Same shape as the piler's four
  `4`s: half the state moved and nothing errored.
- **`maxCount` is a red herring** — enumerated, it has **zero accesses** in the whole assembly.
  Checking it cost one script and removed a plausible suspect.

**That was one half. The half the player actually hit is the belt ports, and the diagnostic built to
catch it measured the wrong field.**

`PowerExchangerComponent` has only `belt0..belt3`, and **`PowerSystem.SetExchangerBelt`'s first two
instructions are `if (slot < 0) return; if (slot > 3) return;`** (IL 0000–0008) — so a belt on the
fifth port or beyond is discarded before `AlterBelt` is even reached. `slot` *is* the index into the
building's belt-port array (`BuildTool.GetLocalPorts` @0089 returns exactly that array, and
`ReadObjectConn` indexes `entityConnPool[objId * 16 + slot]` with the same number). The mega chassis
is cloned from 物流运输站, which carries far more than four ports. Shipped symptom: the belt connects
on screen, `belt0..3` stay 0, nothing errors, and the plant never charges.

**The field names are inverted between the two types, and that is what made the 1.12.4 diagnostic
useless.** `PrefabDesc.ReadPrefab` @11AD writes `SlotConfig.slotPoses` into **`PrefabDesc.portPoses`**
(the belt ports) and @1211 writes `SlotConfig.insertPoses` into **`PrefabDesc.slotPoses`** (the
*inserter* poses). The dump read `slotPoses`, measured **0**, and its "WARN if > 4" check therefore
could never fire — **a measurement that cannot vary with the thing being measured is not a
measurement**, the third time this file records that shape.

**The fix is a remap, not a trim, and the safety argument is read out of the IL rather than assumed.**
A prefix on `SetExchangerBelt` moves a slot ≥ 4 onto a free component slot. The component slot index
carries no geometry — `InsertItemToBelt` / `PickItemFromBelt` take a `beltId` and `FindTheNextSlot`
just rotates through the four. Decisively, **`PowerSystem.DisconnectToExchanger` (IL 0029/0051/0079/
00A1) clears by `beltId`, comparing it against all four slots**, not by port index — so whichever slot
a belt lands in, removal still finds it. Trimming `portPoses` to 4 would also work and was rejected:
it takes effect immediately on already-built plants, silently invalidating belts the player has
already laid.

**And the remap alone cannot rescue an existing save, which is trap 1 again.** `belt0..3` are
per-component and **saved**, while `SetExchangerBelt` runs only on entity creation and on belt
connect/disconnect; `PowerSystem.Import` reads the stored 0 straight back and never re-derives. So
`RescueBelts` re-reads `entityConnPool` at load and fills the four slots from whatever is actually
connected.

**The dump prints every gate in the chain, not the suspected one** — mode, `networkId`, both item
ids with counts, `maxPoolEnergy`/`currPoolEnergy`, all four belts with their directions, and the
chassis port count. Five stages all present as "the belt delivered and nothing charges", which is
the courier-chain lesson: *state dump first, hypothesis second.*

**But the dump ran only at `GameData.Import`, so it could not answer the question it was built for.**
It reported `belt0..3 = 0/0/0/0` — which is equally consistent with "the belts are on dead ports" and
with "the player had not laid any belts at the moment of that load". A load-time dump is a *status*
line; what was missing was the *event* line, and on top of that the feature had **no startup status
line at all**, so "the patch is not wired up" and "nothing happened this session" were
indistinguishable. `MegaExchangerDefaultPatches.Report()` (registered after `PatchAll`, read out of
`Harmony.GetAllPatchedMethods()`) now answers the first, and the remap logs each move. **Seventh time
this file records it: the status line answers "is it wired up", the event line answers "what did it
decide", and neither substitutes for the other.**

**Note `PrefabDesc.slotPoses` being 0 on this chassis is a real and separate fact** — the mega
buildings genuinely have no *inserter* attachment poses. It is simply not the number that governs
belts, which is why reading it produced a green-looking diagnostic over a broken feature.

### The reference-rate panels quote a number the engine forbids — `src/Patches/UI/ReferenceRatePatches.cs`

Reported as 「生产和消耗对账对不上」 with a screenshot: 铁矿 produced **600 k/min**, consumed
**216 k/min**; 铁块 produced 216 k/min with a 参考速率 of **600 k/min**. **Every number was exact and
nothing was lost** — one 小型速采机 is `oresPerMinute: 600000` and one 冶铸熔炉 is 60 cycles/tick ×
3600 = 216,000/min, with the 384 k/min difference sitting in the panel's own 仓储数量 column.

**What made it read as a bug is that both 参考速率 cells said 600 k, so the pair looked like it should
balance.** One of them is real (the miner's) and one is fiction. `UIReferenceSpeedTip
.AddEntryDataWithFactory` @0191 is
`3600 × AssemblerComponent.speed / recipeExecuteData.timeSpend` — a formula that knows neither the
one-cycle-per-tick ceiling nor the output gate above. At `speed = 1e8` it reports **167× the truth**.

**The fix is a clamp, and it is unit-free because vanilla's own expression is already in the right
units**: `speed / timeSpend` *is* "how many cycles one tick's `time` increment covers", so
`min(rate, cap × 3600)` needs no new conversion and is **inert on vanilla machines**, which cannot
fill even one cycle per tick.

**Where to clamp is the whole design, and the obvious point is wrong.** After the `div`, vanilla folds
in the proliferator: `加速模式` does `rate *= accMulti` (@01FD) while `增产模式` does not. Acc buys a
mega building **nothing** (`speedOverride` is already orders above `timeSpend`), so clamping at the
`div` would be overridden by up to ×3.5, and clamping only in the acc branch would miss the other.
The clamp therefore goes **immediately before `rate × productCounts[j]`**, where the value is final in
both modes; `min` is idempotent, so patching every such use costs nothing.

**Five sites in two methods, and the count was measured before the transpiler was written** — the
repo's rule, and it mattered: enumerating `AssemblerComponent.speed` / `LabComponent.speed` reads
across the assembly gives 参考速率 3 (assembler consume / assembler produce / lab) and 理论产能 2
(assembler / lab), but those 5 sites hold **9** `counts` multiplications, because **the lab site is
used by both the consume and the produce side in each panel**. Patching "the first use" would have
made one panel disagree with itself. A PowerShell simulation implementing the transpiler's exact rule
— including its "each site owns the region up to the next speed site" bound — reported 3/4 and 2/4
offline; the patch asserts both and **applies nothing at all on a mismatch**, because a half-applied
display fix is harder to read than the original lie.

Three smaller things the sites forced:

- **All five component locals are `AssemblerComponent&` / `LabComponent&`** (checked, not assumed), so
  cloning the `ldloc` in front of `ldfld speed` yields a ready-made `ref` argument and no `ldloca` is
  needed. Had any been by value the emit would have had to differ per site.
- **Two of the five copy the per-minute value into a second local before folding in the proliferator**
  (`stloc X ; ldloc X ; stloc Y` — both `ProductionExtraInfoCalculator` sites), so the transpiler
  tracks that hop; anchoring on `X` would have found no uses there.
- **`PlanetFactory` sits at a different argument index in each method** (both are instance methods:
  `ldarg.1` vs `ldarg.2`), so it is located by scanning `original.GetParameters()` — the same thing
  `MinerProductStat_Transpiler` already does.

**Labs are clamped at 1 cycle/tick unconditionally**, which is the engine's own limit rather than a
mod-specific number: `GameLogic` has `_lab_produce_parallel` and no multi-cycle path, so a lab is
3600 crafts/min however high `lab.json`'s `assembleSpeed` goes.

### More than two products — `src/Patches/UI/MultiProductUIPatches.cs`

**Vanilla's product UI is two unrolled widget sets, not a loop, and this repo has ten recipes that need more.** `UIAssemblerWindow` carries `productIcon0/1`, `productCountText0/1`, `productProgress0/1`, `extraProductProgress0/1`, `productButton0/1`; `UIReplicatorWindow`'s crafting tree carries `treeMainIcon0/1` and `treeMainCountText0/1`. Nine `ores.json` recipes have three products and 铬块 · 烃热还原 has four, so the third one has no widget to be drawn into.

**It is not just a missing icon — it also lays the whole window out wrong, and that is the louder symptom.** The assembler window's three horizontal positions are constants chosen by product count, but the code branches only on `products.Length > 1`, so a three-product recipe gets the **two-product layout**: `productGroup` 128 wide, `speedGroup` pinned at x=144, `servingGroup` at x=224. The player sees the rate text and the ingredient box in the wrong place next to a product that is not there — which reads as a rendering glitch rather than as "this window cannot count past two".

**The layout law is linear and was read out of the two vanilla branches, not estimated:**

| Products | `productGroup` width | `speedGroup.x` | `servingGroup.x` |
|---|---|---|---|
| 1 | 64 | 80 | 160 |
| 2 | 128 | 144 | 224 |
| N | 64N | 16 + 64N | 96 + 64N |

The tree is the same shape: `treeMainBox` width `64 + 50(N-1)`, icon *i* at `x = (i - (N-1)/2) × 50`, count text at the icon's `(-24, -11)`. Substituting N=1 and N=2 reproduces vanilla's own constants exactly (64/114, 0/-25, -24/-49), so this extends vanilla's rule rather than inventing a parallel one — **check that property before trusting any extrapolated layout**, because it is the only evidence that the rule was read rather than guessed.

**Products are read from `recipeExecuteData.products`, not from the `RecipeProto`.** That is what vanilla reads, and it is also what the per-building alloy ratio feature replaces with its own clone — reading the proto instead would make the panel disagree with the machine.

#### Cloning the extra slots

**"Flat" means the *group* is shared, not that the five widgets are unnested — clone only the roots.** `ResolveAsmTemplate` computes the nearest common ancestor of slot 1's five widgets and checks whether it also contains slot 0's icon. **Measured: it does** — the assembler window's five product widgets are not a per-slot container. But they are not unnested either: `productButton1`'s GameObject *contains* `productIcon1`. Cloning all five individually therefore produced **two** icons for the extra slot — one carried in as a child of the button clone, one standing alone — stacked at the same position. The sprite was written to the standalone one while the button's child drew on top, so slot 3 showed **slot 2's item with slot 3's count**: a wrong picture next to a right number. `CloneAsmPieces` now clones only the subset of the five that no other of the five contains, and resolves the rest out of those clones by relative path. Container cloning is kept for a prefab that does group them; the log says which path was taken.

**Slot spacing is measured, never taken from the table.** Each cloned root is offset by **its own** `pos1 - pos0`; a widget whose own delta is ~0 (it is nested, so slot 0 and slot 1 share a local position) falls back to the largest delta among the five, or every clone stacks on slot 1.

**A slot's hide/show must cover every GameObject the slot owns.** When icon and count are cloned as two independent nodes, hiding "the slot" by its icon leaves the cloned `x N` on screen forever. Both `AsmSlot` and `TreeSlot` carry a `Parts` array and are shown/hidden through it; container-cloned slots simply have one entry. Extra slots are hidden **all of them first, then shown as needed**, rather than from an index computed off the product count — the arithmetic was right, but "right by exactly one" is how a stale icon survives a recipe change.

#### Two things vanilla will not do for you

**Whatever vanilla treats as a prefab constant has to be restored by hand.** `OnSelectedRecipeChange` writes slot 0's icon and count positions on every call but **never writes slot 1's** — it reads the prefab value and assumes nothing ever changes it. Moving slot 1 to the centre for a three-product layout therefore sticks: the next two-product recipe drew its two icons at -25 and 0, crammed together, with vanilla "correcting" only the one it owns. `RestoreSlot1` records slot 1's home position at measure time, before anything is moved, and puts it back whenever n <= 2. The box size and slot 0 need no such treatment because vanilla rewrites them every call — **the rule is to restore exactly what vanilla does *not* rewrite**, which means reading the vanilla path for its *writes*, not just its reads. Same shape as the build menu's "a slot that empties is never cleaned up".

**A retracted claim: vanilla *does* draw the downstream branch for a three-product recipe.** This file previously said it does not, reading the guard at IL 05C4–05CB (`product0 != null && product1 == null`) as covering every case. That guard only gates the **single**-product path; a second path at 081D handles `both non-null`, and a three-product recipe walks into it, building consumer nodes for products 0 and 1 at hardcoded `x = ±(90 + 46k)`, `y = 52` — coordinates derived from icon0 at -25 and icon1 at +25. Once the icons move to -50/0/+50 those nodes and their connector lines no longer line up with anything. Since the owner explicitly allowed dropping the middle product's downstream, `HideDownstream` switches the whole up-branch off for n >= 3 rather than re-deriving a second set of geometry. **The lesson is about reading branches, not about trees: an early guard that returns for one shape does not mean no later guard handles the others.** Enumerate the exits before concluding "vanilla skips this".

#### Positioning, and one deliberate divergence

**The tree's count text is positioned in world space, not by `anchoredPosition` arithmetic.** The icons and the count texts share a parent (`center-icon`, which *is* `treeMainBox` — confirmed by probe), so widening the box moves every child whose anchors are not point-anchored at the same spot, and icons and texts need not agree. Vanilla never notices because it only ever writes slot 0's position, at two fixed box widths. `count.rectTransform.position = icon.rectTransform.position + measuredWorldDelta` is anchor-agnostic and returns the same answer as the arithmetic when the anchors do match, so it is strictly safer. The world delta is measured off vanilla's slot 1 **before anything is moved** — the `(-24, -11)` pair it replaces was back-derived from vanilla's two constant sets, and deriving it correctly is not the same as the prefab actually being laid out that way.

**The clone's take-out handler deliberately diverges from vanilla, and this is the one behavioural change.** `OnProductIcon1Click` does `produced[1] = 0` unconditionally after `TryAddItemToPackage` — so with a full inventory the items are destroyed. The extra slots deduct only what was actually added. Copying a vanilla bug into new code has no upside.

**`Translate` keys must be read out of the assembly, not transcribed from an IL dump.** The Chinese in a PowerShell/Cecil dump comes back mojibake, and the key here was guessed as 「正在手动搬运物品」 when it is actually 「不能手动放入物品」. `Localization.Translate` returns the key unchanged when it is not registered, so a wrong key ships as Chinese text on an English client and nothing errors. Re-extract with `[Console]::OutputEncoding = UTF8` and compare. Being a *vanilla* key, it must also **not** get an `i18n.json` entry — that would overwrite vanilla's own translation.

#### What this cost, and why

This feature took six round trips to land. Three of them are one mistake repeated in different clothes, and the diagnostics that ended it are still in the file (`ProbeAsm`, `ProbeTree`, `DumpBox`, capped per session — keep them; they are the fastest way back in if a game update moves the prefab).

**Do not diagnose a UI layout from a screenshot once the logged coordinates match the intent.** Three rounds were spent trying to tell a stray icon apart from a leftover clone and from the translucent tree panel's background bleed-through — none of which a screenshot can distinguish. The tree's numbers were correct well before it looked correct: the count `Text` has pivot **(0, 1)** on a 50×20 rect, so its glyphs sit ~20 units right of the icon centre, and vanilla's own two-product rendering has the identical offset. Compare against an untouched vanilla recipe before "fixing" anything. `DumpBox` — every child of `treeMainBox` with name and `anchoredPosition`, mod clones carrying a `projecteden-` prefix, **plus the downstream nodes, which are not children of the box and would be missed by listing the box alone** — settled two open questions in one line: the three-product layout was exact, and `icon 2@(0.00, 0.00)` was the two-product regression sitting in plain sight. **When a UI question becomes "which object is this", enumerate the objects.**

**A branch with no implementation behind it is an unfinished feature, not caution.** The first version bailed out in the flat case, and the piece-wise path was drafted and then *deleted* on the argument that DSP surely nests everything under the button, so it would be dead code — citing this file's own rule that a mechanism which *could* explain a symptom is not evidence that it *did*. That rule is about not shipping a fix for an unverified **cause**; it does not license deleting a structurally-guarded **alternative path** whose guard already exists and whose cost is bounded.

**And the silent path cost a round on its own.** The only log lines were on "extended" and "gave up"; the `products.Length <= 2` path logged nothing, so "still two products" could not be distinguished from "the window was never opened". `ProbeAsm` now reports `assemblerId`, `recipeId`, the proto's result count and the `recipeExecuteData` count once, unconditionally. **This is the fourth time this exact shape has cost a round trip here** (`AlloyRatioPatches.ReapplyAll`, `ReportCheats`, `CargoShaderIncProbe`, this) — when a feature has a "nothing to do" branch, that branch needs a log line in the same commit.


### Shared proto slot resolution — `src/Utils/ProtoSlots.cs`

Item/recipe/model id, replicator `GridIndex` and build-menu `BuildIndex` allocation, used by both `OreRegistry` and `MachineRegistry`. It exists because the pitfalls encoded in it (scan `dataArray` not `ProtoSet.Select`; never change replicator page; extend columns not rows; model id < `dataArray.Length + 64`) were duplicated once and would only ever get fixed on one side. Callers pass a `pending` predicate for protos allocated this session but not yet in `LDB`.

**A hand-built proto with a null array field crashes vanilla, and the stack trace blames other mods.**
Vanilla protos are deserialized from `resources.assets`, so every array field is a real array —
possibly length 0, never null. Most registries here clone one (`DescFields = source.DescFields`);
`DrillBitRegistry` builds its `ItemProto` from scratch and omitted `DescFields`. Registration,
icon, recipes and localization were all fine — until a mouse hover reached
`UIItemTip.SetTip`, whose `ldfld ItemProto::DescFields ; ldlen` threw
`NullReferenceException`. **The error report named LDBTool and UXAssist and never mentioned this
mod**, because both have patches on that call path while our contribution was a null *field*, not
code. (Locating it needed the same offset reconstruction as the vein crash: Harmony expands every
short branch to long form, so patched `IL_0255` is original `IL_01EE`.)

`ProtoArrayCheck` (last on `PostAddDataAction`) now walks `ProtoSlots.OwnItemIds` /
`OwnRecipeIds` and loud-fails on a null `DescFields`, `Upgrades`, or any of a recipe’s four
arrays, printing a pass line when clean. **The value is not the assertion — it is converting a
crash whose stack points at somebody else into one line at startup.** When building a proto by
hand rather than cloning, assume every array field is load-bearing.

**During `PreAddDataAction` the LDB contains no mod protos at all.** `LDBTool.PreAddProto` only queues a proto; it lands in `LDB.items` / `recipes` / `models` when LDBTool builds the tables, i.e. *after* every `PreAddDataAction` handler has run. So a registry that scans `dataArray` to find a free id, grid cell or build slot sees **only vanilla** — it cannot see what an earlier registry in the same pass already claimed. That is how 电化学厂 took build slot 1 of category 12, which 天空装配厂 already owned: the mega buildings were registered first but were still invisible. `ProtoSlots` keeps a reservation ledger (`ReserveItemId`/`ReserveGrid`/`ReserveBuildIndex`/…) that every occupancy check consults; `MegaBuildingRegistry` clears it at the top of its pass (it runs first) and each registry files what it claims. **A new registry must both consult and file, or it will collide with whatever ran before it.**

### Assets & config

**Editing `data/*.json` does nothing until you rebuild.** They are embedded resources, so the
running game reads the copy inside `ProjectEden.dll`, not the one on disk. A script that validates
the design by reading `ProjectEden/data/*.json` is therefore reading a *different file* from the one
the game loaded — and the two silently diverge the moment you edit the JSON without `dotnet build`.
This cost a launch on the combustible-liquid chain: two fuels' temperatures were swapped in the
JSON, the validation script reported the new order, and the game logged the old one. Either rebuild
before every launch, or put the file in `BepInEx/config/ProjectEden/` and use the disk-override path
(which logs a WARNING every time, precisely so it cannot be forgotten).

`data/*.json` and `assets/icons/*.png` are embedded resources (`JsonHelper` → `ProjectEden.data.<name>.json`, `TextureHelper` → `ProjectEden.assets.icons.<name>.png`). **`JsonHelper.Load` checks `BepInEx/config/ProjectEden/<name>.json` first and falls back to the embedded copy**, logging a WARNING every time a disk override is used — same shape as the LDBTool `CustomID.cfg` trap: a forgotten override makes every later edit to the embedded JSON look like it did nothing, silently. This exists because embedding alone means **one rebuild per switch flip**, which is fine for content configs and unusable for `cheats.json`; that is exactly how the first cheats build was reported as broken — all five switches were `false` and there was no file in the profile to change. `TextureResourcesPatches` prefixes `Resources.Load` for `Assets/projecteden/`, so custom icons need no AssetBundle. `src/Compatibility/` holds one file per third-party mod, all wired as `SoftDependency`.

`planet.json` (bigger planets: the master switch — **on by default since 1.12.1** — the radius
multiplier, and the `probe` diagnostic; see *Bigger planets*) is the twenty-third.

The 25 configs: `megabuildings.json` (tab, build category 12, the seven buildings with their pinned model IDs 704, 708 and 723–727, station block, plus **`globalTickDivider`** — the throughput-neutral working-set lever, see *The per-building working set is the real cost*), `advancedminer.json` (miner/pump limits, **`minerPeriod` — the miner's output ceiling, and the only lever on it; see the advanced-miner section**, the ore→ingot product map, the plain miner's own buffer via `smallMinerCapacity` — **which also scales the throttle divisor**, see the advanced-miner section — whether a pump may draw 岩浆 from a lava ocean, and the three rendering knobs added in 1.9.4: `stackedRenderLimit` / `stackedRenderRadius` — how many coincident same-proto buildings to draw — plus `veinMiningCircles` and the two diagnostics `veinMiningReport` / `renderCensus`, see the stacked-buildings section), `stations.json` (slot capacity/count, `stationEnergy` — per-station charging power in **watts** and energy capacity in **joules**; 2103 / 2104 / 6531 all ship at 5 GW / 150 GJ — carry capacity, stacking, gas collector, `localDispatchPerTick` — how many planetary drones one station may launch per tick, see *Game internals: planetary drone dispatch* — and `inventoryStackSize`, the one `ItemProto.StackSize` shared by the inventory, chests, the delivery package and the mecha's ammo/fuel slots, see trap 4c), `lab.json` (matrix production speed, `matrixTimeSpend` — every matrix recipe's craft time in ticks, swept over `LabComponent.matrixIds`, see the matrix-lab section — the lab↔station virtual feed, whether techs list 生物矩阵 directly, and how it shows in the lab’s 3-D animation), `recipes.json` (cloned recipes retyped for other machines, plus `vanillaEdits` — append ingredients to a vanilla recipe in place; see the extra-recipes section), `power.json` (power node coverage), `ores.json` (the custom vein table: extra items, per-ore item/vein ids, vein rarity, recolour parameters, each ore's recipe list, and the `gases[]` injected into gas giants), `machines.json` (cloned machines: source building, `kind`, recipe type, tint, build recipe), `belts.json` (per-tier belt speed, plus `throughputProbe` — the four-causes-one-symptom flow probe, default off), `metals.json` (the four-axis property table; `fieldIdBase` 74), `alloys.json` (the per-building 硬质合金 ratio: parts, cobalt range, grade buckets, waste penalty), `cheats.json` (the six rule-bypass switches, all **on** by default), `i18n.json` (the Chinese→English string table), `ammo.json` (the five ammo tiers and how a pair of alloys maps to damage and yield), `cargoprobe.json` (one bool: the shader `inc` probe), `composite.json` (the Living Composite: candidate fillers, the four grades' part thresholds, yield and percolation parameters, and the sintering outputs), `combustibles.json` (combustible liquid power: each liquid's working temperature, the Carnot cold-side temperature and second-law efficiency, the fuel-type bit, the property row's field id), `proliferator.json` (living proliferators: the candidate list shared by both feedstock slots, the character/grade score thresholds, and each outcome's spray level, spray count and yield), `alienvein.json` (the alien vein: which vein type consumes drill bits, the bit predicate’s hardness margin, yield formula and **exclusion list**, the miner’s bit slot and its capacity, and the rare-vein prospector switch), `redox.json` (the redox combustion plant: the reductant and oxidiser candidate lists with their **oxygen balance per item**, the three grain tiers with their heat values and density thresholds, and the oxidiser-ratio slider's range), `lens.json` (the living lens: power multiplier and photon multiplier — **independent**, see the catalyst-slot section — the heal rate, and which vanilla catalyst counts as "the other lens", resolved by `ItemProto.Name`), `abnormality.json` (one bool: suppress the "abnormal data" determination, **on** by default — see the next section for why a content mod trips it unavoidably).

**Vector-authored icons live in `tools/make_icons.py`** (`drawsvg` → SVG → `resvg-py` → PNG; on Windows `cairosvg`/`renderPM` are dead ends, see below). Items are 80×80 and vein icons 480×480, matching GenesisBook's own split. An `icon` / `ingotIcon` / `oreIcon` field in `ores.json`, or a recipe's `icon`, names one of these files under `assets/icons/`.

**LDBTool re-binds proto IDs from its own config, after your code sets them.** `LDBTool.PreAddProto` → `Bind` → `IdBind` / `GridIndexBind` records every mod proto's ID and GridIndex in `BepInEx/config/LDBTool/LDBTool.CustomID.cfg` and `LDBTool.CustomGridIndex.cfg`, **keyed by the proto's display name**, and on every later launch it writes those stored values *back onto the proto*. So changing an ID in this repo's JSON has **no effect** on a proto that has already been registered once — the first ID a proto is ever given is sticky until that cfg entry is deleted. Cobalt sat on 电磁矩阵's 6001 through three config edits because of this. When an ID looks ignored, check that cfg before anything else, and delete the entry (both files) to let the new value take. `OreRegistry.VerifyIds` now checks the post-registration reality and names the file.

**Do not use `ProtoSet.Select(id) != null` as an occupancy test.** For `LDB.items` it reported 200 consecutive IDs as taken; scan `dataArray` for `proto.ID == id` instead. Related: vanilla item/recipe protos live in `resources.assets`, not in the assembly, so **there is no way to enumerate used IDs by decompiling** — the only authoritative table is the running `LDB`. Known landmines: matrices occupy items **6001–6006** (电磁矩阵 is 6001) **plus 6007, which this mod took for 生物矩阵 — matrix ids must stay dense from 6001, see the seventh-matrix section**, and this repo already uses items 6500–6505, 6510–6520, 6530–6536, 6560–6568, 6580–6590, 6594–6599, 6617–6631, 6636–6639, **6640 (岩浆)**, **6641–6643 (沸石催化剂 / 待生沸石催化剂 / 丙烯)**, **6644–6645 (尿素 / 乌洛托品)**, **6646–6647 (丙烯腈 / 聚丙烯腈)**, **6648–6651 (苯 / 异丙苯 / 苯酚 / 丙酮)**, **6652 (硫磺)**, **6653–6654 (石脑油 / 蜡油)**, **6506 (熔岩冷却厂)**, **6507 (催化反应器)**, **6508 (综合化学厂)**, **6509 (氧化还原燃烧厂)** **6655–6657 (双元推进剂 / 金属浆料燃料 / 固体复合推进剂)** **6658 (活性透镜)**, **6659 (同位提纯厂)**, **6660 (电解液)** and **6661 (精炼油燃料棒)** (**6591–6593 and 6600–6611 were freed when the alloy grade tiers were removed — reuse them only in a fresh save**, an existing save holding one of those items would be left with an ID that has no proto), plus recipes 6500–6505, 6510, 6520–6524, 6530–6533, 6535–6536, 6540–6550, 6560–6562, 6570–6573, 6580–6586, 6590–6592, 6600–6604, 6632–6635, 6640–6644, **6506** and **6645–6647 (the three cumulate recipes)**, **6507**, **6648–6653 (catalyst synthesis / regeneration, three type-14 reactor recipes, propylene carbothermic)** **6654–6656 (urea / hexamine / urea-formaldehyde resin → vanilla plastic)** **6657–6659 (acrylonitrile / PAN / PAN carbonisation → vanilla carbon nanotube)** **6660–6664 (benzene ×2 routes / cumene / cumene cleavage / phenolic resin → vanilla plastic)** **6665–6667 (catalytic reforming / residue HDS / contact-process sulfuric acid)** **6668 (hydrocracking)**, **6509 (the plant itself)**, **6669 (药柱压制)**, **6670 (活性透镜 · 晶格培养)**, **6671 (同位提纯厂)**, **6672 (电解液 · 配液)**, **6673–6675 (the three purification recipes)** and **6676 (精炼油燃料棒 · 凝胶成型)**. **This ledger went stale once already** — it stopped at item 6658 / recipe 6670 while 6659–6660 and 6671–6675 were in use, so **re-derive it from the JSON (`grep -o '"itemId": [0-9]*' data/*.json | sort -n | tail`) rather than trusting this line**; `ProtoSlots` will shift a colliding id and warn, but a warning is not a plan. **Vanilla recipe 75 (宇宙矩阵) is edited in place** rather than cloned — it gains 生物矩阵 as a seventh ingredient. Model IDs **702**, 703, 704, **707**, 708 and 723–727 (mega buildings), **701, 705, 709, 714, 715, 717, 718, 719** (cloned machines — pinned to measured values, see the cascade note below) and 710–713, 716, 720–722 (ore veins) are likewise spoken for. **727 is the ceiling** — `ResolveModelId` scans down from `LDB.models.dataArray.Length + 64 - 1`, and every pinned id above was assigned by that downward scan, so `dataArray.Length` is 664 here and 728 would be rejected. `ERecipeType` 9 is 电化学, 10 is 氧化还原, 11 is 生化培养 (生物温室), 12 is 锻造 (锤锻精工厂), 13 is 熔岩处理 (熔岩冷却厂) 14 is 催化 (催化反应器), **16 is 综合化学 (综合化学厂)** and **17 is 氧化还原燃烧 (氧化还原燃烧厂)** — a type with **no recipes of its own**, it is only the key of the multi-type compatibility table. **14 is not a ceiling** — see the `ERecipeType` paragraph under *Cloned buildings*; 16 and up are equally usable.


**Model IDs drift silently whenever a building is inserted above them, and it has already happened
once.** `ResolveModelId` scans **down** from `dataArray.Length + 64 - 1` for the first free id, and
mega buildings register before `machines.json`. So 1.6.5 giving 综合化学厂 id 707 displaced
可燃性液体发电厂, which took 电化学厂's configured 719, which took 综合物流枢纽's 718 — a clean
seven-deep rotation, every machine stealing the next one's slot:

```
可燃性液体发电厂 707 → 719    锂电池能量枢纽 714 → 709
电化学厂         719 → 718    风力发电机集群  709 → 705
综合物流枢纽      718 → 715    碳化硅能量枢纽  705 → 701
锂电池蓄电器      715 → 714
```

**Model ids go into saves**, so this is not cosmetic: an entity built before the shift keeps its
baked `modelIndex` and will render as whatever now owns that number. The registry warns per
building ("期望的模型 ID X 不可用，改用 Y") and the warning's own advice is the fix — those seven are
now **pinned to their measured values** in `machines.json`, with a `//` at the top of the file
saying why. Adding another building above them no longer moves them.

The general rule: **a resolver that logs "I picked a different value than you configured" is
reporting an unstable id, not a successful fallback.** Pin it the first time you see the line.

**PowerShell scripts for IL inspection must be pure ASCII.** Windows PowerShell reads `.ps1` as ANSI, so a heredoc-written script containing Chinese characters gets mangled into parser errors that look nothing like an encoding problem (`unexpected token 'case'`, `missing string terminator`).

### Belt speed — `src/Patches/Belt/BeltSpeedPatches.cs`

`PrefabDesc.beltSpeed` (Int32) is copied straight into `BeltComponent.speed` with no clamp anywhere, and **items/s = speed × 6** (a cargo occupies `CargoPath.kCargoLength = 10` buffer units, 60 ticks/s). That reproduces vanilla exactly: Mk.I 1 → 6/s, Mk.II 2 → 12/s, Mk.III 5 → 30/s. `belts.json` ships 2 / 4 / 8.

**Do not trust `CargoPath.kMaxCargoFlowSpeedPerSecond = 120`.** It looks like the engine's own stated ceiling (speed 20) and it is **not implemented** — nothing in the assembly reads it. Setting Mk.III to 20 on that basis crashed `CargoPath.Update` with `IndexOutOfRangeException` at IL_03B8, inside the parallel `FactoryCargoPath` phase. That site walks *backwards* from a cargo's position looking for free buffer slots, one iteration per unit of speed, and **has no lower bound check on the buffer index** — a large step runs off the head of the array on short paths. Vanilla's max of 5 never reaches far enough to trip it. The real ceiling is unknown; `kCargoLength = 10` is the plausible bound but is unverified, so raising it means stepping one notch at a time and running belts of varied lengths for several minutes. **A named constant with zero readers is a claim, not a guarantee — check for readers before believing it.**

**"The belt is not hitting spec" has four causes that look identical in game, so the probe exists to
separate them** (`BeltThroughputProbe`, `belts.json`'s `throughputProbe`, default off): the belt was
never retuned (the two saved speeds disagree — **cargo moves at the `chunks` one**), the belt is not
fed full (low occupancy → the bottleneck is upstream), stacking never happened (mean stack ≈ 1, so
120 cargo/s is 120 *items*/s and not 600k), or **the feeder structurally cannot fill it** — vanilla
inserts **at most one cargo per tick per insertion point** (`TryInsertItemAtHeadAndFillBlank` →
`TestBlankAtHead`), i.e. **60 cargo/s, exactly half a 120 cargo/s belt**. A single-source Mk.III belt
running at 50% is therefore correct behaviour, not a defect.

**It measures an identity, not a counter**: one cargo occupies `kCargoLength = 10` buffer units, a
belt advances `speed` units per tick at 60 tick/s, so `flow = speed × 6 × occupancy`. One snapshot
suffices and nothing is hung on the tick path. Measured in game it settled the question in one
launch — 硫酸 at `36/36 堆, 速度 20/20, 平均 5000 层 → 600,000 件/秒`, i.e. exactly spec, beside a
电浆蓄能柜 belt at the same 120 cargo/s but **1 layer**, which is what "not hitting spec" actually
was. **Known limit, stated because the number reads as authoritative and is not**: on a belt holding
one or two cargo the spatial-average occupancy is noise, so sparse belts report a rate that can be
an order of magnitude high.

**Reading a cargo needs the engine's own decoder.** `CargoPath.QueryItemAtIndex`'s two `out byte`
are widened to `out short` by this repo's preloader, so it is bound at runtime like the rest of that
family (`CargoWidening.QueryAt`). Do **not** decode the buffer by hand: the cargo id is packed into
`CargoPath.buffer` (markers 246–255 give the distance from the cargo head, the id bytes follow the
250 marker, each stored as value + 1) — a private format, and a second copy of it is one more thing
that can silently drift.

**Existing belts need a runtime fix-up, and `CargoTraffic.UpgradeBeltComponent` cannot be used for it.** Speed is baked into the save twice: `BeltComponent.speed` *and* `CargoPath.chunks` (stride 3, the speed in slot +2). **Cargo actually moves at the chunks value**, so patching only the component gives "the number changed but nothing moves faster". Vanilla's own upgrade routine does both — but it also does `planet.physics.isPlanetPhysicsColliderDirty = true` **unguarded five instructions after null-checking `planet.physics` for a different use** (IL_0045). It never fires in vanilla because the method only ever runs for the planet the player is standing on; calling it while walking every planet in a save throws immediately on any unloaded one. `BeltSpeedPatches.Retune` therefore replicates just the data half — component speed, `InsertChunk`, and `SyncBuckleSpeed` for closed loops — and touches no rendering or physics, which is correct anyway since the belt's *tier* is unchanged. Copy the segment-length rule too: the **last belt of a path** must extend to `pathLength - segIndex`, or the tail keeps its old speed.

## English localization — `src/Utils/I18N.cs` + `data/i18n.json`

Three facts out of IL, and together they explain both the bug and the fix:

1. `Localization` holds `namesIndexer` (key → index), `strings[language][index]`, and `currentStrings` for the active language.
2. **`Localization.Translate(key)` returns the key unchanged when it is not registered** — IL 0024 is literally `ldarg.0 ; ret`.
3. `ItemProto.name` / `description` are *not* fields: they are `Name.Translate()` / `Description.Translate()`, written by `Preload` and `RefreshTranslation`.

So this mod's Chinese `Name` was never a key, `Translate` handed it straight back, and the Chinese displayed correctly in **both** languages. The fix is therefore **to register the Chinese string itself as the key**, not to replace `Name` with a synthetic key. Replacing `Name` would also work and costs much more: LDBTool's `CustomID.cfg` / `CustomGridIndex.cfg` are **keyed by the proto's name**, so renaming orphans every binding, and several self-checks in this repo compare names.

**The one real hazard is colliding with a vanilla key**, because vanilla's keys are Chinese strings too (`Translate("风能")` is ordinary code in `ItemProto.GetPropValue`). Writing over one silently changes that string everywhere in the game. `I18N.Apply` therefore checks `namesIndexer.ContainsKey` first and **skips**, listing the skipped keys in a WARNING. That guard is also why vanilla names (铁块, 钢材, 低速传送带 …) need not — and must not — appear in the table.

**CommonAPI's `ProtoRegistry.RegisterString` was deliberately not used.** Its `AddModTranslations` is an unconditional `strings[lang][indexer[key]] = value` for every registered key, so it writes over an existing vanilla key without complaint — exactly the check that matters here. The implementation instead follows soarqin/DSP_Mods' `UXAssist.Common.I18N` (MIT): postfix `Localization.LoadSettings` to register (that is the earliest point at which `namesIndexer` is populated) and postfix `Localization.LoadLanguage(index)` to re-apply, because **languages are loaded lazily** and a later load overwrites `strings[index]`. Growing `strings[lang]` to the new `namesIndexer.Count` before writing is mandatory — and `floats[lang]` is a parallel table that has to grow with it.

Non-Chinese languages all fall back to English rather than to the key, so French/German/Japanese players see English rather than Chinese.
**The Chinese side of the table is the key itself, so a key must BE the Chinese display
text.** `I18N.ApplyLanguage` ends in
`table[pair.Value] = lcid == LcidZhcn ? pair.Key : _table[pair.Key];` — Chinese gets the key
verbatim and only other languages get the translation. That is correct for item and recipe
names (the key *is* the Chinese name), but it means a **UI string key named descriptively
renders as that name**. The five existing panel titles are written that way
(`合金配比面板标题`, `合金弹药面板标题`, `活性复合材面板标题`, `烧结析出面板标题`,
`活性增产剂面板标题`), so on a Chinese client those panels are titled with the literal key.
English is fine (`Ratio — {0} parts total, balance {1}` etc.). The catalyst panel's keys are
written as real Chinese sentences instead (`催化剂床　装填与活性`, `还能跑 {0:0} 秒　　催化剂库存 {1}`)
and do not have the problem. **Any new `.Translate()` key for UI text must be the sentence
you want a Chinese player to read, placeholders included.**

**Two self-checks had to stop comparing `name`.** `MachineRegistry.OnPostAddData` and `MetalPropertyPatches` both compared the *translated* `name` against the config's Chinese; in English every one of them would have fired. They now compare `Name`, the raw key. **Any future check of this shape must do the same** — `proto.Name` is data, `proto.name` is presentation.

`I18N.VerifyCoverage` runs last on `PostAddDataAction` and warns, once each, for any `Name`/`Description` on a proto **this mod registered** that contains Chinese and has no entry in the table — so forgetting a translation for a new item is caught at startup instead of by a player switching language. "Registered by this mod" is read from `ProtoSlots.OwnItemIds` / `OwnRecipeIds`, **not guessed from an ID range**: with GenesisBook installed, its protos also sit above 6500 and an ID-range rule would report them as our missing translations.

## Preloaders: when to reach for one

A **preloader** (a BepInEx *patcher* plugin) runs before the game's assemblies are handed to the CLR. It receives a Mono.Cecil `AssemblyDefinition` — metadata, not live types — rewrites it, and BepInEx passes the modified bytes on. The whole interface is two members:

```csharp
public static IEnumerable<string> TargetDLLs => new[] { "Assembly-CSharp.dll" };
public static void Patch(AssemblyDefinition assembly) { /* rewrite anything */ }
```

One is already running in this profile: `CommonAPIPreloader`, visible in the log between `Preloader started` and `Preloader finished`.

**The boundary is simple: Harmony changes method bodies, a preloader changes type structure.** Only a preloader can add an enum member, add a field, change a field's *type*, change a method signature, or change a struct's size. ProjectGenesis' preloader is the canonical example and does nothing but those two things — delete `EVeinType.Max`, add six named vein types, add fields to `PlanetData` / `GameDesc` / `RecipeProto`.

**Apply this test before writing one, because it has twice been enough here:**

1. **Is an unnamed enum value legal?** `EVeinType` is byte-backed and `ERecipeType` is int-backed, so `(EVeinType)15` and `(ERecipeType)9` are valid without a name.
2. **Do the arrays size themselves?** `PlanetModelingManager.PrepareWorks` sizes from the last `LDB.veins` proto's ID; `turretNeeds` is `new int[16][]`; `GenerateVeins` sizes from `veinProtos.Length`.

If both hold, the only thing left is hardcoded loop bounds, which a transpiler fixes. That is how this repo added **eight vein types and two recipe types with no preloader at all**.

**One preloader does now ship** — `ProjectEden.Preloader`, which widens `Cargo.inc` from `Byte` to `Int16`. It is the one case where the test above fails on its first question: this is a *field type*, and no amount of transpiling reaches it. See **Cargo.inc widening** below for why the alternative (no preloader, change the semantics to a per-item rate) measured out *more* expensive, not less.

**What stays expensive even with the policy below:**

- **Global and irreversible.** The rewritten assembly is what *every* mod sees, and there is no `UnpatchSelf`. **A preloader can never be a config toggle** — the six cheat switches could not have been built this way.
- **The worst debugging environment in the project.** No `LDB`, no game state, no live types. A mistake usually shows up as the game failing to start with a CLR type-load error that points nowhere near your code. None of this repo's usual method — read the IL, report a match count, loud-fail on zero — is available.
- **Fails silently across game updates.** A transpiler matches an instruction signature and shouts when it cannot; a structural rewrite lands on whatever field is at that position now.

**Decision (owner, recorded): save compatibility is not an objection.** If a preloader is ever needed, **the save format follows this mod** — a save made with it may require it, and that is accepted. So the "it binds the save" cost is off the table; weigh only the three bullets above.

**This does not reopen the cargo stacking ceiling.** Its blocker was never only the save: `CargoContainer.Draw` hands the raw `Cargo[]` to a **compiled shader** that parses an exact 32-byte layout, with the stride hardcoded as a literal `32` in three places. That wall stands regardless of the save policy — see the next section, and read it before proposing the widening again.

The preloader shipped, and `README.md` (repo root — the packaged copy is generated from it by `tools/pack_release.py`) says so outright: a save made with this mod **cannot be opened without it**, which is a stronger claim than the old "do not uninstall after use".

## Game internals: the cargo stacking ceiling

**Status: both halves are done.** `Cargo.inc` **and** `Cargo.stack` are widened to `Int16` by a preloader (see **Cargo.inc widening**). The ceiling is no longer a byte — it is `inc` again, one order further out: full Mk.III needs `stack × 4 ≤ 32767`, so **8191 layers**. `PilerLevelPatches.AbsoluteMax` derives exactly that (`short.MaxValue / Cargo.kSprayIncMax`) rather than hardcoding it, and falls back to 255 when the preloader did not apply.

Widening `stack` turned out to be nearly free once `inc` was done, and the reason is worth keeping: **the struct does not grow.** With only `inc` widened the layout is `stack(Byte)@0, [pad@1], inc(Int16)@2, item@4, pos@8, rot@20` = 36 bytes. Widening `stack` fills that padding byte — still 36 — so the GPU repack, the stride, and the shader all stay exactly as they were.

What `stations.json` ships is a separate decision from what the types allow: it now asks for **5000** (600,000 items/s per lane), well under the 8191 ceiling.

**The sorter was a second, independent ceiling — now widened too.** `GameHistoryData.inserterStackInput/Output` are Int32 and look freely settable, but they land in `InserterComponent.stackInput`/`stackOutput`, which were **Byte**, through three `conv.u1` paths (`NewInserterComponent`, `OnInserterTechChange`, `UpgradeEntityWithComponents`). Setting 5000 there yielded `5000 & 255 = 136` — *worse* than 255, silently. Worse, `PilerLevelPatches` fed the sorter techs from the same `max` as the piler, so raising the belt would have quietly wrecked the sorter; `InserterTarget` is now a separate clamp.

**`inserterStackInput` counts *stacks per swing*, not items — and the ceiling comes from two fields that are NOT widened.** `InserterComponent.InternalUpdate`'s pick loop stops on `stackCount >= stackInput`, incrementing `stackCount` once per **belt stack** grabbed. So items moved per swing ≈ `stackInput × beltStackLevel`, and the accumulators `itemCount` / `itemInc` are **Int16 and deliberately untouched by the preloader**. The binding constraint is therefore:

```
itemInc = itemCount × pointsPerItem(≤4) ≤ 32767   →   itemCount ≤ 8191
itemCount ≈ stackInput × beltStack                →   stackInput ≤ 8191 / beltStack
```

In vanilla this can never bind (4 × 4 = 16). At belt stack 5000 it collapses to **1** — which is not a limitation worth fighting, since one swing already moves 5000 items. Setting it to 5000 instead drove `itemCount` toward 25 million; it wrapped Int16 and the panel showed **-25745** while items vanished. `InserterAbsoluteMax` now derives the cap from `GetMaxPilerStack()` and logs the derivation, so the config can just say "as high as possible".

**This one cost several rounds because the symptom pointed at the wrong building.** "集装分拣器 eats items" was read as the *piler* (自动集装机); it is the stacking **sorter** (the claw). Two real bugs were found and fixed while chasing the piler (the four piler-level constants, the fourth truncation shape), and a conservation probe on the piler reported **zero** imbalance — which was the evidence that finally moved the search. **When a probe says the suspect is clean, believe it and move on.**

**Widening the sorter was an order of magnitude cheaper than `Cargo`**, and the measurement is why it was worth doing: **9 methods touch those two fields and *no* byte-typed API parameter carries them** (they are read inside the sorter's own tick, never passed), and `InserterComponent` is never uploaded to the GPU, so growing the struct costs nothing. Compare 110 call sites and a 32-byte stride for `Cargo`.

**The temp-cargo path truncates too, and it is the one that loses items outright.** While `tmpEnabled`, `CargoContainer.RemoveCargo` stashes a whole belt pile into `ItemPackage` (`tmpCargos`) — that is what happens when a **belt is dismantled**. `AddTempCargo`'s `stack` was widened, but it immediately does `newobj ItemPackage::.ctor(Byte, Int16, Int32)`: a 5000-pile came back as 136 and the remainder was simply gone. Backward propagation cannot reach it — a constructor is not a "widened method" until something names it — so `ItemPackage::stack` and both `.ctor` `_stack` parameters (plus `CargoView`'s, which only affects the belt window display) are listed explicitly. **No save cost: `tmpCargos` is referenced zero times in `Export`.**

Note the naming trap again: the parameters are called `_stack`, so every name-based check walked right past them. The verifier now asserts this path by type, not by name.

**Its save format is deliberately left at one byte.** `InserterComponent.Export` writes a version (4) and `Import` keeps it, so a version branch *was* available — but it is unnecessary: the authoritative value lives in `GameHistoryData` (Int32, saved correctly), and every per-sorter copy is re-derived by vanilla's own `OnInserterTechChange`. So `Export` just clamps at 255 (`ClampByteWrites`) and the format never moves. **The catch this creates is real and was designed around:** after a load every sorter reads back 255 while history holds 5000, and the existing `Raise`-based sync sees no change in history and therefore never refreshes — presenting as "the config says 5000 but sorters still do 255". `LogisticsGlobalPatches` now forces one `OnInserterTechChange()` after each `GameData.Import`, claimed with `Interlocked` because `Apply()` runs on the ~31-thread parallel `PlanetTransport.GameTick` path. The analysis below is kept because it was expensive to produce and answers "why can't stacking go past 255" definitively; **do not reopen it as though one more step would finish the job** — the remaining step is a preloader plus either a replacement cargo shader or a per-frame repack, for a ceiling nobody needs. Note the save-binding objection has since been lifted by policy (see **Preloaders: when to reach for one**), and it changes nothing here: **the compiled cargo shader is the blocker**, not the save. Raise belt speed instead: it is a data change with no byte-width wall.

**Two different ceilings, and conflating them wastes a round.** `stack` and `inc` are *separate* bytes:

| | Limited by | Today | Per-item-rate design | Widening `stack` |
|---|---|---|---|---|
| **How many layers** | `Cargo.stack` — **now an Int16** | **8191** (`inc` is the limiter again), config ships 255 | — | done, and free: it fills existing padding |
| **Full Mk.III at that many layers** | `Cargo.inc` — **now an Int16** | **yes**, since the widening | — (this route was measured and rejected) | — |

Widening `inc` alone buys no extra layers — it buys that the layers you already have stop costing proliferation. Widening `stack` on top raises the layer ceiling itself to 8191. Both are done; what limits throughput now is the config value, not a type.

Investigated in depth; the widening itself is **deliberately not implemented** — recorded so the analysis does not have to be re-derived. `stations.json` now ships **255** and `PilerLevelPatches.AbsoluteMax` clamps at **255** (the `Cargo.stack` byte limit), a deliberate choice made with the trade-off below understood: **stacks of 64+ cannot carry full proliferation**. `CargoIncClampPatches` (below) makes that a *deterministic downgrade* rather than the silent corruption it was. Anything that wants full Mk.III on belts should still set the three `stations.json` stack values back to **63**, which is the largest value that keeps proliferation intact.

### Where the ceiling actually is

`Cargo` is a **32-byte struct**: `stack` (Byte) + `inc` (Byte) + `item` (Int16) + `position` (12) + `rotation` (16). **`inc` holds the whole stack's proliferator points, not the per-item rate** — proven by the piler's unstack path, which recovers the rate with `inc / stack` before scaling it onto the new stack:

```csharp
newStack = (byte)(stack / 2 + 0.5);
newInc   = (byte)(inc / stack * newStack + 0.5);
```

With `Cargo.kSprayIncMax = 4` (proliferator Mk.III), `stack × 4 ≤ 255` gives **stack ≤ 63**.

**`stack` is guarded everywhere, `inc` is guarded nowhere.** There are **two** unguarded additive writes, and both sit immediately after a fully-guarded `stack` write of identical IL shape:

| Method | `stack` guard | `inc` |
|---|---|---|
| `CargoContainer.AddItemStackToCargo` (belt insert) | `if (stack >= maxStack) return;` then only adds the remaining room (IL 0034/0039) | IL 00C0 `dup ldind.u1 ldloc.1 conv.u1 add conv.u1 stind.i1` — nothing |
| `CargoPath.TryUpdateItemAtHeadAndFillBlank` (fractionator output ×4, `StationComponent.UpdateOutputSlots`) | `if (stack + add > maxStack) return false;` — the whole merge is voided (IL 0066) | IL 009A `dup ldind.u1 ldarg.s add conv.u1 stind.i1` — nothing |

**Finding only the first one is the easy mistake here** — it is the one the stacking arithmetic points at, and it looks like the whole story. The second was found by enumerating *every* write to the field rather than reasoning from the ceiling.

The other two writers are safe, **for different reasons — don't generalise from one**: `StorageComponent.AddCargo` passes `inc`'s *address* into `split_inc(byte&, byte&, byte)`, which **drains** it into the Int32 `GRID.inc`; `PilerComponent`'s unstack computes `inc / stack * newStack`, necessarily smaller. Only the two above can grow.

So 63 exists purely to keep `inc` in range: `stack` itself is byte-safe to 255, and `InserterComponent.stackInput` / `stackOutput` (also Byte) agree on that ceiling.

### Overflow degrades silently; it does not crash

Every proliferator table (`incTable`, `accTable`, `fastIncArrowTable`, …) is length **11** (`kIncLevelMax = 10`), and consumers derive the level as `inc / stack` (`StorageComponent.split_inc`). Overflow only happens at `stack ≥ 64`, where a wrapped `inc ≤ 255` yields a level of at most 3 — **it can never index past the table**. Stacks small enough to produce a large level (≤ 25 items) never overflow in the first place (4 × 25 < 256).

So raising the cap past 63 costs exactly one thing: **proliferated cargo on belts drops to a lower bonus tier.** Nothing throws, no save corrupts, rendering is unaffected, and un-proliferated cargo is completely unaffected. `inc` drives *only* proliferator effects — assembler/lab bonus, generator fuel energy, mecha reactor power, turret damage, the on-belt arrow, statistics. Item identity, counts, routing and positions all come from `item` / `stack` / `position`.

**Therefore: if a playthrough never sprays onto belts, `AbsoluteMax = 255` is a one-line change and is safe.** That is the cheap answer, and it should be offered before anyone considers the expensive one.

### What is actually shipped: saturate, don't wrap (`CargoIncClampPatches`)

The above says the failure is a tier drop. **Untreated it is worse than that, and the difference is the whole point of this patch:** the byte wraps *cumulatively*, once per merge, so a 255-stack does not settle at a clean lower tier — it lands on whatever `(Σinc) mod 256` happens to be. Two identical belts can read as different tiers, and "was this stack ever sprayed" stops being answerable.

`CargoIncClampPatches` transpiles both sites above into `min(current + add, 255)`. It **cannot** make 255 stacks and Mk.III coexist (a byte cannot hold 1020) — it converts a **silent, non-deterministic corruption** into a **stated, deterministic downgrade**, and warns once when the clamp first bites.

Three things about how it is written, each of which is a repo rule earning its keep:

- **It rewrites opcodes in place and never removes instructions** — `code[i].opcode = OpCodes.Nop` for the two `conv.u1`s, `OpCodes.Call` for the `add`. `RemoveRange` would drop any branch label sitting on them.
- **The pre-`add` `conv.u1` must be neutralised, not kept.** In `AddItemStackToCargo` the addend is `perItemInc × n` and **can itself exceed 255**; clamping a value that has already wrapped fixes nothing. `TryUpdateItemAtHeadAndFillBlank` has no such instruction (its addend is a `byte` parameter), which is why the matcher treats it as optional rather than assuming one shape.
- **It matches on the field, not the shape.** Both methods contain a byte-identical `stack` write a few instructions earlier; a shape-only matcher patches the one place that was never broken. Offline simulation against both method bodies reports exactly **1** hit each — run that before trusting any future edit to it.

This is a **safety net, not the fix**, and it is deliberately compatible with both real fixes (per-item rate, or widening to Int16). The question both used to hinge on — what the compiled cargo shader does with `inc` — has since been **measured: nothing at all** (see `CargoShaderIncProbe` above), which is what makes the per-item-rate design the cheap one.

### The side-table scaffold is validated (in game, not in theory)

`CargoLedgerProbe` (`src/Patches/Cargo/`, toggled by `stations.json`'s `cargoLedgerProbe`) maintains an `int[]` per `CargoContainer`, parallel to `cargoPool` and indexed by cargo id, then periodically re-checks it against the pool. It changes no game logic — it only answers "can a parallel array actually stay in sync?", which is the prerequisite for carrying a wider `inc` beside the struct instead of inside it.

Measured in a live session: **`层数 63／整堆点数 252／单件点数 4`** — 63 × 4 = 252, the arithmetic derived above hit exactly, empirically. Across stacked-and-sprayed belts, a mid-session belt teardown (58 → 10 → 58 live cargo, the worst case for id recycling) and several save/load cycles, every report came back `对不上 0／残留 0／账本过短 0`. Multi-planet parallelism is the one dimension not yet stressed — that run had a single planet loaded.

Three things the probe had to get right, each of which cost a round when it did not:

- **Hook both `AddCargo` overloads.** Missing one leaves live cargo with no ledger entry.
- **`RemoveCargo`'s parameter is named `index`, not `cargoId`.** Harmony injects by parameter *name*, so the wrong name silently fails to bind — the same class of bug as the `recipeProto` / `recipe` mismatch.
- **Do not schedule periodic work off `GameMain.gameTick`.** It jumps backwards when a different save is loaded, so `next = tick + interval` never comes due again and the reporting dies silently — which reads exactly like "everything is fine". Use `Time.realtimeSinceStartup`.

And one about the diagnostic itself: a snapshot of "right now" routinely misses stacked, sprayed cargo because it is flowing. The probe keeps **session high-water marks** and prints an explicit `⚠ 样本无效` when it has never observed a stack or any proliferator — otherwise "tested but never triggered" gets mistaken for "tested and fine". Two runs were wasted before that line existed.

### What the shader does with `inc` — `CargoShaderIncProbe`

**First, what is knowable offline, because it narrows the question to one byte.** `CargoContainer.Draw` sets exactly three things on `cargoMatInst`: `_Buffer` (the raw `Cargo[]`), `_MainTex` (the icon atlas) and `_IndexBuffer` (`IconSet.itemIconIndexBuffer`). There is no fourth channel — no `SetGlobal*` anywhere in the assembly carries proliferator data, and **`Cargo.fastIncArrowTable`, the arrow-count lookup, is read only by UI classes** (`UIItemTip`, `UIBeltWindow`, `UIStorageGrid`, `UIStationStorage`, …) and never reaches the GPU. So everything the shader can know about proliferation is the `inc` byte inside the struct, and the shader is a compiled asset. **The only way to learn its function is to measure it.**

`CargoShaderIncProbe` (`stations.json`'s `cargoShaderIncProbe`, default off) does that: it writes controlled `(stack, inc)` ladders into the live pool, captures the frame with `ScreenCapture.CaptureScreenshotIntoRenderTexture` + `ReadPixels`, and diffs. Four design choices, each of which is a repo rule:

- **It freezes the scene with `Time.timeScale = 0`, and specifically *not* by pausing.** The factory tick hangs off `GameMain.FixedUpdate → GameLogic.LogicFrame()`, so zeroing `timeScale` stops Unity calling FixedUpdate and cargo positions freeze — while rendering, which runs off `Update` / `OnCameraPostRender`, carries on. **Pausing looks like the obvious choice and is fatal**: `FactoryModel.OnCameraPostRender`'s *first instruction* is `call GameMain::get_isPaused()` followed by an immediate `ret`, and `CargoContainer.Draw` sits downstream of it (`DrawInstancedBatches`). While paused the factory is not drawn at all, so nothing written into `cargoPool` can reach the GPU. The first version paused, and measured nothing.
- **It has a positive control, and refuses to conclude without it.** "Camera not pointed at a belt", "planet not loaded" and "shader ignores `inc`" all present identically as *no change*. So it first translates every cargo 200 m and checks that the frame moved. If that fails it prints `⚠ 样本无效` and stops — it does not report "the shader ignores inc". This is the mistake the retracted vein-icon finding was made of.
- **It sweeps `inc` at two different `stack` values**, and reads the answer off how the threshold moves: unchanged → the shader uses raw `inc`; scaling with `stack` → the shader computes `inc / stack` itself. A single sweep cannot separate those two, and they imply opposite things for the per-item-rate design.
- **Snapshot/restore is in a `finally`.** It is the only code in the repo that writes to `cargoPool` for non-gameplay reasons; leaving a modified pool behind would be a corrupted factory.

**It discards the first capture.** The `RenderTexture` has not been written when `CaptureScreenshotIntoRenderTexture` is first called, so frame 1 is uninitialised memory. Its signature is unmistakable once seen: the reference frame differs from *every* later frame by the same amount, so the noise floor and the positive control print **bit-identical** values (`63.1353` / `63.1353` in the failing run). If those two ever agree exactly again, suspect the capture, not the shader.

**It measures only inside a mask, and the mask comes from the positive control.** Whole-image means cannot see this: 52 cargo boxes in a 960×540 frame are a fraction of a percent, so even total disappearance moves the average less than the frame-to-frame shimmer — the failing run reported a control of `1.0494` against a noise floor of `1.0607`, i.e. *below* it. The control (cargo translated away) is used to mark which pixels the cargo actually occupies, and every later comparison is averaged over that mask alone. The validity test is then a pixel count, not a ratio, which also makes the failure message diagnostic: `0 个像素` means the camera is not on a belt, `40` means it is but too far away.

There is no trigger to press: the driver coroutine retries every 5 s and runs the moment the positive control passes, so the player just walks up to a belt and looks at it. It stops after one valid run.

### Measured answer: the cargo shader does not read `inc`

Run with a 93,402-pixel mask (18% of the frame), noise floor 0.7201. Sweeping `inc` over `1, 2, 3, 4, 8, 16, 32, 64, 128, 200, 252, 255` at `stack = 1` and again at `stack = 50` produced a **flat** response both times — every value inside a ±0.03 band, no step, no trend:

| stack | spread over inc 1→255 | masked noise floor |
|---|---|---|
| 1 | 1.3355 – 1.3872 | 0.7201 |
| 50 | 0.6934 – 0.7233 | 0.7201 |

**Why this is trustworthy rather than another silent negative:** the positive control validates *this exact data path*, not merely "something rendered". `position` and `inc` live in the same 32-byte struct, uploaded by the same `SetData`, on the same frame cadence — and moving the cargo repainted 93k pixels. So the buffer demonstrably carries our writes to the GPU, and "changing `inc` did nothing" cannot be explained by a stale upload. It also agrees with the offline finding that `fastIncArrowTable` is UI-only.

**So proliferated cargo has no on-belt 3-D appearance at all in this version** — the proliferator arrow exists only in UI panels. (The `stack = 1` group sits a constant ~0.6 above the noise floor; that is not an `inc` signal, since it is flat in `inc`. It is temporal-AA settling after the `stack` write, which shifts the whole group by a constant.)

**What this unlocks: the per-item-rate design is visually free**, and that removes its only unknown. See the options below.

**Its switch gets a file of its own (`cargoprobe.json`), and that is not arbitrary.** `JsonHelper`'s disk override is **whole-file**, so putting a developer switch in `stations.json` would mean shadowing the entire logistics config — stack level, slot capacity, charging power — just to flip one bool, after which every later edit to the embedded `stations.json` is silently ignored. That is the `CustomID.cfg` trap with a bigger blast radius. A one-bool file shadows only itself.

**And it shipped with the silent-diagnostic bug, for the third time in this repo.** Default off, and off meant *no log line at all* — so the first run came back with zero probe output, which cannot distinguish "the switch is off" from "this code never reached the DLL". `ProjectEdenPlugin.ReportCargoProbe` now prints one line in every state (config missing / disabled / enabled) and names the exact file to edit, the same shape as `ReportCheats`. **When adding any config-gated feature here, write its status line in the same commit** — this rule has now cost three round trips (`AlloyRatioPatches.ReapplyAll`, `ReportCheats`, this).

### What raising it properly would cost

| Layer | Surface | Verdict |
|---|---|---|
| The field itself | 38 accesses in **22 methods** | mechanical |
| **Byte-typed API** | **29 methods** taking `byte inc` / `out byte inc`, hit from **127 call sites in 46 methods across 18 classes** | unavoidable — the total (up to 255 × 4 = 1020) has to travel through these; includes `PlanetFactory.InsertInto` / `PickFrom`, the universal item I/O every building uses |
| Serialization | 4 sites | **cheap** — `CargoContainer.Export`/`Import` write **field by field** (`Write(Byte)` / `ReadByte()`), not a raw block. The save simply becomes mod-bound |
| **GPU** | 3 hardcoded `32` strides + the shader | **the real blocker** |

**The GPU is the blocker, not the save format.** `CargoContainer.Draw` does `new ComputeBuffer(poolCapacity, 32, …)` — stride is a **hardcoded literal 32** (also in `.ctor` and `Import`) — then `computeBuffer.SetData(cargoPool, 0, 0, cursor)` uploads the `Cargo[]` **raw**, and `cargoMatInst.SetBuffer("_Buffer", …)` hands it to a compiled shader that parses that exact 32-byte layout. Grow the struct and belt rendering breaks; the shader is a compiled asset and cannot be edited from a BepInEx mod. (GenesisBook's `SwapShaderPatches` shows a replacement shader *can* be swapped in, but the cargo shader does instanced-indirect drawing and icon-atlas lookup — far more than the vein colour shader it did that for. Note it does **not** draw a proliferator arrow: that was assumed here for a long time and is measured false, see above.)

**Sizing: `short`, never `int`.** Max total is `255 × 4 = 1020` — 11 bits. By alignment the struct becomes **36 bytes with `short`** (stack@0, inc@2, item@4, pos@8, rot@20) and **40 with `int`**. `cargoPool` is uploaded to the GPU in full every frame, so `int` buys nothing and costs 25% bandwidth instead of 12%.

**The dodge worth trying first:** keep the CPU struct at 36 but **repack into a 32-byte render array inside `Draw`** and upload that. The shader then never changes. The game already uploads the whole array every frame, so this turns "upload" into "repack + upload" — same order of magnitude, and parallelisable.

**And the repack does not need to know what the shader does with `inc`.** Write `min(255, trueInc)` into the render copy's byte and the shader receives, bit for bit, exactly the byte it receives today (`CargoIncClampPatches` already clamps at that value). Rendering is unchanged *by construction*, whatever the shader's function turns out to be. The shader question is therefore decisive for **the per-item-rate design only** — that one changes the meaning of the byte the shader reads, with no repack step to launder it. Settle it with `CargoShaderIncProbe` before choosing that route; the widening route can ignore it.

**Do not hand-write 46 transpilers.** Since a preloader is rewriting the assembly anyway, this is *one* Cecil pass with assertions: field type `byte → int16`; the 29 signatures `Byte`/`Byte&` → `Int16`/`Int16&`; then the mechanical IL fixups `conv.u1 → conv.i2`, `ldind.u1 → ldind.i2`, `stind.i1 → stind.i2`, `Write(Byte) → Write(Int16)`, `ReadByte() → ReadInt16()`.

**A retracted claim, kept because the mistake is instructive — and it was independently re-made and re-caught.** Mid-analysis this file's author asserted that converting at "the belt API boundary" would collapse the change to a handful of methods. That was reasoning from the shape of the code rather than measuring it, and it is wrong: the byte-typed API *is* the boundary. A later session recommended the per-item-rate design on the same unmeasured intuition, then measured it: **59 call sites in 23 methods on the pick side, 50 in 22 methods on the insert side**. That measurement is what inverted the recommendation — both designs pay the same boundary, but widening pays it as one assertion-checked Cecil pass while the per-item-rate design pays it as 109 hand-written semantic edits, each of which fails *silently* when wrong. **The preloader is not the cost; it is what makes those call sites cheap.**

## Cargo.inc widening — `ProjectEden.Preloader/`

The repo's one preloader. It changes `Cargo.inc` from `Byte` to `Int16` plus the byte-typed API that carries it, so a 255-layer stack can hold `255 × 4 = 1020` proliferator points instead of wrapping at 255.

**Build and deploy are deliberately separate.** `dotnet build` compiles the preloader but does **not** install it; `-p:DeployPreloader=true` does. A preloader rewrites the whole assembly before the CLR sees it, and a mistake surfaces as the game failing to start with a type-load error that points nowhere near the patcher — none of this repo's usual method (read the IL, report a match count, loud-fail on zero) is available at that point.

**So it is verified offline instead: `tools/verify_preloader.ps1`.** It loads the real patcher DLL, runs it against a *copy* of `Assembly-CSharp.dll`, writes the result, then **re-reads the result and asserts against it** — verifying the end state, not the transform's own report. Cecil's willingness to write the assembly at all is itself a structural check. Ten assertions now (it started at four); `-Config` picks which build to drive. **Run it after any game update, and with `-Config Release` before packaging** — it defaulted to `bin\Debug` while release packaging took `bin\Release`, so a package once shipped against a verification that never touched the binary inside it. Same sources is an argument, not a check.

### Analyse first, then mutate

`CargoIncWidener.Apply` runs the whole transform **twice** — once read-only, and only if that pass reports zero blockers does it run again to mutate. This is not defensive decoration: the transform walks and edits in one pass, so discovering an unfamiliar shape halfway through would leave a half-rewritten `Assembly-CSharp` that cannot be recovered. Better to change nothing and let the runtime clamp keep covering (the game still runs, just capped at 255).

It paid for itself immediately, catching three real defects **before** anything was written:

- **Overload collision.** `split_inc` has two overloads with the same name, same declaring type and same parameter *count* — only the types differ. The byte one is belt-side; the Int32 one is the storage ledger and must not be touched. Matching on name+count mis-flagged **20** Int32 call sites. Match on the resolved `MethodDefinition`, never on name and arity. (Same family as the `CodeMatch(OpCodes.Call, null)` trap already recorded.)
- **Arguments are expressions, not instructions.** Scanning backwards for a run of address-push opcodes breaks on `StorageComponent.AddCargo`, where the arguments are `ldloc.s V_5` / `ldarg.1 ; ldflda Cargo::inc` / `ldloc.s V_7 ; conv.u1` — walking back hits `conv.u1` first and stops. Argument boundaries must come from **backward stack-depth simulation**; the address is then produced by the *last* instruction of each argument's range.
- **Widening the field is not enough.** `PilerComponent.cacheCargoInc1/2` are Byte fields that cache a cargo's whole-stack points — points would be truncated at the "stack it up" step, and those fields are serialized too. They are now found by **discovery** ("a Byte field assigned from `Cargo.inc`"), not by a hardcoded name, so a future version's new cache field gets caught instead of silently dropping points.

**Truncation hides in every shape the value can travel, so stop enumerating shapes and assert the invariant.** Three separate rules were written, one per discovered shape: `conv.u1` before a `stfld` into a widened field; `conv.u1` at the end of a call argument; `conv.u1` before a `stloc` whose local is later stored into a widened field. Each was added after a symptom, and each missed the next shape — the automatic piler broke on a fourth (`conv.u1 ; stloc V_17 ; … ; AddCargo(item, level, V_17)` — truncation into a local that reaches a widened *argument*), reported as **"the piler eats items and shows negative numbers"**: the emitted stack's `inc` was cut from ~20000 to 32, and the remainder `cacheCargoInc1 = total − 32` then accumulated until it wrapped Int16 negative. Two symptoms, one missed truncation.

`SweepTruncations` now runs last in every method and ignores *how* the value got there: **a `conv.u1` is legitimate only if its destination is still one byte.** Destination already Int16 → it is truncating on the way in → convert. Legitimate cases survive untouched (`PilerComponent.cacheCdTick` really is a Byte). The verifier asserts the same invariant independently, on the re-read file — and it earned its keep immediately, catching three more instances in `InserterComponent`'s three tick variants that the shape rules had also missed.

**Selecting parameters by NAME is wrong, and a checker that shares that rule cannot catch it.** The first version widened byte parameters whose name started with `inc` or `stack`. The same belt chain calls them other things:

```
PlanetFactory.InsertInto(…, Byte itemCount, Byte itemInc, Byte& remainInc)
CargoTraffic.TryInsertItem(…, Byte itemCount, Byte itemInc)
CargoTraffic.PutItemOnBelt(…, Byte itemInc)
```

None of those match, so none were widened — and that is precisely the path a miner or water pump uses to put cargo on a belt. Symptom: stacking configured to 5000, pump output still byte-capped. **Every check reported "passed"**, because checks 1–3 selected by the same names the transform did. A verifier that shares the transform's discriminator is blind to exactly the transform's blind spot.

Two fixes, and the second is the load-bearing one:

- **`Propagate`** walks the call graph to a fixed point: a byte parameter passed as the argument for an already-widened byte parameter must itself be widened. Names only seed the set; dataflow decides it. This alone took `conv` fixes from 26 to **76** and call sites from 111 to 131.
- **A name-independent assertion**: nothing in `{CargoPath, CargoTraffic, CargoContainer, PlanetFactory, StorageComponent}` whose name matches the cargo in/out chain may retain *any* `Byte` parameter. That check immediately found a second leftover propagation could never reach — `InsertInto`'s `out Byte remainInc`, which is *produced inside* the method rather than passed in, so backward propagation never visits it. It is now named explicitly in `ExtraParams`, with the reason recorded there.

**Inserting instructions can silently break *pre-existing* branches, and this one reached the game.** The first deployment crashed on startup with `NullReferenceException ... DMD<CargoContainer::Import> @ IL_01b9: br.s`. Cause: the injected version branch grew `Import` by ~26 bytes, and two vanilla **short** branches (`br.s`, `blt.s`) that jumped across that region no longer fit their displacement in a signed byte. **Cecil does not auto-widen `br.s` to `br`** — it writes the truncated displacement, which lands on no instruction boundary. Nothing complains during the rewrite or the write; it surfaces only when Harmony/MonoMod later reads the body, and the stack trace names the *branch*, not the cause.

The fix is `MethodBody.SimplifyMacros()` before any length-changing edit and `OptimizeMacros()` after (both from `Mono.Cecil.Rocks`). Two follow-on traps came with it: `SimplifyMacros` expands `ldc.i4.2` into `ldc.i4 2`, which broke the version-constant matcher — and it broke it **only in the mutate pass**, i.e. after the analyse pass had already approved, which is exactly the half-rewritten-assembly outcome the two-pass design exists to prevent. Any matcher that runs after a simplify must accept both the macro and the long form.

`verify_preloader.ps1` now has a fifth assertion for precisely this: after the write/re-read round trip, **every branch and switch target must still resolve to an instruction in the body**. Only a re-read of the written file can see this class of damage.

**Truncation can hide one hop away from the field, and only the generalised checker found it.** Widening `stack` exposed a defect the `inc` pass had never triggered: `PilerComponent.InternalUpdate`'s unstack path computes `(byte)(cargo.stack / 2f + 0.5f)` into a **local**, and only later writes that local into the (already widened) cache field. The rule "fix the `conv.u1` immediately before `stfld`" cannot see it — the truncation sits before a `stloc`. Field widened, value still truncated on the way in; an 8191-stack would have split to 255 **silently**. `FixFeederLocals` now finds locals that end up written into a widened field, widens them, and converts the `conv.u1` feeding them.

It was caught only because the verification script was generalised from `inc` to both fields *before* deploying. Had it kept checking only `inc`, it would have reported "all checks passed" for a stack widening it never looked at. **When a transform grows a new case, grow its checker first.**

The verification script itself produced two rounds of false failures before it was right — it flagged `stack` (correctly still a Byte, sitting next to `inc` in every signature) and the Int32 `TakeTailItems` family. That is the point of keeping it independent of the transform: the two disagree, and the disagreements are where the bugs are.

### Save format: version 3, with a migration branch

`CargoContainer.Export` writes a version int (vanilla: `2`) and then writes each cargo **field by field**, so widening the field without touching serialization would silently truncate 1020 back into a byte on every save. The preloader bumps the version to **3** and writes `inc` as `Int16`; `Import` reads that version into `V_0` and branches on it, so **saves made before the mod still load** (byte-wide, values were ≤ 255 anyway). The reverse does not hold — a version-3 save cannot be read without the mod, which is the already-ratified "the save follows this mod" policy.

`PilerComponent`'s own version is not retained by its `Import`, so there is nothing to branch on; its two cache fields are instead **clamped** (`Math.Min(x, 255)`) before being written at the original byte width. Bounded and deterministic: at most one in-progress stack per piler loses precision across a save, and it degrades rather than wrapping.

### The verifier covers one stage of five, and that produced a false finding

**`tools/verify_preloader.ps1` invokes `CargoIncWidener.Apply` directly. The shipped
`Patcher.Patch` chains five stages** — widener, `QualityFieldAdder`, `QualityChannelBuilder`,
`QualityTransform`, `QualitySaveExtender` (plus `SerializationFixer`). So the binary the script
writes and asserts against **has none of the quality rewrite in it**, and it still prints
`all checks passed`.

That is not a latent risk; it produced a wrong answer. A census run over that binary reported
`CargoPath::TryPickItemAtRear` and `CargoTraffic::TryPickItemAtRear` as *not twinned*, and a
preloader change was designed on top of that. Against the **real** pipeline both methods carry:

```
CargoPath::TryPickItemAtRear     00DE: ldfld Cargo::qua -> 00E3: stsfld Q0
CargoTraffic::TryPickItemAtRear  008D: ldfld Cargo::qua -> 0092: stsfld Q0
```

**The rule this file already states and this violated: when a transform grows a new case, grow
its checker first.** The script now enumerates every `*.Apply` stage in the preloader assembly
and prints a WARNING naming the ones it does not drive — so "passed" can no longer be misread as
"the quality rewrite is validated".

**And the general habit: before measuring a rewritten artifact, prove the artifact is the one
that ships.** One `Cargo::qua` field lookup would have caught it — the patched copy had zero
quality twins, which is impossible for the real pipeline.

To produce the real artifact, drive `Patcher.Patch` (not a single stage), with `BepInEx.dll` and
`0Harmony.dll` loaded first — the patcher logs through BepInEx and otherwise throws only at
`Invoke` time with an assembly-load error that names nothing of ours.

### 品质是 Int32，跟着原值收窄是本期最贵的一类错——三次

品质的目的地**全是 Int32**（孪生字段、侧信道寄存器、孪生局部），而原版的载荷字段是窄的
（`Cargo.inc`、`InserterComponent.itemInc` 都是 Int16）。发射器靠**重放原值表达式**拼出品质
表达式，于是原值末尾那条 `conv.i2` / `conv.u1` 会被一起重放到品质上——**截掉高位**。

同一个错在一期里出现了三次，位置各不相同：

1. **复制传播的合成**认不出 `ldloc ; conv ; stloc`（旁边两个分支早就跳了 conv，只有这一支没跳）
   → 分拣器整族不搬品质 → 「储物柜里品质是 0」，查了四轮。
2. **转发实参重放**带上了 conv → 写进侧信道的品质被截断。
3. **字段赋值发射器**同样带上了 conv → 写进 `itemQua` 的品质被截断（一次扫出 22 处）。

**症状按档位才发作，这是它难查的原因。** 铜每件 50 分正常，铁每件 100 分变负数——同样堆叠下
铁的总分翻倍后越过 32767。而上限巡检接不住：它开头就是 `if (qua <= 0) return;`，
所以负数会在存档里**永远待下去**（修法与当年给 `Cargo.inc` 加写入器同理：病因修掉，存量也得清）。

两条防线，都不靠「下次记得」：

- **剥离放在 `Build` 的单一出口上**（`StripNarrowing`），不是逐个发射器改。逐个改必然漏一个，
  而漏掉的那个不报错。
- **每次启动都断言**（`AssertNoNarrowedQuality`）：全模块扫描，任何一处「收窄之后写品质」
  都是 Blocker，1c 整体不生效（游戏照常能玩，只是没品质），而不是静默把数截成负数。

### A scrub is not coverage — the census that said the sorter was fine

**`stsfld Qn` appears in two opposite roles**: carrying quality outward, and `ldc.i4.0 ; stsfld Qn`
**clearing** it. A census that counts "does this method touch the channel" scores both as coverage,
and that is how `InserterComponent::InternalUpdate` was cleared as healthy — it has 8 channel
writes and **every one of them is a scrub**.

Re-run excluding scrubs and the leak list goes from 94 to **178**, with the decisive entry being all
three sorter tick variants (`InternalUpdate`, `InternalUpdate_Bidirectional`, `InternalUpdateNoAnim`):
they move `itemInc`, never `itemQua`, and scrub the channel after every `PickFrom`. Since the sorter
is the thing between a belt and a storage box, **quality could never reach a box** — the reported
symptom, four rounds after the refinery itself was proven correct.

```
011B: ldloca.s V_0          // out stack
011D: ldloca.s V_1          // out inc  -> a LOCAL, not a field
011F: callvirt PlanetFactory::PickFrom(...)
0124: ldc.i4.0
0125: stsfld Q0             // no `ldsfld Q0 ; stloc <twin>` was ever emitted, so the scrub wins
```

`ScrubAfterCalls` skips the scrub when it sees a paired `ldsfld Qn ; stloc` read-back. Here the
transform never emitted that read-back, so the guard had nothing to recognise and the scrub ran.
`Unhandled` and `Pending` both came back **empty** — the transform believes it handled everything.

**Two rules out of this.** *A diagnostic that cannot distinguish a write from an erase measures
nothing*, and it is the same family as "log the boring state": the distinction has to be in the
instrument, not in the reading. And: **when a census clears a suspect, check what the census counted
before believing it** — this one cleared the actual culprit and sent four rounds elsewhere.

### 「守恒」在合成上是假的——一条口号把算术后果当成了设计

品质的规则原本写着「**品质是可加点数，永远守恒，只会被稀释**」，三件事共用一条：
合并相加、拆分按比例、合成求和后按产出件数摊开。前两件成立，**第三件不成立**。

合成把多件变少件，所以每件分数**必然往上翻**：50 分的铁块 ×2 加 50 分的齿轮 ×1
造一台电动马达，就是 150 分一台。而翻多少倍由 `requireCounts` 决定——那是个纯粹的
平衡数字，没人是为品质挑的（4:1 的配方翻四倍，1:4 的砍四分之一）。
**它不是设计，是算术漏出来的**，而口号把它盖住了。

更硬的证据是它和设计自己的头条冲突：启动日志每局都打「品质的来源只有提纯厂」，
可在求和规则下，顺着生产链每一级都在凭配方比例造品质，铁块 50 → 齿轮 100 → 马达 200。

**而且它已经在四处长出了互相矛盾的夹子**，是玩家问「为什么是 100」时扫 `MaxPerItem`
的每一处用法才发现的：机器合成不夹、手搓夹在 100、存量巡检每 30 秒把箱子夹回
`件数 × 100`、显示也夹在 100。于是同一台马达存的是 150、显示 100、进箱子 30 秒后
真的变成 100——**玩家两次看到的都是 100，中间那个数变过**。

改成**按件数加权平均**（产物每件分数 = 各投入每件分数按件数加权平均）一次解决三件事：
口号变成真的、提纯厂重新是唯一来源、而且**构造上就出不了上限**，四个夹子里三个可以退役。
代价要说清楚：**总分不再守恒**（多件变少件时点数会少）——但守恒从来不是给玩家的承诺，
「好料造好东西」才是。

**一般化的两条：**

- **一条规则如果对 A、B 成立而对 C 只是「看起来像」，它在 C 上就是未经决定的。**
  口号越顺口越危险——「只会被稀释」读起来像个不变量，实际只是两个场景的巧合。
- **同一个上限在四个地方各夹各的，是「规则本身错了」的征兆，不是四个小 bug。**
  正确的规则通常不需要夹子；需要在四处补夹子，说明被夹的那个量本来就不该长那么大。

### 擦除在**被调方体内**时，调用方那边再怎么转译都够不着

「擦除不等于覆盖」这条已经记过一次（分拣器那族）。这次的形态更窄，也更彻底：
**擦除发生在被调方的方法体里**，于是调用方读到的必然是 0，而调用方那边**没有任何**
转译能补救——要补救的那条指令不在它的方法体内。

`Player.TakeItemFromPlayer(ref itemId, ref count, ref inc, bool fromPackage, ItemBundle)`
两条分支都是这个形状：

```
// 背包那一支
0020: callvirt StorageComponent::TakeTailItems(...)   // 被调方把拿走的品质写进 Q0
0025: ldc.i4.0 ; stsfld Q0                            // 紧接着擦掉

// 手上那一支
00A6: call ProjectEdenQualityChannel::Split(...)      // 算出了拿走的那一份（V_5）
00D8: ... sub ; stsfld Q0 ; call set_inhandItemInc    // 只拿它算了个余数写回手上
00E6: ldc.i4.0 ; stsfld Q0                            // 然后擦掉，出参那一半从没发布过
```

`inc` 是**出参**，按协议它的孪生就该是「返回前写 Q0」，而这里两条分支都没有发布，
只有擦除。于是 `EntityFastFillIn` 读回来永远是 0——**每一步都成功，功能整个不在**。

**出路只有两条，选第二条。** 一是改 preloader 让这个方法发布出参品质（要教变换认识
「出参的孪生是寄存器」这件事，是一大块新语义）；二是**在调用方那侧量**。
这里选量，而且它**不是近似**：玩家那一侧（背包格子、手上那一格）都是孪生过的，
品质已经正确扣掉了，所以「玩家少了多少分」精确等于「机器该多多少分」。

**一般化：判断一条侧信道能不能在调用方补救之前，先看擦除指令在谁的方法体里。**
在调用方体内（分拣器那族）→ 转译器能救；在被调方体内 → 只能改 preloader 或者改为测量。

**同一个形状在 `Player.UseHandItems` 上又出现了一次，而它旁边还藏着一个更糟的。**
那 61 条指令里两个分支各漏一半：分支 B（整摞用光）在 `0061` 发布品质、`0070`/`007B`
两次擦除后返回，和上面一模一样；**分支 A（只用掉一部分）根本不扣品质**——
`split_inc` 只劈了 `inc`，没有对应的 `ProjectEdenQualityChannel::Split`，
于是 `set_inhandItemInc(剩余 inc)` 的孪生把 `qua` 写成了**原始全额**。
手上剩下的货顶着整摞的点数，**单件分数越拿越高**。

两条都由一个前后置对修掉（量整摞的分和件数，按用掉的件数把剩下的写回去），
而**两个分支共用同一个式子**：分支 B 里原版已写 0，而「原分 − 全额 = 0」，两者一致。

**而修好之后它仍然一行都不打，因为扣料压根没走那个作用域——本 mod 自己的作弊开关挡的。**
`建造秒完成` 默认开着，而它是**本仓库自己重写的一条建造路径**：`InstantBuildPatches.Pay`
从 `ConstructionBeforeGameTick` 的后置里用 `player.package.TakeTailItems` 付账，
**在 `QualityBuildPatches` 那七个方法的作用域之外**（五把工具的 `CreatePrebuilds` +
`DoUpgradeObject` + `PlaceItems`）。更糟的是它当时还特意把侧信道**清零**了，
注释写着「建好的建筑不保留品质（建筑没有品质槽位）」——那句话在写的时候对，
效果层出现之后就过时了：建筑确实没有品质槽位，但材料的品质会折进它的耗电。

于是玩家放下一座 50 分材料的建筑，**日志一行没有、电费一分不省**，而每一步都「成功」了。

**这是「本仓库为某个功能加的规则，悄悄限制了后来加的另一个功能」那一类，
而这次两边都是我们自己的**——和催化剂槽位撞 `SyncStorageLayout`、钻头槽位撞
`StorageExpandPatches.StorageCount` 完全同形。**给新功能划作用域时，
要问的不是「原版从哪里扣料」，而是「这个存档里所有会扣料的路径有哪些」，
包括本 mod 自己新开的那些。**

修法是让那条路把品质接住（清 → 调 → **读** → 再清，而不是清 → 调 → 清），
按桩号累计到付清为止，再挂进已有的 `QualityBuildStore.SetPending`；
后面 `AddEntityDataWithComponents` 的 `Promote` 一路照旧。
它还得有**自己的一次性日志**——那条路不走「建造扣料」，少了这行就又分不开了。

**顺带一条协议上的混淆，值得单独记：「清」和「读」不是一回事。**
那处原本写的是「调用前后各清一次」，理由完全正确（不清前面会消费别人留下的值，
不清后面会把值留给下一个人）。但**出参方向的正确做法是「读回来再清」**——
直接清掉等于把被调方刚算好的答案扔了。同一个 `Gate()` 式的写法服务两个方向时，
读起来对称，实际不对称。

**这一条还顺带证明了「状态行 ≠ 事件行」那条规矩的价值，是从反面证明的。**
玩家问「建筑按分数省电了吗」，日志里事件行 0 条——而那**分不开**「补丁没生效」和
「这局没建东西」，因为这个功能**没有开机状态行**。代价是回去逐条读 61 句 IL 才答得上。
本文件记过六次，这是第七次：**状态行回答「接上了没有」，事件行回答「它决定了什么」。**

### Quality on the pick side: the callee wrote it, our gate threw it away

`CargoWidening.Gate()` clears the side-channel registers around **every** call this mod makes into
the game's transport API. Its own doc comment says pick-type methods are the reverse direction
(callee writes, caller reads) "so clear again after the call" — and that is exactly what made the
quality vanish: the callee *had* written `Q0 = cargo.qua`, and the `finally { Gate(); }` wiped it
before anything read it.

So the fix is **read, then clear** — never skip the clear (a register outliving one call is how
quality gets invented rather than lost; measured once at 1010 per item against a cap of 100).
`PickAtRear` now has an `out int qua` overload; the old signature forwards to it, so call sites
that do not want quality are unchanged.

**The asymmetry worth remembering: on the insert side the protocol is "caller writes before the
call", on the pick side it is "callee writes before returning".** One `Gate()` helper serving both
directions reads as symmetric and is not.

### `ioTargetTypedId` 的低 24 位不是实体号，每个分支各用各的池子

`PlanetFactory.PickFrom` / `InsertInto` 的 `UInt32` 重载开头是两句掩码：

```
0008: ldarg.1 ; ldc.i4 16777215  ; and ; stloc.1   // id   = typedId & 0x00FFFFFF
0010: ldarg.1 ; ldc.i4 -16777216 ; and ; stloc.2   // kind = typedId & 0xFF000000
```

然后按 `kind` 跳七个分支，而**每个分支把那 24 位当成自己那张池子的下标**——
`0x01000000` → `cargoTraffic.beltPool`，`0x02000000` → `factorySystem.assemblerPool`，
再往后是实验室 / 储物箱 / 物流站 / 电站。**没有一个分支查 `entityPool`。**
`Int32` 重载才是按实体号来的（它开头就是 `entityPool[entityId].beltId`）。

把低 24 位当实体号用**不会越界、不会抛异常**：它会查到一台毫不相干的机器，
然后把那台机器的品质扣掉，发给这一次搬的货。**分数长在错的地方，一个字不报。**
判据只能是先看 `kind`。

**两个重载不是转调关系**（实测 1087 条 vs 1026 条指令，各自一份完整实现），
所以按名字选目标的补丁必须两个都覆盖——而这又和另一条规矩撞上：
它们的 `out stack / out inc` 被 preloader 加宽成了 `Int16&`，
所以只能用 `TargetMethods()` **按首参类型**挑，参数表一写出来就静默解析不到。

一般化：**一个 id 参数叫什么名字不是证据，解码它的那几条指令才是。**
和「一个没有读者的具名常量只是主张」同一族。

### 自动属性把一整条路藏起来了——名字启发式的第五次漏

玩家报的是「合并两堆铜块，品质归零，而且旁边那几堆也跟着变 0」。根因是**鼠标手上那一格**
（`Player.inhandItemInc`）：拖拽、拆分、手动合并、Shift 塞进建筑，全都要在手上过一道，
而它整个不搬品质——手上没有品质，放下去就是往目的地写 0，把原来那一堆的分数冲掉。

**它同时躲过了三道关，而三道关用的是同一种判据：名字。**

| 关卡 | 判据 | 为什么漏 |
|---|---|---|
| 建孪生字段（1a） | `TwinName` 的四条命名规则 | IL 里的名字是 `<inhandItemInc>k__BackingField`，带尖括号，四条全不认 |
| 选载荷参数 | 形参名 `LooksLikeInc` | 唯一写入口是 `set_inhandItemInc(Int32 **value**)`，名字是编译器给的 |
| 切语句 / 选方法 | 方法体里有没有载荷字段指令 | 15 处写、81 处读**全部**走访问器，一条 `ldfld` 都不出现 |

三条全部改成**按形状判定**：后备字段剥壳再套回去；方法体正好是
`ldarg.0 ; ldarg.1 ; stfld 载荷 ; ret` 的就是平凡 set 访问器，它唯一的形参就是载荷参数
（`TrivialSetterSlot`）；而 `Norm` 把**一次平凡访问器调用归一化成一次字段访问**
（`TrivialAccessor` → `ldfld:PAY` / `stfld:PAY`），于是整张形状表一条都不用为属性名重写。

**这是名字启发式第五次漏东西**（前四次：`_stack`、`itemInc`、`cacheCargoInc1`、这次），
而这次的形态最隐蔽：前四次至少有一条 `ldfld`，这次连指令都没有。
本文件早就写着「按名字挑，就会按名字漏」——**代价是那条规矩没被推到「凡是判据是名字的地方」**。

顺带修掉三处同源的脆弱，每一条都是「表面上的小事挡住整条路」：

- **`ExpectedFields` 相减相错了。** GPU 那三个不能加字段的载荷（`TrashObject` /
  `DroneData` / `CourierData`）**本来就不在 `DeclaredPayload` 里**——是并列的另一张表，
  不是它的子集。第一版把两者相减，于是 30 条清单期望出 27 个字段，1a 被自己的断言挡死，
  报错还写着「载荷清单和程序集对不上」。**派生一个数之前，先确认两张表的包含关系。**
- **「读一次载荷、送进一个带载荷形参的调用」不再逐个形状列举。** 前缀
  （`get_inhandItemId`、`get_mecha`、几个 `ldc`……）的组合是无穷的，而要做的事只有一件：
  在那个 call 之前把品质写进寄存器。按**语义**归一化成 `call:forward`，一条规则收掉十三种形状。
  这和「整条记录读存档 → `SaveReadCore`」是同一个路子。
- **「纯取值」改成递归判定**（`IsPureCall`）。原来只认三条指令的取值器，而
  `UIMechaWindow::get_mecha` 是 `ldarg.0 ; call get_data() ; isinst Mecha ; ret` ——四条，
  中间还有一次转型，于是 `mecha.player.inhandItemInc` 这一族整个重放不出来。

**核对末态时又抓出两个，而且两个都只在真正的改写里发作——分析遍看不见。**

- **一个判定的输入被这一遍自己改掉了。** set 访问器本身也要孪生，方法体从四条变成七条
  （多了 `qua = Q0`）。于是「这条 call 是不是平凡访问器」这个**现场解析方法体**的判定，
  在发射遍走到一半时开始返回 false：分析遍全认得，发射遍认不得，而且**一声不吭**——
  那些语句直接从语句表里消失，没有 Unhandled、没有 Pending、没有 Blocker。
  实测 15 个写入口掉了 3 个。修法是**在任何改写之前建表、两遍共用**（`BuildAccessorMap`）。
  **规则：一个判定如果读的是这一遍会改的东西，它必须在改写开始前就固化。**
  这是「两遍式」这套设计的一个盲区——两遍保证的是「表没齐就不改字节」，
  保证不了「第二遍看到的世界和第一遍一样」。
- **同一个目的地，两条路都写，后写的赢。** 第一版让调用点在 call 之前直接写孪生字段,
  紧接着那次 call 就用 `Q0` 盖掉——而 `Q0` 这时往往已经被 `ScrubAfterCalls` 擦成 0
  （`SetHandItems` 的品质由 `TakeItemFromGrid` 的出参接进局部，那次调用之后 Q0 就被擦了）。
  表现是「每一处都写了，手上还是 0」。现在调用点一律写寄存器，由访问器落地。
  **规则：一个字段的孪生只能有一个写入口；协议是「谁落地谁负责」，不是「两边都写保险」。**
- **顺带补上第三种转发来源：常量。** `X.SetInc(0)` 这种「清空」原先不写寄存器，
  于是被调方读到的是上一个人留下的品质——`ScrubAfterCalls` 擦的是 call **之后**，
  救不了这一次。**品质是 0 和「不用管」是两回事**，写进去才是。孪生语句 1038 → **1105**。

末态（跑完整条流水线、写盘、再读回来核对）：`Player.<inhandItemQua>` 存在；
写入口 1 处（值取自侧信道），读它的方法 **23 个**——含 `UIStorageGrid::HandTake/HandPut`、
`Player::UseHandItems/PutHandItems/ThrowHandItems`、`PlanetFactory::EntityFastTakeOut`。
孪生语句 986 → **1105**，方法体 189 → **201**，孪生局部 251 → **268**，侧信道 785 → **887**；
`Unhandled`/`Pending`/`Blockers` 全空，26168 个方法体悬空分支 0、收窄后写品质 0。

**最后一条，和 IL 无关：`PlanetFactory::_test_take_player_inhand_inc` 名字里写着 test，
差点被当成调试残留归进「不用孪生」。** 数了调用点才发现有两个，而且都是 Shift 点一下把
手上的东西塞进建筑 / 传送带走的路（`EntityFastFillIn`、`BeltFastFillIn`）。
**名字像什么不是证据，调用点才是**——和「一个没有读者的具名常量只是主张」同一族。

### 加宽过的**字段**和加宽过的**签名**是同一个坑，而字段这一半没写下来，于是又崩了一次

```
MissingFieldException: Field not found: byte Cargo.stack
  at QualityRepairPatches.ReportArrivalOnce
```

本文件为**方法签名**写过这条——所以 `CargoWidening` 用本程序集声明的委托去调游戏的搬运
API，绝不直接 `call`。但同一条对**字段**同样成立，而那半句从来没写下来：插件是按
**未加宽**的 `Assembly-CSharp` 编译的，源码里一句普通的 `cargo.stack` 会发出
`ldfld byte Cargo::stack`，而运行时那个字段是 `Int16`，**解析不到**。

编译不报错，Harmony 不报错，`verify_*` 也不报错——它只在那行代码**第一次真的执行**时炸，
而这次它挂在每 30 秒一趟的巡检上，所以是进游戏几十秒后崩。

**规矩：凡是 preloader 动过宽度的东西，插件侧一律运行时绑。** 现在这一族是
`Cargo.inc` 和 `Cargo.stack`。读取器按字段**实际的**宽度发射（Byte / Int16 / Int32 都收），
写死任何一种都会在另一种下静默失配——那正是这次的成因。

（`CargoLedgerProbe` 和 `CargoShaderIncProbe` 里还有直接访问，那两个是**加宽生效时整体关掉**的
开发探针，守卫在入口方法上，碰字段的方法根本不会被 JIT——它们是这条规矩的例外，不是漏网。）

### 去重要按「这条指令被谁占了」算，不是按「谁的末指令是它」算

转发合成用 `taken` 防止和真语句抢同一次调用，而 `taken` 里记的是每条语句的**末指令下标**。
`V = X.inc … AddCargo(…)` 这类真语句的末指令是 `stloc`，**不是那次 call**——
于是同一次调用被合成又认领了一遍。两条孪生语句改同一处：先插入的把后面的下标全顶走，
后一条就落成「认得但拼不出来」，4 个 Blocker 全在 `PilerComponent::InternalUpdate`。

它**潜伏了很久**：只有在转发实参放宽到「一整段算式」之后才够得着这些调用点，
在那之前合成只认单条 `ldarg`/`ldloc`，永远碰不到已被真语句占住的那几处。
现在另记一份「真语句覆盖到的全部下标」，只给转发合成用。

同一处还有个对称的坑：**孤儿剪枝要按实参那一段剪，不能按整次调用的区间剪。**
调用区间里总有 itemId、件数这类和品质无关的局部，按它剪会把每一条转发都误杀。
所以转发语句额外记下 `ArgFrom`/`ArgTo`。

**一句话：凡是「这条语句归谁」的判定，判据必须是指令的占用，而不是某个代表性下标。**

### 1c 会撞坏本 mod 自己的转译器——**这是一整类回归，不是一个 bug**

品质改写把语句插进了 **276 个原版方法体**。而本仓库有一批转译器是靠「紧挨着的那几条指令」
定位锚点的——两者一撞，转译器**静默失配**：它要找的形状还在，只是中间多了几条别人的指令。

第一例是活性透镜的传送带入口。`PowerGeneratorComponent.GameTick_Gamma` 里
`PickFrom` 之后原本紧跟 `ldarg.0 ; ldfld catalystId`，1c 在中间插了
`ldsfld Q0 ; stloc`（把出参的品质接回局部），于是两个取货口**同时**失配：

```
[Warning] 活性透镜·诊断：PickFrom 之后没认出「载入 this + ldfld catalystId」。
          实际是 [ldsfld Q0] [stloc.s (23)] [ldsfld Q0]
[Error  ] 活性透镜：应当改写 7 处，实际 5 处（… GameTick_Gamma.取货口 0 …）
```

**这个仓库的老规矩是它唯一被发现的原因。** 每个转译器都报替换计数并在数目不对时大声失败,
所以一次启动的日志就是完整的审计：276 个被改写的方法体里，只有这一个转译器倒了,
其余每一行都报着它期望的计数。**如果当初图省事不报计数，这里会是「活性透镜时好时坏」。**

修法是共用的跳过器 `QualityAccess.SkipChannelNoise`，而不是把窗口从 3 条放宽到 8 条：

- 只跳**认得出是我们自己的**两种成对形状——`ldsfld Q* ; stloc`（接回品质）和
  `ldc.i4.0 ; stsfld Q*`（调用后擦除）；
- **认不出就停**。放宽窗口会让锚点落到一条真正的原版指令上，而那是不报错的
  ——用一个静默的错换掉一个响亮的错，是这份文件反复记过的那类亏。

**给下一次的规矩：在 1c 改写过的方法体里写转译器，锚点之间必须容得下我们自己插的指令。**
哪些方法体被改过，用「原版 vs 改写后逐方法比指令数」量出来，别靠记。

Three things the preloader cannot do:

- **Rendering.** `CargoContainer.Draw` builds `new ComputeBuffer(poolCapacity, 32, …)` — stride is a hardcoded `32` — and uploads the `Cargo[]` raw. At 36 bytes Unity throws on the stride mismatch. A transpiler swaps the `SetData` call for `UploadRepacked`, which copies into a 32-byte render struct. **The `inc` byte in that copy can hold anything** — `CargoShaderIncProbe` measured that the shader never reads it — but it carries the clamped real value so the copy stays self-consistent. Offsets come from `Marshal.OffsetOf` at runtime, never hardcoded: this file is compiled against the *un*-widened layout, so hardcoding would be guessing.
- **A zero-count upload still validates the stride.** `UploadRepacked`'s early-out originally forwarded the array untouched when there was nothing to send — which throws `SetData(): One of C# data stride (36 bytes) and Buffer stride (32 bytes) should be multiple of other`, because Unity checks the stride *before* it looks at `count`. And `CargoContainer.Draw` calls `SetData` **before** its own `cursor <= 0` early return, so this fires on the first frames of every session, when the cargo pool is still empty. Return without uploading instead; never "pass it through unchanged" once the struct width has diverged from the buffer stride.

  This is also the honest limit of `verify_preloader.ps1`: it proves the *rewrite* is structurally sound, and can say nothing about the runtime integration around it. Both of the two crashes that reached the game (the short-branch truncation, this one) were found by launching, not by the harness — the harness grew a check for the first, and cannot grow one for the second.

**A third class got through, and it is the one to remember: structural validity is not type validity.** The quality transform emitted `… mul ; ldc.i4.0 ; add` into `PilerComponent.InternalUpdate`, where the left operand was a `float` — the twin of a `+ 0.5f` rounding term had been rendered as an integer zero. Every offline assertion passed: branch targets resolved, the assembly wrote and re-read cleanly, no signature moved. **The CLR only speaks at JIT time**, and what it says is `InvalidProgramException: Invalid IL code in (wrapper dynamic-method) PilerComponent:DMD<PilerComponent::InternalUpdate>: IL_0588: add` — a Harmony DMD frame, with this mod's name nowhere in the stack and an offset that belongs to neither the original body nor ours.

Two rules come out of it:

- **Anything that generates IL must type-check its own output before emitting it.** `QualityTransform.Build` now runs a stack-type simulation over each twin statement and treats a mismatch as "cannot build" — the gap gets reported instead of shipped. It lives at the single exit rather than in each emitter, so the analysis pass, the fixed point and the emit pass can never disagree about what is buildable.
- **A constant is not a quality source, and `0` has a type.** The bug was `TwinValue` answering "the twin of a constant is 0", which is correct for a whole statement (`X.inc = 4` → `X.qua = 0`) and wrong for one term of an expression. Constants are now lifted out before that question is asked and replayed verbatim; where a genuine zero is still needed, it is `ldc.r4 0` in a float expression and `ldc.i4.0` otherwise.

**And the self-test of that new check was itself wrong the first time.** Reverting *half* the fix left the other half repairing the IL, so the checker stayed silent and looked useless. Reverting half and concluding "the check does not work" is the same error as reverting half and concluding "it is fixed" — **when self-testing a guard, verify the bug is actually back before reading anything into the guard's silence.**

- **This mod's own calls.** `MegaAssemblerPatches` does belt I/O through exactly the API that changed, and our DLL is compiled against the original `out byte inc` — the MemberRef would fail to resolve and throw `MissingMethodException` on the first tick. The fix is **delegate types declared in our own assembly** (which mention only primitives and game classes, never the field width), bound at startup to whichever signature is actually present. No patched reference assembly, no reflection on the tick path, and the un-widened path still works if the preloader ever bails.
- **Standing down the clamp.** `CargoIncClampPatches` gets a `Prepare()` returning `!IsActive`. Its pattern (`stind.i1`, `conv.u1`) no longer exists after widening, so it would loud-fail; and clamping at 255 is precisely what the widening removes.

`IsActive` is read from the **result** — `AccessTools.Field(typeof(Cargo), "inc").FieldType == typeof(short)` — not from whether we think we installed a preloader. `CargoWidening.Report()` prints the state on every launch in both directions, for the reason this repo has now paid for three times.

**Two dev probes are permanently disabled when it is active** (`CargoLedgerProbe`, `CargoShaderIncProbe`): both read `Cargo.inc` as a byte, and both have served their purpose — the ledger probe was the feasibility study for the side-table design that lost to widening, and the shader probe already answered its question. Their guards sit in the *entry* methods so the field-touching methods are never JITted.

## Per-building recipes — `src/Patches/Alloy/`

**The engine does allow a per-building recipe, and this is the only place in the repo that uses it.** 硬质合金 is a WC + Co cermet whose grade is set by the ratio; a slider under the assembler window moves it, the product snaps to one of four pre-registered grade items, and the ratio is per building. Four facts make it work, all read out of IL and none of them guessable:

**1. `recipeExecuteData` is shared, not per-instance.** `RecipeExecuteData` is a **class**, and `AssemblerComponent.SetRecipe` fetches it from a **static `Dictionary<int, RecipeExecuteData>` on `RecipeProto`** (IL 0075) and stores the **reference** (IL 0085). Every assembler running recipe N on every planet holds the *same object*. Mutating `comp.recipeExecuteData.requireCounts[0]` therefore edits the global recipe table — and the machine in front of you does change, so it looks exactly like "I only changed this one". The fix is to point the field at **our own new instance**; `RecipeExecuteData` has a public ctor taking exactly its seven fields, and `Clone()` on the four arrays preserves their lengths. What *is* per-instance: `served[]`, `incServed[]`, `produced[]`, `needs[]` (all `newarr`'d in `SetRecipe`), plus `speedOverride` / `time` / `recipeId`.

**2. Nothing in the tick path re-fetches it.** A whole-assembly scan finds the static dictionary read in exactly three assembler places — `SetRecipe` (once) and `Import` (0x03F5, 0x06AB). `InternalUpdate` and `UpdateNeeds` read the instance field only. So the clone survives, no transpiler is needed and the hot path costs nothing.

**3. Array lengths are load-bearing for the save — but only for a *per-building clone*, and the earlier wording here was too broad.** `AssemblerComponent.Export` reads `recipeExecuteData.requires` / `products` **lengths** (IL 00D5, 019E) to decide how many `served` / `produced` entries to write. Changing a **value** — a require count, `products[0]`, `timeSpend` — is safe. Changing a **length** on a clone is not, and the reason is specifically that **`Import` re-fetches the object from `RecipeProto`'s static dictionary** (IL 03F5 / 06AB) and then resizes `served` / `incServed` / `produced` to *that* object's lengths: a clone's shape provably cannot survive one load, whatever the stream said. Any future "recipe with a variable ingredient list" must allocate the slots up front.

**A *global* edit of the `RecipeProto` itself is a different case and is save-safe** — measured, not assumed, when 处理器 gained a third ingredient (see **Extra recipes**). Both sides then agree on the new length, and the stream is self-describing: `Export` writes **each array's own length before its contents** (IL 00E2 `requires`, 0109 `served`, as a *Byte* — so ≤255 slots), and `Import` reads those counts back and only then resizes to the current recipe. An old 2-ingredient save comes back as a 2-long `served`, gets grown to 3, old amounts kept and the new slot at 0. `LabComponent.Import` is the same self-healing the Universe Matrix's seventh ingredient rides on.

**4. The clone is lost on load, and re-applying it needs *two* hooks whose order is inverted.** `Import` reads `recipeId` back and re-derives from `LDB.recipes.Select` (IL 0181–0199), writing the global object into the field again. `AlloyRatioStore` persists the ratio per `(planetId, entityId)` through `IModCanSave` (SaveVersion **3**; older blocks are migrated, and version 1 has no block at all so `Import` must gate on it). Standard trap-1 shape — except the obvious hook is not enough: **the `GameData.Import` postfix runs *before* DSPModSave's `Post Load` hands us our own block**, so at that moment the store is still empty and nothing is re-applied. Hooking only there loses every saved ratio, silently. `AlloyRatioPatches.ReapplyAll` is therefore called from **both** the `GameData.Import` postfix and the end of `ProjectEdenPlugin.Import`. The tell that found it: the log printed `从版本 2 的存档迁移了 1 台` but never printed the re-apply line at all — because the count was zero, the log statement itself was skipped. **A diagnostic that goes silent when the count is zero cannot distinguish "nothing to do" from "ran too early"**; the re-apply now warns whenever it restores fewer than it stored.

**Hook the callers, not `SetRecipe`.** `SetRecipe(int recipeId, SignData[] signPool)` cannot see the planet. Every caller can: `UIAssemblerWindow.OnRecipePickerReturn`, `BuildingParameters.PasteToFactoryObject(int objectId, PlanetFactory factory)` and `ApplyPrebuildParametersToEntity(..., PlanetFactory factory)` — the last two are the blueprint path, which carries only `recipeId`, so pasted buildings inherit the player's last-used ratio instead.

**Slots are "one balance + N adjustable", and that is what makes ternary tractable.** `slots[0]` is the balance (the base metal); its count is `totalParts` minus the rest, so **dragging any slider needs no coupling rule at all** — the balance absorbs it. Two sliders互为补数 was fine for a binary, but a ternary would have had to answer "drag one, how do the other two split" (proportional? locks?). This is also how real alloy grades are written ("18% Cr, 8% Mn, balance Fe"). Only when there is exactly one adjustable slot is the balance row itself draggable — it is then the unique complement.

**A single-axis grade threshold makes the second degree of freedom decorative, and this was measured, not guessed.** With a linear property model and linear cost, "cheapest mix that still hits the grade" is a linear program, so **the optimum is always at a vertex** — the second slider gets pushed to its limit and decides nothing. Grade thresholds are therefore **multi-axis** (`{"corrosion": 90, "toughness": 48}`): two constraints intersect and the optimum moves to the interior, where chromium buys corrosion but costs toughness and manganese buys it back at a price. Verified by enumeration: 5 of the 9 ternary grades now have interior optima. This is also the same shape as the planned use-predicates (`H≥70 ∧ T≥40`), so grade selection and use filtering end up being one mechanism. Only 不锈钢 / 锰钢 / 铬钒工具钢 are ternary — the other four alloys are genuinely binary and forcing a third slider on them would be fake.

**Continuous in, discrete out — and there are exactly three ints to settle into.** `products[0]` (item id), `productCounts[0]` (yield) and `timeSpend` are all `int` and all safe to change per building; array **lengths** are the thing that must not move. `requireCounts` are `int` too, so **the slider's real resolution is 1/totalParts** (100 parts → 1%). Mixing uses the Voigt–Reuss rule: hardness by power mean p=2 (upper bound), toughness by harmonic mean (weakest link), the rest linear.

**Grades were removed; the ratio now settles into yield and time, not into the product item.** One item per alloy (22 grade items → 7), because `products[0]` is a single int and the *only* way to make the output vary by property is one item per value — and per-stack metadata does not exist anywhere in DSP (`Cargo` is a full 32 bytes; see the cargo stacking analysis). So the four axes are computed live and combined into one scalar that drives `productCounts[0]` and `timeSpend`. Two consequences worth keeping straight:

- **The item tooltip's four rows are necessarily a single fixed set per alloy** (the nominal ratio's values) — properties live on `ItemProto`. The per-building values are live in the ratio panel, and that is the only place they can be.
- **The base for the multiplication must be the recipe proto, not `comp.recipeExecuteData`.** That field is very likely already our own clone, so using it as the base multiplies again on every re-apply and the yield runs away exponentially — silently. `Alloy.BaseYield` / `BaseTime` are read once from `LDB.recipes`.

Dropping grades also deleted the arbitrage that `requireEmptyOutputOnGradeChange` existed to block: nothing in the output buffer can change identity any more.

**Quality is a weighted *geometric* mean of the four axes, normalised per alloy against its own reachable range.** Both halves were forced by measurement:

- A weighted **sum** is blind to an axis going to zero, so dumping every part into one axis scores well — which is not an alloy. A geometric mean goes to zero with any axis, and being non-linear it is what allows an interior optimum against linear cost.
- Enumerating every reachable ratio first showed that the raw quality span differs wildly per alloy: manganese steel moves 23% end to end, vanadium-titanium 1%. Settling off the raw value would have left several sliders inert. `Calibrate` enumerates the grid at registration, records `QMin`/`QMax`, and normalises to [0,1]; it also logs the best/worst ratio with the yield and time each buys, which doubles as the acceptance check for that recipe.

**Do not expect the optimum to be interesting on its own.** Enumeration says 6 of 7 alloys have their maximum-quality ratio at a slider endpoint. What makes the slider a decision is that **the adjustable slots are the scarce metals** — chromium, vanadium, cobalt, tungsten carbide — so "more output" and "spends more of the rare thing" are one lever, and the right setting depends on which of the two is the player's bottleneck. That is deliberately not something the mod decides.

**The slider is hand-drawn `Image`s with polled input, not a `UnityEngine.UI.Slider`** — the build-menu scrollbar already taught this repo not to fight a Unity widget for ownership of a value. Screen→local goes through `UIRoot.ScreenPointIntoRect`.

## One recipe, swappable inputs — `src/Patches/Ammo/`

**This is the idea worth remembering, and it generalises well past ammunition: `requires[i]` is an `int`, so a single recipe can eat *different materials* per building.**

Until now every per-building override in this repo changed *amounts* (`requireCounts`, `productCounts`, `timeSpend`) or the *output item* (`products[0]`). But the input item ids are the same kind of field. `AssemblerComponent.Export` only depends on the **lengths** of `requires` / `products`; every element is free. So:

```
one RecipeProto                       ← the only thing the player sees in the replicator
  requires[0], requires[1]            ← which two materials, per building
  products[0]                         ← which outcome, per building (from a pre-registered set)
  productCounts[0], timeSpend         ← how much / how fast, per building, continuous
```

That buys three things that were previously impossible:

- **N materials collapse into one recipe.** 7 alloys pair into 28 combinations, but the replicator shows **one** recipe and the item grid pays for **5** outcome items, not 28. Without this you would need one recipe per combination, and the replicator cannot add rows (`row >= 8` is skipped) — 28 recipes is a paging problem, 1 is not.
- **The recipe can be a secret.** Nothing in the game's UI lists which pair gives which outcome, because those pairs are not recipes. Discovery becomes a real mechanic rather than a wiki lookup.
- **Inputs and outputs are decoupled.** The outcome set is fixed and small (it has to be — see below); the input space can be large and is free.

**The wall that forces outcomes to be a small set**: anything the player must be able to *distinguish* has to be a separate `ItemProto`. Ammo damage is `ItemProto.Ability` (`TurretComponent.SetNewItem` does `bulletDamage = proto.Ability`; `Mecha.get_ammoDamage` reads it live), rounds-per-box is `ItemProto.HpMax`. Per-stack metadata does not exist anywhere in DSP — the same wall as alloy properties and as the cargo `inc` byte. So: **inputs may vary freely, outcomes must be enumerated**, and the design has to put the interesting variation on the input side.

**Where continuous variation can still live**: `productCounts[0]` and `timeSpend`. Alloy ammo uses damage-in-tiers (5 items) and yield-continuous (no items), which is the cheapest split.

### 合金弹药 concretely

| | |
|---|---|
| Tiers | 5 items (6612–6616), damage and rounds as **multipliers off a vanilla anchor** resolved at runtime — the lowest-ID item of the same `AmmoType`. Verified: 机枪弹箱 (1601), 700 dmg / 30 rounds → tiers 700/945/1260/1680/2240 and 30/33/36/39/44 |
| Pair → tier | Penetration score = hardness × 0.85 + corrosion × 0.15, over the two alloys blended 50:50 by the same mixture rule the alloys themselves use (hardness power mean, **toughness harmonic mean**) |
| Pair → yield | Diminishing returns on toughness, clamped. Brittle stock cracks during forming, so a batch yields less |
| Storage | Reuses `AlloyRatioStore` — it already stores "one `int[]` per building", and two item ids have the same shape. **No SaveVersion bump** |
| UI | The alloy ratio panel in picker mode: rows become `◀ name ▶`, click left/right half to cycle. One panel, two modes; `LayoutRow` must run every refresh or a machine of the other kind inherits the wrong layout |

**The blocker that would have failed silently**: ammo reaches turrets through `ItemProto.turretNeeds`, built by `InitTurretNeeds` at **IL 0x08AB** of `VFPreload.PreloadThread` — right next to `InitFluids` at 0x08B0, and **LDBTool re-runs neither**. `TurretComponent.BeltUpdate` passes that table into `TryPickItem` **as the filter array**, so an unlisted item is never picked up, hand insertion is blocked by the same table, and nothing errors. `ProjectEdenPlugin.RefreshTurretNeeds` re-runs it (wholesale rebuild from `dataArray`, idempotent; `turretNeeds` is `new int[16][]` and ammo types stop at 6, so no resize). Verified in game: `炮塔弹药白名单已核对：Bullet 8` — vanilla 3 plus our 5.

**Known limit, measured not guessed**: the turret window has exactly **three** hand-fill widgets (`handFillAmmoIcon0/1/2`), and vanilla's three bullet ammos fill them, so none of the five tiers appears on those quick-fill buttons. Belt feeding and shift-click insertion both work — both iterate the whole `turretNeeds[type]` array by `Length`.

**Balance observation from the first play session**, recorded because the numbers are not obvious: tier V is 2240 dmg × 8 yield = 17920 damage per craft against tier I's 700 × 20 = 14000, so **the yield penalty does not offset the damage gain**. The actual brake is that every IV/V combination contains 硬质合金, and tungsten is a rare-slot ore with no synthetic fallback. The decision is therefore close to binary ("do I have tungsten"). To make it a gradient instead, steepen `toughnessExponent` / lower `yieldMax` in `ammo.json`.

## Game internals: interstellar ship dispatch

Read out of IL. **Patched** since 1.10.7 (`RemoteDispatchBurstPatches`); the cadence half is still
deliberately unpatched.

`GalacticTransport.GameTick` slices each second into 6 passes gated by `DetermineFramingDispatchTime(tick, t)` (`t=1` → `tick%10`, `t=2,3` → `%30`, rest → `%60`); each pass only visits stations whose `routePriority` matches.

**Two claims that stood here for several versions are measured false, and the second one had been
quoted as a design constraint.** This section used to say `StationComponent.DetermineDispatch` is
*"straight-line code with no backward branch"* that *"examines exactly one supply/demand pair … and
launches at most one ship"*, and prescribed a lever built on that. Against the shipped assembly
(1250 instructions):

- **There are two backward branches**: `0D0C bne.un → 0109` (the outer pair ring) and
  `0B65 bne.un → 0873` (the demand side's inner ring).
- **The method contains zero `stfld` to `idleShipCount` / `workShipCount`** — launching happens
  inside `DispatchSupplyShip` / `DispatchDemandShip`.

"At most one ship" is right as an *effect*; the mechanism is not. The real shape is **identical to
`InternalTickLocal`** — a scan over the whole segment ring that leaves early on the first pair it
settles:

```
0078  if (segment length <= 0) return;      // the only early return; cursor untouched
0109  do {                                  // ring head
          ref pair = remotePairs[cursor];
          ...
          // this pair is settled  → br      0D11   ← LEAVES the ring
          // inner scan launched   → brtrue  0D11   ← leaves
          // power ratio <= 0.1    → brfalse 0D11   ← leaves (left alone)
          // nothing to do here    →
0CD8      cursor++ ; wrap at segment length
0D0C  } while (cursor at entry != cursor);
0D11  cursor++ ; wrap at segment length
0D3A  ret
```

Two identical cursor-advance blocks, one for continue and one for exit — the same mould as the
planetary `1175` / `11A5`. **So the fix is the same one**: redirect the three "settled → leave"
branches through a budget check into vanilla's own continue path at `0CD8`.

**Doing only that measured 2.28× with the budget never once exhausted, which named the next
bottleneck precisely.** Advancing the cursor after every ship makes one evaluation's ceiling *"how
many pairs in this ring have work"* — measured at 2.3 of ~7, with 0% of evaluations stopped by the
10-ship budget. So the remaining limit is **one ship per route per evaluation**, and the fix is a
third destination: **jump back to the ring head `0109` without advancing the cursor**, re-examining
the same pair.

**Retrying a pair is self-limiting by vanilla's own bookkeeping, and every step of that is
measured:**

- **A dispatch debits both ends immediately.** `DispatchSupplyShip` @034A does
  `storage[pair.supplyIndex].count -= carryCnt` *and* @02E9 `other.storage[demandIndex].remoteOrder
  += carryCnt`; `DispatchDemandShip` (which sends an empty ship to fetch, so no `count` write at all)
  does @0305 `+= ` on the demand side and @0340 `-= ` on the supply side. And
  `remoteDemandCount = max - (count + remoteOrder)`, `remoteSupplyCount = count + remoteOrder`. So
  the retry reads numbers that have already been debited.
- **When a side runs out, vanilla itself moves on.** `DetermineDispatch` @0351 / @0359 are
  `if (supply <= 0) goto 0499` / `if (totalDemand <= 0) goto 0499`, and `0499` → `SetPriorityLock`
  → `04A9 br → 0CD8`, the continue tail.
- **A retry cannot spin.** `DispatchSupplyShip` and `DispatchDemandShip` have **exactly one
  `return false` each**, reached from **exactly one branch** — `QueryIdleShip(...) < 0` at `001F blt`
  / `000F blt`. Enumerated: no other source jumps to either `ldc.i4.0 ; ret`. So *"no idle ship"* is
  the only failure mode, and that is already the guard's second gate.

**What the retry path does give up is the ring bound**, because it bypasses the
`cursor at entry != cursor` test. The replacement bounds are the per-evaluation budget and a
per-route cap, and **every ship passes through `BurstMode`**, so both are enforced per dispatch.

**The per-route cap is a fairness knob, not a safety one — and it is load-bearing because of this
mod's own 10M slots.** Vanilla would self-limit on demand; here `storage[].max` is 10,005,000, so
"the demand is satisfied" essentially never happens and one route would swallow the whole
evaluation, leaving the rest of the ring unserved that round. `remoteSameRouteMax` ships at **4**
against a budget of 10, i.e. at least ⌈10/4⌉ = **3 distinct routes per evaluation**; cross-evaluation
rotation is unaffected either way, because budget exhaustion exits through `0D11`, which advances the
cursor. Same family as the catalyst slot colliding with `SyncStorageLayout` and the drill-bit slot
with `StorageCount`: **a rule this repo added for one feature silently constrains a later one.**

**The three-way dispatch is a `switch`, deliberately, and `dup` would have been invalid IL.** A
`dup` + `beq` pair leaves the duplicated int on the stack when the branch *is* taken, and the branch
target must be reached with an empty stack — structurally writable, rejected only at JIT, which is
the exact failure class recorded under the quality transform. `OpCodes.Switch` pops its operand and
dispatches three ways with every target at depth 0. The jump table is built **indexed by the mode
constants** (`targets[ModeStop] = lExit` …) so the constants and the branch order cannot drift apart.

**Why NOT the "call it N−1 more times" lever this section used to prescribe.** Its precondition
does hold (the cursor advances on every early-out; the `0078` return never enters the ring, and
not advancing there is correct). But each repeat re-runs the method prologue — `_tmp_iter_remote++`,
re-deriving the segment bounds, re-entering `Monitor` — where redirecting three branches costs only
"one more slot in the ring". It also cannot interact with the six-pass rotation outside.

**The array-overflow hazard that made the planetary guard mandatory does not exist here, and the
asymmetry is worth keeping.** Planetary `idleDroneCount <= 0` sits *outside* the loop and is never
re-checked inside, so bursting without re-checking runs off the end of `workDroneDatas`. Interstellar
re-checks **twice**: `03F5` / `07F9` inside the ring body (`idleShipCount == 0`, `energy > 6000000`),
and `DispatchSupplyShip` @0017 / `DispatchDemandShip` @0007 open with `QueryIdleShip(nextShipIndex)`
and `return false` on a negative — and `QueryIdleShip` scans `idleShipIndices`, which
`IdleShipGetToWork` keeps in lockstep with `idleShipCount`, so *"an idle ship exists"* and *"a
`workShipDatas` slot is free"* are the same fact. `BurstContinue` copies both gates verbatim anyway,
**to stop early rather than to stop a crash**.

**One of the three rewritten branches is not strictly "a ship went out", and that has to be stated
precisely.** `V_35` (supply site) and `V_43` (demand site) are set to 1 at the *merge point* of the
"dispatched" and "not dispatched" paths (IL `045E` / `0BA0`) — they mean **"there was enough energy
to try"**. So vanilla also leaves the ring when a pair had energy but no ship. Continuing there
would waste one ring slot — except that *no idle ship* is the **only** way `Dispatch*Ship` fails, and
`BurstContinue`'s first gate is exactly `idleShipCount > 0`. The two predicates agree, so no local
variable is needed to capture the return value.

**Threading: main thread, measured.** `GameLogic.OnGameLogicFrame` → `GalacticTransportGameTick` →
`GalacticTransport.GameTick`, one serial loop over `stationPool`; there is **no `_Parallel` variant**
(contrast `FactoryTransportGameTick_Parallel` on the planetary side). So the budget is a plain
static, not `[ThreadStatic]`.

**`_tmp_iter_remote` is a per-call counter written into `ShipData::gene`, and `gene` has no readers
but `Export`/`Import`.** Several ships in one call therefore share a gene value, and nothing acts on
it. Checked because it is exactly the kind of per-call identity that bursting would break.

`remotePairOffsets[7]` splits `remotePairs` into 6 segments, each with its own cursor:

| Segment | Filled by | Pass | Rate | Station's `routePriority` |
|---|---|---|---|---|
| 0 | `AddRemotePair` — ordinary auto-matched pairs | 6 | **1×/s** | `Ignore` (the default) |
| 1 | `AddRouteRemotePair(2)` | 1 | **6×/s** | Prioritize / Only / Designated |
| 2, 3 | `AddRouteRemotePair(3, 4)` | 2, 3 | 2×/s | same |
| 4, 5 | `AddRouteRemotePair(5, 6)` | 4, 5 | 1×/s | same (segment 5 is Prioritize-only) |

So before 1.10.7 a default station holding 20 pairs took 20 seconds to cycle once: **slow fetching is dispatch cadence, not ship speed, carry capacity or storage size.** Giving the *fetching* station a route priority moves its pairs into segment 1 — **6×**, not the 12× this line used to claim (`tick%10` fires 6 times a second, `tick%60` once) — with no code change, which is why the cadence half stays unpatched. The two levers multiply: route priority × `remoteShipsPerDispatch`.

Multiple ships per route already work: `StationStore.remoteOrder` reserves both ends (`remoteDemandCount = max - (count + remoteOrder)`), so the next evaluation sees the reduced demand. The real gates are `idleShipCount > 0` and `energy >= 6 MJ + CalcTripEnergyCost` (which adds a flat **100 MJ per warp jump** — this is what the 30 GW charging power buys). **64 ships per station is a type-level cap**: `idleShipIndices` is a `UInt64` bitmask indexed `1L << (index & 63)`.

**Anchoring the transpiler needed one more step than the planetary one, and that is the transferable
part.** The "cursor +1 with wrap" pattern occurs **6 times** here (`016D, 02C9, 056F, 064E, 0CD8,
0D11`), not twice, so "take them in order" does not work. The rule is three checkable facts instead:
the **exit** block is the unique anchor that reaches a `ret` within 24 instructions before reaching
any backward branch; the **continue** block is the highest-indexed anchor below it; and there must be
**exactly one** backward branch between them, targeting an index above neither — i.e. the ring head.
All of it was simulated against the shipped assembly before the patch was written: 6 anchors, exit
unique at `0D11`, continue at `0CD8`, one backward branch to `0109`, branches to the exit
**2 `br` / 1 `brtrue` / 1 `brfalse` / 0 other**, and all three rewrite sites outside every
try/handler range.

**The retracted lever, and why the retraction matters more than the fix.** This section used to
prescribe *"a reentrancy-guarded postfix calling it N−1 more times"*, and added that the trick is
*"specific to this method and does NOT port to the planetary side"* — because `InternalTickLocal`
fuses dispatch with station charging, the adaptive-interval sampler and in-flight drone simulation.
That contrast was real and it was also **backwards**: the planetary method was patched by redirecting
three branches, and the same redirection turned out to be the better answer *here* too. The sentence
that sent the reasoning the wrong way was the unmeasured "straight-line code with no backward branch"
above it. Same family as *"only 14 `ERecipeType` values remain"* and `kMaxCargoFlowSpeedPerSecond`:
**a sentence in this file is a claim until the IL is re-read**, and a lever derived from an unmeasured
claim inherits its error.

## Game internals: planetary drone dispatch — and why the interstellar idiom does not port

Read out of `StationComponent.InternalTickLocal` (2600 instructions). **Patched** by
`LocalDispatchBurstPatches` (1.10.4); the measurements are recorded here because the shape is not
what the interstellar section above would lead you to predict.

**Two throttles, stacked, and neither is the one people assume.**

```
00A3  if (timeGene % droneTaskInterval != id % droneTaskInterval) goto 1297;  // per-station stagger
010B  if (localPairCount <= 0) goto 11C8;
0117  if (idleDroneCount  <= 0) goto 11C8;     // ← outside the loop. Never re-checked inside.
0123  if (energy <= 800000)    goto 11C8;
0134  localPairProcess %= localPairCount;  int start = localPairProcess;   // V_22
015F  do {                                              // loop head
          ref pair = localPairs[localPairProcess];
          ... three dispatch branches ...
          // dispatched      → br 11A5   (0758, 0ED6, 1173)   ← BREAKS OUT
          // not enough energy→ blt 11A5 (0410, 0EE3)         ← breaks out
          // nothing to do here →
1175      localPairProcess = (localPairProcess + 1) % localPairCount;
11A0  } while (start != localPairProcess);
11A5  localPairProcess = (localPairProcess + 1) % localPairCount;
```

**That loop is a scan for work, not a dispatch loop.** It already walks the entire pairing ring;
it just leaves the moment it launches one drone. So the cap is **one drone per station per assigned
tick**, and the fix is to redirect the three "dispatched" breaks to `1175` — vanilla's own
continue path, which does *identical* work (cursor +1 with wrap); the only difference is the
loop-back test. **Nothing of vanilla's dispatch decision is reimplemented.**

**The stagger is adaptive and tops out at 1, which is why tuning it is not the answer.**
`droneDispatchStatus` is `new byte[30]` (`Init` @0517); each assigned tick zeroes one slot and each
dispatch increments it. Once the cursor wraps (`11C8: if (droneStatusCursor != 0) skip`), IL
11D3–1292 computes `busy = sum / 30` and moves the interval: `busy < 0.75` → `ceil(interval / f)`
with `f = busy*0.25 + 0.75` (**grows** it), `busy > 0.9` → `floor(interval * 0.8 + 0.1)`
(**shrinks** it), 0.75–0.9 is a hysteresis band; then clamped to `[1, totalDrones >= 75 ? 10 : 20]`.
**So the interval bottoms out at 1** — 60 drones/s per station.

**"…and a busy station drives itself there unaided" was written here and is measured false.** On the
owner's save the interval sits at **average 16.5, max 20, with 0–1 of 614 stations at 1**. The
arithmetic says why: `UpdateOutputSlots` runs 3.06M times per 60 s, i.e. ~850 stations × 3600 ticks,
so at interval ≈17 there are ~180k dispatch attempts per minute — against **~13k actual dispatches**.
Only ~7% of attempts find anything to do, so `busy ≈ 0.14`, far under the 0.75 threshold, and the
controller grows the interval until it pins at the cap. (Cap 20, not 10, means most of those
stations carry fewer than 75 drones.)

**The reason there is so little drone work is this mod's own doing**: `MegaVirtualLogisticsPatches`
moves goods directly between mega-building slots and other stations without launching anything
("巨型建筑已改为虚拟物流：储物格之间直接搬运，无人机不再起飞"). So on a mega-building factory the
drone fleet is handling only the leftovers. **Before tuning either throttle, check how much work
exists** — both throttles are downstream of that.

**The interstellar idiom does not port, and this is the part worth remembering.** The section above
*used to* say the lever for `DetermineDispatch` is a reentrancy-guarded postfix calling it N−1 more
times, because that method is dispatch-only and straight-line. **That premise was measured false in
1.10.7** — `DetermineDispatch` is a ring scan too, and it was patched by the same branch
redirection — so the contrast drawn here is no longer between two methods, only between two
techniques. The conclusion below stands unchanged on its own evidence: **`InternalTickLocal` fuses
four jobs**, and re-calling it corrupts three of them:

| IL | what it does | what a second call does |
|---|---|---|
| 0002–0054 | charges the station (`energy += energyPerTick`) | charges twice in one tick |
| 00BD–00F8 | advances `droneStatusCursor` | burns the 30-sample window N× faster, mis-computes `busy` |
| 11C8–1292 | recomputes `droneTaskInterval` | recomputes off that corrupted sample |
| 1297–end | **advances every in-flight drone** (`t += direction × speed`) | every drone flies N× faster |

Suppressing four regions is strictly more invasive than redirecting three branches. **"Method X was
safe to re-call" is a fact about X, not about its neighbours** — check what else is fused into the
body before porting the trick.

**The guard that has to move inside the loop, and why omitting it is a crash rather than a bug.**
`idleDroneCount <= 0` is tested at 0117, **outside** the loop, and the body never re-checks it —
one check suffices when you leave after one dispatch. But a dispatch writes
`workDroneDatas[workDroneCount]`, and that array is `new DroneData[prefabDesc.stationMaxDroneCount]`
(`Init` @0128–0135), i.e. exactly the bound on `workDroneCount + idleDroneCount`. Burst without
re-checking and the second drone writes past the end. The guard is therefore **copied verbatim from
vanilla's own two gates** rather than re-derived — the drill-bit lesson. The two `blt` energy
give-ups stay untouched: they are the exact per-trip cost, already inside the loop.

**Cost shape, stated accurately because the easy claim is wrong in both directions.** The outer
loop's iteration count is *already* bounded by `localPairCount` (the `start != localPairProcess`
test), so bursting **cannot exceed vanilla's worst case**. What changes is the typical case. But it
is not free either: the demand-side branch carries its own inner ring scan (0A3A–0ECF), and that
runs once per *outer* iteration — so the added cost is ≈ extra dispatches × what vanilla's first
iteration cost. Since a *successful* iteration is by definition one that found work early, the cost
tracks the throughput it buys.

**And the first version's speedup number was an artifact — the probe could not have reported
anything else.** It printed `1 + extra/dispatches`, where `extra` counts "we permitted a
continuation". Whenever the per-tick budget is not the binding constraint, `extra ≈ dispatches` and
that expression reads **≈2.00 regardless of the true multiplier**; it duly reported "1.97×" on three
consecutive windows. The true denominator is *attempts that dispatched at least once* (vanilla
dispatches exactly one per such attempt), which those two counters cannot recover. It is now counted
directly: the first dispatch of a call is the one that sees `_left == _max`. Same family as the
status-line lesson — **a reading that cannot vary with the thing being measured is not a
measurement** — and it is the third probe-design mistake recorded in this file.

**The discriminator for the transpiler is branch opcode, not offset.** Five branches target the
exit; `br`/`brtrue` (3) mean "dispatched", `blt` (2) mean "out of energy". The anchor is the
6-instruction `ldarg.0 ; ldarg.0 ; ldfld localPairProcess ; ldc.i4.1 ; add ; stfld localPairProcess`,
which occurs **exactly twice** (the other four writes to that field are `rem` or `ldc.i4.0`) — first
is the continue path, second is the exit. Simulated offline against the shipped assembly before the
transpiler was trusted: 2 anchors, 2/1/2/0, all three rewrite sites outside every try/handler range.

## Game internals: assembler input buffering

Also investigated and **deliberately not patched**.

**GenesisBook does not batch production.** Its mega assembler is `MegaAssemblerSpeed = 300000` (30×), which stays under the engine's one-cycle-per-tick ceiling, so it never needs multi-cycle settlement — `AssemblerComponent.InternalUpdate` runs stock. Its transpiler on `FactorySystem.GameTick` / `_assembler_parallel` is only a pre-hook for belt I/O and power (the shape this repo ported). `RunExtraCycles` is ours alone.

What it *does* batch is the **input request**. Vanilla `AssemblerComponent.UpdateNeeds`:

```csharp
int num2 = speedOverride * 180 / recipeExecuteData.timeSpend + 1;   // 180 ticks ≈ 3s of output
if (num2 < 2) num2 = 2;
needs[i] = served[i] < requireCounts[i] * num2 ? requires[i] : 0;
```

`AssemblerNeedsPatches.CalcNeedsBatch` recomputes it in `long` and caps the effective speed at `timeSpend`, fixing two vanilla bugs: `speedOverride * 180` **overflows Int32** past ~11.9M speed (falling through to the `< 2` floor, i.e. permanent starvation), and the formula assumes speed converts linearly into output when only one cycle can settle per tick, so high speeds over-request and vacuum up the whole line's materials.

**This repo does not patch it, and at the current numbers that is fine** — but the reasoning matters if `assemblerSpeed` is ever retuned:

- At `speedOverride = 1e8` the overflow happens to wrap **positive** (≈820M → ~1367 batches). At 1.1e8 it wraps negative and collapses to 2. It works by luck, not by design.
- Even collapsed, throughput would barely move, because `needs` is not what limits either feed path:
  - **Station feed** (`MegaStationPatches`) never reads `needs` — it tops `served` up to `requireCounts × requireStockMultiplier` (200) every tick against a 60/tick draw, so it sustains full speed on its own.
  - **Belt feed** (`MegaAssemblerPatches.UpdateInputSlots`) calls `TryPickItemAtRear` **once per input slot per tick** — at most one cargo (≤50 items at stack 50) against `60 × requireCounts` consumed, so `served` never climbs to the `needs[i] = 0` gate.

So the real lever for belt-fed throughput is **looping the pick** (same idea as `RunExtraCycles`), not the batch count. Fixing `CalcNeedsBatch`-style overflow would be pure insurance against a future speed change.

## Game internals: the logic frame, and the three things that actually cost

Measured on a real save — one planet with **1078 mega buildings, 894 miners, 1975 stations,
2 inserters, 411 belts** — which went from **25.2 ms / 40 ups to 6.7 ms / 142 ups** in one
session. Everything below is measured; the wrong guesses are recorded because they cost rounds.

**First, the measurement tool: the game has a per-subsystem CPU breakdown and it is free until
opened.** `UIStatisticsWindow.performancePanelUI` (统计面板 → 性能测试), and
`UIPerformancePanel._OnOpen`'s first act is `PerformanceMonitor.SetCpuProfilerActive(true)` — so
the profiler is **off** until that tab is opened. 48 `ECpuWorkEntry` buckets; the ones that matter
are `Facilities` (生产设施), `CargoTrafficMisc` (传送带附属设施), `LogisticsTransport`,
`Inserters`, `CargoPaths`. **Read that before theorising**: on this save inserters and belts came
to **1%**, against the general expectation that they dominate. *A general rule is not a
measurement, and this mod's whole point is to delete belts and inserters.*

**`行星工厂` is a container, not a cost** — it is the sum of the ten rows indented under it.

### One planet is one work item, so adding cores does nothing

Every factory subsystem's scatter task is reset with the **same** work count:

```
GameLogic.ContextCollect_FactoryComponents_MultiMain
    gameThreadContext.<miner|assembler|inserter|cargoPath|labProduce|…>
        .ResetFrame(timei, GameLogic::factoryCount, threadCount)
                           ^^^^^^^^^^^^^^^^^^^^^^^ work items = number of PLANETS
```

and `_inserter_parallel` decodes a work item as
`factories[(batchCurrent + batchOffset) % factoryCount]`, then runs that planet's **entire**
`inserterCursor` loop on the one thread. `ScatterTaskContext.Redispatch` steals whole planets.

**So a single overbuilt planet is single-threaded and cannot be parallelised further.** The two
real levers are *fewer objects on that planet* and *spread the factory across planets*. (And
within-planet splitting is not available even in principle: inserters on one planet write into
shared `CargoPath` buffers, `entityNeeds` and assembler `served[]`, and vanilla is thread-safe
**only** because of this one-planet-one-thread guarantee. Splitting it corrupts items silently.)

Also: the planet you stand on costs more — `_inserter_parallel` picks
`InternalUpdate` (with anim, 815 instr) when `factory == localLoadedFactory` and
`InternalUpdateNoAnim` (721) otherwise. Flying away and comparing is a free A/B.

### `AssemblerComponent.InternalUpdate`: the steady-state unit and the output gate

One call does **two** things: settle the previous cycle (IL 0101 onward, gated on
`time >= timeSpend`) and consume inputs for the next (IL 038E onward, gated on `!replicating`).
So in steady state one call's net effect is exactly

```
served[i]   -= requireCounts[i]
produced[j] += productCounts[j]
cycleCount  += 1
```

and **`time` needs no adjustment**: each call is `time -= timeSpend` then
`time += power * speedOverride`, and at `speedOverride = 1e8` that overshoots any `timeSpend` by
one to two orders of magnitude, so the call always ends with `time` saturated at "next cycle ready".
Same for `replicating` / `speedOverride`.

**The output gate is a table keyed on `recipeType`** (IL 013E–02F5; the single-product branch is
just the unrolled form of the multi-product one):

| `recipeType` | refuses when | max settled cycles/tick |
|---|---|---|
| 1 `Smelt` | `produced[j] + productCounts[j] > 100` | ~100/count |
| 4 `Assemble` | `produced[j] > productCounts[j] * 9` | 10 |
| everything else (2/3/5/default) | `produced[j] > productCounts[j] * 19` | 20 |

**This is NOT the lab's formula** (`10 × ceil(speedOverride/10000)`) — that one belongs to
`LabComponent`, and using it here would be wrong in both directions.

**That table is the real ceiling for most mega buildings, and this file spent a long time implying
`cyclesPerTick` was.** The gate is re-read and complete: single-product at IL 0138–0184, multi-product
at 01D1–02F5, **the same three tiers in both**, applied per product index. At `cyclesPerTick = 60` the
effective cap is therefore `min(60, gate)`:

| `recipeType` | cap | crafts/min | who |
|---|---:|---:|---|
| 1 `Smelt` | `100 / count` | **216,000** at count 1 | 冶铸熔炉 — the only one that reaches 60 |
| 4 `Assemble` | **10** | 36,000 | 天工装配厂 |
| everything else (2/3/5 **and every custom type 9–17**) | **20** | 72,000 | the other eight mega buildings |

**Two independent measurements already in this file fit that model quantitatively, which is what
makes it more than a reading of the IL.** The `cyclesPerTick` note records 21,423 settled cycles per
tick across 1079 mega buildings — a mean of **19.85**, hugging 20 — and 60 → 30 dropping the total to
14,765. Solving `S + O = 21423`, `S/2 + O = 14765` gives `S = 13316` (≈222 buildings pinned at 60, i.e.
Smelt) and `O = 8107` (≈405 pinned at 20). *The bimodal distribution that note describes is those two
tiers*, not "starved vs. full".

**So "every mega building does 216,000 crafts/min" is false and was stated in both feature guides**
(and in a reply to the owner) before this was read. The greenhouse's documented `14400 木材/秒` was
wrong by 3× for the same reason. Corrected in 1.12.4.

**And in 1.12.8 the gate itself was raised — `MegaOutputGatePatches`.** Asked "can the mega
buildings go faster", the answer is that **neither existing knob is a lever**: `assemblerSpeed` is
already orders above any `timeSpend`, and `cyclesPerTick = 60` is unreachable for **15 of the 16**
buildings because this table pins them at 10 or 20. Only 冶铸熔炉 (Smelt) ever sees 60. So the gate
is the only thing between 20 and 60, and it is now transpiled to `cyclesPerTick - 1` **for mega
buildings only** (`speed >= megaSpeedThreshold`, the repo's standing discriminator) — 装配 ×6,
其余 ×3, ordinary assemblers untouched.

Four things about how it was done, each a rule this file already states:

- **The sites were enumerated and classified by shape before a line was written**, and the result
  is unusually clean: every `9`/`19`/`100` in the method is a gate — **7 multiplicative, 2 additive
  (Smelt), 0 other uses**. The anchor is `ldelem.i4 ; ldc ; mul ; ble*`, never the constant alone.
  `tools/check_output_gate.ps1` is that enumeration, kept so a game update is re-derived offline
  rather than by launching; the transpiler asserts 7 and **applies nothing** on a mismatch.
- **The Smelt tier is deliberately left alone.** Its gate is additive (`produced + counts <= 100`),
  so the cap is `100/counts` — already above `cyclesPerTick` at the common `counts = 1`, i.e. that
  building was never gate-bound. Rewriting it would buy nothing and cost a `counts` multiplication.
- **Only raise, never lower.** The gate is a ceiling; throttling already has three knobs
  (`cyclesPerTick`, the greenhouse's light scaling, `tickDivider`). Letting a ceiling double as a
  throttle is how two mechanisms end up fighting over one number.
- **The gate is also `produced[]`'s back-pressure**, so raising it triples that buffer's depth. The
  next bottleneck is therefore the station slot and the drain — the same shape as the miner's
  `period`: the limit moves from "the machine computes slowly" to "the goods cannot leave".

**`PlanetCensus` was over-reporting by 3–6× the whole time**, and that is worth recording because it
is the *fourth* instance of the same shape: it printed `cyclesPerTick × 台数` as "equivalent 1×
assemblers" while knowing nothing about the gate. It now sums `min(cyclesPerTick, gate)` per
building, reading the gate through **the same `Scale` the transpiler uses**, so the diagnostic and
the behaviour cannot drift apart.

**There was a second hand-kept copy and the same commit missed it — found by reading the log.**
`ReferenceRatePatches.OutputGate` hardcoded the same `10` / `20`, so the moment the gate was raised
the 参考速率 / 理论产能 panels flipped from over-reporting to **under-reporting by 3–6×**. The tell
was two adjacent startup lines disagreeing: `参考速率：…组装类到 36,000/min` right above
`巨型建筑产出闸：…一律放到 60`. It now routes through `Scale` as well. **The lesson is not "fix the
second copy" — it is that "I made the diagnostic read the real function" is only true of the
diagnostic you were looking at.** Enumerate who else holds the number *before* changing it; here the
search is one grep for the constant, and it would have returned both.

### The three optimisations, and which one carries risk

| | how | risk |
|---|---|---|
| `RunExtraCycles` early exit | observe three snapshots, stop when nothing moved | **none** — cannot change output |
| `StationOutputSkipPatches` | copy vanilla's own first two tests | **none** — equivalence proved from IL |
| `MegaBatchSettle` | measure one cycle, multiply; **reproduces one gate** | **real**, covered by replay audit |

**The early exit's break condition needs all three of `produced[]`, `time`, `extraTime`.** Watching
產出 alone misreads "time advanced but has not filled" as idle; extra products ride an *independent
second timer*, so watching `time` alone misses a pure-proliferator cycle. Measured idle share: **48.8%**.
Note it **changes what `cyclesPerTick` is**: before, lowering it saved on idle buildings too; after,
idle buildings cost one probe each, so lowering it only removes *productive* cycles. It became a
pure exchange rate — measured at **31% of output for 3.5 ms**.

**`StationComponent.UpdateOutputSlots` is skippable when no slot is an output port, and that is
provable rather than argued.** Both of its loops open with `dir != IODir.Output → continue` then
`beltId == 0 → continue`, and *every* write in the method — `StationStore.count`/`inc`,
`SlotData.counter`/`beltId`, `SignData.iconType`/`iconId0`, `warperCount` — is downstream of those
two tests. The one write that a skip does forgo is `outSlotOffset` (IL 03F2, the round-robin
cursor), and nothing reads it while there are no output ports. Measured skip rate **97.6%** on the
save it was written against; `CargoTrafficMisc` 7.075 → 0.549 ms.

**That 97.6% is a property of that save, not of the patch, and a later save measured 58.9%.** The
skip fires only when a station has no *output belt port*, and on the second save four in ten mega
buildings really do have one — so the nested loop runs for them and the rate drops by 40 points.
Both numbers are correct; what is wrong is reading either as "the skip rate". **When a diagnostic
prints a ratio, the ratio describes the factory, not the code** — quote it with the save it came
from, or it becomes a claim that the next measurement contradicts. **Note that bucket contains no belts**:
`FactoryCargoTrafficMiscGameTick_Parallel`'s body calls exactly piler, monitor, spraycoater and
station_output.

**Batching is "measure then multiply", not a reimplementation** — the same move `RunExtraCycles`'
`settled` counter already makes. Its two measured bounds (inputs, budget) are safe; the third
(the output gate) is *reproduced*, which is the drill-bit failure shape, **so the replay audit is
not decoration — it is the reason the route is allowed**. `MegaBatchAudit` deep-copies the
component (it is a **struct**: plain assignment shares the arrays, so `served`/`incServed`/
`produced`/`needs` must each be cloned or the audit corrupts the building it is checking), runs
`n+1` real cycles on the copy against **scratch registers** (using the real ones would double the
production statistics), and disables batching for the session on any shortfall. Measured coverage
**98.9%**, `Facilities` 12.276 → 1.577 ms.

**Proliferated buildings are deliberately excluded from batching.** `split_inc_level` drains
`incServed` each cycle and `extraSpeed` drops to 0 when it runs out (IL 04CF–0549) — a genuine
regime change mid-batch, and "measure one unit then multiply" assumes the unit is constant.
Excluding it makes the batch structurally regime-free; the discriminator is **state**
(`incUsed` / `incServed` / `extraSpeed` / `extraTime`), never recipe or building type, because
whether a machine is sprayed changes at runtime.

### Two mistakes from this episode, both already rules here

- **A mean hides a bimodal distribution.** "Average 20 settled cycles against a cap of 60, so
  halving the cap is nearly free" predicted no output loss; the measurement said **−31%**. The
  population is two groups — permanently starved, and pinned at the cap — and the mean shows
  neither. `megabuildings.json`'s `cyclesPerTick` comment records this so the argument is not
  re-made.
- **`Stopwatch.Frequency` is self-reported, and a self-reported number is a claim.** The first
  `MegaTickProfiler` divided by it and reported **240 ms/frame** against a 16 ms logic frame — 12×
  out, and *not* explicable by parallelism (1080 of 1194 mega buildings were on one planet). It now
  calibrates against wall clock across the reporting window, warns when the two disagree by >5%,
  and prints "how many threads' worth" so a genuine parallelism reading is visible instead of
  hidden. Same family as `kMaxCargoFlowSpeedPerSecond`.
- **And a probe that reaches the same order as what it measures stops measuring it.** After
  batching, `phaseTiming`'s twelve `Stopwatch.GetTimestamp()` reads per building per tick
  (~860k/s under Mono, where it is an icall) became comparable to the work left in `MegaTick`.
  It ships **off** for that reason, not because it is finished.

### Diagnostics kept

`PlanetCensus` prints the local planet's object counts **grouped by the performance panel's own
buckets**, re-printing only when the planet or the counts change; it additionally splits stations
by ownership (mega building / collector / plain) and computes `Σ storage.Length × slots.Length`,
the exact inner-loop count of `UpdateOutputSlots`. That split is what disproved the
"894 miners are wasting 30 slots each" theory — they average **1.1**.

### A second campaign, on a bigger save — and the instrument was wrong four times before the code was

Measured on **29,251 entities / 4,632 mega buildings / 6,397 stations / 1,764 miners** on one
planet. Outcome: **`LogisticsTransport` 8.0 → 2.4 ms per frame**, with no output traded away.
Everything below is measured; the four instrument failures are recorded because each one produced a
confident, wrong number first.

**Getting a live per-task breakdown into the log took four attempts, and the first three all looked
like they worked.**

| attempt | what it produced | why |
|---|---|---|
| `SetCpuProfilerActive(true)` + read `timeCostsAve` | five samples **bit-identical** | those 8 instructions only set a bool |
| drive `SummarizeCpuStats` ourselves each frame | every bucket **0** | its samplers all open with `ldsfld DeepProfiler::watchEnabled`, which was false |
| set `watchEnabled` once at startup | worked one session, **dead the next** | `DeepProfilerLateScript.LateUpdate` writes it too — a race |
| set it **every frame**, read `ThreadManager.performanceCountersOn*Task*` | live | those counters are a *different pipeline* from the DeepProfiler sample pool |

**The rule this cost four launches to re-learn: a switch named `SetXxxActive` is not evidence that
anything is being driven.** `DeepProfiler.watchEnabled` is the real master — `GameThreadController.LogicFrame`
@005B–0060 copies it into `ThreadManager.samplePerformanceCounters` every logic frame, and every
`DeepProfiler.Begin*/End*Sample` opens by reading it. Same family as *a named constant with zero
readers is a claim*: **find who writes the number, do not trust what the switch is called.**

**And a probe needs a liveness signal that does not participate in the reading.** The frozen
`timeCostsAve` looked exactly like a very stable factory. What separated them was `aveFrame` — a
counter that has nothing to do with milliseconds and must advance every frame. The first version
only warned on `Total == 0`, which catches "nothing was ever sampled" and not "sampled once, then
stopped" — and the second is the default state.

**The per-task table cannot be read as a per-task cost.** `FactoryFacility` (1601), `FactoryLabResearch`
(1700), `FactoryTransport` (1751) and `FactoryLabOutput` (1800) are *consecutive* stages, and
`GetThreadTaskTime_MainToAll` measures "from my begin until every worker finished" — so their spans
**overlap**, and four numbers near 5 ms each sum to more than the whole frame. Small numbers in that
table are trustworthy; large ones are not additive.

**Then the top bucket had six of this mod's own hooks inside it, and the vanilla profiler
structurally cannot see that.** It times *tasks*, and our code lives inside one. The split is two
sentinels on the same method — a prefix and a postfix at `Priority.First`, another postfix at
`Priority.Last` — so `mid − begin` is vanilla's body and `end − mid` is ours, **with no existing
code touched**. Measured: **86.5% vanilla, 13.5% ours.** A `Phase(name)` call at the top of each of
our postfixes then split our half further, and its semantics are *"the previous owner stops here"*,
so **the postfix order (which Harmony does not guarantee for equal priorities) never has to be
known**.

Counting them also corrected a claim this file had made: **there are five postfixes and one
prefix**, not six postfixes — `LogisticsGlobalPatches` is a prefix, so its cost was always inside
"vanilla". *Which method a patch hangs off is not evidence; the annotation is.*

**The two shipped cuts, both from that breakdown:**

- **`StationCapacityPatches` was the single most expensive one — 0.5 ms/frame — and it had been
  "done" for hours.** Its bootstrap really is once per station (`Bootstrapped.TryAdd` guards it),
  but **the loop around the guard ran every tick forever**: 6,397 stations × a `HashSet.Contains`
  and a tuple-keyed `ConcurrentDictionary` probe, 60 times a second. It now records, per planet, the
  `stationCursor` at which a pass changed nothing and skips until the cursor moves or a 600-tick
  fallback expires. **604 → 23 ms per 20 s.** The rule: **"this is done once" and "deciding whether
  this is done costs once" are different statements** — the ledger stopped the repeated *writes*, not
  the traversal that consulted it.
- **Mega-building stations skip the dispatch scan** (`skipIdleMegaStationTick`, **off by default**).
  Vanilla's scan leaves early when it finds work and **walks the entire pair ring when it does not** —
  and a mega building never finds work, because `MegaVirtualLogisticsPatches` already moved the
  goods. 4,632 buildings ÷ a 12.8-frame interval ≈ **21,000 whole-ring empty scans per second**.
  Skipping is **not an equivalence**, it is the stated decision that these stations stop launching
  drones, which is what virtual logistics was for; hence the default and the three guards
  (virtual logistics on, `workDroneCount == 0`, and the repo's usual speed-threshold discriminator).
  Measured **−35% on vanilla's half**, with **+33% on ours** — the work moved rather than vanishing,
  and the total still fell.

**`生产设施` was then measured and deliberately left alone, and the reasoning is the useful part.**
`MegaTickProfiler` already existed and was off, because twelve `Stopwatch.GetTimestamp()` per
building per tick is ~860k icalls/s — *the same order as what it measures*. Sampling 1 in 64 fixes
the **aggregate** cost; it does **not** fix the per-sample bias, which is a distinction this file's
own rule did not spell out. With the probe self-calibrating (23 ns per timestamp, 1.4% of the total)
and the interval split one level further, the breakdown is: recipe cycles **85.7%**, of which
`InternalUpdate` itself is 60.8% at **1.7 calls per building per tick**, the rest being batch-settle
bookkeeping. Storage sync is **6–7%** — so the low-risk dirty-flag idea has a ceiling of 7% and is
not worth it, and the call count is already near its floor (the early exit saves 63.6%, batching
covers 94.9%). **The remaining lever is caching the batch-settle probe call, which trades a
correctness guarantee for ~30% of `MegaTick`; the owner declined it.**

And one unresolved number, recorded rather than explained away: **a single `InternalUpdate` measures
5,669 ns against the 200–400 ns its 693 IL instructions suggest.** Probe overhead is ruled out
*without* relying on the self-calibration — `_tOther` contains three intervals (six timestamps) and
`_tCycles` one (two), so if timestamps dominated, `_tOther` would be the larger; it is 1.2% against
85.7%. The likely mechanism is cache traffic on `productRegister[]` / `consumeRegister[]`, which are
shared across the ~31 worker threads, but that is a hypothesis and is labelled as one.

**Both halves of that paragraph are now retracted, and the retraction is the most reusable thing in
this whole section.**

- **The hypothesis was wrong.** Those two registers come from `factoryStatPool[factory.index]`
  (`_assembler_parallel` @011D–0141) — **one pair per planet**, and one planet is one work item on
  one thread. The 5 `Monitor.Enter/Exit` pairs inside `InternalUpdate` (@006D/@00B4/@01A3/@031A on
  `productRegister`, @0474 on `consumeRegister`) are therefore **never contended**. A whole
  optimisation (per-thread scratch registers, merge once per planet tick) was designed on that
  hypothesis and cancelled by one Cecil dump before a line was written.
- **The number itself was the probe measuring itself.** The bound that settles it needs nothing but
  the vanilla panel: `生产设施` = 3.688 ms of wall clock, and the planet holding 7,240 mega
  buildings is **one thread**, so the whole per-building-tick budget is ≈ **509 ns** — against the
  probe's claimed 6,273 ns. Parallelism cannot bridge that: only three planets carry mega buildings,
  so at most 3×. The real figure is **150–350 ns per call, which is exactly what 693 IL instructions
  should cost.** There was never an anomaly.

**Why the self-calibration could not catch it, which is the transferable part.** The probe measures
one `GetTimestamp()` in a **tight loop** (22 ns) and subtracts `15 × 22 ns` per sampled building.
But the 15 timestamps in the real code are on a **cold path** interleaved with cache-missing work,
where the same call can cost an order of magnitude more. So the probe is **cheap in aggregate**
(turning it off moved the frame by less than the noise) and **wrong per-sample by 12×** — and those
two facts look contradictory until you notice the subtraction is scaled back up by the 1/64
sampling factor. *A self-calibration is only as good as the representativeness of its calibration
workload*, and a probe whose instrumentation sits **inside** the interval it measures can never
calibrate that away by sampling less.

**The rule: cross-check any probe against a number it does not produce.** Here that number was free
— the vanilla performance panel, divided by the thread count the scheduler actually permits. The
relative history (213 → 11.5 ms) stayed valid throughout because the same probe was on the whole
time; only the absolute per-call figure was fiction. **Relative deltas from a biased instrument are
usually fine; absolute values from one are not.**

**And what that leaves for `InternalUpdate` itself: nothing.** Enumerated — 10 backward branches
(all bounded by `requireCounts.Length` / `productCounts.Length`, i.e. 1–8), **0 `newobj`/`newarr`,
0 `callvirt`**, 11 `call` of which 10 are the Monitor pair. It is a fixed-step state machine at
`O(inputs + outputs)` with no search, no sort, no allocation and no virtual dispatch. The only data
structure with any slack is the **3–4 small heap arrays per component** (`served` / `incServed` /
`produced` / `needs`) versus the sequential `assemblerPool` struct array and the *shared*
`recipeExecuteData` (all buildings on one recipe point at one object, so it stays hot). Defragmenting
those arrays into allocation order was costed at ~15% and rejected: it depends on Mono sgen's
placement and compaction, which is an implementation detail that fails silently. **The lever that
did work was the call count — 47 → 1.9 → 0.8 per building-tick — never the cost per call.**

### The per-building working set is the real cost, and `globalTickDivider` is the lever

`megabuildings.json`'s `globalTickDivider` (default **2**) gives each building a turn once every
G ticks and runs G× the cycles on that turn. **Throughput is independent of G by construction** —
the divider becomes `per-building × G` (`MegaThrottle.Decide`) and the cycle count becomes
`base × G` (`MegaThrottle.CyclesFor`), so `cycles / divider` cancels; this holds for the four
antimatter buildings (`cyclesPerTick 1 / tickDivider 70`) with no special case. Offline replay in
`tools/sim_throttle.py` asserts it at G = 1/2/3/4/8 (420,000 cycles each).

**Two couplings, and missing either is silent:**

- **The output gate must scale with G** (`MegaOutputGatePatches.Scale`), or the gate pins the G×
  cycles back to 1× — presenting as "I turned on the divider and throughput dropped to 1/G".
- **The input buffer must cover one settlement's worth of cycles** (`MegaStationPatches.StockCycles`),
  or `MegaBatchSettle.BatchSize`'s ingredient bound bites first and the remainder falls back to
  one-at-a-time. At G = 4 that is 240 cycles against a `requireStockMultiplier` of 200 — so the
  multiplier is now a **floor**, and the real value is derived.

Both were made *derived* rather than copied, because that constant has now been hand-copied to four
places and gone stale in three of them (`PlanetCensus`, `ReferenceRatePatches`,
`MegaBatchSettle.BatchSize`, `MegaStationPatches`).

### An over-strict guard does not fail loudly — it persists through the save

**The single most expensive bug of this campaign: batch settlement was off for an entire save, and
nothing reported it.** Measured: coverage **0%**, every mega building calling vanilla's settlement
**47 times per tick** instead of ~2, `生产设施` 14 ms → 210 ms. **Output was exact the whole time.**

The chain, in the order it has to be read:

1. `MegaThrottle.RewindExtra`'s fallback wrote `extraTime = -extraSpeed - 1`. With no proliferator
   `extraSpeed == 0`, so it wrote **−1**.
2. `extraTime` is a **save field**, and vanilla's only instruction that advances it (IL 0586,
   `extraTime += (int)(power * extraSpeed)`) multiplies by that same zero. **Nothing in vanilla ever
   clears it.** So one session with the divider on poisoned every mega building in the save,
   permanently — and reverting the config did not undo it.
3. `MegaBatchSettle.CanBatch` had `if (extraTime != 0) return false;`.

**The guard was asking the wrong question.** Once `extraSpeed == 0` the extra timer is *frozen* — it
provably cannot cross `extraTimeSpend` during a batch — so its value is irrelevant. The correct test
is *"can the timer move"*, which the preceding `extraSpeed != 0` check already answers. Deleting the
over-strict line **also repairs poisoned saves with no migration**, because the fix is retroactive by
construction. `RewindExtra` and `MegaLightPatches.Suppress` additionally stopped writing a sentinel
that cannot do anything (`-0 - 1`), and `sim_throttle.py` grew an assertion that suppression leaves
`extraTime == 0` when nothing is sprayed — **the checker before the C#**, as usual.

Three rules out of it:

- **A guard that is too strict does not error; it makes the feature quietly not happen.** Prefer the
  narrowest predicate that is *provably* sufficient, and write down the proof — here, "the timer
  cannot advance" is one IL line.
- **Never write a sentinel into a field vanilla will not clear.** `extraTime`'s only writer outside
  our code is gated on the very value that makes the sentinel pointless, and the field is
  serialized. A per-tick scratch value that reaches the save is a permanent decision.
- **A single counter summarising several distinct rejection reasons measures nothing.** `CanBatch`
  had four conditions behind one counter and `IsSteadyUnit` six behind another; splitting them
  named the culprit in one launch each, twice in one session. This is the same lesson as *a scrub is
  not coverage* — the distinction has to be in the instrument.

## Game internals: building is part of the logic frame, and this mod makes it quadratic

Measured on the same save, during a build spree. The player's own report was the decisive half
("the logic frame jumps from 6 ms to 20 ms **while building**"), and the panel then split it:
`伊卡洛斯 29.4 ms` + `行星工厂 26.2 ms`, of which `建设系统 21.2 ms`, while `生产设施` was down at
**1.6 ms** — i.e. the factory was fine and the *building* was the cost.

**Construction runs inside the logic frame.** The chain is
`GameLogic::FactoryBeforeGameTick @0016 → PlanetFactory::ConstructionBeforeGameTick @000E →
ConstructionSystem::BeforeGameTick @0007 → ExecuteFastBuild @002F → FastBuild(batchSize)`.
So anything done per placed building is added straight to the frame — including this repo's
`InstantBuildPatches`, which reproduces `FastBuild`'s body.

### `PlanetTransport.RefreshStationTraffic` is O(stations²), and this mod's core design feeds it

66 instructions, two loops to `stationCursor`: the first calls `ClearLocalPairs()` per station,
the second passes **the whole `stationPool`** into `RematchLocalPairs` — every station re-matches
against every other. Vanilla never notices. **This mod makes every mega building a logistics
station**, so the measured planet carries **2078 stations (1175 of them mega buildings)** →
2078² ≈ 4.3M pair evaluations → **81 ms per call**.

**And it is called once per building placed**, from
`BuildFinally → BuildingParameters.ApplyPrebuildParametersToEntity @1024`. Measured: 19 buildings
in 24.5 s cost `BuildFinally` 1937 ms, of which essentially all was this.

`StationTrafficCoalescer` defers **every** call (vanilla's included) into a per-planet dirty flag
flushed at most once per 120 ticks from that planet's own `PlanetTransport.GameTick`. Measured
result: per building **102 ms → 0.63 ms**, and this method's CPU share **20% → 1.9%**. The stated
cost is that supply/demand pairing can be up to 2 s stale — in vanilla it is instant. `Import`'s
call is unaffected (`last` is 0 after a load, so the next tick flushes immediately).

**And "every call" was wrong — it shipped a crash, and the distinction it missed is the lesson.**
There are two kinds of staleness and only one of them is a delay:

| | example | deferring it means |
|---|---|---|
| **content** | a slot's item changed, a station was built | the pairing is 2 s out of date — harmless, and it is what this optimisation buys |
| **existence** | a station was removed | the table holds a **dangling reference** — not a delay |

`RemoveStationComponent` @02D1 calls `Reset()`, which nulls `storage` (@00BC) and zeroes `id`
(@0001) while **leaving the component in `stationPool`** (it only goes on the recycle list); it then
**synchronously** rebuilds the pair table at @02F5. That synchronous rebuild is exactly why
`InternalTickLocal` @07CF can get away with null-checking only the *object*:

```
07C1: V_47 = stationPool[pair.supplyId]
07CF: if (V_47 == null) goto 1175;   // the object only
07D6: V_26 = V_47.storage            // null on a recycled station
07E4: Monitor.Enter(V_26, ...)       // ArgumentNullException on a worker thread
```

Deferring @02F5 broke that invariant, so for up to 2 s after dismantling a logistics station every
other station's `localPairs` still named it. Reported by a player as
`ArgumentNullException … mono_monitor_enter … DMD<InternalTickLocal>`. **1.10.4's burst dispatch was
not the cause but widened the exposure** an order of magnitude (10 pairs examined per tick instead
of 1), which is why it appears on the stack — and a second, silent symptom shares the root:
`stationRecycle` hands the same index to a *new* station, so inside that window a stale pair points
at an unrelated station and goods are delivered to the wrong slot with nothing logged.

Fixed in 1.10.6 by flushing immediately when the call comes from `RemoveStationComponent`
(a `[ThreadStatic]` flag set by a prefix on it, read in `Defer`). **Exactly one of the four callers
needed it** — enumerated: `Import` (the table is empty, not dangling), `SetStationStorage`
(content), `ApplyPrebuildParametersToEntity` (building — the one this optimisation exists for), and
`RemoveStationComponent`. The cost is one full rebuild per dismantled station; at the indexed 6.7 ms
against vanilla's own 81 ms that is still an order of magnitude cheaper than what vanilla does, and
the build path is untouched.

**The general rule: before deferring a vanilla call, ask what invariant its synchronicity is
holding up.** A guard that null-checks one level and not the next is a *signal* that something
upstream guarantees the rest — here, `!= null` on the object with no check on its array only makes
sense because the table can never name a dead station.

**The steady-state finding is the one nobody had looked for**: in a 34.8 s window with only 15
buildings placed, the planet was marked dirty **138 times** — mega-building layout changes alone
trigger ~4 planet-wide re-matches per second. Under the old behaviour that is 138 × 81 ms ≈ 11 s
of CPU per 35 s (**32%**), permanently, with nothing building.
`MegaStationPatches`' guard "only call it when the layout actually changed" was correct all along;
**what nobody measured was how often it changes.**

### Replacing that join with a hash index — 81 ms → 6.7 ms

**The matching predicate is one line, so this is an equi-join.** `RematchLocalPairs`' inner test is
only `itemId` equal (@0072 / @0126) and direction complementary (@0082 / @0136). No distance, no
priority, no grouping. A tree or a heap buys nothing here — they solve ordering and extremum, and
neither is asked for. The standard answer to an equi-join is an index on the join key.

**Measured scale, one save, five planets:**

| planet | stations | slots | active slots | logical pairs | vanilla scan | indexed | ratio |
|---|---|---|---|---|---|---|---|
| 3701 | 28 | 728 | 45 | 28 | 15,756 | 784 | 20× |
| 3703 | 112 | 228 | 118 | 110 | 18,429 | 448 | 41× |
| 103 | 558 | 3,174 | 661 | 5,315 | 1,223,061 | 13,804 | 89× |
| **104** | **2,101** | **37,190** | **4,017** | **303,093** | **89,279,581** | **643,376** | **139×** |

89.3M inner iterations against the measured 81 ms is 0.9 ns each — so the model is sound. **Do not
expect 139×**: 606k of the 643k are the `AddLocalPair` calls themselves, which are the output.

**The cut point is that `stationCursor` is used only as those two loop bounds.** Across the whole
method arg2 appears exactly twice, at @00BA and @0179, both the `blt` of a station loop. The second
half (drone-order repair, 2800 of the method's bytes) is skipped wholesale at @018C when
`keyStationId <= 0`, and it indexes `stationPool` by a drone's `endId`, not by the cursor. Therefore

> **`RematchLocalPairs(realPool, 0, keyStationId, droneCarries)` runs the drone repair and nothing
> else** — the matching loops start at `this.id + 1` and `this.id >= 1`, so `blt 0` never enters.

`LocalPairIndex` uses that: it clears, emits pairs from its own index, then calls vanilla's method
per station with a cursor of 0 so **vanilla's 2800 bytes run verbatim**. Flat arrays with chained
buckets (`itemId → (station, slot)`), `[ThreadStatic]` and reused, so a rebuild allocates nothing.
Stations and slots are walked **descending** while inserting, so head-insertion leaves each chain
ascending — which is what makes the emitted pair sequence **identical to vanilla's, element for
element**, not merely the same set.

**Measured result on planet 104: 81 ms → 6.67 ms (≈12×)**, with 606,186 pair entries verified
element-for-element against vanilla.

**The audit is why this route is allowed at all**, same as `MegaBatchSettle`: every 20 rebuilds
(and the first 3 per planet) it runs vanilla's matcher too and compares an **order-sensitive**
checksum. A snapshot would cost 12 MB at this scale; the checksum is O(pairs) and zero memory, and
because our emission order is deliberately aligned with vanilla's, order-sensitivity is the
*stronger* test, not the weaker one. A mismatch logs ERROR and disables the index for the session —
worst case "no faster", never "wrong pairs". Vanilla's table is what stays after an audit.

### Incremental maintenance — the only way past O(OUT), and the three traps it hides

**The index is already at the theoretical floor, so the next win had to come from not materialising
the output at all.** This is a binary equi-join (match by `itemId`, complementary direction), i.e. a
*free-connex* query, and for those **Yannakakis (1981) achieves O(N + OUT) and that is also the lower
bound** — every input must be read once and every result written once. Our flat bucket index (direct
indexing on `itemId`, chained nodes, `[ThreadStatic]`, zero allocation) is already better than a hash
table and already O(N + OUT). **`OUT` is 3.04M pairs on that planet, so no data structure beats
it.** The only remaining lever is **incremental view maintenance**: a placed building only adds its
own pairs.

Measured, one planet, 8,467 stations / 3,043,138 pairs: vanilla **81 ms** → full index **32–49 ms**
→ **incremental 1.14 ms**.

**Three traps, all of which fail silently, and all of which are now covered by
`tools/sim_pairindex.py`** (add / change-slot / remove / batched, plus two negative controls):

- **The audit's checksum must become order-insensitive first.** Full rebuild re-emits in
  station-then-slot order; incremental appends. Same *set*, different *order* — so the old
  order-sensitive checksum would fail every single increment and, by design, disable the index and
  fall back to O(stations²): **slower than not doing IVM at all**. It is now a multiset sum, and
  **sum rather than xor**, because xor cancels duplicates and "emitted the same pair twice" is
  exactly what IVM gets wrong.
- **`EmitPairsFor` must NOT keep the "counterpart id > mine" guard.** That guard is the full
  traversal's way of emitting each logical pair once; when emitting for a single station it silently
  drops every counterpart with a lower id.
- **A batch shares one index rebuild, and then keys pair with each other twice.** Detach A, detach B,
  emit A (B is in the index → A↔B), emit B (A is in the index → A↔B *again*). Fixed by skipping
  keys already emitted in this batch. **And the batch itself is the point**: building the index is
  ~2 ms (O(slots)) while emitting is ~96% of a full rebuild, so per-key index rebuilds made 20 keys
  cost the same as a full rebuild — the first version capped the batch at 8 keys on a cost model that
  was 7× wrong, and **incremental never ran once**.

**Removal has to happen in `RemoveStationComponent`'s PREFIX, not at the `RefreshStationTraffic`
call inside it.** Measured: @02D1 calls `Reset()` (which zeroes `id` and nulls `storage`) and only
@02F5 calls `RefreshStationTraffic`. **`Reset` does not touch `localPairs`** — enumerated, zero
instructions — so by @02F5 the pair array is intact but the station no longer knows its own id.
The prefix sees it whole.

**And "this station is gone" is not "incremental failed".** The removed station's key stays in
`PendingKeys` — and *must*, because vanilla uses it for the drone-order repair — so the next flush
looks it up and finds a reset component. The first version returned `false` there, which triggered a
**full rebuild on every dismantle**. `RebuildOne` now returns `Applied` / `Vanished` / `Unsupported`;
only the last one falls back. Same family as the `CanBatch` and `IsSteadyUnit` counters: **one return
value covering two opposite situations cannot distinguish the normal path from a fault.**

**A dropped key is a correctness hole, not a performance one — and IVM created it.** The coalescer
caps how many changed stations it records. Before IVM a dropped key only meant "that station's drone
orders were not repaired", because the table was rebuilt wholesale anyway. **With IVM it means that
station's pairs are never updated** — `keys` is non-empty and looks like an ordinary delta batch. So
the cap is now 512 (the break-even against a full rebuild is ~8,400 keys) **and overflow sets a
per-planet "delta incomplete" flag that forces a full rebuild** — the same rule as "empty keys →
full rebuild", in a more hidden form. Note the flag must be a *separate signal* from `keys`:
emptying `keys` to force the full path would also drop the drone-order repair, which is the silent
bug one door over.

**Reconciliation replaces the audit for the incremental path, and the guarantee is transitive.**
Every 200 increments per planet, one flush is promoted to a full rebuild and the two are compared as
multisets. Full-index ≡ vanilla is still audited on small planets; incremental ≡ full-index is
reconciled everywhere; the two compose. Comparing incremental against *vanilla* directly would cost
O(stations²) — 1,300 ms on this planet, which is the very freeze the cost budget below exists to
stop.

### The audit's throttle was counted, but its cost scales — "every 20th" is not a budget

`LocalPairIndex`'s replay audit re-runs **vanilla's own matcher**, which is O(stations²). Its
"first 3 per planet, then every 20th" cadence was set on a **2,101-station** planet where that
measured 81 ms. On an **8,465-station** one the same rule gives `(8465/2101)² × 81 ≈ 1,300 ms` —
reported by the owner as **"placing a single building stutters"**, with three back-to-back freezes on
first entering the planet.

Fixed by throttling on **estimated cost** instead: the budget is derived from that one measured point
(81 ms ↔ 2,101² ≈ 4.41M iterations, so a 30 ms ceiling is ~1.6M, about 1,280 stations), and a planet
over it skips the audit **while printing the estimate it skipped** — a silently degraded safety net
is not a safety net. Small planets still verify pair-for-pair, and that is sufficient because
**the audit verifies a property of the algorithm, not of the planet**: a 539-station planet exercises
the same code.

**The general rule: a throttle counted in occurrences is only correct while the cost per occurrence
is constant.** Anything whose cost grows with world size needs the budget expressed in the same unit
as the cost.

**`RefreshStationTraffic(int keyStationId = 0)` has an optional parameter, and that cost a silent
bug.** Writing `RefreshStationTraffic()` compiles, runs and reports nothing — while passing 0 is
exactly the `keyStationId <= 0` that skips the entire drone-order repair. The coalescer shipped
that way for a version: the pair table was rebuilt correctly and **the drone repair had not run
once**. The prefix now captures the real key, and the coalescer keeps a deduplicated set of pending
keys and replays the repair for each. Measured cost of that repair: **0.1–0.35 ms**, because its
loop bound is `workDroneCount` (@0CCF) — a station with nothing in flight does zero iterations.

### `EndFlattenTerrain`'s tail is unconditional

`BeginFlattenTerrain` merely clears `tmp_levelChanges` (10 instructions). `EndFlattenTerrain` ends
with four heavy calls that run **whether or not anything changed**: `PlanetData::UpdateDirtyMeshes`
@0100, `PlanetFactory::RenderLocalPlanetHeightmap` @0115, `PlanetAlgorithm::CalcLandPercent` @0123
(whole planet) and `GPUInstancingManager::SyncAllGPUBuffer` @0128 — the last walks **every**
`ObjectRenderer` calling `SyncInstBuffer()`. Vanilla amortises it over a batch of up to 100;
placing one building at a time pays it in full each time.

**Skipping it when `tmp_levelChanges` is empty is safe, and that is a checked fact**: the only
writers of that dictionary in the whole assembly are `FlattenTerrain`, `FlattenTerrainOffline`,
`ComputeFlattenTerrainReform` and `FlattenTerrainReform` — all terrain edits. And
`SyncAllGPUBuffer` is **not** "a new model needs syncing": it sits in the terrain path because a
terrain change moves every instance's ground height; vanilla's ordinary drone-built path never
calls it, and a single model syncs through `AddModel(setBuffer: true)` instead.

### `GPUInstancingManager.RemoveModel` is O(1) — a retracted guess

68 instructions, all of it appending `(modelIndex, modelId, flags)` to `objRendererRefreshPool`
under a lock. It is **not** "removing one model rebuilds the instance buffer". Recorded because
that guess cost a round: the third argument `setBuffer` is encoded as `(setBuffer ? 1 : 0) << 1`,
and vanilla does pass `false` on its bulk paths (`LoadingPlanetFactoryMain` @0230/@04C9,
`PlanetReformRevert`) and `true` everywhere else — which *looks* like a smoking gun and is not.

### How this was found, and the process cost

**Six wrong guesses in a row, every one killed by measurement, none by thinking:** `RemoveModel`'s
complexity, the build preview's physics query (dead — the 无碰撞 cheat disables `ColliderPool`
entirely), the stacked-render bookkeeping (measured at **3.4 ms per 10 s**, 0.03%), the batch-size
knob (lowered 100 → 15 on a model that turned out to be wrong, then restored), the terrain tail
(real, but not this symptom), and finally the attribution of the refresh calls themselves — this
file's author asserted "~30 of 42 come from `MegaStationPatches`" without measuring, built a
coalescer that only covered that path, and the next log said `标脏 0 次 / 实际刷新 0 次`.

What worked, both times this repo has had a performance problem, is the same move: **put
timestamps around each candidate segment and read the numbers.** The guidance that keeps being
re-earned is already in this file — *a mechanism that could explain a symptom is not evidence that
it did*.

**A global counter is the wrong shape for "report N times", and this file's author got it wrong
three times in one session, in one file.** The pair-size probe ("measure three times per session")
had its three slots consumed by small planets and never measured the one that mattered; the audit
cadence ("audit the first 3 rebuilds") did the same, and additionally raced — three planets tick in
parallel, `_rebuilds++` is not atomic, and all three audit lines printed "第 3 次"; the timing probe
accumulated across planets while printing one planet's id, which is how a **fabricated "the drone
repair costs ~25 ms"** was derived from two windows that were never comparable. The rule is
**count per object, not per occurrence** — and on the parallel tick path, per object *and* with
`Interlocked`.

Two probe-design lessons worth keeping:

- **A probe that only reports after a fixed window is silent on short bursts.** The instant-build
  probe needed a 10 s window and the build spree lasted a few seconds, so the first two sessions
  produced no line at all. Report on an *event* threshold as well as on a timer.
- **A periodic sample aliased with a periodic process reads as "frozen".** The hub's status line
  (every 600 ticks) sampled a filter rotating every 60 ticks; with two candidates, ten rotations
  land back on the start, so `当前服务` printed the same item forever and looked broken. The line
  now carries a rotation counter. Same family as "a mean hides a bimodal distribution".

## Compatibility damage this mod causes, and how it is repaired

**`CargoIncWidener` changes 33 method signatures, and every other mod compiled against
vanilla still names the old ones.** That is not a hypothetical: measured across the nine
plugins in this profile, **exactly two call sites break**, both in UXAssist —
`BeltSignalsForBuyOut` → `CargoPath.TryInsertItem(int,int,byte,byte)` (传送带信号购买) and
`ProtectVeinsFromExhaustion` → `PlanetFactory.InsertInto(int,int,int,byte,byte,out byte)`
(矿脉保护). Both throw `MissingMethodException` the first time they execute. **They have
been broken since the preloader shipped in 1.7.0** and nobody reported it, presumably
because those paths run rarely.

**A `SoftDependency` on the target mod is not optional decoration — it is load order, and
leaving it out makes the whole compat layer silently inert.** Without it BepInEx may load
this mod first, at which point `Chainloader.PluginInfos` does not yet contain the target and
`IsLoaded` returns false. Measured, in the log, in exactly that order:

```
[Project Eden] UXAssist 没装，跳过它的兼容补丁
[BepInEx]      Loading [UXAssist 1.5.8]
```

**That two-line pair is only visible because the compat layer logs the boring state.** Had
it logged only on success, the symptom would have been "the fix does nothing" with no clue
why — the fifth-time-lesson from `LensPatches.ReportInsert`, paying for itself again.

**Only one of the two is repaired, and the reason the other is not is worth more than the
repair would be.** Reading UXAssist's source settles it: `ProtectVeinsFromExhaustion`'s
prefix **returns `false` and fully reimplements `MinerComponent.InternalUpdate`** (vein, oil
and water branches). This repo's `AdvancedMinerPatches` *transpiles that same body* — the
ore→ingot remap, the capacity gates, drill-bit consumption and the small miner's throttle
all live inside it. So making the signature resolve would buy a **silent feature conflict**:
vein protection works, and copper ore quietly stops becoming copper ingot, the buffer drops
back to 50, and drill bits stop being consumed, with nothing logged.

The two features also overlap: `forceMiningCostRate: 0` already means "veins never deplete"
for the advanced miner, pumps and oil extractors. The only gap was the plain miner, now
closed by `protectSmallMinerVeins`, so **this mod covers the whole of what UXAssist's switch
does** and turning it off costs the player nothing. `UXAssistCompat.CheckMinerConflictOnce`
therefore detects and explains instead of patching — **from the miner tick path, not at
startup**, because that switch can be toggled mid-game, and by reading Harmony's own patch
table rather than UXAssist's config field (the applied state is the fact; a config field
name is a guess that rots).

**And the other one cannot be repaired either — nor even switched off.** Three attempts, each
failing differently, before the diagnostic settled it: matching the call by name (never fired),
matching it by `operand == null` (fired, replaced, and the write still threw on the same
instruction), and finally giving up on repair and trying to install a prefix that returns
`false` to disable the method (failed identically). Dumping every call instruction with its
actual operand made the reason plain: **HarmonyX has to re-emit the entire original method
body for any patch at all**, and that body holds a MemberRef to a signature the preloader has
removed. It reads back null and the write fails no matter what the patch does. **This is not
"the technique has not been found"; the route does not exist.**

So `UXAssistCompat` patches nothing. It reports both limitations — once, at startup for the
belt one, and from the miner tick for the vein one — and the delegates, shims and transpiler
written for the abandoned repair were deleted rather than left as dead code. The limitation is
recorded in `README.md` and both feature guides.

**The general rule this establishes: a preloader signature change is unrepairable from
outside for any third-party method that calls it.** Harmony can only patch a method it can
re-emit. Weigh that before widening anything else — the blast radius is every mod compiled
against the old signature, and there is no compat layer that can cover it.

The repair that *was* attempted for **`BeltSignalsForBuyOut` by transpiling**, swapping the
call to the old signature for a shim in this assembly that reaches the widened API through
a runtime-bound delegate — the same technique `CargoWidening` already uses for this mod's
own calls. Harmony's transpiler runs before JIT, so the broken MemberRef is never resolved.

**The rejected alternative is worth recording: do NOT synthesize old-signature overloads in
the game assembly.** It would fix every mod at once, and it creates a worse problem —
an extra overload is an extra target for anything patching *by name*. This repo's own
`MegaAssemblerPatches` uses `TargetMethods()` to take **every** `InsertInto` overload, so a
forwarder would be transpiled too and the same logic would run twice; other mods' patching
strategies are not knowable at all. (CLAUDE.md already records `AmbiguousMatchException`
from name-based patching of overloads as a crash that takes the whole mod down.)

**A transpiler over another mod's broken call cannot match on the method — because Harmony
hands it a `null` operand.** Harmony's `MethodBodyReader` resolves every MemberRef to a
`MethodInfo` while reading the body; a MemberRef naming a signature the preloader has
already changed **cannot be resolved**, so `CodeInstruction.operand` is null. Matching on
`operand as MethodInfo` therefore never fires, the `callvirt null` survives the transpiler,
and Harmony's writer throws:

```
Failed to patch ...: ArgumentNullException: Invalid argument for callvirt NULL
```

This file already records that exception — from the **opposite direction**, where *we*
emitted a null operand by resolving a `MethodInfo` that came back null. Same exception,
reversed cause. The matcher must therefore key on **"a call whose operand is null"**, and
since resolution has destroyed the identity, on which *method is being patched* for the
rest: measured offline, each of the two UXAssist methods has exactly one such call, so the
transpiler asserts exactly one and refuses to touch anything otherwise.

**The measurement method matters here, because the first attempt got it wrong.** Comparing
pre/post signatures by *name + parameter count* reports 1277 changes — almost all of them
overload pairs like `VectorLF3::.ctor(Single,Single,Single)` vs `(Double,Double,Double)`
masquerading as edits. Matching **by index within the type** (the widener never adds or
removes methods) gives the true answer: 33. An earlier claim in this session that
InstantDelivery was also broken came from that bad comparison and is **retracted** —
InstantDelivery calls nothing that the widener touches.

## Third-party mod bugs worked around

**GalacticScale 2 (2.78.8) can make a save look corrupted.** Its postfix on `GameDesc.Import` calls `GS2.Import(r, loadPath)`, which reads **two length-prefixed strings straight out of the game's save stream with no marker check**, parses the second as JSON, and on failure rewinds `BaseStream.Position` and loads the galaxy from the `.gs2` sidecar instead. That fallback is fine; the missing guard is not. When the save holds no GS2 block at that offset (the normal case here — the log says `DSV Contained No GS2 Data` on every load), `ReadString` interprets whatever bytes are there as a 7-bit-encoded length. Land on a plausible length and you get garbage, a failed parse and the healthy fallback; land on a huge one and it throws `EndOfStreamException` out through `GameSave.LoadCurrentGame`, and the save simply won't load. **Whether a given save opens is therefore luck, and it can flip between two saves of the same world.**

`GalacticScaleCompat` transpiles those two `ReadString` calls to `SafeReadString`, which decodes the length prefix itself and returns `""` (position restored) when the string cannot fit in the remaining stream — so a garbage length becomes GS2's own "no GS2 data" path instead of an exception. It changes no GS2 behaviour and needs no reference to GalacticScale: the type is resolved by `AccessTools.TypeByName` and patched manually via `CompatibilityRegistry.ApplyPatches(_harmony)` **after** `PatchAll`, since `PatchAll(Assembly)` only sees this assembly's own `[HarmonyPatch]` classes.

**When a save "breaks", establish where the exception is thrown before touching this mod's code.** This one was reported as save corruption caused by a lab change; the stack showed GalacticScale, at `GameDesc.Import`, which runs *before* any ProjectEden data is read — and ProjectEden writes nothing into the `.dsv` at all (its `IModCanSave` block goes to the separate `.moddsv`, and its only `GameData` hooks are postfixes on `Import`). `Player.log` under `%USERPROFILE%\AppData\LocalLow\Youthcat Studio\Dyson Sphere Program\` carries the full stack; `Player-prev.log` is the session before it, which is what shows whether the same code path survived last time and why.

## Where the guide is outdated

Written when DSP ran Unity 2018.4. Two build instructions no longer apply:

- **`TargetFramework` is `net472`**, not `netstandard2.0` — the game's assemblies target netstandard 2.1, so netstandard2.0 fails with `CS1705`. net472 is what all four working mods in the profile use. The guide's core warning still holds: **never `net8.0`**.
- **Don't use the `UnityEngine.Modules` NuGet package** — far behind Unity 2022.3. The csproj references `$(ManagedDir)\UnityEngine*.dll` directly, which is the guide's own §4.2 fallback.

Everything else — Harmony semantics, the community library stack, the performance rules, Thunderstore packaging — is still accurate. `Polyfills.cs` is not needed on net472.

## Rules that bite

- **Prefer Prefix/Postfix over Transpiler.** When a transpiler is unavoidable, match by instruction *signature*, log the replacement count so a failed match is loud, and never `Advance(n)` off a fixed offset.
- **Never allocate on the tick path.** No `new`, boxing, closures, LINQ, string interpolation or per-frame logging inside the miner/assembler/station updates.
- Preserve labels when rewriting IL: mutate the existing `CodeInstruction` (`.opcode` / `.operand`) rather than replacing the object, or branch targets are lost.
- Check whether a game member is a **field or a property** before `AccessTools` — `FactorySystem.factory` is a field, and `PropertyGetter` returned null, emitting corrupt IL with no error.
- `EVeinType` is `byte`-backed; unboxing its values straight to `int` throws `InvalidCastException`.
- `Krafs.Publicizer` is applied to `Assembly-CSharp`, so private/internal members are directly accessible — prefer that over Harmony's `___fieldName` injection.
- A `(wrapper dynamic-method)` frame in a stack trace means the exception came from one of these patches.

## Cheats — `src/Patches/Cheat/`

Six rule-bypass switches in `cheats.json`, **all on by default** (they were off until the owner flipped the default after 1.5.0), behind one `enabled` master switch. They are kept apart from everything else because they are a different kind of change: the rest of this mod retunes content, these bypass the rules. `ProjectEdenPlugin.ReportCheats` logs the state on every launch — a WARNING naming the switches that are on, an INFO line when none are — because in a repo whose entire debugging method is reading `LogOutput.log`, "no collision is on" and "buildings can overlap, that's a bug" are otherwise indistinguishable six months later.

Design is borrowed from soarqin/DSP_Mods' CheatEnabler (MIT) but not copied — CheatEnabler replaces whole method bodies, while this repo's rule is prefix/postfix first. The whole feature needs **one** transpiler, and it is not for any of the rules: it is for the cursor text (below). Everything that actually changes what can be built is a postfix or a single `SetActive(false)`.

**Verified in game** (`LogOutput.log`, zero exceptions): `PowerTooClose(5)`, `WindTooClose(6)`, `Collide(34)` and `NotEnoughItem(2)` each observed being rejected and then cleared, plus `作弊：建造秒完成已生效，本次结算建好 1 个`. Belt connection with no-collision on is also verified after the `coverObjId` fix below. `NeedWater(24)` is the one path not yet exercised in a session — it only logs when a pump is actually placed on land.

**`ReportCheats` logs unconditionally, and that is the point.** The first version only logged when a switch was on, so a report of "the cheats don't work" produced a log with zero cheat lines — which cannot distinguish *switches off* from *this code never shipped*. That cost a full round trip, and it is the **same** lesson already recorded for `AlloyRatioPatches.ReapplyAll`: **a diagnostic that goes silent when its count is zero cannot tell "nothing to do" from "never ran"**. It now prints one line in all three states (config missing / master switch off / nothing enabled) and names the file path to edit.

**One postfix covers 无条件建造 / 无碰撞 / 发电无间距 / 平地抽水**, because all four are the same mechanism: `CheckBuildConditions` writes its verdict into each `BuildPreview.condition` and only then folds them into the return value, so erasing the conditions you want to allow is the whole implementation. `BuildConditionCheatPatches` hangs off all five build tools at `Priority.Last` so `MinerBuildRulePatches`' postfix settles first.

- **Do not blanket-replace the spacing constants.** CheatEnabler rewrites every `ldc.r4 110.25f` / `144f` in `CheckBuildConditions` to `1f`. In this game version `BuildTool_BlueprintPaste.CheckBuildConditions` contains **seven** `110.25f`, and **three of them are turret spacing** (`TurretComponent` / `PrebuildData` / `BuildPreview.lpos` sqrMagnitude blocks) — trap 2 exactly. Erasing by condition separates them for free: the turret sites never write `condition`.
- The vanilla spacing rule is three tiers, one shared local: `desc.geothermal ? 144f : desc.windForcedPower ? 110.25f : 12.25f`, landing on `PowerTooClose(5)` / `WindTooClose(6)` / `GeothermalTooClose(7)`. **Solar panels are governed by the generic 3.5 m tier**, which is why "wind + solar" is one switch and it necessarily takes geothermal with it.
- `NeedWater(24)` is set at three sites, all in the same `desc.geothermal ? NeedGeothermalResource : NeedWater` branch, so clearing it touches nothing but the water pump. The pump's product still comes from `PlanetData.waterItemId` — a waterless planet pumps nothing.
- **Miners are excluded from 无条件建造** (`veinMiner` / `oilMiner`), the same carve-out CheatEnabler makes: their build check is simultaneously the "is there actually ore here" check, so forcing it yields a miner with an empty `veins[]` that builds fine and never produces.
- The postfix **only raises the return value, never lowers it** (`if (cleared && allOk) result = true`). Each tool tolerates one non-failure condition at the end (paste tolerates `NotEnoughItem`, click tolerates `NeedConn`); recomputing `result = allOk` unconditionally would erase that and make a half-affordable blueprint build nothing — presenting as "the cheat mod broke blueprints". Since the cursor transpiler below also feeds that same summary loop, the return value is usually already true by the time the postfix runs; the raise is kept as the belt to the transpiler's braces, and it is what still works if the anchor is ever lost.

**Clearing the condition is not enough to clear the *message*, and that needs the one transpiler in this feature.** `CheckBuildConditions` ends with its own summary loop that both computes the return value and writes the cursor:

```csharp
bool ok = true;
foreach (var bp in buildPreviews) {
    if (bp.condition == Ok) continue;
    if (bp.condition == <this tool's tolerated one>) continue;
    ok = false;
    actionBuild.model.cursorState = -1;                 // red X
    actionBuild.model.cursorText  = bp.conditionText;   // the red text
}
if (ok) { cursorState = 0; cursorText = "点击鼠标建造" + …; /* ~700 bytes more */ }
```

A postfix runs *after* that, so the building gets built and the red "cannot build here" text stays on screen. Reproducing the `ok` branch in a postfix means copying those ~700 bytes (the drag count `(3)`, the geothermal strength readout); miss one and it's a new bug. Instead `CursorText_Transpiler` swaps just the condition *reads* in that guard for `EffectiveCondition(BuildPreview)` — one opcode+operand each, the `BuildPreview` is already on the stack — and vanilla computes the right cursor on its own. With the cheats off the helper returns the field unchanged, so the transpiler is inert by default.

Locating it: `ldc.i4.m1 ; stfld BuildModel::cursorState` occurs **exactly once** in each of the five tools. From there, walk back at most 24 instructions and rewrite only reads shaped like `ldfld condition` + (`brfalse` | `ldc.i4.X ; beq`). Simulated against the shipped assembly before writing the patch: Click 2, BlueprintPaste 3, Path/Addon/Inserter 1 each — and the shape test is what keeps BlueprintPaste's nearby `preview.output.condition` reads and its `AddErrorMessage(condition, preview)` read out of the rewrite. `AddErrorMessage` is deliberately left alone, so the blueprint paste error panel still lists what it would have rejected.

**无碰撞 needs both halves, and the first shipped version had only one — it did not work.** Turning off the collider pool alone does *not* stop the build-time `Collide` verdict. The chain is `Physics.OverlapBoxNonAlloc` (IL 0DDF) → `PlanetPhysics.GetColliderData` (0E23) → either a `coverObjId` or `collided` → `condition = Collide` (132F), and the same loop also picks up `BuildPreviewModel` colliders on layer 18 — other previews' own models, which are not pool objects. So `Collide(34)` (written by all five tools) has to be erased at the verdict, **together with `coverObjId`, because the same loop writes both**; clearing only the condition presents as "no error message, but clicking still does nothing". That pairing is the exact trap `MinerBuildRulePatches` already documents.

**Clearing `coverObjId` must be skipped for `BuildTool_Path`, because on belts that field is not an obstacle — it *is* the connection.** For a building, `coverObjId` is a silent second gate (`CreatePrebuilds` does `if (bp.coverObjId != 0) continue;`), so it has to be cleared or the click does nothing. For a belt, the same field means "this preview lands on that existing belt, join it". Clearing it makes every belt laid against an existing one build as an independent segment — they abut, each keeps its own end cap, and nothing flows. Reported from play.

**The general lesson: one field, opposite meanings depending on the tool.** This is trap 2 ("the same constant, two meanings") one level up — it applies to *fields*, not just literals, and the discriminator is which `BuildTool` is asking. Do not clear by field; clear per tool.

**And the postfix was reading the wrong collection for one of the five tools, so the whole feature was dead on the blueprint-paste path — for its entire life.** `Relax` iterated `BuildTool.buildPreviews`, but **`BuildTool_BlueprintPaste` keeps its previews in its own `bpPool` / `bpCursor`**; the base list is empty there. So the method hit `Count == 0` and returned **before even its one-shot log line**, which is why nothing in `LogOutput.log` ever hinted at it.

Reported as 「巨型建筑蓝图复制之后没办法粘贴」, and the owner's own diagnosis was the one that landed: *mega buildings are still treated as logistics stations, so two of them close together refuse to build.* `TowerTooClose(8)` fires between stations, and mega buildings carry `isStation` — they are simply the buildings a player puts side by side. **The bug was never mega-building-specific**: nothing a blueprint paste placed could be relaxed by any of the four switches.

Two things made it survive a long time and are worth copying as tells:

- **The transpiler half kept working.** `CursorText_Transpiler` is injected *into the method body*, so it never touches that collection — the cursor text was correctly relaxed while the conditions were not. **A feature that looks half-applied is evidence about *which mechanism* is broken, not about how much of the feature is wrong.**
- **The probe that found it printed the three gates of `CreatePrebuilds` side by side** (`bpgpuiModelId` / `condition` / `coverObjId`) rather than the one that was suspected. The standing hypothesis at the time was `ArrangeOverlapBP` zeroing `bpgpuiModelId`; the log came back `bpgpuiModelId=1, condition=TowerTooClose(8)` and killed it in one round. **Print every gate, not the one you believe in.**

`CreatePrebuilds`'s skip list is worth recording in full, because the first entry was news: `if (bp.bpgpuiModelId <= 0) continue;` (IL 0030) runs **before** the condition check at IL 003B and the `coverObjId` check at IL 0196. `ArrangeOverlapBP` writes `bpgpuiModelId = -1` and `condition = BlueprintBPOverlap(51)` **as a pair**, in all four of its sites — so if that path is ever cleared, both fields have to be restored, exactly like `coverObjId`.

**That path was cleared in 1.12.8 (`BlueprintOverlapPatches`), and the third field is the lesson.**
Reported as 「建筑可以堆叠，蓝图框选也框得到，但一粘贴就只建出来一座」. The blueprint data is
complete; what is switched off is the paste preview — `ArrangeOverlapBP` marks every preview within
**0.5 m** of another (`sqrMagnitude < 0.25f`) and `CreatePrebuilds` drops it at that first gate. So
**clearing `condition` alone does nothing**, which is exactly why the existing build-condition cheat
(which clears conditions) never fixed it. The restore value is **snapshotted, not guessed**: a prefix
on `ArrangeOverlapBP` records `bpgpuiModelId` immediately before it runs, because the only other
writers are `.ctor`/`ResetAll` (both −1), `Clone` (verbatim copy) and the blueprint-copy tool.

**And `coverbp` — the third field written at those same four sites — must be left alone, which the
"restore the pair" habit gets backwards.** `BuildTool_BlueprintPaste.CheckBuildConditions`
@256D–2584 reads it as an **exemption**:

```
2564: if (distance² >= threshold) goto skip;    // far enough apart, nothing to check
256D: if (a.coverbp == b) goto skip;            // a known overlap pair — do not test them against each other
257B: if (b.coverbp == a) goto skip;
2589: ...otherwise run the proximity/collision test
```

So `coverbp` *is* the record of "these two are deliberately coincident". Clearing it would have the
just-restored preview immediately re-rejected by the very next stage. **The general rule: before
restoring every field a vanilla routine wrote, read who else reads them — some of those writes are
the permission slip, not the lock.** Same family as the `coverObjId` split (an obstacle for a
building, the connection itself for a belt), one level in.

**And that `coverObjId` split left a hole that took until 1.12.13 to surface, because a belt has
TWO paths out of `CreatePrebuilds` and only one of them is the gate everyone reads.** Reported as
「堆叠建造模式下，传送带**可能**会出现不建造」 — and *"possibly"* is this bug's fingerprint, since
four of the six conditions are about a **different** preview.

```
012F: if (bp.coverObjId > 0 && bp.desc.isBelt
       && bp.output != null && bp.output.desc.isBelt
       && bp.output.coverObjId > 0          // ← the DOWNSTREAM belt must also be covering
       && bp.output.condition == Ok)        // ← and must itself be buildable
       bpIdWitchWillRebuildCoverBelt[cursor++] = i;   // registered → built at @07AB

0196: if (bp.coverObjId != 0) continue;     // ← everything else is dropped in silence
```

So a covering belt is **either** rebuilt by a second loop **or** discarded with no error, no red
text and nothing in the log. The chain breaks wherever the downstream preview is absent, is not a
belt, or is not covering anything — which is common the moment a blueprint is pasted onto a
partially-overlapping area, i.e. exactly what stacked pasting does.

**Half of this is self-inflicted, and it is the repo's own recurring shape.** `BuildConditionCheatPatches`
clears `coverObjId` under 无碰撞 *so that buildings can stack*, but exempts belts and inserters
(`IsConnectionCarrier`) because for those the field **is** the connection — two separate bug reports
paid for that exemption. The result is that stacking was granted to buildings and withheld from
belts, and the withholding is invisible. *A rule this repo added for one feature silently
constrained another one added later* — **and here both sides are ours.**

`BlueprintCoverBeltPatches` reproduces all six conditions term for term and clears `coverObjId`
**only** for belts vanilla would have dropped; anything heading for the rebuild path is untouched.
That is the drill-bit rule again — *a hook that runs before vanilla decides must reproduce that
decision, not its nominal shape* — and `tools/check_bp_coverbelt.ps1` asserts the shape offline, so
a game update that moves it fails loudly instead of silently mis-reproducing it.

**Two process notes from writing that checker, both already rules here.**
Its first run reported three failures and **all three were the checker's own**: the array is read
**three** times, not two (the extra one is `if (arr == null) arr = new int[…]`), and a fixed byte
window dragged in the tail of the preceding `addonType` block, which also reads `condition`. Classify
each read by *what the next instructions do with it* (`stelem` → register, `ldelem` → consume,
`stfld` same field → init) and anchor the window on the gate's own first `coverObjId` read.
**Confirm a failure is real before reading anything into a checker's verdict.**
And the existing paste probe had been reporting every `coverObjId != 0` belt as "skipped" — including
the ones that were building perfectly well through the rebuild path. **A probe that cannot tell
"blocked" from "took the other path" is not measuring what it claims to.**

**A wrong diagnosis, kept because the reasoning error is the point.** The first fix blamed the collider pool: `BuildTool_Path.UpdateRaycast` genuinely does contain five `Physics.Raycast` calls, and disabling the pool genuinely does blind them, so the story was coherent — and wrong. Restoring the pool did not fix the belts. **A mechanism that *could* explain a symptom is not evidence that it *did*.** The pool split is kept anyway on its own merits (overlapping never needed it, and it blinds the build tools' raycasts), but the belt bug was `coverObjId`.

That round also retracted a narrower claim: this file said "picking and dismantling are unaffected — `RaycastLogic` contains zero references to `UnityEngine.Physics`". That is still true *for picking and dismantling*, and it had been over-generalised into "interaction is unaffected". **The build tools do not use `RaycastLogic`; they use Unity physics directly.**

When it is on: `ColliderPool.TakeCollider` parents every collider under the pool's own transform (`SetParent`, IL 006F), so `instance.gameObject.SetActive(false)` retires all of them, applied from a postfix on `ColliderPool.Awake` (whose first instruction is `set_instance(this)`) plus one on `GameMain.Begin` as a hot-reload fallback.

**建造秒完成 rides vanilla's own sandbox path.** `PlanetFactory.ConstructionBeforeGameTick` → `ConstructionSystem.BeforeGameTick` → `ExecuteFastBuild`, which is gated on nothing but `GameMain.sandboxToolsEnabled` and `GamePrefsData.fastBuildBatchSize > 0`, then calls `RecycleDronesForConstructInstantly()` and `FastBuild(n)`. `FastBuild` *is* the batching layer: `PlanetFactory.batchBuild = true` (read at `BuildFinally` IL 01CC to skip `OnSinglyBuildEntity`) + `BeginFlattenTerrain`/`EndFlattenTerrain` + a four-step tail (`raycastLogic.NotifyBatchObjectRemove`, `audio.SetPlanetAudioDirty`, `onBatchBuild`, `onFactoryBatchBuild`). **So CheatEnabler's extra layer — five prefixes suppressing `CargoTraffic.AlterBeltRenderer`/`AlterPathRenderer`/`RefreshPathUV`/the two `Refresh*BatchesBuffers` — is not needed here; vanilla itself does not bother.** `InstantBuildPatches` reproduces `FastBuild`'s body with exactly one difference: `FastBuild` ignores `itemRequired` (sandbox is free), so this pays from `player.package` first via `TakeTailItems` and skips anything it cannot afford, leaving it to the normal drones. It is a **postfix**, so in real sandbox mode vanilla has already emptied the pool and the loop no-ops rather than downgrading free building to paid.

## Abnormality detection and achievements — `src/Patches/Abnormality/`

**This repo trips DSP's anti-cheat determination unavoidably, and not because of the cheats.**
`ABN_ProtoData.CheckProto` is one statement:

```csharp
if (!protoTable.Signature.Equals(ProtoSignature_0_10_30_3100.CalculateSignature(protoTable)))
    abnormalData.TriggerAbnormality(protoId, 0, new long[] { protoType });
```

`protoType` 1–5 selects `LDB.items / techs / recipes / veges / veins` (`OnInit`'s switch). This mod
the determinator hangs off `onGameBegin` and `beforeGameSave`, and `minRecordVersion` is **0**, so no
version gate holds it. **Measured in game: four hits per load — items(2), techs(3), recipes(4),
veins(6)** — with all six `cheats.json` switches off.

**A prediction of three was wrong, and the fourth one is the interesting half.** Items, recipes and
veins follow from registering protos, which is the obvious accounting. **Techs was not predicted**:
nothing here *adds* a tech, but `PilerLevelPatches` rewrites the stacking techs' `UnlockValues` and
`MatrixLabPatches` rewrites `techSpeed`'s — and a signature is over the table's *contents*, not its
length. **Editing a vanilla proto in place trips the same check as adding one**, which also means
`recipes.json`'s `vanillaEdits` can never be "free" on this axis.

**The whole system is four facts, all read out of IL:**

| | |
|---|---|
| One write funnel | `ABN.GameAbnormalityData_0925.TriggerAbnormality(protoId, minRecordVersion, vals)` — **all twenty** `ABN_*` determinators go through it (enumerated). Its own first instruction is `if (gameDesc.isSandboxMode) return;` |
| One read funnel | `NothingAbnormal()` — **nine** consumers: `AchievementLogic.get_active`, `PropertyLogic.get_active` (**metadata**), `GameSave.SaveCurrentGame` (gates the Milky Way upload login), `UIAchievementPanel`, `UIPropertyWindow`, `UIAbnormalityTip`, `UIAbnormalityCheckInfo` ×2, plus the leftover `TestAbnormalityCheck.Update` |
| Storage | `runtimeDatas` = `new AbnormalityRuntimeData[3000]`, a **stride-30 ring buffer** per proto id (ids are clamped to `(0,100)`). `Import`/`Export` write all 100×30, so **the flag is in the save** |
| Wipe | `ClearAbnormality(protoId)`; **protoId 0 takes the "clear the entire array" branch** |

**Sandbox is not a workaround**: `AchievementLogic.get_active` is
`NothingAbnormal() && achievementEnable && isSelfFormalGame && !isSandboxMode`, so sandbox fails it on
its own last term.

**Shipped: three patches, and the second one is what makes the feature work at all.** Blocking
`TriggerAbnormality` only stops *new* records — but every save that has ever run this mod is already
flagged, so a block-only version does nothing visible on an existing save. That is the repo's own
*two gates, one symptom* shape, and it was caught at design time here rather than in a round trip. So:
prefix `TriggerAbnormality` (stop recording), postfix `NothingAbnormal` → true (unblock the nine
consumers, **including already-flagged saves**), postfix `IsAbnormalTriggerred` → false (the
achievement panel's per-entry query).

**Deliberately non-destructive: `ClearAbnormality(0)` is NOT called.** It is available and would look
more thorough, and it irreversibly rewrites the player's save; the postfix covers all nine consumers
just as completely and reverts the instant the switch is turned off. **When a return value solves it,
do not edit someone else's data.**

**Do not implement this by killing the determinators.** Prefixing `AbnormalityLogic.InitDeterminators`
to return false leaves `determinators` **null** — that method only `newobj`s the dictionary at IL
0000–0006 — and `GameTick`'s second instruction is `callvirt GetEnumerator()` on it, i.e. a null
reference every tick. A postfix `Clear()` avoids the crash and is **still incomplete**: the
event-driven determinators subscribed inside `DeterminatorBase.Init` are not in that dictionary's
control, and `ABN_ProtoData` — the one we provably trip — is exactly one of those.

**The switch is `abnormality.json`, on by default, and it is deliberately not in `cheats.json`** —
`JsonHelper`'s disk override is whole-file, so folding it in would mean shadowing all six cheat
switches to flip one bool (`cargoprobe.json` already exists for that same reason), and the two are
different in kind: this one fires with every cheat off.

**What it does not do: it does not stop the Steam upload.** With it on, achievements really unlock and
reach Steam. PhantomGamers' AchievementsEnabler is the precedent for the other split (unlock locally,
intercept `SteamAchievementManager` so nothing is uploaded); that layer is unwritten here. Recorded as
an owner decision, not an oversight.

## Known gaps

- **Pasting a blueprint of several thousand coincident buildings still freezes, and the
  investigation was stopped rather than finished (owner decision).** Measured on
  `BuildTool_BlueprintPaste.CheckBuildConditions`, three points: 1,840 previews → 109 s,
  3,364 → 342 s, 6,076 → **1,041 s**, i.e. `log(3.04)/log(1.806) = 1.88` — **O(previews²)**.
  Four candidates were measured, not reasoned about, and the first three were wrong:
  `Physics.OverlapBoxNonAlloc` **0.06%**, `AddErrorMessage` **0.0006%**, the station-proximity block
  **~6%** (exactly one sixth — it is one of six such loops). The six preview-vs-preview loops were
  then found by enumerating the loop nesting offline and are now skipped when 无条件建造 is on
  (`BlueprintPairLoopPatches`, verified by `check_bp_inner.ps1` to write nothing but `condition`) —
  **and it still froze**, so even those are not the whole story and no measurement survives the
  freeze (a frozen call never returns, so no postfix records it). **The remaining cost is unidentified.**
  The three offline checkers are kept so the next attempt does not repeat the enumeration; the
  scenario itself (thousands of buildings stacked on one point) is outside what this mod sets out to
  support. **Note the failure mode of that whole session: four mechanisms that each *could* explain
  the symptom, three of which did not — every one of them killed by a measurement, none by thinking.**

  **And "O(previews²)" is only half the curve — at small n there is a FLAT ~177 ms floor that has
  nothing to do with the preview count.** Measured incidentally in 1.12.13 on `CreatePrebuilds`
  (a different method from the one above, but the same click):

  | previews | samples (ms) |
  |---|---|
  | 4 | 175.5 · 176.84 · 176.88 · 178.4 · 182.42 |
  | 24 | 176.03 · 176.56 · 177.05 · 178.3 · 181.17 · 238.97 |

  **Six times the previews, identical median.** Extrapolating the quadratic from 1,840 → 109 s puts
  the n² term at **0.02 ms** for n = 24, i.e. all 177 ms is fixed cost. So the player-visible shape at
  ordinary blueprint sizes is *"every paste click stalls ~0.18 s no matter how small the blueprint"*,
  and that is a different bug from the freeze at thousands of previews — the quadratic only takes over
  somewhere above n ≈ 1,000. **Not investigated** (the owner stopped the earlier probe), but the
  distinction is recorded because attacking the quadratic would not move this number at all.

  The probe's own line already prints both normalisations side by side (`每个` and `n² 摊`) and says
  which to read: whichever stays constant across n *is* the complexity. Here **neither** does —
  `每个` falls 6× and `n² 摊` rises 1000× — which is exactly the signature of a constant.

- **More radius-200 constants almost certainly remain.** The four found so far (`kMaxMeshCnt`,
  `GetModPlane`'s `20020`, `TrashSystem.Gravity`'s `210/800/600`, and the stale `AstroData.uRadius`)
  were each produced by a player-visible symptom, never by a sweep. A heuristic scan — "methods that
  read a planet/astro radius **and** contain a literal in 150–900" — flags **143 methods**; most are
  false positives (360/180 are angles), but it proves the class is not exhausted. The most
  suspicious survivor is `GameCamera.Logic` (155 / 192.8 / 220.95 / 255 / 200). **The cheapest route
  stays the same: a player reports "X behaves oddly on a big planet", then read the IL of whatever
  owns X.** Building a reliable automatic sweep would mean separating "planet-scale distance" from
  "angle/colour/duration", which the literal alone cannot do.

- **物质分解设施** (GenesisBook's 6th mega building) is not ported — its 垃圾回收 recipe type has no vanilla equivalent.
- ~~Mega buildings' 30 slots are invisible to the player.~~ **No longer true.** `MegaStationWindowPatches` still reports `stationId` as 0 — that is what keeps `inspectStationId` at 0 so vanilla writes nothing about this building — and `MegaBothWindowsPatches` opens the station window **itself**, from a per-frame `UIGame._OnUpdate` postfix gated on "is the assembler window open", so **both panels are open at once**. The opposite split was tried first (stop filtering, let vanilla open the station window) and it **broke every station-carrying building**: `inspectStationId` was left non-zero while the window ended up closed, after which `inspectStationId == 0 && stationId > 0` never held again and advanced miners and ordinary stations stopped opening at all. **Do not share ownership of a switch vanilla keeps state for** — take it entirely or leave it entirely; the middle ground strands its state machine somewhere it cannot return from, and the symptom shows up on unrelated buildings. The item in each slot is still automatic (requires→Demand, products→Supply), but the **direction is written once, when the slot is first assigned to that item, and is the player's afterwards** — forcing it every tick is what made the station panel's three buttons snap back, and setting an input slot to Storage is how you feed a mega building by belt. `UIEntityBriefInfo.icons` is still the binding constraint for the *hover* panel.
- Inserter `inserterSTT` (swing speed) is untouched — only stacking and belt speed were raised. At 5000-level stacking the swing rate, not the stack, is what a sorter's throughput is bounded by.
- **The five ammo tiers land at item grid columns 23–27**, so like the other overflow items they are reached through the item picker’s search box or its horizontal paging; the loot filter / signal pickers cannot draw them at all.
- **`UILootFilter`, `UISignalPicker` and `UISignalTagPicker` still clip mod items at column 14.** They draw the item grid and have neither paging nor the search box; `UIItemPicker` (the one that matters — inserter filters, station slots, storage filters) has both. Porting the search row is the cheaper half if they ever need it: `ItemPickerSearchPatches`’ fill routine is independent of `GridIndex`, but each of those windows re-implements `RefreshIcons` and would need its own prefix.
- **`cost` is designed but not implemented.** It is a design-time scalar (never shown, never saved) whose only job is to make the dominance check work: *B dominates A on all four axes **and** costs no more* → A is dead content; more expensive but stronger is legitimate depth. Neither the field nor the validator exists yet, and the validator has to run **twice** — once globally and once inside each use's hit set, because a predicate discards the axes it does not test and can kill a material that survives globally.
- **The alloys exist but nothing consumes them.** All nine are built, but the predicate-driven recipe variants ("any material with H≥70") that would *use* them are designed and unwritten. They must be generated at registration time as one recipe per matching material, never evaluated at runtime — `RecipeProto.Items` is an `int[]` and cannot express a predicate, and a runtime consumption hook would have to fool inserter insertion, `UpdateNeeds` and the values already baked into saves. Until those exist, the alloys are craftable and useless — **the one kind of dead content the cost check cannot catch**, because it compares materials to each other and never asks whether anything wants them.
