# Offline verification for the Cargo.inc widening preloader.
#
# WHY THIS EXISTS: a preloader rewrites the whole Assembly-CSharp before the CLR sees it.
# A mistake shows up as the game failing to start with a CLR type-load error that points
# nowhere near the patcher. None of this repo's usual method (read the IL, report a match
# count, loud-fail on zero) is available at that point. So the transform is driven here,
# offline, against a COPY -- and the result is re-scanned and asserted -- before the DLL is
# ever placed in BepInEx/patchers.
#
# This file must stay pure ASCII: Windows PowerShell reads .ps1 as ANSI, and non-ASCII text
# gets mangled into parser errors that look nothing like an encoding problem.
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools\verify_preloader.ps1

# VERIFY THE BUILD YOU ARE ABOUT TO SHIP. This defaulted to Debug while release
# packaging took bin\Release, so a package once went out against a verification that
# never touched the binary inside it. Same sources is an argument, not a check.
#   -Config Release   before packaging
param(
  [ValidateSet("Debug","Release")][string]$Config = "Debug",
  # Machine paths. Pass them on the command line, or set PROJECTEDEN_BEPINEX /
  # PROJECTEDEN_MANAGED. The fallbacks below are one developer's box: a fork will
  # not have them, and a hardcoded path here means the script simply does not run.
  [string]$BepInEx = $env:PROJECTEDEN_BEPINEX,
  [string]$Managed = $env:PROJECTEDEN_MANAGED
)

$ErrorActionPreference = "Stop"

if (-not $BepInEx) { $BepInEx = "$env:APPDATA\r2modmanPlus-local\DysonSphereProgram\profiles\ProjectEden\BepInEx" }
$bepinex  = $BepInEx
if (-not $Managed) { $Managed = "G:\SteamLibrary\steamapps\common\Dyson Sphere Program\DSPGAME_Data\Managed" }
$managed  = $Managed
$preload  = "$PSScriptRoot\..\ProjectEden.Preloader\bin\$Config\ProjectEden.Preloader.dll"
$work     = Join-Path $env:TEMP "projecteden-preloader-verify"

if (-not (Test-Path $preload)) { throw "Preloader not built: $preload" }

New-Item -ItemType Directory -Force -Path $work | Out-Null
$copy = Join-Path $work "Assembly-CSharp.dll"
$out  = Join-Path $work "Assembly-CSharp.patched.dll"
Copy-Item "$managed\Assembly-CSharp.dll" $copy -Force

# Load by bytes, not Add-Type -Path: that would lock the files and break the next build.
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$bepinex\core\Mono.Cecil.dll"))
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes("$bepinex\core\Mono.Cecil.Rocks.dll"))
$pre = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($preload))

# Cecil must resolve the game's other assemblies (UnityEngine etc.) while reading.
$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($managed)
$rp = New-Object Mono.Cecil.ReaderParameters
$rp.AssemblyResolver = $resolver

$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($copy, $rp)

$widener = $pre.GetType("ProjectEden.Preloader.CargoIncWidener")
$apply   = $widener.GetMethod("Apply", [Reflection.BindingFlags]"NonPublic,Static")
$report  = $apply.Invoke($null, @($asm.MainModule))

function Field($o, $n) {
  $f = $o.GetType().GetField($n, [Reflection.BindingFlags]"NonPublic,Instance,Public")
  return $f.GetValue($o)
}

$blockers = Field $report "Blockers"
$notes    = Field $report "Notes"

Write-Host "=== transform report ===" -ForegroundColor Cyan
foreach ($n in $notes) { Write-Host "  note: $n" }
Write-Host ("  applied        : {0}" -f (Field $report "Applied"))
Write-Host ("  widened params : {0}" -f (Field $report "WidenedParams"))
Write-Host ("  widened locals : {0}" -f (Field $report "WidenedLocals"))
Write-Host ("  indirect fixed : {0}" -f (Field $report "FixedIndirect"))
Write-Host ("  conv fixed     : {0}" -f (Field $report "FixedConv"))
Write-Host ("  call sites     : {0}" -f (Field $report "CallSites"))

if ($blockers.Count -gt 0) {
  Write-Host "=== BLOCKED, nothing was rewritten ===" -ForegroundColor Red
  foreach ($b in $blockers) { Write-Host "  $b" -ForegroundColor Red }
  exit 1
}

