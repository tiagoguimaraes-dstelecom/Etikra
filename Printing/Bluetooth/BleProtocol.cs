using System.IO;
using System.Text;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace Etikra.Printing.Bluetooth;

public sealed record BlePrinterStatus(
    bool BufferFull,
    bool LabelReadWriteError,
    bool LabelEnd,
    bool LabelModeError,
    bool RibbonReadWriteError,
    bool RibbonEnd,
    bool LowBattery,
    bool DeviceBusy,
    bool PrintheadTooHot,
    bool CoverOpen,
    bool UsbInserted,
    bool Printing,
    bool LabelNotInstalled,
    ushort PrintCount)
{
    public IReadOnlyList<string> Errors
    {
        get
        {
            var errors = new List<string>();
            if (LabelReadWriteError) errors.Add("label read/write error");
            if (LabelEnd) errors.Add("label roll empty");
            if (LabelModeError) errors.Add("label mode mismatch");
            if (RibbonReadWriteError) errors.Add("ribbon read/write error");
            if (RibbonEnd) errors.Add("ribbon empty");
            if (LowBattery) errors.Add("low battery");
            if (PrintheadTooHot) errors.Add("printhead temperature too high");
            if (CoverOpen) errors.Add("cover open");
            if (LabelNotInstalled) errors.Add("label not installed");
            return errors;
        }
    }

    public IReadOnlyList<string> BlockingErrors(bool ignoreDirectThermalRibbonEnd = false)
    {
        if (!ignoreDirectThermalRibbonEnd || !RibbonEnd)
        {
            return Errors;
        }

        return Errors.Where(error => error != "ribbon empty").ToArray();
    }

    public static BlePrinterStatus Parse(ReadOnlySpan<byte> response)
    {
        BleProtocol.ValidateResponse(response, BleProtocol.CommandInquiryStatus, 20);
        return new BlePrinterStatus(
            (response[14] & 0x01) != 0,
            (response[14] & 0x02) != 0,
            (response[14] & 0x04) != 0,
            (response[14] & 0x08) != 0,
            (response[14] & 0x10) != 0,
            (response[14] & 0x20) != 0,
            (response[14] & 0x40) != 0,
            (response[15] & 0x04) != 0,
            (response[15] & 0x08) != 0,
            (response[16] & 0x08) != 0,
            (response[16] & 0x10) != 0,
            (response[16] & 0x40) != 0,
            (response[17] & 0x01) != 0,
            BitConverter.ToUInt16(response[18..20]));
    }
}

public sealed record BleMaterialReport(
    byte[] RawResponse,
    string RfidUid,
    string RfidCode,
    ushort LabelSerial,
    byte LabelType,
    byte WidthMm,
    byte HeightMm,
    byte GapMm,
    uint? FirmwareCounter)
{
    public bool IsContinuous => LabelType == 0 && GapMm == 0;

    public bool HasPlausibleGeometry =>
        WidthMm is >= 4 and <= 100 &&
        HeightMm <= 250 &&
        GapMm <= 30 &&
        (IsContinuous || HeightMm > 0);

    public string GeometryDescription => IsContinuous
        ? $"{WidthMm} mm continuous media (length variable; device field {HeightMm} mm)"
        : $"{WidthMm} × {HeightMm} mm, {GapMm} mm gap";
    public string RawHex => BleProtocol.FormatHex(RawResponse);

    // RETURN_MAT's raw type is not the print-buffer material code. The E12 has
    // printed successfully with buffer code 1 for raw die-cut type 1, and the
    // vendor protocol maps continuous tape to buffer code 1 as well.
    public bool TryGetE12PrintMaterialCode(out byte materialCode)
    {
        if (LabelType is 0 or 1)
        {
            materialCode = 1;
            return true;
        }

        materialCode = 0;
        return false;
    }

    public static BleMaterialReport Parse(ReadOnlySpan<byte> response)
    {
        BleProtocol.ValidateResponse(response, BleProtocol.CommandReturnMaterial, 43);
        return new BleMaterialReport(
            response.ToArray(),
            Convert.ToHexString(response[22..29]),
            Convert.ToHexString(response[29..37]),
            BitConverter.ToUInt16(response[37..39]),
            response[39],
            response[40],
            response[41],
            response[42],
            response.Length >= 47 ? BitConverter.ToUInt32(response[43..47]) : null);
    }
}

