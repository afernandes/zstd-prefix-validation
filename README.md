# Zstandard `SetPrefix` validation — dotnet/runtime#129214

Measurement harness and raw results behind the numbers posted in
[dotnet/runtime#129214](https://github.com/dotnet/runtime/issues/129214)
(*[API Proposal]: Expose HashLog and ChainLog on ZstandardCompressionOptions*).

It measures how effective `ZstandardEncoder.SetPrefix` (delta compression / "patch-from") is per
quality level, on synthetic self-delta probes and on real version pairs, through both the .NET 11
managed API and vanilla native libzstd 1.5.7 (where `ZSTD_c_hashLog` — the knob the API proposal
exposes — can be set).

## Findings (all measured, every real-pair delta round-trip-verified via SHA-256)

1. **Quality ≤ 15 is unaffected by the prefix-truncation issue** at any tested size: the
   self-delta probe stays at 0.011% with 32 MB … 1280 MB prefixes (`results/expA`, `expB`).
   On real version pairs the cost of staying at q15 instead of bt levels + `hashLog` is ~1.7–2.2×.
2. **The probe's catastrophic cliff (q16–19, ≥48 MB → 100%) is content-specific, not
   size-specific.** Self-delta with *real* content (a slice of `linux-6.12.92.tar` as its own
   prefix, same parameters) gives 0.017–0.019% where the random-content probe gives 100.002%
   (`expG` vs `expA`/`expF`). On real pairs, managed q19/q22 delta a 1.5 GB prefix just fine —
   aligned or with all 92,418 tar entries reversed (`expC`, `expE2`).
3. **LDM is what rescues real content.** With LDM force-disabled (`ZSTD_c_enableLongDistanceMatching = 2`,
   native-only — the managed `false` maps to 0 = auto = still ON at `windowLog ≥ 27`), native q19 with
   default tables collapses to 9.35% on the linux pair and 47.18% on a production pair; with
   `hashLog = 28` it recovers to 0.11% **without LDM** (`expE2`). The knob makes prefix referencing
   robust independently of whether LDM can find the matches.
4. **`hashLog` specifically unlocks the bt strategies**: at q15 it changes nothing
   (2,737,868 → 2,728,695 bytes on the linux pair), consistent with the truncation
   `1 << MAX(hashLog+3, chainLog+1)` applying under bt match-finders.
5. The managed wrapper is a faithful pass-through: native runs at default parameters produce
   **byte-identical** deltas to the managed API (`expH` vs `expC`).

Headline table — `linux-6.12.92.tar → linux-6.12.93.tar` (1,548,513,280 → 1,548,564,480 bytes,
the same pair used by the maintainer in the issue thread; `WindowLog=31`, LDM on, single thread):

| config | delta bytes | % of target | wall time |
|---|--:|--:|--:|
| managed q3 | 2,581,560 | 0.1667% | 3.2 s |
| managed q15 | 2,737,868 | 0.1768% | 20.2 s |
| managed q19 | 1,573,757 | 0.1016% | 169.7 s |
| managed q22 | 1,276,452 | 0.0824% | 384.4 s |
| native q19 + `hashLog=28` | 1,387,510 | 0.0896% | 536.1 s |
| native q15 + `hashLog=28` | 2,728,695 | 0.1762% | 267.8 s |

## Layout

- `Probe/` — .NET 11 console driver. Subcommands:
  - `matrix <qualities> <sizesMi> [wlog]` — synthetic self-delta (random buffer, seed 42, compressed
    against itself as prefix; an effective prefix ⇒ ~0% output). Literal repro from the issue.
  - `real <base> <target> managed <q>` — managed patch-from on real files + SHA-256 round-trip.
  - `real <base> <target> native <q> <hashLog> [ldm]` — native libzstd patch-from
    (`ZSTD_CCtx_refPrefix` + `ZSTD_compressStream2`), optional `hashLog`, LDM switch (0 auto / 1 on /
    2 force-off) + SHA-256 round-trip.
  - `selfslice <file> <sizeMi> <q>` — self-delta with real content.
  - `revtar <in.tar> <out.tar>` — same entries, reverse order (matches at non-constant offsets,
    so repeat-offsets can't chain).
- `Tests/` — xunit; one test per numeric fact cited in the thread (96 MiB scale).
- `results/` — raw outputs of every run cited in the issue (see `results/README.md` for the map).
- `setup.ps1` — downloads the datasets and native libzstd, SHA-256-verified.
- `run-experiments.ps1` — reproduces everything in order (~1.5–2 h total).

## Requirements

- .NET SDK `11.0.100-preview.4.26230.115` (pinned by `global.json`) — same as the issue report.
- Windows x64 (the native runs load the official `libzstd.dll` from the
  [zstd v1.5.7 release](https://github.com/facebook/zstd/releases/tag/v1.5.7),
  SHA-256 `8f07e1112ed283e5cd2798833e9a3c32d8961381bc36da04af57a1b0ca9bd40b`).
  On Linux the `DllImport("libzstd")` resolves a system `libzstd.so` — use 1.5.7 for comparable
  numbers (untested here).
- ≥ 16 GB RAM for the 1.5 GB pairs (prefix + target + output + tables are all in memory).

## Datasets

| file | bytes | SHA-256 |
|---|--:|---|
| `data/linux-6.12.92.tar` | 1,548,513,280 | `6357d098952cad2d4fb5179da50f3cfe883085fec50fdcaf169f2415b7e01b63` |
| `data/linux-6.12.93.tar` | 1,548,564,480 | `5c9cd2d34b009dd237489906cd2beba122aa2a17680048fb3746817d06d5c345` |

Both come from `cdn.kernel.org` (`.tar.xz` decompressed); the byte sizes match the listing the
maintainer posted in the issue, so the pairs are identical.

The **production pair** cited in the issue (two adjacent releases of a 1.35 GB install-tree tar:
.NET assemblies + APK + MSIX) is proprietary and not redistributable; its raw result lines are
preserved in `results/` so the numbers can be inspected, but those runs cannot be re-executed from
this repository.

## Methodology notes

- `WindowLog` = smallest value covering prefix+input, capped at zstd's max (31). For the 1.5 GB
  pairs that cap is hit; an attached prefix stays referenceable and every delta decodes correctly
  with `refPrefix` (verified).
- LDM (`EnableLongDistanceMatching`) is on in all runs unless stated; note the managed `false`
  cannot force-disable it at `windowLog ≥ 27` (native `0` = auto), which is why LDM-off controls
  use the native path with value `2` (`ZSTD_ps_disable`).
- Timing is `Stopwatch` around the compress call only (file I/O excluded), single-threaded,
  one process per configuration, on an i7-12700H / 64 GB. Output sizes are deterministic;
  times are single-run and indicative. BenchmarkDotNet was deliberately not used: runs take
  seconds to minutes each and the primary metric (output size) is deterministic, so statistical
  repetition would multiply machine-hours without changing any conclusion.
- `results/expE-controls.txt` is the first, partially invalid attempt of the reversed-tar
  experiment (a `revtar` crash left a 0-byte tar; lines with `targetBytes=0` are garbage).
  It is kept for transparency; `expE2` is the valid rerun.

## License

MIT — see [LICENSE](LICENSE).
