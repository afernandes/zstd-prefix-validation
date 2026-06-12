using System.IO.Compression;
using Zstd129214;

// Subcommands:
//   matrix <qualities csv> <sizesMi csv> [wlog]   — managed synthetic self-delta (literal issue repro)
//   real <base> <target> managed <quality>        — managed .NET 11 patch-from on real files
//   real <base> <target> managed-noldm <quality>  — same, EnableLongDistanceMatching=false (NOTE: maps to
//                                                   native 0 = auto = still ON at windowLog>=27; kept to document that)
//   real <base> <target> native <q> <hlog> [ldm]  — native libzstd patch-from (hashLog = proposed knob; 0 = default;
//                                                   ldm: 0=auto 1=on 2=force-off)
//   selfslice <file> <sizeMi> <quality>           — self-delta with REAL content (first N MiB of a file)
//   revtar <in.tar> <out.tar>                     — rewrites the tar with entries in reverse order
//                                                   (real content, non-constant offsets — rep-codes can't chain)
// Every result line is prefixed with RESULT/MATRIX for parsing.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: matrix <q,..> <mi,..> [wlog] | real <base> <target> managed|managed-noldm <q> | real <base> <target> native <q> <hashLog> [ldm] | selfslice <file> <mi> <q> | revtar <in> <out>");
    return 1;
}

switch (args[0])
{
    case "matrix":
    {
        int[] qualities = args[1].Split(',').Select(int.Parse).ToArray();
        int[] sizes = args[2].Split(',').Select(int.Parse).ToArray();
        int forcedWlog = args.Length > 3 ? int.Parse(args[3]) : 0;
        Console.WriteLine($"# managed .NET 11 ({Environment.Version}), random self-delta seed 42, LDM on, windowLog {(forcedWlog != 0 ? forcedWlog.ToString() : "auto")}");
        foreach (int q in qualities)
        {
            foreach (int mi in sizes)
            {
                var (pct, secs) = Experiments.SelfDeltaManaged(q, mi, forcedWlog);
                Console.WriteLine($"MATRIX quality={q} sizeMi={mi} wlog={forcedWlog} pct={pct:F3} seconds={secs:F1}");
            }
        }

        return 0;
    }

    case "real":
    {
        string basePath = args[1];
        string targetPath = args[2];
        string api = args[3];
        int quality = int.Parse(args[4]);
        int hashLog = api == "native" ? int.Parse(args[5]) : 0;
        int ldmSwitch = api == "native" && args.Length > 6 ? int.Parse(args[6]) : 1;

        var baseData = File.ReadAllBytes(basePath);
        var target = File.ReadAllBytes(targetPath);
        string dataset = $"{Path.GetFileName(basePath)}->{Path.GetFileName(targetPath)}";

        long delta;
        double secs;
        bool roundTrip;
        int windowLog;
        string deltaSha;
        if (api == "managed" || api == "managed-noldm")
        {
            (delta, secs, roundTrip, windowLog, deltaSha) = Experiments.RealManaged(baseData, target, quality, ldm: api == "managed");
        }
        else
        {
            Console.WriteLine($"# native libzstd {Native.Version()}");
            (delta, secs, roundTrip, windowLog, deltaSha) = Experiments.RealNative(baseData, target, quality, hashLog, ldmSwitch);
        }

        Console.WriteLine(
            $"RESULT dataset={dataset} api={api} quality={quality} hashLog={hashLog} ldm={ldmSwitch} windowLog={windowLog} " +
            $"baseBytes={baseData.Length} targetBytes={target.Length} deltaBytes={delta} " +
            $"pct={delta * 100.0 / target.Length:F4} seconds={secs:F1} roundtrip={(roundTrip ? "OK" : "FAIL")} deltaSha={deltaSha}");
        return roundTrip ? 0 : 2;
    }

    case "selfslice":
    {
        // Self-delta with REAL content: prefix = first N MiB of the file, input = a copy (distinct memory).
        int mi = int.Parse(args[2]);
        int q = int.Parse(args[3]);
        var slice = new byte[mi << 20];
        await using (var f = File.OpenRead(args[1]))
        {
            await f.ReadExactlyAsync(slice);
        }

        var copy = (byte[])slice.Clone();
        var (d, s, rt, w, dsha) = Experiments.RealManaged(slice, copy, q);
        Console.WriteLine($"RESULT dataset=selfslice:{Path.GetFileName(args[1])}:{mi}Mi api=managed quality={q} hashLog=0 ldm=1 windowLog={w} baseBytes={slice.Length} targetBytes={copy.Length} deltaBytes={d} pct={d * 100.0 / copy.Length:F4} seconds={s:F1} roundtrip={(rt ? "OK" : "FAIL")} deltaSha={dsha}");
        return rt ? 0 : 2;
    }

    case "revtar":
    {
        var entries = new List<System.Formats.Tar.TarEntry>();
        await using (var input = File.OpenRead(args[1]))
        await using (var reader = new System.Formats.Tar.TarReader(input))
        {
            // copyData detaches each DataStream from the source archive (own MemoryStream).
            while (await reader.GetNextEntryAsync(copyData: true) is { } entry)
            {
                entries.Add(entry);
            }
        }

        entries.Reverse();
        await using (var output = File.Create(args[2]))
        await using (var writer = new System.Formats.Tar.TarWriter(output))
        {
            foreach (var entry in entries)
            {
                await writer.WriteEntryAsync(entry);
            }
        }

        Console.WriteLine($"REVTAR in={args[1]} out={args[2]} entries={entries.Count} bytes={new FileInfo(args[2]).Length}");
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown subcommand: {args[0]}");
        return 1;
}
