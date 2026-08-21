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
            ("End-to-end sample-label raster", SampleLabelRaster),
            ("Known USB model registry", ModelRegistry),
            ("E12 BLE advertisement signature", E12AdvertisementSignature),
            ("E12 BLE command frame", E12CommandFrame),
            ("E12 material layout validation", E12MaterialLayout),
            ("E12 continuous material mapping", E12ContinuousMaterial),
            ("E12 BLE raster data framing", E12DataFrames),
            ("SUPVAN one-to-three block transfer ordering", SupvanBlockTransferOrdering),
            ("SUPVAN buffer-full retry and optional acknowledgement", SupvanBufferRetryAndOptionalAcknowledgement),
            ("SUPVAN premature-idle transfer failure", SupvanPrematureIdle),
            ("SUPVAN transfer cancellation and write failure", SupvanTransferCancellationAndFailure),
            ("SUPVAN head-axis mirror", PrintheadAxisMirror),
            ("E12 15 mm tape centered on 12 mm head", E12ContinuousTapeCenterCrop),
            ("E12 landscape raster rotation", E12LandscapeRotation),
            ("E12 60 mm multi-buffer raster", E12SixtyMillimeterRaster),
            ("E12 direct-thermal ribbon flag", E12RibbonFlag),
            ("Native monochrome print preview", MonochromePrintPreview),
            ("Document snapshots preserve element identity", SnapshotIdentity),
            ("Editor history undo, redo, and save checkpoint", EditorHistoryLifecycle),
            ("Editor history capacity and redo invalidation", EditorHistoryCapacity),
            ("Editor viewport fit and zoom bounds", EditorViewportMath),
            ("Editor numeric validation is culture aware", EditorNumericValidation),
            ("Element clipboard round trip and paste clamping", ElementClipboardRoundTrip),
            ("Settings persistence excludes media", SettingsPersistence),
            ("Label v1 migration and v2 media requirement", DocumentMediaMigration),
            ("Media compatibility and pristine adaptation", MediaCompatibilityAndAdaptation),
            ("Print readiness separates BLE and USB safety", PrintReadinessStates),
            ("Persistent session refresh and pre-print media gate", PrinterSessionLifecycle),
            ("Session connection cancellation and fault state", PrinterSessionCancellationAndFault)
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
        Console.WriteLine($"Prepared {data.WidthDots} × {data.HeightDots} dots in {data.BufferCount} buffer{(data.BufferCount == 1 ? string.Empty : "s")}, {data.CompressedByteCount} compressed bytes. Sending one ETIKRA test label…");
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

        True(raw.SequenceEqual(DecompressLzma(compressed)), "LZMA round trip");
    }

    private static void SampleLabelRaster()
    {
        var profile = PrinterProfiles.Find(0x2073) ?? throw new Exception("T50M Pro profile missing");
        var data = SupvanRasterEncoder.Encode(DocumentService.CreateSampleDocument(), profile, 7);
        True(data.BufferCount >= 1, "at least one buffer");
        True(data.Blocks.All(block => block.Length is > 13 and <= ushort.MaxValue), "valid compressed block lengths");
        Equal(data.Blocks.Sum(block => block.Length), data.CompressedByteCount, "aggregate compressed byte count");
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

    private static void SupvanBlockTransferOrdering()
    {
        for (var blockCount = 1; blockCount <= 3; blockCount++)
        {
            var channel = new FakeBlockTransferChannel(
                Enumerable.Repeat(new SupvanTransferStatus(true, false), blockCount));
            SupvanBlockTransfer.TransferAsync(
                    CreateTransferData(blockCount),
                    channel,
                    null,
                    CancellationToken.None,
                    bufferReadyAttempts: 3,
                    bufferReadyDelayMilliseconds: 0,
                    bufferSettleDelayMilliseconds: 0)
                .GetAwaiter().GetResult();

            var expected = Enumerable.Range(1, blockCount)
                .SelectMany(index => new[] { "status:ready", $"announce:{index}", $"write:{index}", $"commit:{index}" })
                .ToArray();
            True(channel.Events.SequenceEqual(expected), $"{blockCount}-buffer transfer ordering");
        }
    }

    private static void SupvanBufferRetryAndOptionalAcknowledgement()
    {
        var channel = new FakeBlockTransferChannel(
            [
                new SupvanTransferStatus(true, false),
                new SupvanTransferStatus(true, true),
                new SupvanTransferStatus(true, false)
            ])
        {
            BufferCommitAcknowledgementOptional = true,
            OmitCommitAcknowledgementForBlock = 0
        };
        var messages = new List<string>();
        SupvanBlockTransfer.TransferAsync(
                CreateTransferData(2),
                channel,
                new InlineProgress<string>(messages.Add),
                CancellationToken.None,
                bufferReadyAttempts: 3,
                bufferReadyDelayMilliseconds: 0,
                bufferSettleDelayMilliseconds: 0)
            .GetAwaiter().GetResult();

        True(channel.Events.SequenceEqual(new[]
        {
            "status:ready", "announce:1", "write:1", "commit:1", "status:full", "status:ready", "announce:2", "write:2", "commit:2"
        }), "buffer-full status should be retried before the second transfer");
        True(messages.Any(message => message.Contains("optional acknowledgement", StringComparison.Ordinal)),
            "omitted commit acknowledgement should be reported and tolerated");
    }

    private static void SupvanPrematureIdle()
    {
        var channel = new FakeBlockTransferChannel(
        [
            new SupvanTransferStatus(true, false),
            new SupvanTransferStatus(false, false)
        ]);
        var failed = false;
        try
        {
            SupvanBlockTransfer.TransferAsync(
                    CreateTransferData(2),
                    channel,
                    null,
                    CancellationToken.None,
                    bufferReadyAttempts: 3,
                    bufferReadyDelayMilliseconds: 0,
                    bufferSettleDelayMilliseconds: 0)
                .GetAwaiter().GetResult();
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("buffer 2 of 2", StringComparison.Ordinal))
        {
            failed = true;
        }

        True(failed, "premature idle should fail before the unsent buffer");
        True(!channel.Events.Contains("announce:2"), "premature idle must not announce another buffer");
    }

    private static void SupvanTransferCancellationAndFailure()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var channel = new FakeBlockTransferChannel([new SupvanTransferStatus(true, true)])
            {
                CancelAfterStatusRead = cancellation
            };
            var cancelled = false;
            try
            {
                SupvanBlockTransfer.TransferAsync(
                        CreateTransferData(1),
                        channel,
                        null,
                        cancellation.Token,
                        bufferReadyAttempts: 3,
                        bufferReadyDelayMilliseconds: 0,
                        bufferSettleDelayMilliseconds: 0)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            True(cancelled, "cancellation while waiting for buffer space should propagate");
        }

        var failingChannel = new FakeBlockTransferChannel([new SupvanTransferStatus(true, false)])
        {
            FailWriteForBlock = 0
        };
        var writeFailed = false;
        try
        {
            SupvanBlockTransfer.TransferAsync(
                    CreateTransferData(1),
                    failingChannel,
                    null,
                    CancellationToken.None,
                    bufferReadyAttempts: 3,
                    bufferReadyDelayMilliseconds: 0,
                    bufferSettleDelayMilliseconds: 0)
                .GetAwaiter().GetResult();
        }
        catch (IOException)
        {
            writeFailed = true;
        }

        True(writeFailed, "block write failure should propagate");
        True(!failingChannel.Events.Contains("commit:1"), "failed writes must not commit a partial buffer");
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
        Equal(1, data.BufferCount, "40 mm E12 label remains a single-buffer transfer");
    }

    private static void E12SixtyMillimeterRaster()
    {
        var document = new LabelDocument { WidthMm = 60, HeightMm = 15 };
        var data = SupvanRasterEncoder.Encode(
            document,
            PrinterProfiles.E12,
            4,
            1,
            SupvanRasterOrientation.RotateCounterClockwise);

        Equal(120, data.WidthDots, "60 mm raster retains the 15 mm tape width before head cropping");
        Equal(480, data.HeightDots, "60 mm E12 feed length");
        Equal(2, data.BufferCount, "60 mm E12 buffer count");
        Equal(data.Blocks.Sum(block => block.Length), data.CompressedByteCount, "60 mm aggregate compressed bytes");

        var first = DecompressLzma(data.Blocks[0].Payload);
        var last = DecompressLzma(data.Blocks[1].Payload);
        Equal(4096, first.Length, "first uncompressed buffer size");
        Equal(4096, last.Length, "last uncompressed buffer size");
        Equal((byte)0x02, first[2], "first buffer page-start flags");
        Equal((byte)0x0C, last[2], "last buffer page-end flags");
        Equal((byte)12, first[6], "transferred raster uses the 96-dot E12 printhead");
        Equal((byte)12, last[6], "all transferred buffers use the 96-dot E12 printhead");
        Equal((ushort)339, BitConverter.ToUInt16(first, 4), "first buffer image columns");
        Equal((ushort)125, BitConverter.ToUInt16(last, 4), "last buffer image columns");
        Equal(480, BitConverter.ToUInt16(first, 4) + BitConverter.ToUInt16(last, 4) + SupvanRasterEncoder.PageMarginDots * 2,
            "image columns plus page margins preserve 60 mm feed length");
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

    private static void SnapshotIdentity()
    {
        var id = Guid.NewGuid();
        var document = new LabelDocument();
        document.Elements.Add(new LabelElement { Id = id, Kind = LabelElementKind.Text, FontFamily = "Arial" });
        var snapshot = DocumentService.CreateSnapshot(document);
        Equal(id, snapshot.Elements[0].Id, "snapshot element id");
        Equal("Arial", snapshot.Elements[0].FontFamily, "snapshot font family");
    }

    private static void EditorHistoryLifecycle()
    {
        var document = new LabelDocument { Name = "Saved" };
        var history = new EditorHistory();
        history.Reset(EditorSnapshot.Capture(document, null, true, false), markSaved: true);
        True(!history.IsDirty, "fresh saved history should be clean");

        document.Name = "Edited";
        history.Push(EditorSnapshot.Capture(document, null, false, false));
        True(history.CanUndo && history.IsDirty, "edit should enable undo and mark dirty");
        var selectedId = Guid.NewGuid();
        history.UpdateCurrentSelection(selectedId);
        Equal("Saved", history.Undo()!.Document.Name, "undo document");
        True(!history.IsDirty, "undo to save checkpoint should be clean");
        var redone = history.Redo()!;
        Equal("Edited", redone.Document.Name, "redo document");
        Equal(selectedId, redone.SelectedElementId, "selection-only update should follow the current history state");
        history.MarkSaved();
        True(!history.IsDirty, "save should move checkpoint");
    }

    private static void EditorHistoryCapacity()
    {
        var document = new LabelDocument();
        var history = new EditorHistory(3);
        history.Reset(EditorSnapshot.Capture(document, null, false, false), markSaved: false);
        for (var index = 1; index <= 4; index++)
        {
            document.Name = $"Edit {index}";
            history.Push(EditorSnapshot.Capture(document, null, false, false));
        }
        Equal(3, history.Count, "bounded history state count");
        history.Undo();
        document.Name = "Branched";
        history.Push(EditorSnapshot.Capture(document, null, false, false));
        True(!history.CanRedo, "new edit should discard redo states");
    }

    private static void EditorViewportMath()
    {
        Equal(2d, EditorViewport.CalculateFitZoom(880, 380, 400, 150), "fit zoom");
        Equal(EditorViewport.MinimumZoom, EditorViewport.CalculateFitZoom(10, 10, 400, 150), "minimum fit zoom");
        Equal(EditorViewport.MaximumZoom, EditorViewport.CalculateFitZoom(4000, 4000, 100, 100), "maximum fit zoom");
        Equal(1.1d, EditorViewport.Step(1, 1), "zoom in step");
        Equal(0.9d, EditorViewport.Step(1, -1), "zoom out step");
    }

    private static void EditorNumericValidation()
    {
        var portuguese = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");
        True(EditorInputValidation.TryParseNumber("12,5", 8, 100, out var comma, out _, portuguese), "Portuguese decimal should parse");
        Equal(12.5d, comma, "Portuguese decimal value");
        True(EditorInputValidation.TryParseNumber("12.5", 8, 100, out var dot, out _, portuguese), "invariant decimal should parse");
        Equal(12.5d, dot, "invariant decimal value");
        True(!EditorInputValidation.TryParseNumber("wide", 8, 100, out _, out var invalid, portuguese) && invalid is not null, "invalid value should explain error");
        True(!EditorInputValidation.TryParseNumber("101", 8, 100, out _, out var range, portuguese) && range is not null, "out-of-range value should explain error");
    }

    private static void ElementClipboardRoundTrip()
    {
        var id = Guid.NewGuid();
        var element = new LabelElement
        {
            Id = id,
            Kind = LabelElementKind.Text,
            XMm = 39,
            YMm = 14,
            WidthMm = 30,
            HeightMm = 8,
            Content = "Copied",
            FontFamily = "Arial"
        };
        var payload = LabelElementClipboard.Serialize(element);
        var pasted = LabelElementClipboard.CreatePastedElement(payload, new LabelDocument { WidthMm = 40, HeightMm = 15 });
        True(pasted is not null, "clipboard payload should deserialize");
        True(pasted!.Id != id, "paste should assign a new id");
        Equal("Copied", pasted.Content, "paste content");
        Equal("Arial", pasted.FontFamily, "paste font family");
        True(pasted.XMm + pasted.WidthMm <= 40 && pasted.YMm + pasted.HeightMm <= 15, "paste should clamp into document");
        True(LabelElementClipboard.CreatePastedElement("not json", new LabelDocument()) is null, "invalid clipboard payload should be ignored");
    }

    private static void SettingsPersistence()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"etikra-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var service = new SettingsService(path);
            var candidate = new PrinterCandidate(
                "ble:A49340B01CBA",
                "E12 test",
                PrinterTransport.BluetoothLe,
                PrinterProfiles.E12,
                BluetoothAddress: 0xA49340B01CBA);
            var settings = new EtikraSettings
            {
                Density = 9,
                LastPrinter = RememberedPrinter.FromCandidate(candidate)
            };
            service.SaveAsync(settings).GetAwaiter().GetResult();
            var loaded = service.LoadAsync().GetAwaiter().GetResult();
            Equal((byte)9, loaded.Density, "density round trip");
            Equal(candidate.BluetoothAddress, loaded.LastPrinter?.BluetoothAddress, "remembered BLE address");
            True(!File.ReadAllText(path).Contains("media", StringComparison.OrdinalIgnoreCase), "settings must not persist installed media");

            File.WriteAllText(path, "{ invalid");
            loaded = service.LoadAsync().GetAwaiter().GetResult();
            True(loaded.LastPrinter is null, "corrupt settings should fall back safely");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private static void DocumentMediaMigration()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"etikra-document-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "legacy.etikra");
        try
        {
            File.WriteAllText(path, """
                { "FormatVersion": 1, "Name": "Legacy", "WidthMm": 40, "HeightMm": 12, "Elements": [] }
                """);
            var legacy = DocumentService.LoadAsync(path).GetAwaiter().GetResult();
            Equal(1, legacy.FormatVersion, "legacy format retained until save");
            True(legacy.MediaRequirement is null, "legacy document should load unbound");

            legacy.MediaRequirement = new LabelMediaRequirement
            {
                Kind = LabelMediaKind.Continuous,
                TapeWidthMm = 15
            };
            DocumentService.SaveAsync(legacy, path).GetAwaiter().GetResult();
            var upgraded = DocumentService.LoadAsync(path).GetAwaiter().GetResult();
            Equal(2, upgraded.FormatVersion, "save should upgrade to format v2");
            Equal(LabelMediaKind.Continuous, upgraded.MediaRequirement?.Kind, "continuous media kind round trip");
            Equal(15d, upgraded.MediaRequirement?.TapeWidthMm, "tape width round trip");
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static void MediaCompatibilityAndAdaptation()
    {
        var fixedMedia = CreateMaterial(rawType: 1, width: 12, height: 40, gap: 3, serial: 25004);
        var equivalentRoll = CreateMaterial(rawType: 1, width: 12, height: 40, gap: 3, serial: 25005);
        var continuous = CreateMaterial(rawType: 0, width: 15, height: 50, gap: 0, serial: 25001);
        var requirement = MediaCompatibility.ToRequirement(fixedMedia);
        True(MediaCompatibility.IsCompatible(requirement, equivalentRoll), "equivalent replacement roll should remain compatible");
        True(!MediaCompatibility.IsCompatible(requirement, continuous), "different media kind/geometry should conflict");

        var document = new LabelDocument
        {
            WidthMm = 40,
            HeightMm = 12,
            MediaRequirement = requirement
        };
        True(DocumentMediaAdapter.CanAutoAdapt(document, true, continuous), "pristine empty document can auto-adapt");
        document.Elements.Add(new LabelElement { Kind = LabelElementKind.Text, XMm = 35, YMm = 10, WidthMm = 12, HeightMm = 8 });
        True(!DocumentMediaAdapter.CanAutoAdapt(document, true, continuous), "document with artwork must not auto-adapt");
        DocumentMediaAdapter.ResizeAndBind(document, continuous, preserveContinuousLength: true);
        Equal(40d, document.WidthMm, "continuous adaptation preserves requested length");
        Equal(15d, document.HeightMm, "continuous adaptation uses detected tape width");
        True(document.Elements.All(element => element.XMm + element.WidthMm <= document.WidthMm && element.YMm + element.HeightMm <= document.HeightMm),
            "explicit resize should clamp artwork into physical canvas");
    }

    private static void PrintReadinessStates()
    {
        var document = new LabelDocument { WidthMm = 40, HeightMm = 12 };
        var usb = new PrinterCandidate("usb", "USB test", PrinterTransport.UsbHid, new PrinterProfile("USB", 1, 203, 96, "test"), "path");
        var usbReady = PrintSafety.Evaluate(document, usb, PrinterConnectionState.Ready, null, InstalledMediaSnapshot.Unknown, null);
        True(usbReady.CanPrint, "active supported USB path should be warning-only");
        True(usbReady.Checks.Count(check => check.Level == ReadinessLevel.Warning) == 2, "USB should warn for health and media");
        var usbDisconnected = PrintSafety.Evaluate(document, usb, PrinterConnectionState.Disconnected, null, InstalledMediaSnapshot.Unknown, null);
        True(!usbDisconnected.CanPrint, "disconnected USB path must block printing");

        var media = CreateMaterial(1, 12, 40, 3, 25004);
        document.MediaRequirement = MediaCompatibility.ToRequirement(media);
        var ble = new PrinterCandidate("ble", "E12", PrinterTransport.BluetoothLe, PrinterProfiles.E12, BluetoothAddress: 1);
        var health = PrinterHealthSnapshot.FromBle(CreateReadyStatus());
        var info = CreateDeviceInformation();
        var bleReady = PrintSafety.Evaluate(document, ble, PrinterConnectionState.Ready, health, InstalledMediaSnapshot.From(media), info);
        True(bleReady.CanPrint, "matching live BLE media should be print-ready");
        var absent = PrintSafety.Evaluate(document, ble, PrinterConnectionState.Ready, health, InstalledMediaSnapshot.Absent(), info);
        True(!absent.CanPrint, "absent BLE media must block printing");
    }

    private static void PrinterSessionLifecycle()
    {
        var fixedMedia = CreateMaterial(1, 12, 40, 3, 25004);
        var continuous = CreateMaterial(0, 15, 50, 0, 25001);
        var candidate = new PrinterCandidate("ble:test", "E12 test", PrinterTransport.BluetoothLe, PrinterProfiles.E12, BluetoothAddress: 1);
        var factory = new FakePrinterTransportFactory(candidate, fixedMedia);
        using var manager = new AsyncDisposableScope(new PrinterSessionManager(factory));
        manager.Value.ConnectAsync(candidate).GetAwaiter().GetResult();
        Equal(PrinterConnectionState.Ready, manager.Value.ConnectionState, "session should become ready");
        Equal(MediaReadState.Ready, manager.Value.Media.State, "connect should read media");

        var sawClearedMedia = false;
        manager.Value.StateChanged += (_, _) => sawClearedMedia |= manager.Value.Media.State == MediaReadState.Reading;
        factory.Current!.Material = continuous;
        manager.Value.RefreshAsync().GetAwaiter().GetResult();
        True(sawClearedMedia, "refresh should clear stale media before reading");
        Equal((byte)15, manager.Value.Media.Material?.WidthMm, "refreshed media width");

        var document = new LabelDocument
        {
            WidthMm = 40,
            HeightMm = 12,
            MediaRequirement = MediaCompatibility.ToRequirement(fixedMedia)
        };
        var blocked = false;
        try
        {
            manager.Value.PrintAsync(document, 7, null).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            blocked = true;
        }
        True(blocked, "pre-print refresh should block incompatible media");
        Equal(0, factory.Current.PrintCount, "incompatible media must send zero print jobs");

        factory.Material = fixedMedia;
        factory.Current.RaiseConnectionLost();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while ((factory.ConnectCount < 2 || manager.Value.ConnectionState != PrinterConnectionState.Ready) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(25);
        }
        True(factory.ConnectCount >= 2, "connection loss should trigger bounded reconnect");
        Equal(PrinterConnectionState.Ready, manager.Value.ConnectionState, "reconnected session should return to ready");

        manager.Value.DisconnectAsync().GetAwaiter().GetResult();
        Equal(MediaReadState.Unknown, manager.Value.Media.State, "disconnect should discard media snapshot");
        True(manager.Value.DeviceInformation is null, "disconnect should discard device snapshot");
    }

    private static void PrinterSessionCancellationAndFault()
    {
        var candidate = new PrinterCandidate("ble:test", "E12 test", PrinterTransport.BluetoothLe, PrinterProfiles.E12, BluetoothAddress: 1);
        using (var manager = new AsyncDisposableScope(new PrinterSessionManager(new CancellablePrinterTransportFactory())))
        {
            var connect = manager.Value.ConnectAsync(candidate);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            while (manager.Value.ConnectionState != PrinterConnectionState.Connecting && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            var disconnect = manager.Value.DisconnectAsync();
            try
            {
                connect.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation is the behavior under test.
            }
            disconnect.GetAwaiter().GetResult();
            Equal(PrinterConnectionState.Disconnected, manager.Value.ConnectionState, "cancelled connection should become disconnected");
            Equal(MediaReadState.Unknown, manager.Value.Media.State, "cancelled connection must not retain media");
        }

        using (var manager = new AsyncDisposableScope(new PrinterSessionManager(new FaultingPrinterTransportFactory())))
        {
            var failed = false;
            try
            {
                manager.Value.ConnectAsync(candidate).GetAwaiter().GetResult();
            }
            catch (IOException)
            {
                failed = true;
            }
            True(failed, "transport failure should surface to caller");
            Equal(PrinterConnectionState.Faulted, manager.Value.ConnectionState, "transport failure should enter faulted state");
            Equal(MediaReadState.Faulted, manager.Value.Media.State, "transport failure should expose a non-stale media fault");
        }
    }

    private static BleMaterialReport CreateMaterial(byte rawType, byte width, byte height, byte gap, ushort serial) => new(
        [], "UID", "CODE", serial, rawType, width, height, gap, null);

    private static BlePrinterStatus CreateReadyStatus() => new(
        false, false, false, false, false, false, false, false, false, false, false, false, false, 1);

    private static PrinterDeviceInformation CreateDeviceInformation() => new(
        "E12", "G15", "151225", 1, 8, 96, 251, "WriteWithoutResponse", DateTimeOffset.Now);

    private static SupvanPrintData CreateTransferData(int blockCount)
    {
        var blocks = Enumerable.Range(0, blockCount)
            .Select(index => new SupvanCompressedBlock([(byte)(index + 1)]))
            .ToArray();
        return new SupvanPrintData(blocks, 60, 96, 320);
    }

    private static byte[] DecompressLzma(byte[] compressed)
    {
        var rawLength = checked((int)BitConverter.ToInt64(compressed, 5));
        var decoder = new Decoder();
        decoder.SetDecoderProperties(compressed[..5]);
        using var input = new MemoryStream(compressed, 13, compressed.Length - 13, writable: false);
        using var output = new MemoryStream(rawLength);
        decoder.Code(input, output, compressed.Length - 13, rawLength, null);
        return output.ToArray();
    }

    private sealed class FakeBlockTransferChannel(IEnumerable<SupvanTransferStatus> statuses) : ISupvanBlockTransferChannel
    {
        private readonly Queue<SupvanTransferStatus> _statuses = new(statuses);

        public List<string> Events { get; } = [];
        public bool BufferCommitAcknowledgementOptional { get; set; }
        public int? OmitCommitAcknowledgementForBlock { get; set; }
        public int? FailWriteForBlock { get; set; }
        public CancellationTokenSource? CancelAfterStatusRead { get; set; }

        public Task<SupvanTransferStatus> ReadTransferStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_statuses.Count == 0)
            {
                throw new InvalidOperationException("Fake transfer status queue is empty.");
            }

            var status = _statuses.Dequeue();
            Events.Add(status.BufferFull ? "status:full" : status.Printing ? "status:ready" : "status:idle");
            CancelAfterStatusRead?.Cancel();
            return Task.FromResult(status);
        }

        public Task AnnounceBlockAsync(
            SupvanCompressedBlock block,
            int blockIndex,
            int blockCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"announce:{blockIndex + 1}");
            return Task.CompletedTask;
        }

        public Task WriteBlockAsync(
            SupvanCompressedBlock block,
            int blockIndex,
            int blockCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"write:{blockIndex + 1}");
            return FailWriteForBlock == blockIndex
                ? Task.FromException(new IOException("simulated block write failure"))
                : Task.CompletedTask;
        }

        public Task CommitBlockAsync(
            SupvanCompressedBlock block,
            ushort speed,
            int blockIndex,
            int blockCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"commit:{blockIndex + 1}");
            return OmitCommitAcknowledgementForBlock == blockIndex
                ? Task.FromException(new TimeoutException("simulated omitted acknowledgement"))
                : Task.CompletedTask;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class FakePrinterTransportFactory(PrinterCandidate candidate, BleMaterialReport material) : IPrinterTransportFactory
    {
        public BleMaterialReport Material { get; set; } = material;
        public FakePrinterTransport? Current { get; private set; }
        public int ConnectCount { get; private set; }

        public Task<IPrinterSessionTransport> ConnectAsync(PrinterCandidate requested, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            Current = new FakePrinterTransport(candidate, Material);
            return Task.FromResult<IPrinterSessionTransport>(Current);
        }
    }

    private sealed class FakePrinterTransport(PrinterCandidate candidate, BleMaterialReport material) : IPrinterSessionTransport
    {
        public PrinterCandidate Candidate => candidate;
        public BleMaterialReport Material { get; set; } = material;
        public int PrintCount { get; private set; }
        public event EventHandler? ConnectionLost;

        public Task<PrinterDeviceInformation> ReadDeviceInformationAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CreateDeviceInformation());
        public Task<PrinterHealthSnapshot?> ReadHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PrinterHealthSnapshot?>(PrinterHealthSnapshot.FromBle(CreateReadyStatus()));
        public Task<InstalledMediaSnapshot> ReadMediaAsync(CancellationToken cancellationToken) =>
            Task.FromResult(InstalledMediaSnapshot.From(Material));
        public Task<string> PrintAsync(LabelDocument document, InstalledMediaSnapshot media, byte density, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            PrintCount++;
            return Task.FromResult("printed");
        }
        public void RaiseConnectionLost() => ConnectionLost?.Invoke(this, EventArgs.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellablePrinterTransportFactory : IPrinterTransportFactory
    {
        public async Task<IPrinterSessionTransport> ConnectAsync(PrinterCandidate candidate, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    private sealed class FaultingPrinterTransportFactory : IPrinterTransportFactory
    {
        public Task<IPrinterSessionTransport> ConnectAsync(PrinterCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromException<IPrinterSessionTransport>(new IOException("simulated connection failure"));
    }

    private sealed class AsyncDisposableScope(PrinterSessionManager value) : IDisposable
    {
        public PrinterSessionManager Value { get; } = value;
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
