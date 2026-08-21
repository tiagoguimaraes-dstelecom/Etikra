using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using Etikra.Models;

namespace Etikra.Services;

public static class DocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task SaveAsync(LabelDocument document, string path)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
    }

    public static async Task<LabelDocument> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<LabelDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The label document is empty.");

        if (document.FormatVersion > 1)
        {
            throw new InvalidDataException($"This label uses the newer format version {document.FormatVersion}.");
        }

        document.Elements ??= [];
        return document;
    }

    public static LabelDocument CreateStarterDocument()
    {
        var document = new LabelDocument { Name = "Kitchen label", WidthMm = 40, HeightMm = 30 };
        document.Elements.Add(new LabelElement
        {
            Kind = LabelElementKind.Text,
            XMm = 3,
            YMm = 5,
            WidthMm = 34,
            HeightMm = 8,
            Content = "ORGANIC TEA",
            FontSizePt = 16,
            Bold = true
        });
        document.Elements.Add(new LabelElement
        {
            Kind = LabelElementKind.Line,
            XMm = 7,
            YMm = 14,
            WidthMm = 26,
            HeightMm = 0.5,
            StrokeThicknessMm = 0.3
        });
        document.Elements.Add(new LabelElement
        {
            Kind = LabelElementKind.Text,
            XMm = 3,
            YMm = 17,
            WidthMm = 34,
            HeightMm = 6,
            Content = "Jasmine · 2026",
            FontSizePt = 9
        });
        return document;
    }
}
