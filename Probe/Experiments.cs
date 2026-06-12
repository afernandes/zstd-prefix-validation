using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Zstd129214;

/// <summary>
/// Experiments validating (with measured data only) the discussion in
/// dotnet/runtime#129214: SetPrefix effectiveness per quality level, on synthetic
/// self-delta probes and on real version pairs, through the .NET 11 managed API and
/// through native libzstd (with hashLog, the proposed knob).
/// </summary>
public static class Experiments
{
    /// <summary>Same windowLog computation as the issue repro: smallest window covering prefix+input, capped at zstd's max (31).</summary>
    public static int ComputeWindowLog(long size)
    {
        int windowLog = 10;
        while (windowLog < 31 && (1L << windowLog) < size)
        {
            windowLog++;
        }

        return windowLog;
    }

    /// <summary>Managed synthetic self-delta: compresses a random buffer using itself as the prefix (literal issue repro).</summary>
    public static (double Pct, double Seconds) SelfDeltaManaged(int quality, int sizeMi, int forcedWindowLog = 0)
    {
        var prefix = new byte[sizeMi << 20];
        new Random(42).NextBytes(prefix);
        var input = (byte[])prefix.Clone();

        int windowLog = forcedWindowLog != 0 ? forcedWindowLog : ComputeWindowLog(2L * prefix.Length);
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions
        {
            Quality = quality,
            WindowLog = windowLog,
            EnableLongDistanceMatching = true,
        });
        encoder.SetPrefix(prefix);

        var dest = new byte[ZstandardEncoder.GetMaxCompressedLength(input.Length)];
        var sw = Stopwatch.StartNew();
        var status = encoder.Compress(input, dest, out _, out var written, isFinalBlock: true);
        sw.Stop();
        if (status != OperationStatus.Done)
        {
            throw new Exception(status.ToString());
        }

        return (written * 100.0 / input.Length, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Managed patch-from on real files, round-trip verified via SHA-256. DeltaSha = SHA-256 of the delta itself (for cross-API byte-identity checks).</summary>
    public static (long DeltaBytes, double CompressSeconds, bool RoundTrip, int WindowLog, string DeltaSha) RealManaged(
        byte[] baseData, byte[] target, int quality, bool ldm = true)
    {
        int windowLog = ComputeWindowLog((long)baseData.Length + target.Length);
        using var encoder = new ZstandardEncoder(new ZstandardCompressionOptions
        {
            Quality = quality,
            WindowLog = windowLog,
            EnableLongDistanceMatching = ldm,
        });
        encoder.SetPrefix(baseData);

        var dest = new byte[ZstandardEncoder.GetMaxCompressedLength(target.Length)];
        var sw = Stopwatch.StartNew();
        var status = encoder.Compress(target, dest, out _, out var written, isFinalBlock: true);
        sw.Stop();
        if (status != OperationStatus.Done)
        {
            throw new Exception(status.ToString());
        }

        using var decoder = new ZstandardDecoder(ZstandardCompressionOptions.MaxWindowLog);
        decoder.SetPrefix(baseData);
        var restored = new byte[target.Length];
        var dstatus = decoder.Decompress(dest.AsSpan(0, written), restored, out _, out var restoredLen);
        bool roundTrip = dstatus == OperationStatus.Done
            && restoredLen == target.Length
            && SHA256.HashData(restored).AsSpan().SequenceEqual(SHA256.HashData(target));

        string deltaSha = Convert.ToHexStringLower(SHA256.HashData(dest.AsSpan(0, written)));
        return (written, sw.Elapsed.TotalSeconds, roundTrip, windowLog, deltaSha);
    }

    /// <summary>Patch-from on native libzstd (refPrefix + compressStream2), with optional hashLog — the knob from the API proposal.</summary>
    public static unsafe (long DeltaBytes, double CompressSeconds, bool RoundTrip, int WindowLog, string DeltaSha) RealNative(
        byte[] baseData, byte[] target, int quality, int hashLog, int ldmSwitch = 1)
    {
        int windowLog = ComputeWindowLog((long)baseData.Length + target.Length);
        var cctx = Native.ZSTD_createCCtx();
        try
        {
            Check(Native.ZSTD_CCtx_setParameter(cctx, 100, quality));  // ZSTD_c_compressionLevel
            Check(Native.ZSTD_CCtx_setParameter(cctx, 101, windowLog)); // ZSTD_c_windowLog
            Check(Native.ZSTD_CCtx_setParameter(cctx, 160, ldmSwitch)); // ZSTD_c_enableLongDistanceMatching (ZSTD_ps_auto=0, enable=1, disable=2)
            if (hashLog != 0)
            {
                Check(Native.ZSTD_CCtx_setParameter(cctx, 102, hashLog)); // ZSTD_c_hashLog
            }

            var dest = new byte[Native.ZSTD_compressBound((nuint)target.Length)];
            long written;
            var sw = Stopwatch.StartNew();
            fixed (byte* prefix = baseData)
            fixed (byte* src = target)
            fixed (byte* dst = dest)
            {
                Check(Native.ZSTD_CCtx_refPrefix(cctx, prefix, (nuint)baseData.Length));
                nuint dstPos = 0, srcPos = 0;
                nuint r;
                do
                {
                    r = Native.ZSTD_compressStream2_simpleArgs(
                        cctx, dst, (nuint)dest.Length, &dstPos, src, (nuint)target.Length, &srcPos, 2); // ZSTD_e_end
                    Check(r);
                } while (r != 0);
                written = (long)dstPos;
            }

            sw.Stop();
            bool roundTrip = VerifyNative(baseData, target, dest, written);
            string deltaSha = Convert.ToHexStringLower(SHA256.HashData(dest.AsSpan(0, (int)written)));
            return (written, sw.Elapsed.TotalSeconds, roundTrip, windowLog, deltaSha);
        }
        finally
        {
            Native.ZSTD_freeCCtx(cctx);
        }
    }

    private static unsafe bool VerifyNative(byte[] baseData, byte[] target, byte[] delta, long deltaLen)
    {
        var dctx = Native.ZSTD_createDCtx();
        try
        {
            Check(Native.ZSTD_DCtx_setParameter(dctx, 100, 31)); // ZSTD_d_windowLogMax
            var restored = new byte[target.Length];
            fixed (byte* prefix = baseData)
            fixed (byte* src = delta)
            fixed (byte* dst = restored)
            {
                Check(Native.ZSTD_DCtx_refPrefix(dctx, prefix, (nuint)baseData.Length));
                nuint dstPos = 0, srcPos = 0;
                nuint r;
                do
                {
                    r = Native.ZSTD_decompressStream_simpleArgs(
                        dctx, dst, (nuint)restored.Length, &dstPos, src, (nuint)deltaLen, &srcPos);
                    Check(r);
                } while (r != 0 && srcPos < (nuint)deltaLen);
                if (dstPos != (nuint)target.Length)
                {
                    return false;
                }
            }

            return SHA256.HashData(restored).AsSpan().SequenceEqual(SHA256.HashData(target));
        }
        finally
        {
            Native.ZSTD_freeDCtx(dctx);
        }
    }

    private static void Check(nuint code)
    {
        if (Native.ZSTD_isError(code) != 0)
        {
            throw new Exception(Marshal.PtrToStringAnsi(Native.ZSTD_getErrorName(code)));
        }
    }
}

internal static unsafe class Native
{
    private const string Dll = "libzstd";

