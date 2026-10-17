public static class SectionGeometry
{
    public static double GrossArea(FrameSection section)
    {
        var area = section switch
        {
            { Shape: "Rectangular", Width: > 0, Depth: > 0 } => section.Width.Value * section.Depth.Value,
            { Shape: "Circle", Diameter: > 0 } => Math.PI * section.Diameter.Value * section.Diameter.Value / 4,
            _ => double.NaN
        };
        return double.IsFinite(area) && area > 0 ? area : double.NaN;
    }
}
