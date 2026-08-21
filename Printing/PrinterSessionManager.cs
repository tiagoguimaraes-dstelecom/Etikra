using Etikra.Models;
using Etikra.Printing.Bluetooth;

namespace Etikra.Printing;

internal interface IPrinterSessionTransport : IAsyncDisposable
{
    PrinterCandidate Candidate { get; }
    event EventHandler? ConnectionLost;
    Task<PrinterDeviceInformation> ReadDeviceInformationAsync(CancellationToken cancellationToken);
    Task<PrinterHealthSnapshot?> ReadHealthAsync(CancellationToken cancellationToken);
    Task<InstalledMediaSnapshot> ReadMediaAsync(CancellationToken cancellationToken);
    Task<string> PrintAsync(
        LabelDocument document,
        InstalledMediaSnapshot media,
        byte density,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}

internal interface IPrinterTransportFactory
{
    Task<IPrinterSessionTransport> ConnectAsync(PrinterCandidate candidate, CancellationToken cancellationToken);
}

internal sealed class DefaultPrinterTransportFactory : IPrinterTransportFactory
{
    public async Task<IPrinterSessionTransport> ConnectAsync(PrinterCandidate candidate, CancellationToken cancellationToken)
    {
        if (!candidate.IsSupported)
        {
            throw new NotSupportedException("This printer candidate has no verified Etikra connection profile.");
        }

        if (candidate is { Transport: PrinterTransport.BluetoothLe, BluetoothAddress: ulong address, Profile: { } profile })
        {
            var protocol = await BleProtocol.ConnectAsync(address, cancellationToken);
            return new E12BleSessionTransport(candidate, profile, protocol);
        }

        if (candidate is { Transport: PrinterTransport.UsbHid, DevicePath: { } path, Profile: { } usbProfile })
        {
            return new UsbSessionTransport(candidate, path, usbProfile);
        }

        throw new NotSupportedException("The selected printer transport is not implemented.");
    }
}

internal sealed class E12BleSessionTransport : IPrinterSessionTransport
{
    private readonly PrinterProfile _profile;
    private readonly BleProtocol _protocol;
    private PrinterDeviceInformation? _deviceInformation;

    public E12BleSessionTransport(PrinterCandidate candidate, PrinterProfile profile, BleProtocol protocol)
    {
        Candidate = candidate;
        _profile = profile;
        _protocol = protocol;
        _protocol.ConnectionLost += Protocol_ConnectionLost;
    }

    public PrinterCandidate Candidate { get; }
    public event EventHandler? ConnectionLost;

    public async Task<PrinterDeviceInformation> ReadDeviceInformationAsync(CancellationToken cancellationToken)
    {
        var information = await _protocol.ReadDeviceInformationAsync(cancellationToken);
        _deviceInformation = new PrinterDeviceInformation(
            information.BluetoothName,
            information.ProtocolDeviceName,
            information.ProtocolRevisionRawHex ?? information.ProtocolRevision,
            information.FirmwareVersion,
            information.DotsPerMillimeter,
            _profile.PrintheadDots,
            information.AttMtu,
            information.CommandWriteOption.ToString(),
            DateTimeOffset.Now);
        return _deviceInformation;
    }

    public async Task<PrinterHealthSnapshot?> ReadHealthAsync(CancellationToken cancellationToken) =>
        PrinterHealthSnapshot.FromBle(await _protocol.ReadStatusAsync(cancellationToken));

    public async Task<InstalledMediaSnapshot> ReadMediaAsync(CancellationToken cancellationToken) =>
        InstalledMediaSnapshot.From(await _protocol.ReadMaterialAsync(cancellationToken));

