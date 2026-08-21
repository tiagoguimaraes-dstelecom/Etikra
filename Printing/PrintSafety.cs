using Etikra.Models;
using Etikra.Printing.Bluetooth;

namespace Etikra.Printing;

public static class PrintSafety
{
    public static (double HorizontalMm, double VerticalMm) GetE12Margins(
        LabelDocument document,
        PrinterProfile profile,
        double dotsPerMillimeter)
    {
        var feedMarginMm = SupvanRasterEncoder.PageMarginDots / dotsPerMillimeter;
        var printheadWidthMm = profile.PrintheadDots / dotsPerMillimeter;
        var tapeEdgeMarginMm = Math.Max(feedMarginMm, (document.HeightMm - printheadWidthMm) / 2);
        return (feedMarginMm, tapeEdgeMarginMm);
    }

    public static IReadOnlyList<LabelElement> FindBoundaryViolations(
        LabelDocument document,
        double horizontalMarginMm,
        double verticalMarginMm) => document.Elements
        .Where(element =>
            element.XMm < horizontalMarginMm ||
            element.YMm < verticalMarginMm ||
            element.XMm + element.WidthMm > document.WidthMm - horizontalMarginMm ||
            element.YMm + element.HeightMm > document.HeightMm - verticalMarginMm)
        .ToArray();

    public static void ValidateE12Document(
        LabelDocument document,
        BleMaterialReport material,
        PrinterProfile profile,
        double dotsPerMillimeter)
    {
        if (Math.Abs(document.HeightMm - material.WidthMm) > 0.1 ||
            (!material.IsContinuous && Math.Abs(document.WidthMm - material.HeightMm) > 0.1))
        {
            var expected = material.IsContinuous
                ? $"any length × {material.WidthMm} mm"
                : $"{material.HeightMm} × {material.WidthMm} mm";
            throw new InvalidOperationException(
                $"The design is {document.SizeDescription}, but installed media requires {expected}. No print data was sent.");
        }

        if (!MediaCompatibility.IsCompatible(document.MediaRequirement, material))
        {
            throw new InvalidOperationException(
                document.MediaRequirement is null
                    ? "This label is not bound to installed media. Bind it before printing; no print data was sent."
                    : "The installed media changed and is not compatible with this label; no print data was sent.");
        }

        var loadedWidthDots = (int)Math.Round(material.WidthMm * dotsPerMillimeter);
        if (loadedWidthDots < profile.PrintheadDots)
        {
            throw new InvalidOperationException(
                $"The media provides only {loadedWidthDots} dots across, but the E12 printhead needs {profile.PrintheadDots}. No print data was sent.");
        }

        var margins = GetE12Margins(document, profile, dotsPerMillimeter);
        var violations = FindBoundaryViolations(document, margins.HorizontalMm, margins.VerticalMm);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"{violations.Count} element{(violations.Count == 1 ? string.Empty : "s")} cross the E12 print-safe boundary " +
                $"({margins.HorizontalMm:0.#} mm feed ends, {margins.VerticalMm:0.#} mm tape edges). No print data was sent.");
        }
    }

    public static PrintReadiness Evaluate(
        LabelDocument? document,
        PrinterCandidate? candidate,
        PrinterConnectionState connectionState,
        PrinterHealthSnapshot? health,
        InstalledMediaSnapshot media,
        PrinterDeviceInformation? deviceInformation)
    {
        var checks = new List<ReadinessCheck>();
        checks.Add(document is null
            ? new("Label", ReadinessLevel.Blocking, "Create or open a label.")
            : new("Label", ReadinessLevel.Ready, document.SizeDescription));

        if (candidate is null)
        {
            checks.Add(new("Printer", ReadinessLevel.Blocking, "Connect a label maker."));
            return new PrintReadiness(checks);
        }

        if (candidate.Transport == PrinterTransport.UsbHid)
        {
            checks.Add(connectionState != PrinterConnectionState.Ready
                ? new("Printer", ReadinessLevel.Blocking, connectionState.ToString())
                : candidate.IsSupported
                ? new("Printer", ReadinessLevel.Ready, $"{candidate.DisplayName} · USB HID")
                : new("Printer", ReadinessLevel.Blocking, "This USB model has no verified raster profile."));
            checks.Add(new("Health", ReadinessLevel.Warning, "Live USB health is unavailable."));
            checks.Add(new("Media", ReadinessLevel.Warning, "Installed USB media is not verified; dimensions are manual."));
            return new PrintReadiness(checks);
        }

        checks.Add(connectionState == PrinterConnectionState.Ready
            ? new("Printer", ReadinessLevel.Ready, candidate.DisplayName)
            : new("Printer", ReadinessLevel.Blocking, connectionState.ToString()));

        if (health is null)
        {
            checks.Add(new("Health", ReadinessLevel.Blocking, "Printer health has not been read."));
        }
        else if (health.BlockingErrors.Count > 0 || !health.IsReady)
        {
            var message = health.BlockingErrors.Count > 0
                ? string.Join(", ", health.BlockingErrors)
                : health.IsPrinting ? "Printing" : health.IsBusy ? "Busy" : "Not ready";
            checks.Add(new("Health", ReadinessLevel.Blocking, message));
        }
        else
        {
            checks.Add(new("Health", ReadinessLevel.Ready, "Ready"));
        }

        if (media.State != MediaReadState.Ready || media.Material is null)
        {
            checks.Add(new("Media", ReadinessLevel.Blocking, media.State switch
            {
                MediaReadState.Reading => "Reading installed media…",
                MediaReadState.Absent => "No media installed.",
                MediaReadState.Unsupported => media.Error ?? "Unsupported media.",
                MediaReadState.Faulted => media.Error ?? "Media read failed.",
                _ => "Installed media has not been read."
            }));
        }
        else if (document?.MediaRequirement is null)
        {
            checks.Add(new("Media", ReadinessLevel.Blocking, "Bind this label to the installed media."));
        }
        else if (!MediaCompatibility.IsCompatible(document.MediaRequirement, media.Material))
        {
            checks.Add(new("Media", ReadinessLevel.Blocking, "Installed media differs from this label."));
        }
        else
        {
            checks.Add(new("Media", ReadinessLevel.Ready, media.Material.GeometryDescription));
        }

        if (document is not null && media.Material is { } printableMaterial &&
            MediaCompatibility.IsCompatible(document.MediaRequirement, printableMaterial) &&
            deviceInformation?.DotsPerMillimeter is double dpmm && candidate.Profile is { } profile)
        {
            try
            {
                ValidateE12Document(document, printableMaterial, profile, dpmm);
                checks.Add(new("Artwork", ReadinessLevel.Ready, "Inside printable area"));
            }
            catch (InvalidOperationException exception)
            {
                checks.Add(new("Artwork", ReadinessLevel.Blocking, exception.Message.Replace(" No print data was sent.", string.Empty)));
            }
        }

        return new PrintReadiness(checks);
    }
}
