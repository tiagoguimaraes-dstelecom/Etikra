using Etikra.Printing;
using Etikra.Services;
using SevenZip.Compression.LZMA;

namespace Etikra.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Code 128-B checksum", Code128Checksum),
            ("SUPVAN print-buffer header", PrintBufferHeader),
            ("LZMA firmware parameters and round trip", LzmaRoundTrip),
            ("End-to-end starter-label raster", StarterLabelRaster),
            ("Known USB model registry", ModelRegistry)
        };

        try
        {
            foreach (var test in tests)
            {
                test.Run();
                Console.WriteLine($"PASS  {test.Name}");
            }

            Console.WriteLine($"{tests.Length} protocol/editor checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL  {exception.Message}");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void Code128Checksum()
    {
        var symbols = Code128Encoder.Encode("A");
        Equal(4, symbols.Count, "symbol count");
        Equal(104, symbols[0], "start B");
        Equal(33, symbols[1], "A code");
        Equal(34, symbols[2], "checksum");
        Equal(106, symbols[3], "stop");
    }

    private static void PrintBufferHeader()
    {
        var image = Enumerable.Repeat((byte)0x5A, 48 * 10).ToArray();
        var buffer = SupvanRasterEncoder.BuildPrintBuffer(image, 48, 10, true, true, 7);
        Equal(4096, buffer.Length, "buffer size");
        Equal((byte)0x0E, buffer[2], "page flags");
        Equal((byte)0x5C, buffer[3], "material/density register");
        Equal((ushort)10, BitConverter.ToUInt16(buffer, 4), "column count");
        Equal((byte)48, buffer[6], "line bytes");
        Equal((ushort)8, BitConverter.ToUInt16(buffer, 8), "top margin");
        Equal((byte)0x5A, buffer[14], "image start");

        uint checksum = 0;
        for (var i = 2; i < 14; i++) checksum += buffer[i];
        for (var i = 1; i <= (14 + 480) / 256; i++) checksum += buffer[i * 256 - 1];
        Equal((ushort)checksum, BitConverter.ToUInt16(buffer, 0), "stride checksum");
    }

    private static void LzmaRoundTrip()
    {
        var raw = Enumerable.Range(0, 8192).Select(i => (byte)(i * 17)).ToArray();
        var compressed = SupvanRasterEncoder.CompressLzma(raw);
        Equal((byte)0x5D, compressed[0], "LZMA property byte");
        Equal(8192u, BitConverter.ToUInt32(compressed, 1), "dictionary size");
        Equal((long)raw.Length, BitConverter.ToInt64(compressed, 5), "uncompressed size");

        var decoder = new Decoder();
        decoder.SetDecoderProperties(compressed[..5]);
        using var input = new MemoryStream(compressed, 13, compressed.Length - 13, writable: false);
        using var output = new MemoryStream();
        decoder.Code(input, output, compressed.Length - 13, raw.Length, null);
        True(raw.SequenceEqual(output.ToArray()), "LZMA round trip");
    }

    private static void StarterLabelRaster()
    {
        var profile = PrinterProfiles.Find(0x2073) ?? throw new Exception("T50M Pro profile missing");
        var data = SupvanRasterEncoder.Encode(DocumentService.CreateStarterDocument(), profile, 7);
        True(data.BufferCount >= 1, "at least one buffer");
        True(data.Compressed.Length is > 13 and <= ushort.MaxValue, "valid compressed page length");
        Equal(203, profile.Dpi, "T50 DPI");
        Equal(384, profile.PrintheadDots, "T50 printhead width");
    }

    private static void ModelRegistry()
    {
        Equal("TP86A Pro", PrinterProfiles.Find(0x2081)?.Name, "TP86 PID");
        True(PrinterProfiles.Find(0xFFFF) is null, "unknown PID is rejected");
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{label}: expected {expected}, got {actual}");
        }
    }

    private static void True(bool condition, string label)
    {
        if (!condition)
        {
            throw new Exception(label);
        }
    }
}