public sealed record BlePrinterInformation(
    string BluetoothName,
    string? ProtocolDeviceName,
    string? ProtocolRevision,
    byte? FirmwareVersion,
    double? DotsPerMillimeter,
    ushort AttMtu,
    GattWriteOption CommandWriteOption,
    BlePrinterStatus Status,
    BleMaterialReport Material,
    IReadOnlyDictionary<byte, byte[]> RawResponses)
{
    public double? Dpi => DotsPerMillimeter * 25.4;
    public string? ProtocolRevisionRawHex =>
        RawResponses.TryGetValue(BleProtocol.CommandReadRevision, out var response) && response.Length >= 25
            ? BleProtocol.FormatHex(response.AsSpan(22, 3))
            : null;
}

public sealed record BleDeviceInformation(
    string BluetoothName,
    string? ProtocolDeviceName,
    string? ProtocolRevision,
    byte? FirmwareVersion,
    double? DotsPerMillimeter,
    ushort AttMtu,
    GattWriteOption CommandWriteOption,
    IReadOnlyDictionary<byte, byte[]> RawResponses)
{
    public string? ProtocolRevisionRawHex =>
        RawResponses.TryGetValue(BleProtocol.CommandReadRevision, out var response) && response.Length >= 25
            ? BleProtocol.FormatHex(response.AsSpan(22, 3))
            : null;
}

