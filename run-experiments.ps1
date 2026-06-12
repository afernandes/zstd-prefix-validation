# Reproduces every experiment referenced in dotnet/runtime#129214 (results land in results/).
# Run setup.ps1 first. Total wall time on an i7-12700H: roughly 1.5–2 h, dominated by the
# native q19 runs on the 1.5 GB pair. Each configuration runs in its own process.
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

# Exp A — synthetic self-delta matrix (replicates the issue-thread matrix rows)
& $P matrix 3,15,19 32,96,160                      | Tee-Object results/expA-matrix.txt

# Exp B — does the "quality <= 15" workaround hold at scale?
& $P matrix 15 320,640,1280                        | Tee-Object results/expB-q15-scale.txt

# Exp C — real pair (linux 6.12.92 -> 6.12.93), managed + native with the proposed hashLog knob
& $P real $B $T managed 3
& $P real $B $T managed 15
& $P real $B $T native 19 28 1
& $P real $B $T native 15 28 1
& $P real $B $T managed 19
& $P real $B $T managed 22

# Exp E2 — reversed-tar (real content, non-constant offsets) + LDM-off controls (native only:
# the managed bool cannot force-disable LDM — false maps to auto, which is ON at windowLog >= 27)
& $P revtar $T $R
& $P real $B $R managed 19
& $P real $B $R native 19 28 1
& $P real $B $R managed 15
& $P real $B $R managed 3
& $P real $B $T native 19 0 2
& $P real $B $T native 19 28 2

# Exp F — synthetic q19 with windowLog forced to 31 (rules out windowLog as the cliff variable)
& $P matrix 19 96,160 31
& $P matrix 19 320 31

# Exp G — self-delta with REAL content at the probe's scale (isolates content as the variable)
& $P selfslice $B 160 19
& $P selfslice $B 96 19

# Exp H — native defaults with LDM on (completes the 2x2; byte-identical to the managed runs)
& $P real $B $T native 19 0 1
& $P real $B $T native 15 0 1

# Claim tests (xunit) — one test per numeric fact cited in the thread
dotnet test Tests/Tests.csproj -c Release
