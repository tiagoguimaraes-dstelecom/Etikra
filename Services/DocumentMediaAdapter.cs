using Etikra.Models;
using Etikra.Printing;
using Etikra.Printing.Bluetooth;

namespace Etikra.Services;

public static class DocumentMediaAdapter
{
    public static bool CanAutoAdapt(LabelDocument document, bool isPristine, BleMaterialReport material) =>
        isPristine &&
        document.Elements.Count == 0 &&
        (document.MediaRequirement is null || !MediaCompatibility.IsCompatible(document.MediaRequirement, material));

    public static void ResizeAndBind(LabelDocument document, BleMaterialReport material, bool preserveContinuousLength)
    {
        if (!material.IsContinuous || !preserveContinuousLength)
        {
            document.WidthMm = material.HeightMm;
        }
        document.HeightMm = material.WidthMm;
        document.MediaRequirement = MediaCompatibility.ToRequirement(material);

        foreach (var element in document.Elements)
        {
            element.WidthMm = Math.Min(element.WidthMm, document.WidthMm);
            element.HeightMm = Math.Min(element.HeightMm, document.HeightMm);
            element.XMm = Math.Min(element.XMm, Math.Max(0, document.WidthMm - element.WidthMm));
            element.YMm = Math.Min(element.YMm, Math.Max(0, document.HeightMm - element.HeightMm));
        }
    }
}