# Writing is itself a strong check: Cecil refuses many kinds of structural damage.
$asm.Write($out)
Write-Host "wrote $out" -ForegroundColor Green

# Re-read the RESULT and assert against it -- verify the end state, not our own delta.
$check = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($out, $rp)
$mod = $check.MainModule
$fail = 0

# Both widened fields are checked. An earlier version checked only `inc`, which would have
# reported "all checks passed" for a stack widening it never actually looked at.
$WATCHED = @("inc", "stack")
foreach ($w in $WATCHED) {
  $f = ($mod.GetType("Cargo").Fields | Where-Object { $_.Name -eq $w -and -not $_.IsStatic })
  if ($f.FieldType.MetadataType -ne [Mono.Cecil.MetadataType]::Int16) {
    Write-Host "FAIL: Cargo.$w is $($f.FieldType.FullName), expected Int16" -ForegroundColor Red; $fail++
  } else { Write-Host "ok: Cargo.$w is Int16" -ForegroundColor Green }
}

function AllTypes($m) {
  $stack = New-Object System.Collections.Stack
  foreach ($t in $m.Types) { $stack.Push($t) }
  while ($stack.Count -gt 0) {
    $t = $stack.Pop(); $t
    foreach ($n in $t.NestedTypes) { $stack.Push($n) }
  }
}

$types = @(AllTypes $mod)

# 1. no byte-typed inc parameters may survive
$leftover = @()
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    foreach ($p in $m.Parameters) {
      if (-not ($WATCHED | Where-Object { $p.Name.StartsWith($_) })) { continue }
      $pt = $p.ParameterType
      $isByte = ($pt.MetadataType -eq [Mono.Cecil.MetadataType]::Byte)
      if ($pt.IsByReference) { $isByte = ($pt.ElementType.MetadataType -eq [Mono.Cecil.MetadataType]::Byte) }
      if ($isByte) { $leftover += ("{0}::{1}" -f $t.Name, $m.Name) }
    }
  }
}
if ($leftover.Count -gt 0) {
  Write-Host "FAIL: byte inc/stack params survived: $($leftover -join ', ')" -ForegroundColor Red; $fail++
} else { Write-Host "ok: no byte-typed inc/stack parameters remain" -ForegroundColor Green }

