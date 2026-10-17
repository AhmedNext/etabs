using System.Globalization;

/// <summary>Pure mechanics and explicit display-rounding bounds; no ETABS calls or code limits.</summary>
public static class EngineeringCalculations
{
    public readonly record struct Number(double Value, double Error = 0)
    {
        public static Number operator +(Number a, Number b) => new(a.Value + b.Value, a.Error + b.Error);
        public static Number operator -(Number a, Number b) => new(a.Value - b.Value, a.Error + b.Error);
        public static Number operator *(Number a, double b) => new(a.Value * b, a.Error * Math.Abs(b));
        public bool Agrees(Number other) => Math.Abs(Value - other.Value) <= Error + other.Error + 1e-8 * Math.Max(1, Math.Max(Math.Abs(Value), Math.Abs(other.Value)));
    }
    public static Number Displayed(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
            throw new InvalidOperationException("Missing input: a finite numeric table value is required.");
        var parts = value.Trim().ToLowerInvariant().Split('e');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = parts[0].IndexOf('.'); var decimals = dot < 0 ? 0 : parts[0].Length - dot - 1;
        return new(result, .5 * Math.Pow(10, exponent - decimals));
    }
    public static double Gravity(string lengthUnit) => 9.80665 / GeometryMath.MetersPerUnit(lengthUnit);
    public static double Drift(double top, double bottom, double height)
    {
        Require(top, bottom, height); if (height <= 0) throw new InvalidOperationException("Missing input: positive story height.");
        return (top - bottom) / height;
    }
    public static double StabilityTheta(double gravityForce, double storyDrift, double storyShear, double storyHeight)
    {
        Require(gravityForce, storyDrift, storyShear, storyHeight);
        if (gravityForce < 0 || storyHeight <= 0 || storyShear == 0) throw new InvalidOperationException("Missing input: nonnegative gravity force, positive height, and nonzero story shear.");
        return gravityForce * Math.Abs(storyDrift) / (Math.Abs(storyShear) * storyHeight);
    }
    public static double[] EqualStoryShearBuildingPeriods(int stories, double stiffness, double mass)
    {
        Require(stiffness, mass); if (stories < 1 || stiffness <= 0 || mass <= 0) throw new InvalidOperationException("Missing input: positive story count, stiffness, and mass.");
        return Enumerable.Range(1, stories).Select(i => 2 * Math.PI / Math.Sqrt(2 * stiffness / mass * (1 - Math.Cos((2 * i - 1) * Math.PI / (2 * stories + 1))))).ToArray();
    }
    public static (double PinReaction, double FixedReaction, double FixedMoment) ProppedCantileverUniform(double loadPerLength, double length)
    {
        Require(loadPerLength, length); if (length <= 0) throw new InvalidOperationException("Missing input: positive beam length.");
        return (3 * loadPerLength * length / 8, 5 * loadPerLength * length / 8, -loadPerLength * length * length / 8);
    }
    public static double PolygonArea(IReadOnlyList<Coordinate> points)
    {
        if (points.Count < 3) throw new InvalidOperationException("Missing input: an area boundary with at least three points.");
        var origin = points[0]; double x = 0, y = 0, z = 0;
        for (int i = 1; i + 1 < points.Count; i++)
        {
            var a = points[i]; var b = points[i + 1];
            x += (a.Y-origin.Y)*(b.Z-origin.Z)-(a.Z-origin.Z)*(b.Y-origin.Y);
            y += (a.Z-origin.Z)*(b.X-origin.X)-(a.X-origin.X)*(b.Z-origin.Z);
            z += (a.X-origin.X)*(b.Y-origin.Y)-(a.Y-origin.Y)*(b.X-origin.X);
        }
        var area = .5 * Math.Sqrt(x*x+y*y+z*z); Require(area);
        if (area <= 0) throw new InvalidOperationException("Missing input: a nonzero planar area.");
        // Reject warped polygons: a fan triangulation is not a verified shell-load area.
        foreach (var p in points)
            if (Math.Abs(x*(p.X-origin.X)+y*(p.Y-origin.Y)+z*(p.Z-origin.Z)) > 1e-8 * 2 * area * Math.Max(1, points.Max(q => GeometryMath.Distance(q, origin))))
                throw new InvalidOperationException("Missing input: a planar loaded area boundary.");
        return area;
    }
    private static void Require(params double[] values)
    { if (values.Any(x => !double.IsFinite(x))) throw new InvalidOperationException("Missing input: finite engineering values."); }
}
