public sealed record RebarEstimationRates(double LowKgPerM3 = 120, double BaseKgPerM3 = 135, double HighKgPerM3 = 150);
public sealed record RebarEstimationSettings
{
    public RebarEstimationRates DefaultRates { get; init; } = new();
    public IReadOnlyDictionary<string, RebarEstimationRates> ByObjectType { get; init; } = new Dictionary<string, RebarEstimationRates>();
}
public sealed record RebarEstimateRow(string ObjectType, string Material, double ConcreteVolumeM3,
    RebarEstimationRates Rates, double LowSteelKg, double BaseSteelKg, double HighSteelKg);
public sealed record RebarEstimate(IReadOnlyList<RebarEstimateRow> Items, IReadOnlyList<string> Limitations)
{
    public double? TotalConcreteM3 => Items.Count == 0 ? null : Items.Sum(x => x.ConcreteVolumeM3);
    public double? LowSteelKg => Items.Count == 0 ? null : Items.Sum(x => x.LowSteelKg);
    public double? BaseSteelKg => Items.Count == 0 ? null : Items.Sum(x => x.BaseSteelKg);
    public double? HighSteelKg => Items.Count == 0 ? null : Items.Sum(x => x.HighSteelKg);
}