# 2. no byte-width indirect access may remain next to Cargo::inc
$bad = @()
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    for ($i = 0; $i -lt $ins.Count; $i++) {
      $o = $ins[$i].Operand
      if ($o -eq $null -or $o.GetType().Name -notlike "Field*") { continue }
      if ($o.DeclaringType.Name -ne "Cargo" -or $WATCHED -notcontains $o.Name) { continue }
      for ($j = $i + 1; $j -lt [Math]::Min($i + 10, $ins.Count); $j++) {
        $n = $ins[$j].OpCode.Name
        if ($n -eq "ldind.u1" -or $n -eq "stind.i1" -or $n -eq "conv.u1") {
          $bad += ("{0}::{1} @{2:X4} {3}" -f $t.Name, $m.Name, $ins[$j].Offset, $n)
        }
        if ($n -eq "ldflda" -or $n -eq "ldfld") { break }
      }
    }
  }
}
if ($bad.Count -gt 0) {
  Write-Host "FAIL: byte-width access near a widened Cargo field:" -ForegroundColor Red
  $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: no byte-width indirect access near widened Cargo fields" -ForegroundColor Green }

# 3. every local passed by-ref into a widened method must be Int16
$badLocals = @()
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    for ($i = 0; $i -lt $ins.Count; $i++) {
      $o = $ins[$i].Operand
      if ($o -eq $null -or $o.GetType().Name -notlike "Method*") { continue }
      # Only the inc parameter matters. `stack` sits right next to it and is CORRECTLY
      # still a Byte -- an earlier version of this check flagged it and produced 26 false
      # failures. Check exactly one address push: the one for inc, which is the last
      # parameter in every one of these signatures.
      $incIdx = -1
      for ($k = 0; $k -lt $o.Parameters.Count; $k++) {
        $p = $o.Parameters[$k]
        if (-not ($WATCHED | Where-Object { $p.Name.StartsWith($_) })) { continue }
        if (-not $p.ParameterType.IsByReference) { continue }
        # Only belt-side inc. StorageComponent's TakeTailItems family has an Int32& named
        # inc -- that is the storage ledger, already wide, deliberately untouched.
        $et = $p.ParameterType.ElementType.MetadataType
        if ($et -ne [Mono.Cecil.MetadataType]::Byte -and $et -ne [Mono.Cecil.MetadataType]::Int16) { continue }
        $incIdx = $k
      }
      if ($incIdx -lt 0) { continue }
      # Address pushes for trailing out/ref args are one instruction each and appear in
      # parameter order, so the push for inc sits this far back from the call.
      # (PickFuelForPowerGenFrom has an `out bool` AFTER inc, so inc is not always last.)
      $back = $o.Parameters.Count - $incIdx
      if ($i - $back -lt 0) { continue }
      $a = $ins[$i - $back]
      if ($a.OpCode.Name -ne "ldloca" -and $a.OpCode.Name -ne "ldloca.s" -and
          $a.OpCode.Name -notlike "ldarg*" -and $a.OpCode.Name -ne "ldflda") {
        $badLocals += ("{0}::{1} -> {2}: inc arg is {3}, checker cannot verify" -f $t.Name, $m.Name, $o.Name, $a.OpCode.Name)
        continue
      }
      if ($a.OpCode.Name -eq "ldloca" -or $a.OpCode.Name -eq "ldloca.s") {
        $v = $a.Operand
        if ($v.VariableType.MetadataType -eq [Mono.Cecil.MetadataType]::Byte) {
          $badLocals += ("{0}::{1} -> {2} inc local V_{3} still Byte" -f $t.Name, $m.Name, $o.Name, $v.Index)
        }
      }
    }
  }
}
if ($badLocals.Count -gt 0) {
  Write-Host "FAIL: byte locals passed by-ref into widened methods:" -ForegroundColor Red
  $badLocals | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: all by-ref inc/stack locals are Int16" -ForegroundColor Green }

# 3b. the inserter's two stacking fields must be Int16, with no truncation feeding them.
# Added when the transform grew to cover InserterComponent -- the rule is: when a transform
# grows a new case, grow its checker in the same change, or it will report "all checks
# passed" about something it never looked at.
$badIns = @()
$ins_t = $mod.GetType("InserterComponent")
foreach ($fn in @("stackInput", "stackOutput")) {
  $f = ($ins_t.Fields | Where-Object { $_.Name -eq $fn })
  if ($f.FieldType.MetadataType -ne [Mono.Cecil.MetadataType]::Int16) {
    $badIns += "InserterComponent.$fn is $($f.FieldType.FullName), expected Int16"
  }
}
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ii = @($m.Body.Instructions)
    for ($i = 1; $i -lt $ii.Count; $i++) {
      if ($ii[$i].OpCode.Name -ne "stfld") { continue }
      $o = $ii[$i].Operand
      if ($o -eq $null -or $o.DeclaringType.Name -ne "InserterComponent") { continue }
      if ($o.Name -ne "stackInput" -and $o.Name -ne "stackOutput") { continue }
      if ($ii[$i-1].OpCode.Name -eq "conv.u1") {
        $badIns += ("{0}::{1} @{2:X4} conv.u1 -> {3}" -f $t.Name, $m.Name, $ii[$i].Offset, $o.Name)
      }
    }
  }
}
if ($badIns.Count -gt 0) {
  Write-Host "FAIL: inserter stacking fields:" -ForegroundColor Red
  $badIns | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: inserter stacking fields are Int16 with no truncation" -ForegroundColor Green }

# 3c. The real invariant, independent of parameter NAMES.
#
# Checks 1-3 select by name (inc* / stack*), which is how the transform used to select too --
# so they agreed with each other and both missed PlanetFactory.InsertInto(.., Byte itemCount,
# Byte itemInc, ..), CargoTraffic.TryInsertItem and PutItemOnBelt. That is the path miners and
# water pumps use to put cargo on a belt, so stacking stayed byte-capped while every check
# reported "passed". A checker that shares the transform's discriminator cannot catch the
# transform's blind spot. This one asserts on the chain itself: nothing that moves cargo in or
# out of a belt may still take a Byte.
$chainTypes = @("CargoPath", "CargoTraffic", "CargoContainer", "PlanetFactory", "StorageComponent")
$chainRe = "Insert|Pick|AddCargo|AddItemStack|QueryItem|split_inc|PutItemOnBelt|TryUpdateItem"
$byteChain = @()
foreach ($tn in $chainTypes) {
  $t = $mod.GetType($tn)
  if ($t -eq $null) { continue }
  foreach ($m in $t.Methods) {
    if ($m.Name -notmatch $chainRe) { continue }
    foreach ($p in $m.Parameters) {
      $pt = $p.ParameterType
      $isByte = ($pt.MetadataType -eq [Mono.Cecil.MetadataType]::Byte)
      if ($pt.IsByReference) { $isByte = ($pt.ElementType.MetadataType -eq [Mono.Cecil.MetadataType]::Byte) }
      if ($isByte) { $byteChain += ("{0}::{1}({2} {3})" -f $tn, $m.Name, $pt.Name, $p.Name) }
    }
  }
}
if ($byteChain.Count -gt 0) {
  Write-Host "FAIL: cargo in/out chain still has Byte parameters:" -ForegroundColor Red
  $byteChain | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: no Byte parameters left anywhere in the cargo in/out chain" -ForegroundColor Green }

# 3e. The temp-cargo path (used when a belt is dismantled) must not truncate.
# ItemPackage is what RemoveCargo stashes a whole belt pile into while tmpEnabled; its
# stack was a Byte, so a 5000-pile came back as 136 and the rest was gone. The constructor
# parameters are named _stack, so the name-based checks above never looked at them.
$badTmp = @()
$ipf = ($mod.GetType("ItemPackage").Fields | Where-Object { $_.Name -eq "stack" })
if ($ipf.FieldType.MetadataType -ne [Mono.Cecil.MetadataType]::Int16) {
  $badTmp += "ItemPackage.stack is $($ipf.FieldType.FullName), expected Int16"
}
foreach ($tn in @("ItemPackage", "CargoView")) {
  $t = $mod.GetType($tn)
  foreach ($m in $t.Methods) {
    if ($m.Name -ne ".ctor") { continue }
    foreach ($p in $m.Parameters) {
      if ($p.ParameterType.MetadataType -eq [Mono.Cecil.MetadataType]::Byte) {
        $badTmp += ("{0}::.ctor({1} {2}) still Byte" -f $tn, $p.ParameterType.Name, $p.Name)
      }
    }
  }
}
if ($badTmp.Count -gt 0) {
  Write-Host "FAIL: temp-cargo path still truncates:" -ForegroundColor Red
  $badTmp | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: temp-cargo path (ItemPackage/CargoView) carries full width" -ForegroundColor Green }

# 3d. Every surviving conv.u1 must land somewhere that is STILL a byte.
#
# Independent of how the transform selects things: a truncation to one byte is only
# legitimate if its destination is genuinely still one byte. If a conv.u1 feeds a field or
# local that is now Int16, the value is being truncated on its way into a widened slot --
# which is exactly the class of bug that made the automatic piler eat items and show
# negative numbers (conv.u1 -> stloc -> passed as a widened AddCargo argument).
# Legitimate cases remain: PilerComponent.cacheCdTick is still a Byte, so conv.u1 into it is fine.
$badConv = @()
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ii = @($m.Body.Instructions)
    for ($i = 0; $i -lt $ii.Count - 1; $i++) {
      if ($ii[$i].OpCode.Name -ne "conv.u1") { continue }
      $n = $ii[$i + 1]
      $dest = $null; $kind = ""
      if ($n.OpCode.Name -eq "stfld" -or $n.OpCode.Name -eq "stsfld") {
        $dest = $n.Operand.FieldType; $kind = "field $($n.Operand.Name)"
      } elseif ($n.OpCode.Name -like "stloc*" -and $n.Operand -ne $null) {
        $dest = $n.Operand.VariableType; $kind = "local V_$($n.Operand.Index)"
      }
      if ($dest -eq $null) { continue }
      if ($dest.MetadataType -eq [Mono.Cecil.MetadataType]::Int16) {
        $badConv += ("{0}::{1} @{2:X4} conv.u1 -> {3} (now Int16)" -f $t.Name, $m.Name, $ii[$i].Offset, $kind)
      }
    }
  }
}
if ($badConv.Count -gt 0) {
  Write-Host "FAIL: conv.u1 truncating into a widened destination:" -ForegroundColor Red
  $badConv | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: every surviving conv.u1 lands on something still byte-wide" -ForegroundColor Green }

# 4. every branch target must still resolve after the write/re-read round trip.
#
# This is the check that was missing when the first deployment crashed on startup.
# Inserting instructions grows a method; a pre-existing short branch (br.s) whose
# displacement no longer fits in a signed byte gets written truncated, and Cecil does NOT
# auto-widen it. Nothing complains at rewrite time or at write time -- it surfaces later as
# a NullReferenceException inside Harmony/MonoMod reading the body, pointing at the branch
# rather than at the cause. Only a re-read of the written file can see it.
$dangling = @()
foreach ($t in $types) {
  foreach ($m in $t.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = @($m.Body.Instructions)
    $set = @{}
    foreach ($i in $ins) { $set[[System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($i)] = $true }
    foreach ($i in $ins) {
      $ot = $i.OpCode.OperandType
      $isBr = ($ot -eq [Mono.Cecil.Cil.OperandType]::ShortInlineBrTarget -or
               $ot -eq [Mono.Cecil.Cil.OperandType]::InlineBrTarget)
      $isSw = ($ot -eq [Mono.Cecil.Cil.OperandType]::InlineSwitch)
      if (-not $isBr -and -not $isSw) { continue }
      $targets = if ($isSw) { $i.Operand } else { @($i.Operand) }
      foreach ($tg in $targets) {
        if ($tg -eq $null) {
          $dangling += ("{0}::{1} @{2:X4} {3} -> NULL" -f $t.Name, $m.Name, $i.Offset, $i.OpCode.Name)
        } elseif (-not $set.ContainsKey([System.Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($tg))) {
          $dangling += ("{0}::{1} @{2:X4} {3} -> outside body" -f $t.Name, $m.Name, $i.Offset, $i.OpCode.Name)
        }
      }
    }
  }
}
if ($dangling.Count -gt 0) {
  Write-Host "FAIL: dangling branch targets after round trip:" -ForegroundColor Red
  $dangling | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
  $fail++
} else { Write-Host "ok: all branch targets resolve after write/re-read" -ForegroundColor Green }

# --- check 11: the pipeline this script drives must BE the shipped pipeline -------------
#
# This script invoked CargoIncWidener.Apply directly, which is ONE of the stages
# Patcher.Patch chains. When the quality stages were added, this script was not
# grown with them -- so it kept reporting "all checks passed" for a binary that
# had none of the quality rewrite in it.
#
# That is not hypothetical: a census run against this script's output concluded
# "TryPickItemAtRear is not twinned", which is false. The real pipeline twins it.
# An hour went into a preloader change that was never needed.
#
# Repo rule, already written down and violated anyway: when a transform grows a
# new case, grow its checker first. This check makes the omission loud instead.
$patcherType = $pre.GetType("ProjectEden.Preloader.Patcher")
$stages = @()
if ($patcherType) {
  foreach ($mm in $patcherType.GetMethods([Reflection.BindingFlags]"NonPublic,Public,Static")) {
    foreach ($ins in @()) { }
  }
  # Stage entry points are the *Apply methods this assembly exposes.
  foreach ($tt in $pre.GetTypes()) {
    $am = $tt.GetMethod("Apply", [Reflection.BindingFlags]"NonPublic,Public,Static")
    if ($am) { $stages += $tt.Name }
  }
}
$driven = @("CargoIncWidener")
$missed = $stages | Where-Object { $driven -notcontains $_ }
if ($missed.Count -gt 0) {
  Write-Host ("WARNING: this script drives only {0}; the shipped Patcher also runs: {1}" -f ($driven -join ", "), ($missed -join ", ")) -ForegroundColor Yellow
  Write-Host "         those stages are NOT covered by any check here." -ForegroundColor Yellow
  Write-Host "         Do not read this script's output as validating the quality rewrite." -ForegroundColor Yellow
} else {
  Write-Host "ok: every *.Apply stage in the preloader is exercised by this script" -ForegroundColor Green
}

if ($fail -gt 0) { Write-Host "`n$fail check(s) FAILED -- do not deploy" -ForegroundColor Red; exit 1 }
Write-Host "`nall checks passed" -ForegroundColor Green
