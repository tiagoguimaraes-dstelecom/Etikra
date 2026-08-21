using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace Etikra.Printing.Bluetooth;

public sealed record BleAdvertisement(
    ulong Address,
    string AddressText,
    string Name,
    short Rssi,
    IReadOnlyList<Guid> ServiceUuids)
{
    public bool LooksLikeE12 =>
        Name.Contains("E12", StringComparison.OrdinalIgnoreCase) ||
        ServiceUuids.Contains(BleDiscovery.Fee7Service) ||
        ((Address & 0xFFFFFF000000UL) == 0xA49340000000UL && Regex.IsMatch(Name, "^[TGD]\\d{2}", RegexOptions.IgnoreCase));
}

public sealed record BleCharacteristicInfo(Guid Uuid, string Properties);
public sealed record BleServiceInfo(Guid Uuid, IReadOnlyList<BleCharacteristicInfo> Characteristics);
public sealed record BleProbeResult(
    ulong Address,
    string Name,
    string ConnectionStatus,
    IReadOnlyList<BleServiceInfo> Services)
{
    public bool HasKnownE12Path => Services.Any(service =>
        service.Uuid == BleDiscovery.Fee7Service &&
        service.Characteristics.Any(characteristic => characteristic.Uuid == BleDiscovery.Fec1Characteristic));
}

public static class BleDiscovery
{
    public static readonly Guid Fee7Service = Guid.Parse("0000fee7-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Fec1Characteristic = Guid.Parse("0000fec1-0000-1000-8000-00805f9b34fb");
    public static readonly Guid E0ffService = Guid.Parse("0000e0ff-3c17-d293-8e48-14fe2e4da212");
    public static readonly Guid Ffe1Characteristic = Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Ffe9Characteristic = Guid.Parse("0000ffe9-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Ff00Service = Guid.Parse("0000ff00-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Ff01Characteristic = Guid.Parse("0000ff01-0000-1000-8000-00805f9b34fb");
    public static readonly Guid Ff02Characteristic = Guid.Parse("0000ff02-0000-1000-8000-00805f9b34fb");

    public static async Task<IReadOnlyList<BleAdvertisement>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var seen = new ConcurrentDictionary<ulong, BleAdvertisement>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var incoming = new BleAdvertisement(
                args.BluetoothAddress,
                FormatAddress(args.BluetoothAddress),
                args.Advertisement.LocalName ?? string.Empty,
                args.RawSignalStrengthInDBm,
                args.Advertisement.ServiceUuids.ToArray());
            seen.AddOrUpdate(args.BluetoothAddress, incoming, (_, existing) => Merge(existing, incoming));
        }

        watcher.Received += OnReceived;
        try
        {
            watcher.Start();
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnReceived;
        }

        return seen.Values
            .OrderByDescending(device => device.LooksLikeE12)
            .ThenByDescending(device => device.Rssi)
            .ToArray();
    }

    public static async Task<BleProbeResult> ProbeAsync(ulong address, CancellationToken cancellationToken = default)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(cancellationToken)
            ?? throw new IOException($"Windows could not open BLE device {FormatAddress(address)}.");

        var serviceResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (serviceResult.Status != GattCommunicationStatus.Success)
        {
            throw new IOException($"GATT service discovery failed: {serviceResult.Status} (protocol error {serviceResult.ProtocolError?.ToString() ?? "none"}).");
        }

        var services = new List<BleServiceInfo>();
        foreach (var service in serviceResult.Services)
        {
            using (service)
            {
                var characteristicResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                var characteristics = characteristicResult.Status == GattCommunicationStatus.Success
                    ? characteristicResult.Characteristics
                        .Select(characteristic => new BleCharacteristicInfo(characteristic.Uuid, characteristic.CharacteristicProperties.ToString()))
                        .ToArray()
                    : [];
                services.Add(new BleServiceInfo(service.Uuid, characteristics));
            }
        }

        return new BleProbeResult(
            address,
            string.IsNullOrWhiteSpace(device.Name) ? FormatAddress(address) : device.Name,
            device.ConnectionStatus.ToString(),
            services);
    }

    public static bool TryParseAddress(string value, out ulong address)
    {
        var normalized = value.Replace(":", string.Empty).Replace("-", string.Empty).Trim();
        return ulong.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out address) && address <= 0xFFFFFFFFFFFF;
    }

    public static string FormatAddress(ulong address) => string.Join(":", Enumerable.Range(0, 6)
        .Select(index => ((address >> ((5 - index) * 8)) & 0xFF).ToString("X2")));

    private static BleAdvertisement Merge(BleAdvertisement existing, BleAdvertisement incoming)
    {
        var name = string.IsNullOrWhiteSpace(incoming.Name) ? existing.Name : incoming.Name;
        var services = existing.ServiceUuids.Concat(incoming.ServiceUuids).Distinct().ToArray();
        return incoming with { Name = name, ServiceUuids = services, Rssi = Math.Max(existing.Rssi, incoming.Rssi) };
    }
}
