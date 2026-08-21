using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Etikra.Models;

namespace Etikra.Services;

public static class LabelRenderer
{
    public static RenderTargetBitmap Render(LabelDocument document, int dpi)
    {
        var scale = dpi / 25.4;
        var pixelWidth = Math.Max(1, (int)Math.Round(document.WidthMm * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(document.HeightMm * scale));
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, pixelWidth, pixelHeight));
            foreach (var element in document.Elements)
            {
                DrawElement(context, element, scale);
            }
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static byte[] ToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource RenderMonochromePreview(LabelDocument document, int dpi)
    {
        var rendered = Render(document, dpi);
        var rows = ToOneBitRows(rendered);
        var stride = rendered.PixelWidth;
        var pixels = new byte[stride * rendered.PixelHeight];
        var inputStride = (rendered.PixelWidth + 7) / 8;
        for (var y = 0; y < rendered.PixelHeight; y++)
        {
            for (var x = 0; x < rendered.PixelWidth; x++)
            {
                var black = (rows[y * inputStride + x / 8] & (1 << (7 - x % 8))) != 0;
                pixels[y * stride + x] = black ? (byte)0 : (byte)255;
            }
        }

        var preview = BitmapSource.Create(
            rendered.PixelWidth,
            rendered.PixelHeight,
            96,
            96,
            PixelFormats.Gray8,
            null,
            pixels,
            stride);
        preview.Freeze();
        return preview;
    }

    public static void SavePng(LabelDocument document, string path, int dpi)
    {
        File.WriteAllBytes(path, ToPng(Render(document, dpi)));
    }

    /// <summary>Returns MSB-first row-major monochrome pixels (one means a heated/black dot).</summary>
    public static byte[] ToOneBitRows(BitmapSource source, byte threshold = 160)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var outputStride = (converted.PixelWidth + 7) / 8;
        var output = new byte[outputStride * converted.PixelHeight];
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var offset = y * stride + x * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                var alpha = pixels[offset + 3];
                var luminance = (r * 54 + g * 183 + b * 19) >> 8;
                if (alpha > 32 && luminance < threshold)
                {
                    output[y * outputStride + x / 8] |= (byte)(1 << (7 - (x % 8)));
                }
            }
        }

        return output;
    }

    private static void DrawElement(DrawingContext context, LabelElement element, double scale)
    {
        var rect = new Rect(
            element.XMm * scale,
            element.YMm * scale,
            Math.Max(1, element.WidthMm * scale),
            Math.Max(1, element.HeightMm * scale));

        context.PushTransform(new RotateTransform(element.Rotation, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        switch (element.Kind)
        {
            case LabelElementKind.Text:
                DrawText(context, element, rect, scale);
                break;
            case LabelElementKind.Barcode:
                DrawBarcode(context, element.Content, rect);
                break;
            case LabelElementKind.Rectangle:
                var rectangleStroke = Math.Max(1, element.StrokeThicknessMm * scale);
                var rectangleBounds = new Rect(
                    rect.X + rectangleStroke / 2,
                    rect.Y + rectangleStroke / 2,
                    Math.Max(0, rect.Width - rectangleStroke),
                    Math.Max(0, rect.Height - rectangleStroke));
                context.DrawRectangle(null, new Pen(Brushes.Black, rectangleStroke), rectangleBounds);
                break;
            case LabelElementKind.Line:
                context.DrawLine(
                    new Pen(Brushes.Black, Math.Max(1, element.StrokeThicknessMm * scale)),
                    new Point(rect.Left, rect.Top + rect.Height / 2),
                    new Point(rect.Right, rect.Top + rect.Height / 2));
                break;
            case LabelElementKind.Image:
                if (TryLoadImage(element.ImageData, out var image))
                {
                    DrawContainedImage(context, image, rect);
                }
                break;
        }

        context.Pop();
    }

    private static void DrawText(DrawingContext context, LabelElement element, Rect rect, double scale)
    {
        var typeface = new Typeface(
            new FontFamily(element.FontFamily),
            FontStyles.Normal,
            element.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        var formatted = new FormattedText(
            element.Content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            element.FontSizePt * scale / 2.834645669, // points to mm, then output pixels
            Brushes.Black,
            1)
        {
            MaxTextWidth = rect.Width,
            MaxTextHeight = rect.Height,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var y = rect.Top + Math.Max(0, (rect.Height - formatted.Height) / 2);
        context.DrawText(formatted, new Point(rect.Left, y));
    }

    private static void DrawBarcode(DrawingContext context, string content, Rect rect)
    {
        var runs = Code128Encoder.GetRuns(content);
        var totalModules = runs.Sum(run => run.Modules);
        var moduleWidth = rect.Width / totalModules;
        var x = rect.Left;
        foreach (var run in runs)
        {
            var width = run.Modules * moduleWidth;
            if (run.IsBar)
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(x, rect.Top, Math.Max(0.5, width), rect.Height));
            }

            x += width;
        }
    }

    private static bool TryLoadImage(string? imageData, out BitmapImage image)
    {
        image = new BitmapImage();
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(imageData);
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawContainedImage(DrawingContext context, BitmapSource image, Rect bounds)
    {
        var ratio = Math.Min(bounds.Width / image.PixelWidth, bounds.Height / image.PixelHeight);
        var width = image.PixelWidth * ratio;
        var height = image.PixelHeight * ratio;
        var rect = new Rect(
            bounds.Left + (bounds.Width - width) / 2,
            bounds.Top + (bounds.Height - height) / 2,
            width,
            height);
        context.DrawImage(image, rect);
    }
}
