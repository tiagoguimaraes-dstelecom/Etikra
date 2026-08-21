using System.Text.Json;
using System.Text.Json.Serialization;
using Etikra.Models;

namespace Etikra.Services;

internal static class LabelElementClipboard
{
    public const string DataFormat = "Etikra.LabelElement.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(LabelElement element) => JsonSerializer.Serialize(element, JsonOptions);

    public static bool TryDeserialize(string? payload, out LabelElement? element)
    {
        try
        {
            element = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<LabelElement>(payload, JsonOptions);
            return element is not null;
        }
        catch (JsonException)
        {
            element = null;
            return false;
        }
    }

    public static LabelElement? CreatePastedElement(string? payload, LabelDocument document, double offsetMm = 1)
    {
        if (!TryDeserialize(payload, out var source) || source is null)
        {
            return null;
        }

        source.Id = Guid.NewGuid();
        source.WidthMm = Math.Min(source.WidthMm, document.WidthMm);
        source.HeightMm = Math.Min(source.HeightMm, document.HeightMm);
        source.XMm = Math.Clamp(source.XMm + offsetMm, 0, Math.Max(0, document.WidthMm - source.WidthMm));
        source.YMm = Math.Clamp(source.YMm + offsetMm, 0, Math.Max(0, document.HeightMm - source.HeightMm));
        return source;
    }
}
