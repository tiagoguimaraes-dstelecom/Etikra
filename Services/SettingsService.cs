using System.IO;
using System.Text.Json;
using Etikra.Printing;

namespace Etikra.Services;

public sealed class EtikraSettings
{
    public int FormatVersion { get; set; } = 1;
    public RememberedPrinter? LastPrinter { get; set; }
    public byte Density { get; set; } = 7;
}

public sealed class RememberedPrinter
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public PrinterTransport Transport { get; set; }
    public ulong? BluetoothAddress { get; set; }
    public string? DevicePath { get; set; }
    public string? ProfileName { get; set; }
    public ushort? ProductId { get; set; }
    public DateTimeOffset LastConnectedUtc { get; set; }

    public static RememberedPrinter FromCandidate(PrinterCandidate candidate) => new()
    {
        Id = candidate.Id,
        DisplayName = candidate.DisplayName,
        Transport = candidate.Transport,
        BluetoothAddress = candidate.BluetoothAddress,
        DevicePath = candidate.DevicePath,
        ProfileName = candidate.Profile?.Name,
        ProductId = candidate.Profile?.ProductId,
        LastConnectedUtc = DateTimeOffset.UtcNow
    };

    public PrinterCandidate ToCandidate()
    {
        var profile = ProfileName == PrinterProfiles.E12.Name
            ? PrinterProfiles.E12
            : ProductId is ushort productId ? PrinterProfiles.Find(productId) : null;
        return new PrinterCandidate(Id, DisplayName, Transport, profile, DevicePath, BluetoothAddress);
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Etikra",
            "settings.json");
    }

    public async Task<EtikraSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new EtikraSettings();
            }

            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<EtikraSettings>(stream, JsonOptions, cancellationToken);
            return settings is { FormatVersion: <= 1 } ? settings : new EtikraSettings();
        }
        catch (JsonException)
        {
            return new EtikraSettings();
        }
        catch (IOException)
        {
            return new EtikraSettings();
        }
    }

    public async Task SaveAsync(EtikraSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("The Etikra settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