    public async Task<string> PrintAsync(
        LabelDocument document,
        InstalledMediaSnapshot media,
        byte density,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var material = media.Material
            ?? throw new InvalidOperationException("Installed media is unavailable; no print data was sent.");
        if (_deviceInformation?.DotsPerMillimeter is not double dotsPerMillimeter)
        {
            throw new InvalidOperationException("Printer resolution is unavailable; no print data was sent.");
        }

        if (!material.TryGetE12PrintMaterialCode(out var printMaterialCode))
        {
            throw new InvalidOperationException($"Unsupported raw material type {material.LabelType}; no print data was sent.");
        }

        PrintSafety.ValidateE12Document(document, material, _profile, dotsPerMillimeter);
        var liveProfile = _profile with { Dpi = (int)Math.Round(dotsPerMillimeter * 25.4) };
        progress?.Report($"Preparing {material.GeometryDescription} raster at {liveProfile.Dpi} dpi…");
        var data = await Task.Run(
            () => SupvanRasterEncoder.Encode(
                document,
                liveProfile,
                density,
                printMaterialCode,
                SupvanRasterOrientation.RotateCounterClockwise),
            cancellationToken);
        await _protocol.PrintAsync(data, progress, cancellationToken);
        return $"Printed {data.WidthDots} × {data.HeightDots} dots over Bluetooth on {_deviceInformation.ProtocolModel ?? Candidate.DisplayName}.";
    }

    private void Protocol_ConnectionLost(object? sender, EventArgs e) => ConnectionLost?.Invoke(this, EventArgs.Empty);

