public static class OptimizationChecks
{
    private const double Threshold = .35;
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("OPT-001", "Column over-design screening", "Envelope valid positive PMM ratios over all extracted column stations/combinations; maximum<0.35 flags an optimization review. Design mode instead screens maximum required As/Ag<0.012 using matched section geometry; missing or invalid inputs remain NOT_VERIFIED. No automatic section reduction or savings estimate.", [], Columns),
        Rule.Create("OPT-002", "Shear wall over-design screening", "Envelope positive raw demand/capacity ratios, or use the required vertical reinforcement percent as a Design Mode minimum-steel screen. This is a candidate review, not authorization to reduce a wall section.", [], Piers)
    ];

    private static IReadOnlyList<Observation> Columns(InspectionContext c)
    {
        var data = c.Input.ConcreteColumns;
        if (data is null || data.Items.Count == 0)
            return [Rule.NotVerified("Column design ratios are unavailable for value engineering. " + string.Join("; ", data?.Limitations ?? []))];
        List<Observation> output = [];
        foreach (var group in data.Items.GroupBy(x => x.FrameId, StringComparer.Ordinal))
        {
            var rows = group.ToArray();
            if (rows.All(x => x.PmmRatio is null && x.DesignOption is 0 or 2 && x.LongitudinalRebarArea is > 0 && double.IsFinite(x.LongitudinalRebarArea.Value) && string.IsNullOrWhiteSpace(x.ErrorSummary)))
            {
                var ratios = rows.Select(row =>
                {
                    var sections = c.Input.Model.FrameSections?.Items.Where(x => x.Name == row.DesignSection).ToArray() ?? [];
                    if (sections.Length != 1) return double.NaN;
                    var section = sections[0];
                    var ag = SectionGeometry.GrossArea(section);
                    return ag > 0 ? row.LongitudinalRebarArea!.Value / ag : double.NaN;
                }).ToArray();
                if (ratios.All(x => double.IsFinite(x) && x > 0))
                {
                    var rho = ratios.Max();
                    output.Add(new(rho < .012 ? CheckStatus.WARNING : CheckStatus.NOT_APPLICABLE,
                        rho < .012 ? "Design-mode longitudinal reinforcement ratio is below 0.012: minimum-steel-governed candidate for section reduction; review drift, strength and detailing before resizing." : "Design-mode reinforcement ratio is not below 0.012; no section-reduction candidate identified.",
                        group.Key, "Frame", Actual: rho, Limit: .012, Unit: "As/Ag", Evidence: [new("Design-mode reinforcement envelope", "ConcreteColumns + Model.FrameSections", $"maximum required As/Ag={rho:G17}; {rows.Length} stations; required steel does not establish provided capacity.")]));
                    continue;
                }
            }
            var complete = rows.All(x => x.PmmRatio is > 0 && double.IsFinite(x.PmmRatio.Value) && string.IsNullOrWhiteSpace(x.ErrorSummary));
            var maximum = complete ? rows.Max(x => x.PmmRatio!.Value) : (double?)null;
            var status = !complete ? CheckStatus.NOT_VERIFIED : maximum < Threshold ? CheckStatus.WARNING : CheckStatus.NOT_APPLICABLE;
            output.Add(new(status, !complete
                ? "Column optimization is not verified: some extracted stations lack valid positive PMM ratios or report design errors; required-area design mode cannot establish utilization."
                : maximum < Threshold
                    ? "Column PMM utilization is very low. Candidate for value engineering / size reduction. Review all governing design and serviceability constraints before resizing."
                    : "Column envelope is not below the 0.35 under-utilization threshold; no optimization candidate identified by this screen.",
                group.Key, "Frame", Actual: maximum, Limit: Threshold, Unit: "ratio",
                Evidence: [new("Column utilization envelope", "DesignConcrete.GetSummaryResultsColumn",
                    string.Join("; ", rows.Select(x => $"location={x.Location:G17}, combo={x.DesignCombination}, PMM={x.PmmRatio:G17}, option={x.DesignOption}, errors={x.ErrorSummary}, warnings={x.WarningSummary}"))),
                    new("Extraction limitations", "ConcreteColumns.Limitations", string.Join("; ", data.Limitations)),
                    new("Screening scope", "OPT-001", "Maximum over extracted rows only; design freshness and full load coverage are not established. Low PMM alone does not establish a safe smaller section or economic savings.") ]));
        }
        return output;
    }

    private static IReadOnlyList<Observation> Piers(InspectionContext c)
    {
        var data = c.Input.ConcretePiers;
        if (data is null || data.Items.Count == 0)
            return [Rule.NotVerified("Pier design ratios are unavailable for value engineering. " + string.Join("; ", data?.Limitations ?? []))];
        List<Observation> output = [];
        foreach (var group in data.Items.GroupBy(x => (x.StoryName, x.PierName)))
        {
            var rows = group.ToArray();
            if (rows.All(x => x.RawDemandCapacityRatio == 0 && double.IsFinite(x.RequiredVerticalRebarPercent)))
            {
                var rho = rows.Max(x => x.RequiredVerticalRebarPercent / 100.0);
                var hasDesignError=rows.Any(x=>!string.IsNullOrWhiteSpace(x.ErrorSummary));
                var designStatus=hasDesignError?CheckStatus.NOT_APPLICABLE:rho <= .0025 ? CheckStatus.WARNING : CheckStatus.NOT_APPLICABLE;
                output.Add(new(designStatus,
                    hasDesignError ? "Optimization screening is not applicable because ETABS reports a pier design error; resolve the separate wall design failure before considering section changes." :
                    rho <= .0025 ? "Candidate for wall section reduction: design-mode required vertical reinforcement is at or below the 0.25% minimum-steel screen. Review strength, drift, shear and detailing before resizing." : "Design-mode required vertical reinforcement exceeds the minimum-steel screen; no wall section-reduction candidate identified.",
                    group.Key.StoryName + "/" + group.Key.PierName, "Pier", group.Key.StoryName,
                    Actual: rho, Limit: .0025, Unit: "RequiredVerticalRebarPercent/100", Evidence: [new("Design-mode reinforcement screen", "ConcretePiers.RequiredVerticalRebarPercent", $"maximum required vertical steel={rho:G17}; source percent={rows.Max(x => x.RequiredVerticalRebarPercent):G17}; {rows.Length} stations/legs; design errors={string.Join("; ",rows.Select(x=>x.ErrorSummary).Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct())}") ]));
                continue;
            }
            var complete = rows.All(x => x.RawDemandCapacityRatio > 0 && double.IsFinite(x.RawDemandCapacityRatio) && string.IsNullOrWhiteSpace(x.ErrorSummary));
            var maximum = complete ? rows.Max(x => x.RawDemandCapacityRatio) : (double?)null;
            var status = !complete ? CheckStatus.NOT_VERIFIED : maximum < Threshold ? CheckStatus.WARNING : CheckStatus.NOT_APPLICABLE;
            output.Add(new(status, !complete
                ? "Pier optimization is not verified: some extracted stations/legs lack valid positive demand/capacity ratios or report design errors. Zero ratios may denote inapplicable design-mode outputs."
                : maximum < Threshold
                    ? "Shear wall demand/capacity utilization is very low. Candidate for value engineering / size reduction. Review shear, stiffness, drift, boundary detailing and all governing constraints before resizing."
                    : "Pier envelope is not below the 0.35 under-utilization threshold; no optimization candidate identified by this screen.",
                group.Key.StoryName + "/" + group.Key.PierName, "Pier", group.Key.StoryName,
                Actual: maximum, Limit: Threshold, Unit: "ratio",
                Evidence: [new("Pier utilization envelope", "DesignShearWall.GetPierSummaryResults",
                    string.Join("; ", rows.Select(x => $"station={x.Station}, leg={x.PierLeg}, DCRatio={x.RawDemandCapacityRatio:G17}, type={x.DesignType}, errors={x.ErrorSummary}, warnings={x.WarningSummary}"))),
                    new("Extraction limitations", "ConcretePiers.Limitations", string.Join("; ", data.Limitations)),
                    new("Screening scope", "OPT-002", "Maximum over extracted rows per story/pier only. Raw DCRatio is not a substitute for shear utilization, complete seismic adequacy or a savings estimate.") ]));
        }
        return output;
    }
}
