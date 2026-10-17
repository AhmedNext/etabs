public static class ColumnChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("COL-004", "Column PMM utilization", "Check mode evaluates PMM ratio; design mode passes when ETABS returns a finite positive required longitudinal area without design errors.",
            ["ConcreteColumns"], c => Evaluate(c, "COL-004")),
        Rule.Create("COL-007", "Column minimum longitudinal reinforcement", "Required-area screening: rho=As/Ag; <0.01 FAIL. Required reinforcement does not verify the provided reinforcement minimum.",
            ["ConcreteColumns", "FrameSections"], c => Evaluate(c, "COL-007")),
        Rule.Create("COL-008", "Column maximum longitudinal reinforcement", "Required-area screening: rho>0.08 FAIL; rho>0.04 WARNING for practical lap-splice congestion; otherwise PASS. Provided reinforcement remains unverified.",
            ["ConcreteColumns", "FrameSections"], c => Evaluate(c, "COL-008"))
    ];

    private static IReadOnlyList<Observation> Evaluate(InspectionContext c, string id)
    {
        var data = c.Input.ConcreteColumns;
        if (data is null)
            return [Rule.NotVerified("Concrete column design results are unavailable. " + string.Join(" ", data?.Limitations ?? []))];
        if (data.Items.Count == 0)
            return [Rule.NotVerified("Concrete column design results contain no element rows. " + string.Join(" ", data.Limitations))];
        List<Observation> output = [];
        foreach (var row in data.Items)
        {
            var evidence = new List<Evidence>
            {
                new("Column design summary", "DesignConcrete.GetSummaryResultsColumn + GetDesignSection",
                    $"frame={row.FrameId}; section={row.DesignSection}; PMM combination={row.DesignCombination}; option={row.DesignOption}; location={row.Location:G17}; errors={row.ErrorSummary}; warnings={row.WarningSummary}"),
                new("Extraction limitations", "ConcreteColumns.Limitations", string.Join("; ", data.Limitations))
            };
            void Add(CheckStatus status, string message, double? actual = null, double? limit = null, string? unit = "ratio") =>
                output.Add(new(status, $"{message} [location={row.Location:G17}]", row.FrameId, "Frame",
                    Actual: actual, Limit: limit, Unit: unit, Evidence: evidence.ToArray(), Combination: row.DesignCombination));
            if (!string.IsNullOrWhiteSpace(row.ErrorSummary))
            { Add(CheckStatus.NOT_VERIFIED, "ETABS reports a column design error; utilization and reinforcement cannot be verified."); continue; }
            if (id == "COL-004")
            {
                if (row.DesignOption == 1)
                {
                    if (row.PmmRatio is not { } pmm || !double.IsFinite(pmm) || pmm < 0)
                    { Add(CheckStatus.NOT_VERIFIED, "A valid PMM ratio is unavailable for this check-mode column."); continue; }
                    Add(pmm > 1 ? CheckStatus.FAIL : pmm >= .95 ? CheckStatus.WARNING : CheckStatus.PASS,
                        $"ETABS column PMM utilization={pmm:G17}; numerical utilization criterion only.", pmm, 1);
                    continue;
                }
                if (row.DesignOption == 2)
                {
                    if (row.LongitudinalRebarArea is not { } requiredArea || !double.IsFinite(requiredArea))
                    { Add(CheckStatus.NOT_VERIFIED, "ETABS design-mode required longitudinal area is missing or non-finite."); continue; }
                    if (requiredArea <= 0)
                    { Add(CheckStatus.FAIL, "ETABS design-mode required longitudinal area is not positive.", requiredArea, 0, $"{c.Input.Model.Identity.DisplayUnits.Length}²"); continue; }
                    Add(CheckStatus.PASS, "ETABS design-mode required area successfully computed; column adequacy governed by reinforcement design.",
                        requiredArea, unit: $"{c.Input.Model.Identity.DisplayUnits.Length}²");
                    continue;
                }
                Add(CheckStatus.NOT_VERIFIED, $"Unsupported or missing ETABS column design option ({row.DesignOption}); cannot determine check or design mode.");
                continue;
            }
            if (row.DesignOption == 1)
            { Add(CheckStatus.NOT_VERIFIED, "This column is in check mode. Its area result is not a required reinforcement area."); continue; }
            if (row.LongitudinalRebarArea is not { } area || !double.IsFinite(area) || area < 0)
            { Add(CheckStatus.NOT_VERIFIED, "Required longitudinal reinforcement area is unavailable or invalid; provided reinforcement is not inferred."); continue; }
            var sections = c.Input.Model.FrameSections?.Items.Where(s => s.Name == row.DesignSection).ToArray() ?? [];
            var section = sections.Length == 1 ? sections[0] : null;
            double gross = section switch
            {
                { Shape: "Rectangular", Width: > 0, Depth: > 0 } => section.Width.Value * section.Depth.Value,
                { Shape: "Circle", Diameter: > 0 } => Math.PI * section.Diameter.Value * section.Diameter.Value / 4,
                _ => 0
            };
            if (!double.IsFinite(gross) || gross <= 0)
            { Add(CheckStatus.NOT_VERIFIED, "Unique rectangular or circular design-section geometry with positive gross area is required."); continue; }
            var rho = area / gross;
            if (!double.IsFinite(rho))
            { Add(CheckStatus.NOT_VERIFIED, "Calculated reinforcement ratio is nonfinite."); continue; }
            evidence.Add(new("Required longitudinal reinforcement ratio", "Model.FrameSections + ConcreteColumns",
                $"required As={area:G17}; Ag={gross:G17}; rho={rho:G17}; areas use {c.Input.Model.Identity.DisplayUnits.Length}². This is required-area screening, not verification of provided bars."));
            var status = id == "COL-007" ? (rho < .01 ? CheckStatus.FAIL : CheckStatus.PASS)
                : rho > .08 ? CheckStatus.FAIL : rho > .04 ? CheckStatus.WARNING : CheckStatus.PASS;
            Add(status, $"Required longitudinal reinforcement ratio={rho:G17}; {(id == "COL-007" ? "1% minimum screening" : "4% practical lap-splice warning / 8% upper-limit screening")}. Provided reinforcement remains unverified.",
                rho, id == "COL-007" ? .01 : .08);
        }
        return output;
    }
}