    public async ValueTask DisposeAsync()
    {
        _protocol.ConnectionLost -= Protocol_ConnectionLost;
        await _protocol.DisposeAsync();
    }
}

internal sealed class UsbSessionTransport(
    PrinterCandidate candidate,
    string devicePath,
    PrinterProfile profile) : IPrinterSessionTransport
{
    public PrinterCandidate Candidate => candidate;
    public event EventHandler? ConnectionLost { add { } remove { } }

    public Task<PrinterDeviceInformation> ReadDeviceInformationAsync(CancellationToken cancellationToken) => Task.FromResult(new PrinterDeviceInformation(
        candidate.DisplayName,
        profile.Name,
        null,
        null,
        profile.Dpi / 25.4,
        profile.PrintheadDots,
        null,
        "USB HID",
        DateTimeOffset.Now));

    public Task<PrinterHealthSnapshot?> ReadHealthAsync(CancellationToken cancellationToken) => Task.FromResult<PrinterHealthSnapshot?>(null);

    public Task<InstalledMediaSnapshot> ReadMediaAsync(CancellationToken cancellationToken) => Task.FromResult(
        new InstalledMediaSnapshot(MediaReadState.Unsupported, null, null, DateTimeOffset.Now,
            "Installed-media interrogation is not implemented for USB."));

    public Task<string> PrintAsync(
        LabelDocument document,
        InstalledMediaSnapshot media,
        byte density,
        IProgress<string>? progress,
        CancellationToken cancellationToken) =>
        new SupvanUsbPrinterBackend(devicePath, profile).PrintAsync(document, density, progress, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PrinterSessionManager : IAsyncDisposable
{
    private readonly IPrinterTransportFactory _transportFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IPrinterSessionTransport? _transport;
    private CancellationTokenSource? _activeConnectionAttempt;
    private CancellationTokenSource _lifetime = new();
    private bool _manualDisconnect;
    private bool _disposed;

    public PrinterSessionManager() : this(new DefaultPrinterTransportFactory())
    {
    }

    internal PrinterSessionManager(IPrinterTransportFactory transportFactory)
    {
        _transportFactory = transportFactory;
    }

    public PrinterConnectionState ConnectionState { get; private set; } = PrinterConnectionState.Disconnected;
    public PrinterCandidate? ActivePrinter { get; private set; }
    public PrinterDeviceInformation? DeviceInformation { get; private set; }
    public PrinterHealthSnapshot? Health { get; private set; }
    public InstalledMediaSnapshot Media { get; private set; } = InstalledMediaSnapshot.Unknown;
    public IReadOnlyList<PrinterCandidate> Candidates { get; private set; } = [];
    public string? LastError { get; private set; }
    public bool IsScanning { get; private set; }
    public event EventHandler? StateChanged;

    public async Task<IReadOnlyList<PrinterCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var showScanningState = ActivePrinter is null && ConnectionState != PrinterConnectionState.Ready;
        IsScanning = true;
        if (showScanningState)
        {
            ConnectionState = PrinterConnectionState.Scanning;
        }
        OnStateChanged();
        try
        {
            var usbTask = Task.Run(UsbHidDiscovery.FindSupvanPrinters, cancellationToken);
            var bleTask = BleDiscovery.ScanAsync(duration, cancellationToken);
            await Task.WhenAll(usbTask, bleTask);
            var candidates = new List<PrinterCandidate>();
            candidates.AddRange(await usbTask);
            candidates.AddRange((await bleTask)
                .Where(advertisement => advertisement.LooksLikeE12)
                .Select(advertisement => new PrinterCandidate(
                    $"ble:{advertisement.Address:X12}",
                    string.IsNullOrWhiteSpace(advertisement.Name) ? BleDiscovery.FormatAddress(advertisement.Address) : advertisement.Name,
                    PrinterTransport.BluetoothLe,
                    PrinterProfiles.E12,
                    BluetoothAddress: advertisement.Address)));
            Candidates = candidates
                .GroupBy(candidate => candidate.Id)
                .Select(group => group.First())
                .ToArray();
            return Candidates;
        }
        finally
        {
            IsScanning = false;
            if (showScanningState && ConnectionState == PrinterConnectionState.Scanning)
            {
                ConnectionState = PrinterConnectionState.Disconnected;
            }
            OnStateChanged();
        }
    }

    public async Task ConnectAsync(PrinterCandidate candidate, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        CancellationTokenSource? attempt = null;
        try
        {
            attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _activeConnectionAttempt = attempt;
            var operationToken = attempt.Token;
            _manualDisconnect = false;
            await DisconnectCoreAsync(clearCandidate: true);
            ActivePrinter = candidate;
            ConnectionState = PrinterConnectionState.Connecting;
            LastError = null;
            ClearLiveSnapshots();
            OnStateChanged();

            _transport = await _transportFactory.ConnectAsync(candidate, operationToken);
            _transport.ConnectionLost += Transport_ConnectionLost;
            ConnectionState = PrinterConnectionState.Reading;
            OnStateChanged();
            DeviceInformation = await _transport.ReadDeviceInformationAsync(operationToken);
            await RefreshCoreAsync(operationToken);
            ConnectionState = PrinterConnectionState.Ready;
            OnStateChanged();
        }
        catch (OperationCanceledException)
        {
            await DisconnectCoreAsync(clearCandidate: false);
            ConnectionState = PrinterConnectionState.Disconnected;
            ClearLiveSnapshots();
            OnStateChanged();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await DisconnectCoreAsync(clearCandidate: false);
            LastError = exception.Message;
            ConnectionState = PrinterConnectionState.Faulted;
            ClearLiveSnapshots();
            Media = InstalledMediaSnapshot.Faulted(exception.Message);
            OnStateChanged();
            throw;
        }
        finally
        {
            if (ReferenceEquals(_activeConnectionAttempt, attempt))
            {
                _activeConnectionAttempt = null;
            }
            attempt?.Dispose();
            _operationGate.Release();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_transport is null)
            {
                throw new InvalidOperationException("No printer is connected.");
            }

            ConnectionState = PrinterConnectionState.Reading;
            OnStateChanged();
            await RefreshCoreAsync(cancellationToken);
            ConnectionState = PrinterConnectionState.Ready;
            LastError = null;
            OnStateChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            Media = InstalledMediaSnapshot.Faulted(exception.Message);
            ConnectionState = PrinterConnectionState.Faulted;
            OnStateChanged();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<string> PrintAsync(
        LabelDocument document,
        byte density,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_transport is null || ActivePrinter is null)
            {
                throw new InvalidOperationException("No printer is connected; no print data was sent.");
            }

            if (ActivePrinter.Transport == PrinterTransport.BluetoothLe)
            {
                progress?.Report("Re-reading printer health and installed media…");
                ConnectionState = PrinterConnectionState.Reading;
                OnStateChanged();
                await RefreshCoreAsync(cancellationToken);
                ConnectionState = PrinterConnectionState.Ready;
                OnStateChanged();
                var readiness = PrintSafety.Evaluate(document, ActivePrinter, ConnectionState, Health, Media, DeviceInformation);
                var blocking = readiness.Checks.FirstOrDefault(check => check.Level == ReadinessLevel.Blocking);
                if (blocking is not null)
                {
                    throw new InvalidOperationException($"{blocking.Name}: {blocking.Message} No print data was sent.");
                }
            }

            var result = await _transport.PrintAsync(document, Media, density, progress, cancellationToken);
            if (ActivePrinter.Transport == PrinterTransport.BluetoothLe)
            {
                try
                {
                    await RefreshCoreAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LastError = exception.Message;
                    Media = InstalledMediaSnapshot.Faulted(exception.Message);
                    ConnectionState = PrinterConnectionState.Faulted;
                    OnStateChanged();
                    return result + " Post-print health/media refresh failed; reconnect before the next job.";
                }
            }
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var candidate = ActivePrinter;
        if (candidate is null || _manualDisconnect)
        {
            return;
        }

        foreach (var delay in new[] { 1, 2, 5 })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                await ConnectAsync(candidate, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // The final failed attempt leaves the session faulted with Retry available.
            }
        }
    }

    public async Task DisconnectAsync(bool forget = false)
    {
        ThrowIfDisposed();
        _manualDisconnect = true;
        _activeConnectionAttempt?.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        await _operationGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync(clearCandidate: forget);
            ConnectionState = PrinterConnectionState.Disconnected;
            OnStateChanged();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_transport is null || ActivePrinter is null)
        {
            throw new InvalidOperationException("No printer is connected.");
        }

        Media = InstalledMediaSnapshot.Reading;
        OnStateChanged();
        Health = await _transport.ReadHealthAsync(cancellationToken);
        var rawStatus = Health?.RawStatus;
        if (rawStatus?.LabelNotInstalled == true)
        {
            Media = InstalledMediaSnapshot.Absent("The printer reports no installed label media.");
        }
        else if (rawStatus?.LabelEnd == true)
        {
            Media = InstalledMediaSnapshot.Absent("The installed label roll is empty.");
        }
        else if (rawStatus?.LabelReadWriteError == true)
        {
            Media = InstalledMediaSnapshot.Faulted("The printer reported a label read/write error.");
        }
        else if (rawStatus?.LabelModeError == true)
        {
            Media = new InstalledMediaSnapshot(MediaReadState.Unsupported, null, null, DateTimeOffset.Now,
                "The printer reported a label mode mismatch.");
        }
        else
        {
            Media = await _transport.ReadMediaAsync(cancellationToken);
        }
        OnStateChanged();
    }

    private async Task DisconnectCoreAsync(bool clearCandidate)
    {
        if (_transport is not null)
        {
            _transport.ConnectionLost -= Transport_ConnectionLost;
            await _transport.DisposeAsync();
            _transport = null;
        }

        ClearLiveSnapshots();
        if (clearCandidate)
        {
            ActivePrinter = null;
        }
    }

    private async void Transport_ConnectionLost(object? sender, EventArgs e)
    {
        if (_disposed || _manualDisconnect)
        {
            return;
        }

        ConnectionState = PrinterConnectionState.Disconnected;
        ClearLiveSnapshots();
        OnStateChanged();
        await ReconnectAsync(_lifetime.Token);
    }

    private void ClearLiveSnapshots()
    {
        DeviceInformation = null;
        Health = null;
        Media = InstalledMediaSnapshot.Unknown;
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manualDisconnect = true;
        _lifetime.Cancel();
        await _operationGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync(clearCandidate: true);
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
