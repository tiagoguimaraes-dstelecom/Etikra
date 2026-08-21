using Etikra.Models;
using Etikra.Printing;
using Etikra.Printing.Bluetooth;
using Etikra.Services;
using SevenZip.Compression.LZMA;

namespace Etikra.Tests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--ble-scan", StringComparison.OrdinalIgnoreCase))
        {
            return await ScanBluetoothAsync();
        }

        if (args.Length == 2 && args[0].Equals("--ble-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeBluetoothAsync(args[1]);
        }

        if (args.Length == 2 && args[0].Equals("--ble-info", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadBluetoothInformationAsync(args[1]);
        }

        if (args.Length == 2 && args[0].Equals("--ble-test-print", StringComparison.OrdinalIgnoreCase))
        {
            return await PrintBluetoothTestAsync(args[1]);
        }

        var tests = new (string Name, Action Run)[]
        {
            ("Code 128-B checksum", Code128Checksum),
            ("SUPVAN print-buffer header", PrintBufferHeader),
            ("LZMA firmware parameters and round trip", LzmaRoundTrip),
            ("End-to-end starter-label raster", StarterLabelRaster),
            ("Known USB model registry", ModelRegistry),
            ("E12 BLE advertisement signature", E12AdvertisementSignature),
            ("E12 BLE command frame", E12CommandFrame),
            ("E12 material layout validation", E12MaterialLayout),
            ("E12 continuous material mapping", E12ContinuousMaterial),
            ("E12 BLE raster data framing", E12DataFrames),
            ("SUPVAN head-axis mirror", PrintheadAxisMirror),
            ("E12 15 mm tape centered on 12 mm head", E12ContinuousTapeCenterCrop),
            ("E12 landscape raster rotation", E12LandscapeRotation),
            ("E12 direct-thermal ribbon flag", E12RibbonFlag),
            ("Native monochrome print preview", MonochromePrintPreview)
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

    private static async Task<int> ScanBluetoothAsync()
    {
        Console.WriteLine("Scanning BLE advertisements for 15 seconds…");
        var devices = await BleDiscovery.ScanAsync(TimeSpan.FromSeconds(15));
        foreach (var device in devices)
        {
            var marker = device.LooksLikeE12 ? "E12?" : "    ";
            var services = device.ServiceUuids.Count == 0 ? "-" : string.Join(",", device.ServiceUuids);
            Console.WriteLine($"{marker} {device.AddressText}  {device.Rssi,4} dBm  {device.Name,-24}  {services}");
        }

        Console.WriteLine($"Observed {devices.Count} BLE device(s); {devices.Count(device => device.LooksLikeE12)} E12 candidate(s).");
        return 0;
    }

    private static async Task<int> ProbeBluetoothAsync(string addressText)
    {
        if (!BleDiscovery.TryParseAddress(addressText, out var address))
        {
            Console.Error.WriteLine("Invalid Bluetooth address.");
            return 2;
        }

        Console.WriteLine($"Connecting read-only GATT probe to {BleDiscovery.FormatAddress(address)}…");
        var result = await BleDiscovery.ProbeAsync(address);
        Console.WriteLine($"Device: {result.Name}");
        Console.WriteLine($"Connection: {result.ConnectionStatus}");
        foreach (var service in result.Services)
        {
            Console.WriteLine($"Service {service.Uuid}");
            foreach (var characteristic in service.Characteristics)
            {
                Console.WriteLine($"  {characteristic.Uuid}  {characteristic.Properties}");
            }
        }

        Console.WriteLine(result.HasKnownE12Path ? "Known E12 FEE7/FEC1 path confirmed." : "Known E12 FEE7/FEC1 path not found.");
        return 0;
    }

    private static async Task<int> ReadBluetoothInformationAsync(string addressText)
    {
        if (!BleDiscovery.TryParseAddress(addressText, out var address))
        {
            Console.Error.WriteLine("Invalid Bluetooth address.");
            return 2;
        }

        Console.WriteLine($"Connecting to {BleDiscovery.FormatAddress(address)} and enabling notifications…");
        await using var protocol = await BleProtocol.ConnectAsync(address);
        var information = await protocol.ReadInformationAsync();
        Console.WriteLine($"Bluetooth name: {information.BluetoothName}");
        Console.WriteLine($"Protocol name: {information.ProtocolDeviceName ?? "not returned"}");
        Console.WriteLine($"Protocol revision: {information.ProtocolRevision ?? "not returned"}");
        Console.WriteLine($"Protocol revision raw: {information.ProtocolRevisionRawHex ?? "not returned"}");
        Console.WriteLine($"Firmware byte: {(information.FirmwareVersion is byte firmware ? $"0x{firmware:X2} ({firmware})" : "not returned")}");
        Console.WriteLine($"Resolution: {(information.DotsPerMillimeter is double dpmm ? $"{dpmm:0.##} dots/mm ({information.Dpi:0.#} dpi)" : "not returned")}");
        Console.WriteLine($"ATT MTU: {information.AttMtu}; command writes: {information.CommandWriteOption}");
        Console.WriteLine($"Status: {information.Status}");
        Console.WriteLine($"Errors: {(information.Status.Errors.Count == 0 ? "none" : string.Join(", ", information.Status.Errors))}");
        Console.WriteLine($"Material: {information.Material.GeometryDescription}; type code={information.Material.LabelType}; " +
                          $"firmware counter={(information.Material.FirmwareCounter?.ToString() ?? "not exposed")} (meaning unverified); " +
                          $"label SN={information.Material.LabelSerial}; RFID UID={information.Material.RfidUid}; " +
                          $"plausible={information.Material.HasPlausibleGeometry}");
        Console.WriteLine(information.Material.HasPlausibleGeometry
            ? $"Confirmed material geometry: {information.Material.GeometryDescription}."
            : "Material geometry is ambiguous or invalid; printing remains blocked.");
        foreach (var (command, response) in information.RawResponses.OrderBy(item => item.Key))
        {
            Console.WriteLine($"RX 0x{command:X2}: {BleProtocol.FormatHex(response)}");
        }

        return 0;
    }

    private static async Task<int> PrintBluetoothTestAsync(string addressText)
    {
        if (!BleDiscovery.TryParseAddress(addressText, out var address))
        {
            Console.Error.WriteLine("Invalid Bluetooth address.");
            return 2;
        }

        Console.WriteLine("Connecting and re-reading media before test print…");
        await using var protocol = await BleProtocol.ConnectAsync(address);
        var information = await protocol.ReadInformationAsync();
        var material = information.Material;
        if (!material.HasPlausibleGeometry || material.HeightMm == 0)
        {
            throw new InvalidOperationException("A coherent die-cut label size was not returned; no print data was sent.");
        }

        if (material.IsContinuous)
        {
            throw new InvalidOperationException(
                "The fixed-size CLI test is disabled for continuous tape because its length must be chosen by the user; no print data was sent.");
        }

        if (information.DotsPerMillimeter is not double dotsPerMillimeter)
        {
            throw new InvalidOperationException("The printer did not return its resolution; no print data was sent.");
        }

        var printheadDots = (int)Math.Round(material.WidthMm * dotsPerMillimeter);
        if (printheadDots != 96)
        {
            throw new InvalidOperationException(
                $"The returned media/resolution imply {printheadDots} dots across, not the live-tested E12 width of 96; no print data was sent.");
        }

        if (!material.TryGetE12PrintMaterialCode(out var printMaterialCode) ||
            information.Status.BlockingErrors(ignoreDirectThermalRibbonEnd: true).Count > 0)
        {
            throw new InvalidOperationException("The returned material type or printer state is not safe for the verified test path; no print data was sent.");
        }

        var document = new LabelDocument
        {
            Name = "Etikra Bluetooth test",
            WidthMm = material.HeightMm,
            HeightMm = material.WidthMm
        };
        document.Elements.Add(new LabelElement
        {
            Kind = LabelElementKind.Rectangle,
            XMm = 12,
            YMm = 0.75,
            WidthMm = 16,
            HeightMm = material.WidthMm - 1.5,
            StrokeThicknessMm = 0.3
        });
        document.Elements.Add(new LabelElement
        {
            Kind = LabelElementKind.Text,
            XMm = 14,
            YMm = 2,
            WidthMm = 12,
            HeightMm = material.WidthMm - 4,
            Content = "ETIKRA",
            FontSizePt = 8,
            Bold = true
        });

        var profile = PrinterProfiles.E12 with
        {
            Dpi = (int)Math.Round(dotsPerMillimeter * 25.4),
            PrintheadDots = printheadDots
        };
        var data = SupvanRasterEncoder.Encode(
            document,
            profile,
            density: 4,
            materialType: printMaterialCode,
            orientation: SupvanRasterOrientation.RotateCounterClockwise);
        Console.WriteLine($"Printer reports {material.GeometryDescription}, type {material.LabelType}, {dotsPerMillimeter:0.##} dots/mm, firmware counter {material.FirmwareCounter} (meaning unverified). ");
        Console.WriteLine($"Prepared {data.WidthDots} × {data.HeightDots} dots, {data.Compressed.Length} compressed bytes. Sending one ETIKRA test label…");
        var progress = new Progress<string>(Console.WriteLine);
        await protocol.PrintAsync(data, progress, CancellationToken.None);
        Console.WriteLine("Test print completed according to printer status.");
        return 0;
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

    private static void E12AdvertisementSignature()
    {
        var advertisement = new BleAdvertisement(
            0xA49340B01CBA,
            "A4:93:40:B0:1C:BA",
            "T0188A0000000000",
            -50,
            []);
        True(advertisement.LooksLikeE12, "SUPVAN OUI and T-series advertisement should be recognized");
    }

    private static void E12CommandFrame()
    {
        var command = BleProtocol.BuildCommand(0x30, 0x1234, 0x5678);
        Equal(16, command.Length, "command length");
        True(command[..8].SequenceEqual(new byte[] { 0x7E, 0x5A, 0x0C, 0, 0x10, 0x01, 0xAA, 0x30 }), "command prefix");
        Equal((ushort)0x1234, BitConverter.ToUInt16(command, 12), "little-endian first parameter");
        Equal((ushort)0x5678, BitConverter.ToUInt16(command, 14), "little-endian second parameter");
        Equal((ushort)0x0115, BitConverter.ToUInt16(command, 8), "command checksum");
    }

    private static void E12MaterialLayout()
    {
        var response = new byte[47];
        response[0] = 0x7E;
        response[1] = 0x5A;
        response[2] = 43;
        response[7] = 0x30;
        response[37] = 0xFF;
        response[38] = 0xFF;
        response[39] = 1;
        response[40] = 12;
        response[41] = 40;
        response[42] = 3;
        BitConverter.TryWriteBytes(response.AsSpan(43, 4), 57u);

        var material = BleMaterialReport.Parse(response);
        True(material.HasPlausibleGeometry, "material geometry should validate");
        True(!material.IsContinuous, "gapped material should be fixed-size");
        True(material.TryGetE12PrintMaterialCode(out var printCode), "live die-cut raw type should map to a print code");
        Equal((byte)1, printCode, "die-cut print-buffer material code");
        Equal((byte)12, material.WidthMm, "material width");
        Equal((byte)40, material.HeightMm, "material height");
    }

    private static void E12ContinuousMaterial()
    {
        var response = new byte[47];
        response[0] = 0x7E;
        response[1] = 0x5A;
        response[2] = 43;
        response[7] = 0x30;
        response[39] = 0;
        response[40] = 15;
        response[41] = 50;
        response[42] = 0;
        BitConverter.TryWriteBytes(response.AsSpan(43, 4), 7760u);

        var material = BleMaterialReport.Parse(response);
        True(material.HasPlausibleGeometry, "continuous material geometry should validate");
        True(material.IsContinuous, "raw type 0 with no gap should be continuous");
        True(material.TryGetE12PrintMaterialCode(out var printCode), "continuous raw type should map to a print code");
        Equal((byte)1, printCode, "continuous print-buffer material code");
        Equal((byte)15, material.WidthMm, "continuous tape width");
        True(material.GeometryDescription.Contains("length variable", StringComparison.Ordinal), "continuous device field should not become fixed length");
    }

    private static void E12DataFrames()
    {
        var compressed = Enumerable.Range(0, 501).Select(value => (byte)value).ToArray();
        var frames = BleProtocol.BuildDataFrames(compressed);
        Equal(2, frames.Count, "BLE data frame count");
        Equal(512, frames[0].Length, "BLE data frame size");
        True(frames[0][..6].SequenceEqual(new byte[] { 0x7E, 0x5A, 0xFC, 0x01, 0x10, 0x02 }), "BLE outer header");
        Equal((byte)0xAA, frames[0][6], "BLE inner magic 1");
        Equal((byte)0xBB, frames[0][7], "BLE inner magic 2");
        Equal((byte)0, frames[0][10], "first frame index");
        Equal((byte)2, frames[0][11], "first frame total");
        Equal((ushort)frames[0].AsSpan(10).ToArray().Sum(value => value), BitConverter.ToUInt16(frames[0], 8), "BLE inner checksum");
        Equal((byte)0, frames[0][12], "first compressed byte");
        Equal((byte)1, frames[1][10], "second frame index");
        Equal((byte)2, frames[1][11], "second frame total");
        Equal(compressed[500], frames[1][12], "second frame payload");
    }

    private static void PrintheadAxisMirror()
    {
        var leftPixel = new byte[] { 0x80 };
        var canvas = SupvanRasterEncoder.BuildPrintheadCanvas(leftPixel, 8, 1, 8);
        Equal((byte)0x80, canvas[0], "source left pixel should map to physical high-dot end");

        var rightPixel = new byte[] { 0x01 };
        canvas = SupvanRasterEncoder.BuildPrintheadCanvas(rightPixel, 8, 1, 8);
        Equal((byte)0x01, canvas[0], "source right pixel should map to physical low-dot end");
    }

    private static void E12ContinuousTapeCenterCrop()
    {
        var tapeRow = new byte[15]; // 120 dots = 15 mm at 8 dots/mm.
        tapeRow[1] = 0x18;          // Source dots 11 (cropped) and 12 (first printable).
        tapeRow[13] = 0x18;         // Source dots 107 (last printable) and 108 (cropped).

        var canvas = SupvanRasterEncoder.BuildPrintheadCanvas(tapeRow, 120, 1, 96);
        var expected = new byte[12];
        expected[0] = 0x01;
        expected[11] = 0x80;
        True(canvas.SequenceEqual(expected), "15 mm tape should be center-cropped by 12 dots per edge before head-axis mirroring");
    }

    private static void E12LandscapeRotation()
    {
        // A pixel at the landscape image's top-left moves to the bottom-left
        // of the 90-degree counter-clockwise printer input raster.
        var source = new byte[] { 0x80, 0x00, 0x00 };
        var rotated = SupvanRasterEncoder.RotateCounterClockwise(source, 2, 3);
        Equal(2, rotated.Length, "rotated row count");
        Equal((byte)0x00, rotated[0], "rotated top row");
        Equal((byte)0x80, rotated[1], "rotated bottom-left pixel");

        var document = new LabelDocument { WidthMm = 40, HeightMm = 12 };
        var data = SupvanRasterEncoder.Encode(
            document,
            PrinterProfiles.E12,
            4,
            1,
            SupvanRasterOrientation.RotateCounterClockwise);
        Equal(96, data.WidthDots, "rotated E12 head width");
        Equal(320, data.HeightDots, "rotated E12 feed length");
    }

    private static void E12RibbonFlag()
    {
        var response = new byte[20];
        response[0] = 0x7E;
        response[1] = 0x5A;
        response[2] = 16;
        response[7] = 0x11;
        response[14] = 0x20;
        var status = BlePrinterStatus.Parse(response);
        Equal(1, status.Errors.Count, "raw ribbon error count");
        Equal(0, status.BlockingErrors(ignoreDirectThermalRibbonEnd: true).Count, "direct-thermal blocking error count");

        response[16] = 0x08;
        status = BlePrinterStatus.Parse(response);
        Equal(1, status.BlockingErrors(ignoreDirectThermalRibbonEnd: true).Count, "cover error must remain blocking");
    }

    private static void MonochromePrintPreview()
    {
        var document = new LabelDocument { WidthMm = 40, HeightMm = 12 };
        var preview = LabelRenderer.RenderMonochromePreview(document, 203);
        Equal(320, preview.PixelWidth, "preview pixel width");
        Equal(96, preview.PixelHeight, "preview pixel height");
        var pixels = new byte[preview.PixelWidth * preview.PixelHeight];
        preview.CopyPixels(pixels, preview.PixelWidth, 0);
        True(pixels.All(value => value == 255), "blank preview should be white");
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
