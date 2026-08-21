using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Etikra.Models;

public enum LabelElementKind
{
    Text,
    Barcode,
    Rectangle,
    Line,
    Image
}

public enum LabelMediaKind
{
    Fixed,
    Continuous
}

public sealed class LabelMediaRequirement
{
    public LabelMediaKind Kind { get; set; }
    public double TapeWidthMm { get; set; }
    public double? FixedLengthMm { get; set; }
    public double? GapMm { get; set; }
}

public sealed class LabelDocument : INotifyPropertyChanged
{
    private string _name = "Untitled label";
    private double _widthMm = 40;
    private double _heightMm = 30;

    public int FormatVersion { get; set; } = 2;

    public LabelMediaRequirement? MediaRequirement { get; set; }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public double WidthMm
    {
        get => _widthMm;
        set => SetField(ref _widthMm, Math.Clamp(value, 8, 100));
    }

    public double HeightMm
    {
        get => _heightMm;
        set => SetField(ref _heightMm, Math.Clamp(value, 8, 300));
    }

    public ObservableCollection<LabelElement> Elements { get; set; } = [];

    [JsonIgnore]
    public string SizeDescription => $"{WidthMm:0.#} × {HeightMm:0.#} mm";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(WidthMm) or nameof(HeightMm))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeDescription)));
        }
    }
}

public sealed class LabelElement : INotifyPropertyChanged
{
    private double _xMm = 3;
    private double _yMm = 3;
    private double _widthMm = 24;
    private double _heightMm = 8;
    private string _content = "Label text";
    private string _fontFamily = "Segoe UI";
    private double _fontSizePt = 12;
    private double _rotation;
    private double _strokeThicknessMm = 0.35;
    private bool _bold;

    public Guid Id { get; set; } = Guid.NewGuid();
    public LabelElementKind Kind { get; set; }

    public double XMm { get => _xMm; set => SetField(ref _xMm, Math.Max(0, value)); }
    public double YMm { get => _yMm; set => SetField(ref _yMm, Math.Max(0, value)); }
    public double WidthMm { get => _widthMm; set => SetField(ref _widthMm, Math.Max(0.5, value)); }
    public double HeightMm { get => _heightMm; set => SetField(ref _heightMm, Math.Max(0.5, value)); }
    public string Content { get => _content; set => SetField(ref _content, value ?? string.Empty); }
    public string FontFamily { get => _fontFamily; set => SetField(ref _fontFamily, value ?? "Segoe UI"); }
    public double FontSizePt { get => _fontSizePt; set => SetField(ref _fontSizePt, Math.Clamp(value, 4, 96)); }
    public bool Bold { get => _bold; set => SetField(ref _bold, value); }
    public double Rotation { get => _rotation; set => SetField(ref _rotation, value % 360); }
    public double StrokeThicknessMm { get => _strokeThicknessMm; set => SetField(ref _strokeThicknessMm, Math.Clamp(value, 0.1, 5)); }

    /// <summary>A PNG or JPEG encoded as a base64 string. Only used by image elements.</summary>
    public string? ImageData { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LabelElement Clone()
    {
        return new LabelElement
        {
            Kind = Kind,
            XMm = XMm + 1,
            YMm = YMm + 1,
            WidthMm = WidthMm,
            HeightMm = HeightMm,
            Content = Content,
            FontFamily = FontFamily,
            FontSizePt = FontSizePt,
            Bold = Bold,
            Rotation = Rotation,
            StrokeThicknessMm = StrokeThicknessMm,
            ImageData = ImageData
        };
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
