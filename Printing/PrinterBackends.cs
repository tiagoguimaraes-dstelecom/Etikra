using Etikra.Models;
using Etikra.Printing.Bluetooth;
using Etikra.Services;
using System.IO;

namespace Etikra.Printing;

public interface IPrinterBackend
{
    Task<string> PrintAsync(LabelDocument document, byte density, IProgress<string>? progress, CancellationToken cancellationToken);
}

public static class PrinterBackendFactory
{
    public static IPrinterBackend Create(PrinterDevice device) => device switch
    {
        { IsMock: true } => new MockPrinterBackend(),
        { BluetoothAddress: not null, Profile: not null } => new SupvanBlePrinterBackend(device.BluetoothAddress.Value, device.Profile),
        { Profile: not null, DevicePath: not null } => new SupvanUsbPrinterBackend(device.DevicePath, device.Profile),
        _ => throw new NotSupportedException("This printer model is discovered but does not have a verified Etikra profile.")
    };
}

internal sealed class SupvanBlePrinterBackend(ulong address, PrinterProfile profile) : IPrinterBackend
{
    public async Task<string> PrintAsync(LabelDocument document, byte density, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Reading loaded Bluetooth media…");
        await using var protocol = await BleProtocol.ConnectAsync(address, cancellationToken);
        var information = await protocol.ReadInformationAsync(cancellationToken);
        var material = information.Material;
        if (!material.HasPlausibleGeometry)
        {
            throw new InvalidOperationException("The printer did not return coherent loaded-media geometry. No print data was sent.");
        }

        if (material.LabelType > 3)
        {
            throw new InvalidOperationException($"The printer returned unsupported material type {material.LabelType}; no print data was sent.");
        }

        if (information.DotsPerMillimeter is not double dotsPerMillimeter)
        {
            throw new InvalidOperationException("The printer did not return its resolution; no print data was sent.");
        }

        if (Math.Abs(document.HeightMm - material.WidthMm) > 0.1 ||
            (material.HeightMm > 0 && Math.Abs(document.WidthMm - material.HeightMm) > 0.1))
        {
            throw new InvalidOperationException(
                $"The design is {document.SizeDescription}, but the printer reports {material.GeometryDescription} " +
                $"({material.HeightMm} × {material.WidthMm} mm editor orientation). " +
                "Use the loaded-media size before printing. No print data was sent.");
        }

        var blockingErrors = information.Status.BlockingErrors(ignoreDirectThermalRibbonEnd: true);
        if (blockingErrors.Count > 0)
        {
            throw new InvalidOperationException("Printer error: " + string.Join(", ", blockingErrors));
        }

        var loadedWidthDots = (int)Math.Round(material.WidthMm * dotsPerMillimeter);
        if (loadedWidthDots != profile.PrintheadDots)
        {
            throw new InvalidOperationException(
                $"The loaded media and returned resolution imply {loadedWidthDots} dots across, but Etikra's verified E12 path is {profile.PrintheadDots} dots. No print data was sent.");
        }

        ValidatePrintableBounds(document, SupvanRasterEncoder.PageMarginDots / dotsPerMillimeter);

        var liveDpi = (int)Math.Round(dotsPerMillimeter * 25.4);
        var liveProfile = profile with { Dpi = liveDpi };
        progress?.Report($"Preparing {material.GeometryDescription} raster at {liveDpi} dpi…");
        var data = await Task.Run(
            () => SupvanRasterEncoder.Encode(
                document,
                liveProfile,
                density,
                material.LabelType,
                SupvanRasterOrientation.RotateCounterClockwise),
            cancellationToken);
        await protocol.PrintAsync(data, progress, cancellationToken);
        return $"Printed {data.WidthDots} × {data.HeightDots} dots over Bluetooth on {information.ProtocolDeviceName ?? information.BluetoothName}.";
    }

    private static void ValidatePrintableBounds(LabelDocument document, double marginMm)
    {
        var violations = document.Elements
            .Where(element =>
                element.XMm < marginMm ||
                element.YMm < marginMm ||
                element.XMm + element.WidthMm > document.WidthMm - marginMm ||
                element.YMm + element.HeightMm > document.HeightMm - marginMm)
            .Select(element => element.Kind.ToString())
            .ToArray();
        if (violations.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{violations.Length} element{(violations.Length == 1 ? string.Empty : "s")} " +
            $"({string.Join(", ", violations)}) cross the E12's {marginMm:0.#} mm print-safe boundary. " +
            "Move or resize them inside the dashed safe-area guide; no print data was sent.");
    }
}

internal sealed class MockPrinterBackend : IPrinterBackend
{
    public async Task<string> PrintAsync(LabelDocument document, byte density, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Rendering mock print…");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Etikra", "Mock Prints");
        Directory.CreateDirectory(folder);
        var safeName = string.Concat(document.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var path = Path.Combine(folder, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await Task.Run(() => LabelRenderer.SavePng(document, path, 203), cancellationToken);
        progress?.Report("Mock print saved.");
        return path;
    }
}

internal sealed class SupvanUsbPrinterBackend(string devicePath, PrinterProfile profile) : IPrinterBackend
{
    public async Task<string> PrintAsync(LabelDocument document, byte density, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("Preparing thermal raster…");
        var data = await Task.Run(() => SupvanRasterEncoder.Encode(document, profile, density), cancellationToken);
        await using var protocol = new SupvanUsbProtocol(devicePath);
        await protocol.PrintAsync(data, progress, cancellationToken);
        return $"Printed {data.WidthDots} × {data.HeightDots} dots on {profile.Name}.";
    }
}