/// <summary>
/// Persistent Windows GATT connection for E11/E12-class SUPVAN printers.
/// Commands use the shared 7E/5A framing; replies arrive as notifications.
/// </summary>
public sealed class BleProtocol : IAsyncDisposable
{
    internal const byte CommandBufferFull = 0x10;
    internal const byte CommandInquiryStatus = 0x11;
    internal const byte CommandCheckDevice = 0x12;
    internal const byte CommandStartPrint = 0x13;
    internal const byte CommandStopPrint = 0x14;
    internal const byte CommandReadDeviceName = 0x16;
    internal const byte CommandReadRevision = 0x17;
    internal const byte CommandReadLabelDpi = 0x22;
    internal const byte CommandReturnMaterial = 0x30;
    internal const byte CommandNextZippedBulk = 0x5C;
    internal const byte CommandReadFirmwareVersion = 0xC5;

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);
    private const int BleBulkChunk = 180;
    private const int DataPayloadSize = 500;
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _notifyCharacteristic;
    private readonly GattCharacteristic _writeCharacteristic;
    private readonly GattSession _session;
    private readonly Channel<byte[]> _notifications = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private bool _disposed;

    private BleProtocol(
        BluetoothLEDevice device,
        GattDeviceService service,
        GattCharacteristic notifyCharacteristic,
        GattCharacteristic writeCharacteristic,
        GattSession session,
        GattWriteOption commandWriteOption)
    {
        _device = device;
        _service = service;
        _notifyCharacteristic = notifyCharacteristic;
        _writeCharacteristic = writeCharacteristic;
        _session = session;
        CommandWriteOption = commandWriteOption;
        _notifyCharacteristic.ValueChanged += OnValueChanged;
        _session.SessionStatusChanged += OnSessionStatusChanged;
    }

    public string DeviceName => string.IsNullOrWhiteSpace(_device.Name)
        ? BleDiscovery.FormatAddress(_device.BluetoothAddress)
        : _device.Name;

    public ushort AttMtu => _session.MaxPduSize;
    public GattWriteOption CommandWriteOption { get; }
    public event EventHandler? ConnectionLost;

    public static async Task<BleProtocol> ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)
            ?? throw new IOException($"Windows could not open BLE device {BleDiscovery.FormatAddress(address)}.");
        GattDeviceService? selectedService = null;
        GattSession? session = null;
        try
        {
            var serviceResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            EnsureSuccess(serviceResult.Status, serviceResult.ProtocolError, "GATT service discovery");

            GattCharacteristic? notify = null;
            GattCharacteristic? write = null;
            var paths = new[]
            {
                (BleDiscovery.Fee7Service, BleDiscovery.Fec1Characteristic, BleDiscovery.Fec1Characteristic),
                (BleDiscovery.E0ffService, BleDiscovery.Ffe1Characteristic, BleDiscovery.Ffe9Characteristic),
                (BleDiscovery.Ff00Service, BleDiscovery.Ff01Characteristic, BleDiscovery.Ff02Characteristic)
            };

            foreach (var path in paths)
            {
                var service = serviceResult.Services.FirstOrDefault(candidate => candidate.Uuid == path.Item1);
                if (service is null)
                {
                    continue;
                }

                var characteristicResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                if (characteristicResult.Status != GattCommunicationStatus.Success)
                {
                    continue;
                }

                notify = characteristicResult.Characteristics.FirstOrDefault(candidate =>
                    candidate.Uuid == path.Item2 &&
                    (candidate.CharacteristicProperties & (GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)) != 0);
                write = characteristicResult.Characteristics.FirstOrDefault(candidate =>
                    candidate.Uuid == path.Item3 &&
                    (candidate.CharacteristicProperties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse)) != 0);
                if (notify is not null && write is not null)
                {
                    selectedService = service;
                    break;
                }
            }

            if (selectedService is null || notify is null || write is null)
            {
                throw new NotSupportedException("No supported writable/notifiable SUPVAN GATT path was found.");
            }

            foreach (var other in serviceResult.Services.Where(candidate => !ReferenceEquals(candidate, selectedService)))
            {
                other.Dispose();
            }

            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(cancellationToken)
                ?? throw new IOException("Windows could not create a GATT session for the printer.");
            session.MaintainConnection = true;

            var writeOption = (write.CharacteristicProperties & GattCharacteristicProperties.Write) != 0
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;
            var protocol = new BleProtocol(device, selectedService, notify, write, session, writeOption);
            try
            {
                var cccd = (notify.CharacteristicProperties & GattCharacteristicProperties.Notify) != 0
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                var subscription = await notify.WriteClientCharacteristicConfigurationDescriptorAsync(cccd).AsTask(cancellationToken);
                EnsureSuccess(subscription, null, "enabling printer notifications");
                return protocol;
            }
            catch
            {
                await protocol.DisposeAsync();
                throw;
            }
        }
        catch
        {
            session?.Dispose();
            selectedService?.Dispose();
            device.Dispose();
            throw;
        }
    }

    public async Task<BlePrinterInformation> ReadInformationAsync(CancellationToken cancellationToken = default)
    {
        var deviceInformation = await ReadDeviceInformationAsync(cancellationToken);
        var statusResponse = await SendCommandAsync(CommandInquiryStatus, cancellationToken: cancellationToken);
        var materialResponse = await SendCommandAsync(CommandReturnMaterial, cancellationToken: cancellationToken);
        var responses = deviceInformation.RawResponses.ToDictionary(pair => pair.Key, pair => pair.Value);
        responses[CommandInquiryStatus] = statusResponse;
        responses[CommandReturnMaterial] = materialResponse;

        return new BlePrinterInformation(
            deviceInformation.BluetoothName,
            deviceInformation.ProtocolDeviceName,
            deviceInformation.ProtocolRevision,
            deviceInformation.FirmwareVersion,
            deviceInformation.DotsPerMillimeter,
            deviceInformation.AttMtu,
            deviceInformation.CommandWriteOption,
            BlePrinterStatus.Parse(statusResponse),
            BleMaterialReport.Parse(materialResponse),
            responses);
    }

    public async Task<BleDeviceInformation> ReadDeviceInformationAsync(CancellationToken cancellationToken = default)
    {
        var responses = new Dictionary<byte, byte[]>();
        responses[CommandCheckDevice] = await SendCommandAsync(CommandCheckDevice, cancellationToken: cancellationToken);

        var protocolName = await TryReadTextCommandAsync(CommandReadDeviceName, 22, responses, cancellationToken);
        var revision = await TryReadTextCommandAsync(CommandReadRevision, 22, responses, cancellationToken);
        byte? firmware = null;
        var firmwareResponse = await TrySendCommandAsync(CommandReadFirmwareVersion, cancellationToken);
        if (firmwareResponse is not null)
        {
            responses[CommandReadFirmwareVersion] = firmwareResponse;
            if (firmwareResponse.Length > 22)
            {
                firmware = firmwareResponse[22];
            }
        }

        double? dotsPerMillimeter = null;
        var dpiResponse = await TrySendCommandAsync(CommandReadLabelDpi, cancellationToken);
        if (dpiResponse is not null)
        {
            responses[CommandReadLabelDpi] = dpiResponse;
            if (dpiResponse.Length >= 24)
            {
                var hundredths = BitConverter.ToUInt16(dpiResponse, 22);
                // The E12 returns 0x0320: 800 hundredths of a dot/mm, i.e. 8 dpmm
                // (203.2 dpi). Vendor code calls this field "label DPI".
                if (hundredths is >= 200 and <= 5000)
                {
                    dotsPerMillimeter = hundredths / 100d;
                }
            }
        }

        return new BleDeviceInformation(
            DeviceName,
            protocolName,
            revision,
            firmware,
            dotsPerMillimeter,
            AttMtu,
            CommandWriteOption,
            responses);
    }

    public async Task<BlePrinterStatus> ReadStatusAsync(CancellationToken cancellationToken = default) =>
        BlePrinterStatus.Parse(await SendCommandAsync(CommandInquiryStatus, cancellationToken: cancellationToken));

    public async Task<BleMaterialReport> ReadMaterialAsync(CancellationToken cancellationToken = default) =>
        BleMaterialReport.Parse(await SendCommandAsync(CommandReturnMaterial, cancellationToken: cancellationToken));

    public async Task PrintAsync(SupvanPrintData data, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Checking Bluetooth printer…");
        await SendCommandAsync(CommandCheckDevice, cancellationToken: cancellationToken);
        await WaitForStatusAsync(status => !status.DeviceBusy && !status.Printing, 60, "ready", cancellationToken, ignoreDirectThermalRibbonEnd: true);

        progress?.Report("Starting Bluetooth print…");
        await SendCommandAsync(CommandStartPrint, cancellationToken: cancellationToken);
        try
        {
            await WaitForStatusAsync(status => status.Printing, 60, "printing state", cancellationToken, ignoreDirectThermalRibbonEnd: true);
            await WaitForStatusAsync(status => !status.BufferFull, 200, "buffer space", cancellationToken, 20, ignoreDirectThermalRibbonEnd: true);

            var frames = BuildDataFrames(data.Compressed);
            progress?.Report($"Sending {data.Compressed.Length:N0} compressed bytes in {frames.Count} BLE frame{(frames.Count == 1 ? string.Empty : "s")}…");
            await SendCommandAsync(
                CommandNextZippedBulk,
                512,
                checked((ushort)frames.Count),
                cancellationToken);

            for (var index = 0; index < frames.Count; index++)
            {
                await SendDataFrameAsync(frames[index], readResponse: index < frames.Count - 1, cancellationToken);
            }

            await Task.Delay(20, cancellationToken);
            try
            {
                await SendCommandAsync(
                    CommandBufferFull,
                    checked((ushort)data.Compressed.Length),
                    data.Speed,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                // This E12 firmware accepts BUF_FULL and begins/finishes the job,
                // but may not echo command 0x10. Status polling is authoritative.
                progress?.Report("Printer omitted the optional buffer-ready echo; verifying completion from live status…");
            }

            progress?.Report("Printing over Bluetooth…");
            await WaitForStatusAsync(status => !status.DeviceBusy && !status.Printing, 300, "print completion", cancellationToken, ignoreDirectThermalRibbonEnd: true);
        }
        catch
        {
            try
            {
                await SendCommandAsync(CommandStopPrint, cancellationToken: CancellationToken.None);
            }
            catch
            {
                // Preserve the original transfer/print error.
            }

            throw;
        }
    }

    public async Task<byte[]> SendCommandAsync(
        byte command,
        ushort first = 0,
        ushort second = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandLock.WaitAsync(cancellationToken);
        try
        {
            while (_notifications.Reader.TryRead(out _))
            {
            }

            var frame = BuildCommand(command, first, second);
            var result = await _writeCharacteristic.WriteValueWithResultAsync(
                    CryptographicBuffer.CreateFromByteArray(frame), CommandWriteOption)
                .AsTask(cancellationToken);
            EnsureSuccess(result.Status, result.ProtocolError, $"writing command 0x{command:X2}");
            return await ReadResponseAsync(command, CommandTimeout, cancellationToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    internal static byte[] BuildCommand(byte command, ushort first = 0, ushort second = 0)
    {
        var frame = new byte[16];
        frame[0] = 0x7E;
        frame[1] = 0x5A;
        frame[2] = 0x0C;
        frame[4] = 0x10;
        frame[5] = 0x01;
        frame[6] = 0xAA;
        frame[7] = command;
        frame[11] = 0x01;
        BitConverter.TryWriteBytes(frame.AsSpan(12, 2), first);
        BitConverter.TryWriteBytes(frame.AsSpan(14, 2), second);
        BitConverter.TryWriteBytes(frame.AsSpan(8, 2), (ushort)frame.AsSpan(10, 6).ToArray().Sum(value => value));
        return frame;
    }

    internal static IReadOnlyList<byte[]> BuildDataFrames(ReadOnlySpan<byte> compressed)
    {
        var frameCount = (compressed.Length + DataPayloadSize - 1) / DataPayloadSize;
        if (frameCount is <= 0 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(compressed), "A BLE print transfer must contain between 1 and 255 data frames.");
        }

        var frames = new List<byte[]>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            var inner = new byte[506];
            inner[0] = 0xAA;
            inner[1] = 0xBB;
            inner[4] = (byte)index;
            inner[5] = (byte)frameCount;
            var source = compressed.Slice(index * DataPayloadSize, Math.Min(DataPayloadSize, compressed.Length - index * DataPayloadSize));
            source.CopyTo(inner.AsSpan(6));
            BitConverter.TryWriteBytes(inner.AsSpan(2, 2), (ushort)inner.AsSpan(4).ToArray().Sum(value => value));

            var frame = new byte[512];
            frame[0] = 0x7E;
            frame[1] = 0x5A;
            BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)508);
            frame[4] = 0x10;
            frame[5] = 0x02;
            inner.CopyTo(frame, 6);
            frames.Add(frame);
        }

        return frames;
    }

    internal static void ValidateResponse(ReadOnlySpan<byte> response, byte expectedCommand, int minimumLength = 8)
    {
        if (response.Length < minimumLength || response[0] != 0x7E || response[1] != 0x5A || response[7] != expectedCommand)
        {
            throw new InvalidDataException(
                $"Invalid response for command 0x{expectedCommand:X2}: {FormatHex(response)}.");
        }
    }

    internal static string FormatHex(ReadOnlySpan<byte> bytes) =>
        string.Join(" ", bytes.ToArray().Select(value => value.ToString("X2")));

    private async Task<string?> TryReadTextCommandAsync(
        byte command,
        int offset,
        IDictionary<byte, byte[]> responses,
        CancellationToken cancellationToken)
    {
        var response = await TrySendCommandAsync(command, cancellationToken);
        if (response is null)
        {
            return null;
        }

        responses[command] = response;
        if (response.Length <= offset)
        {
            return null;
        }

        var end = response.AsSpan(offset).IndexOf((byte)0);
        var payload = end >= 0 ? response.AsSpan(offset, end) : response.AsSpan(offset);
        var text = Encoding.ASCII.GetString(payload).Trim();
        return text.All(character => character is >= ' ' and <= '~') && text.Length > 0 ? text : null;
    }

    private async Task<byte[]?> TrySendCommandAsync(byte command, CancellationToken cancellationToken)
    {
        try
        {
            return await SendCommandAsync(command, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task<byte[]> ReadResponseAsync(byte command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var accumulated = new List<byte>();
        try
        {
            while (true)
            {
                var notification = await _notifications.Reader.ReadAsync(timeoutSource.Token);
                if (accumulated.Count == 0 && (notification.Length < 8 || notification[0] != 0x7E || notification[1] != 0x5A))
                {
                    continue;
                }

                accumulated.AddRange(notification);
                if (accumulated.Count < 8)
                {
                    continue;
                }

                if (accumulated[7] != command)
                {
                    accumulated.Clear();
                    continue;
                }

                var declaredLength = accumulated[2] | (accumulated[3] << 8);
                var totalLength = declaredLength + 4;
                if (totalLength < 8 || accumulated.Count < totalLength)
                {
                    continue;
                }

                var response = accumulated.Take(totalLength).ToArray();
                ValidateResponse(response, command);
                return response;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Printer did not respond to BLE command 0x{command:X2} within {timeout.TotalSeconds:0.#} seconds.");
        }
    }

    private async Task SendDataFrameAsync(byte[] frame, bool readResponse, CancellationToken cancellationToken)
    {
        while (_notifications.Reader.TryRead(out _))
        {
        }

        foreach (var chunk in frame.Chunk(BleBulkChunk))
        {
            var result = await _writeCharacteristic.WriteValueWithResultAsync(
                    CryptographicBuffer.CreateFromByteArray(chunk),
                    GattWriteOption.WriteWithoutResponse)
                .AsTask(cancellationToken);
            EnsureSuccess(result.Status, result.ProtocolError, "writing BLE raster data");
        }

        if (readResponse)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                _ = await _notifications.Reader.ReadAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Printer did not acknowledge a BLE raster data frame.");
            }
        }
    }

    private async Task WaitForStatusAsync(
        Func<BlePrinterStatus, bool> predicate,
        int attempts,
        string state,
        CancellationToken cancellationToken,
        int delayMilliseconds = 100,
        bool ignoreDirectThermalRibbonEnd = false)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var response = await SendCommandAsync(CommandInquiryStatus, cancellationToken: cancellationToken);
            var status = BlePrinterStatus.Parse(response);
            var blockingErrors = status.BlockingErrors(ignoreDirectThermalRibbonEnd);
            if (blockingErrors.Count > 0)
            {
                throw new InvalidOperationException("Printer error: " + string.Join(", ", blockingErrors));
            }

            if (predicate(status))
            {
                return;
            }

            await Task.Delay(delayMilliseconds, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for Bluetooth printer {state}.");
    }

    private void OnValueChanged(GattCharacteristic _, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            _notifications.Writer.TryWrite(bytes);
        }
        catch
        {
            // A malformed notification will surface as a command timeout with context.
        }
    }

    private void OnSessionStatusChanged(GattSession _, GattSessionStatusChangedEventArgs args)
    {
        if (!_disposed && args.Status == GattSessionStatus.Closed)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void EnsureSuccess(GattCommunicationStatus status, byte? protocolError, string operation)
    {
        if (status != GattCommunicationStatus.Success)
        {
            throw new IOException($"{operation} failed: {status} (protocol error {protocolError?.ToString() ?? "none"}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyCharacteristic.ValueChanged -= OnValueChanged;
        _session.SessionStatusChanged -= OnSessionStatusChanged;
        try
        {
            await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
        }
        catch
        {
            // Connection teardown should not mask the caller's result.
        }

        _notifications.Writer.TryComplete();
        _session.MaintainConnection = false;
        _session.Dispose();
        _service.Dispose();
        _device.Dispose();
        _commandLock.Dispose();
    }
}
