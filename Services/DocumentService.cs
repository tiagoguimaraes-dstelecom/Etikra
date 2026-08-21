using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Collections.ObjectModel;
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
        document.FormatVersion = 2;
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, JsonOptions);
    }

    public static async Task<LabelDocument> LoadAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<LabelDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The label document is empty.");

        if (document.FormatVersion > 2)
        {
            throw new InvalidDataException($"This label uses the newer format version {document.FormatVersion}.");
        }

        document.Elements ??= [];
        if (document.MediaRequirement is { } requirement)
        {
            var valid = requirement.TapeWidthMm is >= 4 and <= 100 &&
                        (requirement.Kind == LabelMediaKind.Continuous ||
                         requirement.FixedLengthMm is >= 1 and <= 300) &&
                        requirement.GapMm is null or >= 0 and <= 30;
            if (!valid)
            {
                throw new InvalidDataException("The label contains an invalid media requirement.");
            }
        }
        return document;
    }

    public static LabelDocument CreateSnapshot(LabelDocument document) => new()
    {
        FormatVersion = document.FormatVersion,
        Name = document.Name,
        WidthMm = document.WidthMm,
        HeightMm = document.HeightMm,
        MediaRequirement = document.MediaRequirement is null ? null : new LabelMediaRequirement
        {
            Kind = document.MediaRequirement.Kind,
            TapeWidthMm = document.MediaRequirement.TapeWidthMm,
            FixedLengthMm = document.MediaRequirement.FixedLengthMm,
            GapMm = document.MediaRequirement.GapMm
        },
        Elements = new ObservableCollection<LabelElement>(document.Elements.Select(element => new LabelElement
        {
            Id = element.Id,
            Kind = element.Kind,
            XMm = element.XMm,
            YMm = element.YMm,
            WidthMm = element.WidthMm,
            HeightMm = element.HeightMm,
            Content = element.Content,
            FontFamily = element.FontFamily,
            FontSizePt = element.FontSizePt,
            Bold = element.Bold,
            Rotation = element.Rotation,
            StrokeThicknessMm = element.StrokeThicknessMm,
            ImageData = element.ImageData
        }))
    };

    internal static LabelDocument CreateSampleDocument()
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
