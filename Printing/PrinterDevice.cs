namespace Etikra.Printing;

public sealed record PrinterProfile(
    string Name,
    ushort ProductId,
    int Dpi,
    int PrintheadDots,
    string Family)
{
    public double PrintheadWidthMm => PrintheadDots * 25.4 / Dpi;
}

public sealed record PrinterDevice(
    string Id,
    string DisplayName,
    PrinterProfile? Profile,
    string? DevicePath,
    bool IsMock = false)
{
    public bool IsSupported => IsMock || Profile is not null;
    public string ConnectionDescription => IsMock ? "Safe file output" : "USB HID";
}

public static class PrinterProfiles
{
    public const ushort SupvanVendorId = 0x1820;

    private static readonly IReadOnlyDictionary<ushort, PrinterProfile> ByProductId =
        new Dictionary<ushort, PrinterProfile>
        {
            [0x2072] = new("T50M", 0x2072, 203, 384, "T50"),
            [0x2073] = new("T50M Pro", 0x2073, 203, 384, "T50"),
            [0x2074] = new("T50M Plus", 0x2074, 203, 384, "T50"),
            [0x2076] = new("T50s", 0x2076, 203, 384, "T50"),
            [0x2077] = new("T50s Pro", 0x2077, 203, 384, "T50"),
            [0x2075] = new("T80M", 0x2075, 201, 568, "T80"),
            [0x207A] = new("T80M Pro", 0x207A, 201, 568, "T80"),
            [0x2090] = new("G11", 0x2090, 193, 190, "G"),
            [0x2091] = new("G15", 0x2091, 193, 190, "G"),
            [0x2092] = new("G18", 0x2092, 193, 190, "G"),
            [0x2093] = new("G18 Pro", 0x2093, 193, 190, "G"),
            [0x202C] = new("TP76I", 0x202C, 305, 912, "TP76"),
            [0x2087] = new("TP76I Pro", 0x2087, 305, 912, "TP76"),
            [0x202E] = new("TP80A", 0x202E, 305, 960, "TP80"),
            [0x2080] = new("TP80A Pro", 0x2080, 305, 960, "TP80"),
            [0x202F] = new("TP86A", 0x202F, 305, 1032, "TP86"),
            [0x2081] = new("TP86A Pro", 0x2081, 305, 1032, "TP86"),
            [0x203E] = new("SP650", 0x203E, 203, 384, "SP650")
        };

    public static PrinterProfile? Find(ushort productId) =>
        ByProductId.TryGetValue(productId, out var profile) ? profile : null;
}
