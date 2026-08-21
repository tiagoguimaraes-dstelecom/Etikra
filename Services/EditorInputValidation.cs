using System.Globalization;

namespace Etikra.Services;

internal static class EditorInputValidation
{
    public static bool TryParseNumber(
        string text,
        double minimum,
        double maximum,
        out double value,
        out string? error,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var parsed = double.TryParse(text, NumberStyles.Float, culture, out value) ||
                     double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (!parsed || !double.IsFinite(value))
        {
            error = "Enter a valid number.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"Enter a value from {minimum:0.##} to {maximum:0.##}.";
            return false;
        }

        error = null;
        return true;
    }
}
