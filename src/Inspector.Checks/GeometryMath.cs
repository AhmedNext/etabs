
public static class GeometryMath
{
    public static double MetersPerUnit(string unit) => unit switch
    {
        "mm" => 0.001, "cm" => 0.01, "m" => 1, "in" or "inch" => 0.0254, "ft" => 0.3048,
        _ => throw new ConfigurationException($"Unsupported length unit '{unit}'. Use mm, cm, m, in or ft.")
    };
    public static double ConvertLength(LengthCriterion criterion, string targetUnit)
    {
        var result = criterion.Value * (MetersPerUnit(criterion.Unit) / MetersPerUnit(targetUnit));
        if (!double.IsFinite(result) || result <= 0) throw new ConfigurationException("Converted length criterion is not finite and positive.");
        return result;
    }
    public static double Distance(Coordinate a, Coordinate b)
    {
        var x = Math.Abs(a.X - b.X); var y = Math.Abs(a.Y - b.Y); var z = Math.Abs(a.Z - b.Z);
        var scale = Math.Max(x, Math.Max(y, z));
        var distance = scale == 0 ? 0 : scale * Math.Sqrt(Math.Pow(x / scale, 2) + Math.Pow(y / scale, 2) + Math.Pow(z / scale, 2));
        if (!double.IsFinite(distance)) throw new InvalidOperationException("Coordinate distance is outside the supported finite numeric range.");
        return distance;
    }
    public static double ModelTolerance(InspectionContext context)
    {
        if (context.Configuration.Settings.Confirmed && context.Configuration.Settings.Geometry?.CoincidentTolerance is { } configured)
            return ConvertLength(configured, context.Input.Model.Identity.DisplayUnits.Length);
        var points = context.Input.Model.Joints?.Items ?? [];
        if (points.Count == 0) return (context.Input.Model.Stories?.Stories.Select(s => Math.Abs(s.Elevation - context.Input.Model.Stories.BaseElevation)).DefaultIfEmpty(0).Max() ?? 0) * 1e-8;
        var min = new Coordinate(points.Min(p => p.Position.X), points.Min(p => p.Position.Y), points.Min(p => p.Position.Z));
        var max = new Coordinate(points.Max(p => p.Position.X), points.Max(p => p.Position.Y), points.Max(p => p.Position.Z));
        // Numerical screening tolerance, proportional to model extent; no code/design limit is implied.
        return Distance(min, max) * 1e-8;
    }
}


