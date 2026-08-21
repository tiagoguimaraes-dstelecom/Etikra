using Etikra.Models;
using Etikra.Services;
using System.IO;

namespace Etikra.Printing;

public interface IPrinterBackend
{
    Task<string> PrintAsync(LabelDocument document, byte density, IProgress<string>? progress, CancellationToken cancellationToken);
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
