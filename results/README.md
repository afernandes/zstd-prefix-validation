# Raw results map

Every number cited in the dotnet/runtime#129214 discussion traces to a line in these files.
`RESULT` lines: `deltaBytes` / `pct` / `seconds` / `roundtrip` (SHA-256 vs the target).
`MATRIX` lines: synthetic self-delta, `pct` = output as % of input (effective prefix ⇒ ~0%).

| file | what | command |
|---|---|---|
| `expA-matrix.txt` | synthetic matrix q3/q15/q19 × 32/96/160 MiB (replicates the thread's matrix rows) | `matrix 3,15,19 32,96,160` |
| `expB-q15-scale.txt` | q15 probe at 320/640/1280 MiB — the "q ≤ 15" workaround holds at scale | `matrix 15 320,640,1280` |
| `expC-linux.txt` | linux 6.12.92→93: managed q3/q15/q19/q22 + native q19/q15 with `hashLog=28` | `real … managed/native …` |
| `expD-pdvomni.txt` | production pair (proprietary, see root README): managed q3/q15/q19 + native q19 `hashLog=28` | `real … managed/native …` |
| `expE-controls.txt` | **partially invalid** first reversed-tar attempt (revtar crash; `targetBytes=0` lines are garbage); kept for transparency | — |
| `expE2-controls.txt` | reversed-tar runs (q3/q15/q19/native+`hashLog`) + LDM-off controls (`ldm=2`) on both pairs | see `run-experiments.ps1` |
| `expF-wlog31.txt` | synthetic q19 with `windowLog` forced to 31 (96/160/320 MiB) — rules out windowLog as the cliff variable | `matrix 19 96,160 31` etc. |
| `expG-selfslice.txt` | self-delta with REAL content slices (96/160 MiB of `linux-6.12.92.tar`, q19) — isolates content as the variable | `selfslice …` |
| `expH-native-controls.txt` | native q19/q15 at default parameters, LDM on — byte-identical to the managed runs | `real … native 19 0 1` etc. |
| `expI-official-dll.txt` | full re-run of every native configuration against the official zstd v1.5.7 release `libzstd.dll` (SHA in root README) | see `run-experiments.ps1` |

Build provenance: `expC`/`expD` were produced by an earlier probe revision whose `RESULT` line did
not yet include the `ldm=` field (semantics identical, LDM on); `expA`/`expB` predate the `wlog=`
field on `MATRIX` lines. All other files come from the current source. The native runs in
`expC`–`expH` used a non-release libzstd 1.5.7 build; `expI` re-validates every native
configuration against the official release binary.