    [DllImport(Dll)] public static extern uint ZSTD_versionNumber();
    [DllImport(Dll)] public static extern IntPtr ZSTD_createCCtx();
    [DllImport(Dll)] public static extern nuint ZSTD_freeCCtx(IntPtr cctx);
    [DllImport(Dll)] public static extern nuint ZSTD_CCtx_setParameter(IntPtr cctx, int param, int value);
    [DllImport(Dll)] public static extern nuint ZSTD_CCtx_refPrefix(IntPtr cctx, byte* prefix, nuint size);
    [DllImport(Dll)] public static extern nuint ZSTD_compressBound(nuint srcSize);
    [DllImport(Dll)] public static extern nuint ZSTD_compressStream2_simpleArgs(IntPtr cctx, byte* dst, nuint dstCap, nuint* dstPos, byte* src, nuint srcSize, nuint* srcPos, int endOp);
    [DllImport(Dll)] public static extern IntPtr ZSTD_createDCtx();
    [DllImport(Dll)] public static extern nuint ZSTD_freeDCtx(IntPtr dctx);
    [DllImport(Dll)] public static extern nuint ZSTD_DCtx_setParameter(IntPtr dctx, int param, int value);
    [DllImport(Dll)] public static extern nuint ZSTD_DCtx_refPrefix(IntPtr dctx, byte* prefix, nuint size);
    [DllImport(Dll)] public static extern nuint ZSTD_decompressStream_simpleArgs(IntPtr dctx, byte* dst, nuint dstCap, nuint* dstPos, byte* src, nuint srcSize, nuint* srcPos);
    [DllImport(Dll)] public static extern int ZSTD_isError(nuint code);
    [DllImport(Dll)] public static extern IntPtr ZSTD_getErrorName(nuint code);

    public static string Version()
    {
        uint v = ZSTD_versionNumber();
        return $"{v / 10000}.{v / 100 % 100}.{v % 100}";
    }
}
