public static class RebarEstimator
{
    public static RebarEstimate Estimate(InspectionInput input, RebarEstimationSettings settings)
    {
        List<string> limitations = ["Preliminary rebar mass estimate = modeled concrete volume × assumed kg/m³. Rates are estimating assumptions, not code requirements or provided reinforcement. Laps, anchorage and waste inclusions must be reflected in the configured rates; no additional allowance is added."];
        var takeoffs = input.MaterialTakeoffs;
        if (takeoffs is null || takeoffs.Items.Count == 0)
            return new([], limitations.Append("Material takeoff is unavailable; rebar mass cannot be estimated.").ToArray());
        double scale;
        try { scale = Math.Pow(GeometryMath.MetersPerUnit(input.Model.Identity.DisplayUnits.Length), 3); }
        catch (ConfigurationException ex) { return new([], limitations.Append(ex.Message).ToArray()); }
        var materials = input.Model.Materials?.Items.GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        List<RebarEstimateRow> rows = [];
        foreach (var item in takeoffs.Items)
        {
            if (materials is null || !materials.TryGetValue(item.Material, out var matches) || matches.Length != 1)
            { limitations.Add($"{item.ObjectType}/{item.Material}: material classification unavailable; quantity excluded."); continue; }
            if (!matches[0].Type.Equals("Concrete", StringComparison.OrdinalIgnoreCase)) continue;
            var volume = item.TotalVolume * scale;
            var rates = settings.ByObjectType.TryGetValue(item.ObjectType, out var selected) ? selected : settings.DefaultRates;
            var low = volume * rates.LowKgPerM3; var basis = volume * rates.BaseKgPerM3; var high = volume * rates.HighKgPerM3;
            if (new[] {volume, low, basis, high}.Any(v => !double.IsFinite(v) || v < 0))
            { limitations.Add($"{item.ObjectType}/{item.Material}: invalid or overflowing quantity excluded."); continue; }
            rows.Add(new(item.ObjectType, item.Material, volume, rates, low, basis, high));
        }
        limitations.AddRange(takeoffs.Limitations);
        if (rows.Count == 0) limitations.Add("No usable concrete quantities are available; no rebar total is reported.");
        return new(rows, limitations);
    }
}
