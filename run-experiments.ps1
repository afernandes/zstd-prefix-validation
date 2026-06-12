# Reproduces every public-dataset experiment referenced in dotnet/runtime#129214
# (results land in results/). Run setup.ps1 first. Total wall time on an i7-12700H:
# roughly 2 h, dominated by the native q19 runs on the 1.5 GB pair. Each configuration
# runs in its own process.
#
# The production-pair runs (a proprietary 1.35 GB install-tree tar) are not reproducible
# from this repository; their raw output lines are preserved in results/ for reference.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet build Probe/Probe.csproj -c Release
$P = './Probe/bin/Release/net11.0/Probe.exe'
$B = 'data/linux-6.12.92.tar'
$T = 'data/linux-6.12.93.tar'
$R = 'data/linux-6.12.93-rev.tar'
New-Item -ItemType Directory -Force results | Out-Null

function Run([string]$OutFile, [string[][]]$Commands) {
    Remove-Item -ErrorAction SilentlyContinue "results/$OutFile"
    foreach ($args in $Commands) {
        & $P @args | Tee-Object -Append "results/$OutFile"
    }
}

# Exp A — synthetic self-delta matrix (replicates the issue-thread matrix rows)
Run 'expA-matrix.txt' @(, @('matrix', '3,15,19', '32,96,160'))

# Exp B — does the "quality <= 15" workaround hold at scale?
Run 'expB-q15-scale.txt' @(, @('matrix', '15', '320,640,1280'))

# Exp C — real pair (linux 6.12.92 -> 6.12.93), managed + native with the proposed hashLog knob
Run 'expC-linux.txt' @(
    @('real', $B, $T, 'managed', '3'),
    @('real', $B, $T, 'managed', '15'),
    @('real', $B, $T, 'native', '19', '28', '1'),
    @('real', $B, $T, 'native', '15', '28', '1'),
    @('real', $B, $T, 'managed', '19'),
    @('real', $B, $T, 'managed', '22'))

# Exp E2 — reversed-tar (real content, non-constant offsets) + LDM-off controls (native only:
# the managed bool cannot force-disable LDM — false maps to auto, which is ON at windowLog >= 27)
Run 'expE2-controls.txt' @(
    @('revtar', $T, $R),
    @('real', $B, $R, 'managed', '19'),
    @('real', $B, $R, 'native', '19', '28', '1'),
    @('real', $B, $R, 'managed', '15'),
    @('real', $B, $R, 'managed', '3'),
    @('real', $B, $T, 'native', '19', '0', '2'),
    @('real', $B, $T, 'native', '19', '28', '2'))

# Exp F — synthetic q19 with windowLog forced to 31 (rules out windowLog as the cliff variable)
Run 'expF-wlog31.txt' @(
    @('matrix', '19', '96,160', '31'),
    @('matrix', '19', '320', '31'))

# Exp G — self-delta with REAL content at the probe's scale (isolates content as the variable)
Run 'expG-selfslice.txt' @(
    @('selfslice', $B, '160', '19'),
    @('selfslice', $B, '96', '19'))

# Exp H — native defaults with LDM on (same delta sizes as the managed runs)
Run 'expH-native-controls.txt' @(
    @('real', $B, $T, 'native', '19', '0', '1'),
    @('real', $B, $T, 'native', '15', '0', '1'))

# Exp J — managed vs native byte-identity via deltaSha (SHA-256 of the delta stream)
Run 'expJ-delta-hashes.txt' @(
    @('real', $B, $T, 'managed', '19'),
    @('real', $B, $T, 'native', '19', '0', '1'),
    @('real', $B, $T, 'managed', '15'),
    @('real', $B, $T, 'native', '15', '0', '1'))

# Claim tests (xunit) — one test per numeric fact cited in the thread
dotnet test Tests/Tests.csproj -c Release
