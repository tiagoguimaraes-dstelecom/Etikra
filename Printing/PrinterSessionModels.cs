using Etikra.Models;
using Etikra.Printing.Bluetooth;

namespace Etikra.Printing;

public enum PrinterTransport
{
    BluetoothLe,
    UsbHid
}

public enum PrinterConnectionState
{
    Disconnected,
    Scanning,
    Connecting,
    Reading,
    Ready,
    Faulted
}

public enum MediaReadState
{
    Unknown,
    Reading,
    Ready,
    Absent,
    Unsupported,
    Faulted
}

public sealed record PrinterCandidate(
    string Id,
    string DisplayName,
    PrinterTransport Transport,
    PrinterProfile? Profile,
    string? DevicePath = null,
    ulong? BluetoothAddress = null)
{
    public bool IsBluetooth => Transport == PrinterTransport.BluetoothLe;
    public bool IsSupported => Profile is not null &&
                               (IsBluetooth ? BluetoothAddress is not null : DevicePath is not null);
    public string TransportDescription => IsBluetooth ? "Bluetooth LE" : "USB HID";
}

public sealed record PrinterDeviceInformation(
    string DisplayName,
    string? ProtocolModel,
    string? ProtocolRevision,
    byte? FirmwareVersion,
    double? DotsPerMillimeter,
    int? PrintheadDots,
    ushort? AttMtu,
    string? CommandWriteMode,
    DateTimeOffset ReadAt)
{
    public double? Dpi => DotsPerMillimeter * 25.4;
    public double? PrintheadWidthMm => DotsPerMillimeter is > 0 && PrintheadDots is int dots
        ? dots / DotsPerMillimeter
        : null;
}

public sealed record PrinterHealthSnapshot(
    bool IsReady,
    bool IsBusy,
    bool IsPrinting,
    bool CoverOpen,
    bool LowBattery,
    bool PrintheadTooHot,
    bool LabelNotInstalled,
    IReadOnlyList<string> BlockingErrors,
    ushort? PrintCount,
    DateTimeOffset ReadAt,
    BlePrinterStatus? RawStatus = null)
{
    public static PrinterHealthSnapshot FromBle(BlePrinterStatus status)
    {
        var hardwareErrors = new List<string>();
        if (status.RibbonReadWriteError) hardwareErrors.Add("ribbon sensor read/write error");
        if (status.LowBattery) hardwareErrors.Add("low battery");
        if (status.PrintheadTooHot) hardwareErrors.Add("printhead temperature too high");
        if (status.CoverOpen) hardwareErrors.Add("cover open");
        return new PrinterHealthSnapshot(
            !status.DeviceBusy && !status.Printing && hardwareErrors.Count == 0,
            status.DeviceBusy,
            status.Printing,
            status.CoverOpen,
            status.LowBattery,
            status.PrintheadTooHot,
            status.LabelNotInstalled,
            hardwareErrors,
            status.PrintCount,
            DateTimeOffset.Now,
            status);
    }
}

public sealed record MediaFingerprint(
    string RfidUid,
    string RfidCode,
    ushort LabelSerial,
    byte RawType,
    byte WidthMm,
    byte DeviceHeightMm,
    byte GapMm)
{
    public static MediaFingerprint From(BleMaterialReport material) => new(
        material.RfidUid,
        material.RfidCode,
        material.LabelSerial,
        material.LabelType,
        material.WidthMm,
        material.HeightMm,
        material.GapMm);
}

public sealed record InstalledMediaSnapshot(
    MediaReadState State,
    BleMaterialReport? Material,
    MediaFingerprint? Fingerprint,
    DateTimeOffset? ReadAt,
    string? Error = null)
{
    public static InstalledMediaSnapshot Unknown { get; } = new(MediaReadState.Unknown, null, null, null);
    public static InstalledMediaSnapshot Reading { get; } = new(MediaReadState.Reading, null, null, null);
    public static InstalledMediaSnapshot Absent(string? error = null) => new(MediaReadState.Absent, null, null, DateTimeOffset.Now, error);
    public static InstalledMediaSnapshot Faulted(string error) => new(MediaReadState.Faulted, null, null, DateTimeOffset.Now, error);

    public static InstalledMediaSnapshot From(BleMaterialReport material)
    {
        var state = material.HasPlausibleGeometry && material.TryGetE12PrintMaterialCode(out _)
            ? MediaReadState.Ready
            : MediaReadState.Unsupported;
        return new InstalledMediaSnapshot(state, material, MediaFingerprint.From(material), DateTimeOffset.Now,
            state == MediaReadState.Unsupported ? $"Unsupported raw media type {material.LabelType}." : null);
    }
}

public static class MediaCompatibility
{
    public static LabelMediaRequirement ToRequirement(BleMaterialReport material) => new()
    {
        Kind = material.IsContinuous ? LabelMediaKind.Continuous : LabelMediaKind.Fixed,
        TapeWidthMm = material.WidthMm,
        FixedLengthMm = material.IsContinuous ? null : material.HeightMm,
        GapMm = material.IsContinuous ? null : material.GapMm
    };

    public static bool IsCompatible(LabelMediaRequirement? requirement, BleMaterialReport material)
    {
        if (requirement is null)
        {
            return false;
        }

        if ((requirement.Kind == LabelMediaKind.Continuous) != material.IsContinuous ||
            Math.Abs(requirement.TapeWidthMm - material.WidthMm) > 0.1)
        {
            return false;
        }

        return requirement.Kind == LabelMediaKind.Continuous ||
               (requirement.FixedLengthMm is double length && Math.Abs(length - material.HeightMm) <= 0.1 &&
                (requirement.GapMm is null || Math.Abs(requirement.GapMm.Value - material.GapMm) <= 0.1));
    }
}

public enum ReadinessLevel
{
    Ready,
    Warning,
    Blocking
}

public sealed record ReadinessCheck(string Name, ReadinessLevel Level, string Message);

public sealed record PrintReadiness(IReadOnlyList<ReadinessCheck> Checks)
{
    public bool CanPrint => Checks.All(check => check.Level != ReadinessLevel.Blocking);
}
