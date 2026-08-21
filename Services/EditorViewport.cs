namespace Etikra.Services;

internal static class EditorViewport
{
    public const double MinimumZoom = 0.25;
    public const double MaximumZoom = 4;
    public const double ZoomStep = 0.1;

    public static double Clamp(double zoom) => Math.Clamp(zoom, MinimumZoom, MaximumZoom);

    public static double Step(double currentZoom, int direction) =>
        Clamp(Math.Round((currentZoom + Math.Sign(direction) * ZoomStep) * 10) / 10);

    public static double CalculateFitZoom(
        double viewportWidth,
        double viewportHeight,
        double documentWidth,
        double documentHeight,
        double horizontalPadding = 80,
        double verticalPadding = 80)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || documentWidth <= 0 || documentHeight <= 0)
        {
            return 1;
        }

        var availableWidth = Math.Max(1, viewportWidth - horizontalPadding);
        var availableHeight = Math.Max(1, viewportHeight - verticalPadding);
        return Clamp(Math.Min(availableWidth / documentWidth, availableHeight / documentHeight));
    }
}
