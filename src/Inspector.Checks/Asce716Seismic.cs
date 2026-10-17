using System.Text.Json;

/// <summary>ASCE 7-16 seismic equations used by the Phase 3 screening checks.</summary>
internal static class Asce716Seismic
{
    internal sealed record Hazard(double Fa, double Fv, double Sds, double Sd1);
    internal sealed record Period(double HeightMeters, double Ct, double X, double Ta, double Cu, double UpperPeriod, double Sd1);

    public static Hazard CalculateHazard(string siteClass, double ss, double s1)
    {
        if (!double.IsFinite(ss) || ss < 0 || !double.IsFinite(s1) || s1 < 0)
            throw new InvalidOperationException("Ss and S1 must be finite, nonnegative values.");
        var site = siteClass.Trim().ToUpperInvariant();
        // ASCE 7-16 Tables 11.4-1 and 11.4-2. The Site D/E high-S1 and Site E high-Ss
        // branches require Section 11.4.8 handling; do not silently use table values there.
        if (site is "D" or "E" && s1 >= .2 || site == "E" && ss >= .75 || site == "F")
            throw new InvalidOperationException("Site Class/ground-motion values require the ASCE 7-16 Section 11.4.8 site-specific procedure or a documented exception.");

        var fa = site switch
        {
            "A" => .8,
            "B" => .9,
            "C" => Interpolate(ss, [.25, .5, .75, 1.0, 1.25, 1.5], [1.3, 1.3, 1.2, 1.2, 1.2, 1.2]),
            "D" => Interpolate(ss, [.25, .5, .75, 1.0, 1.25, 1.5], [1.6, 1.4, 1.2, 1.1, 1.0, 1.0]),
            "E" => Interpolate(ss, [.25, .5, .75], [2.4, 1.7, 1.3]),
            _ => throw new InvalidOperationException($"Unsupported ASCE 7-16 site class '{siteClass}'.")
        };
        var fv = site switch
        {
            "A" or "B" => .8,
            "C" => Interpolate(s1, [.1, .2, .3, .4, .5, .6], [1.5, 1.5, 1.5, 1.5, 1.5, 1.4]),
            "D" => Interpolate(s1, [.1, .2], [2.4, 2.2]),
            "E" => s1 <= .1 ? 4.2 : throw new InvalidOperationException("Site Class E above S1=0.1 requires a documented site-specific procedure or exception."),
            _ => throw new InvalidOperationException($"Unsupported ASCE 7-16 site class '{siteClass}'.")
        };
        return new(fa, fv, 2.0 / 3.0 * fa * ss, 2.0 / 3.0 * fv * s1);
    }

    public static Period CalculatePeriod(string system, double hnMeters, double sd1)
    {
        if (!double.IsFinite(hnMeters) || hnMeters <= 0 || !double.IsFinite(sd1) || sd1 < 0)
            throw new InvalidOperationException("Building height must be positive and SD1 must be finite and nonnegative.");
        var value = system.ToLowerInvariant();
        var (ct, x) = value.Contains("eccentric") && value.Contains("brace") ? (.0731, .75)
            : value.Contains("steel") && value.Contains("moment") ? (.0724, .8)
            : value.Contains("concrete") && value.Contains("moment") ? (.0466, .9)
            : (.0488, .75);
        // Table 12.8-1 is indexed by SD1. Linear interpolation applies between tabulated values.
        var cu = Interpolate(sd1, [.1, .15, .2, .3, .4], [1.7, 1.6, 1.5, 1.4, 1.4]);
        var ta = ct * Math.Pow(hnMeters, x);
        return new(hnMeters, ct, x, ta, cu, cu * ta, sd1);
    }

    public static double ResponseCoefficient(double sds, double sd1, double period, double r, double ie, double s1)
    {
        if (new[] { sds, sd1, period, r, ie, s1 }.Any(x => !double.IsFinite(x)) || sds < 0 || sd1 < 0 || period <= 0 || r <= 0 || ie <= 0)
            throw new InvalidOperationException("ASCE 7-16 base-shear inputs are missing, nonfinite, or outside their valid range.");
        var ratio = r / ie;
        var upper = sd1 / (period * ratio);
        var lower = Math.Max(.044 * sds * ie, .01);
        if (s1 >= .6) lower = Math.Max(lower, .5 * s1 / ratio);
        return Math.Max(lower, Math.Min(sds / ratio, upper));
    }

    public static double ResponseCoefficient(double sds, double sd1, double period, double r, double ie, double s1, double tl)
    {
        if (!double.IsFinite(tl) || tl <= 0)
            throw new InvalidOperationException("A positive long-period transition period TL is required.");
        var shortPeriodResult = ResponseCoefficient(sds, sd1, period, r, ie, s1);
        if (period <= tl) return shortPeriodResult;
        var lower = Math.Max(.044 * sds * ie, .01);
        if (s1 >= .6) lower = Math.Max(lower, .5 * s1 / (r / ie));
        return Math.Max(lower, Math.Min(sds / (r / ie), sd1 * tl / (period * period * (r / ie))));
    }

    public static string RequiredString(JsonElement settings, string name) =>
        TryProperty(settings, name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidOperationException($"Missing seismic.{name}.");

    public static double RequiredNumber(JsonElement settings, string name) =>
        TryProperty(settings, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : throw new InvalidOperationException($"Missing or invalid seismic.{name}.");

    public static double Factor(InspectionSettings settings, string name)
    {
        var configured = name.Equals("cd", StringComparison.OrdinalIgnoreCase) ? settings.SeismicVerification?.Cd : settings.SeismicVerification?.Ie;
        if (configured is > 0 && double.IsFinite(configured.Value)) return configured.Value;
        var value = RequiredNumber(settings.Seismic ?? throw new InvalidOperationException("Seismic settings unavailable."), name);
        return value > 0 ? value : throw new InvalidOperationException($"Missing or invalid seismic.{name}.");
    }

    private static bool TryProperty(JsonElement settings, string name, out JsonElement value)
    {
        value = default;
        if (settings.ValueKind != JsonValueKind.Object) return false;
        var matches = settings.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) return false;
        value = matches[0].Value;
        return true;
    }

    private static double Interpolate(double value, double[] x, double[] y)
    {
        if (value <= x[0]) return y[0];
        if (value >= x[^1]) return y[^1];
        var i = Array.FindIndex(x, v => v >= value);
        var fraction = (value - x[i - 1]) / (x[i] - x[i - 1]);
        return y[i - 1] + fraction * (y[i] - y[i - 1]);
    }
}
