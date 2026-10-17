public static class WallChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("WALL-008", "Available wall design results", "At least one extracted pier summary row confirms availability only; design adequacy and freshness are separate.",
            [], Availability),
        Rule.Create("WALL-009", "Vertical reinforcement ratio screening", "Required-area screening: rho_l=As_required/Ag; rho_l<0.0025 FAIL; rho_l>0.04 WARNING for congestion; otherwise PASS. Requires unambiguous matching pier geometry; provided reinforcement remains unverified.",
            [], Vertical)
    ];

    private static IReadOnlyList<Observation> Availability(InspectionContext c)
    {
        var data = c.Input.ConcretePiers;
        var count = data?.Items.Count ?? 0;
        return [new(count > 0 ? CheckStatus.PASS : CheckStatus.NOT_VERIFIED,
            count > 0 ? "Inventory successfully extracted and available for review."
                : "Pier design summary results are unavailable; run Shear Wall Design if needed.",
            Actual: count, Unit: "pier rows", Evidence: [
                new("Pier result availability", "InspectionInput.ConcretePiers", $"rows={count}; error rows={data?.Items.Count(x => !string.IsNullOrWhiteSpace(x.ErrorSummary)) ?? 0}"),
                new("Extraction limitations", "ConcretePiers.Limitations", string.Join("; ", data?.Limitations ?? []))
            ])];
    }

    private static IReadOnlyList<Observation> Vertical(InspectionContext c)
    {
        var data = c.Input.ConcretePiers;
        if (data is null || data.Items.Count == 0)
            return [Rule.NotVerified("Required pier reinforcement and matching gross wall geometry are unavailable. " + string.Join("; ", data?.Limitations ?? []))];
        var groups = data.Items.GroupBy(x => (x.StoryName, x.PierName, x.Station))
            .ToDictionary(g => g.Key, g => g.Count());
        List<Observation> output = [];
        foreach (var row in data.Items)
        {
            var id = $"{row.StoryName}/{row.PierName}/{row.Station}/{row.PierLeg}";
            List<Evidence> evidence = [
                new("Pier design summary", "DesignShearWall.GetPierSummaryResults",
                    $"pier={row.PierName}; story={row.StoryName}; station={row.Station}; leg={row.PierLeg}; type={row.DesignType}; errors={row.ErrorSummary}; warnings={row.WarningSummary}"),
                new("Extraction limitations", "ConcretePiers.Limitations", string.Join("; ", data.Limitations))
            ];
            void Add(CheckStatus status, string message, double? ratio = null, double? limit = null) =>
                output.Add(new(status, message, id, "Pier", row.StoryName, Actual: ratio, Limit: limit,
                    Unit: "ratio", Evidence: evidence.ToArray(), Combination: row.FlexuralCombo));
            if (!string.IsNullOrWhiteSpace(row.ErrorSummary))
            {
                Add(CheckStatus.FAIL, "ETABS reports a wall design error: " + row.ErrorSummary);
                continue;
            }
            if (row.DesignType.Equals("Check", StringComparison.OrdinalIgnoreCase) && row.PmmRatio is { } pmm && double.IsFinite(pmm) && pmm >= 0)
            {
                Add(pmm > 1 ? CheckStatus.FAIL : CheckStatus.PASS, "Wall demand-to-capacity ratio compared with 1.0.", pmm, 1);
                continue;
            }
            if (groups[(row.StoryName, row.PierName, row.Station)] != 1 ||
                !row.DesignType.Equals("Uniform", StringComparison.OrdinalIgnoreCase))
            { Add(CheckStatus.NOT_VERIFIED, "Pier gross area mapping is ambiguous or uses a nonuniform design type; multiple legs and overlapping wall areas require independent geometry mapping."); continue; }
            if (row.RequiredVerticalRebarArea is not { } area || !double.IsFinite(area) || area < 0 ||
                row.LegLength is not { } length || !double.IsFinite(length) || length <= 0 ||
                row.LegThickness is not { } thickness || !double.IsFinite(thickness) || thickness <= 0)
            { Add(CheckStatus.NOT_VERIFIED, "Finite required vertical area and positive matching leg length/thickness are needed to calculate gross wall area."); continue; }
            var gross = length * thickness;
            var rho = area / gross;
            if (!double.IsFinite(gross) || gross <= 0 || !double.IsFinite(rho))
            { Add(CheckStatus.NOT_VERIFIED, "Gross wall area or vertical reinforcement ratio is nonfinite or invalid."); continue; }
            evidence.Add(new("Required vertical reinforcement ratio", "ConcretePiers + matched Shear Wall Pier Design Summary geometry",
                $"As_required={area:G17}; length={length:G17}; thickness={thickness:G17}; Ag=length*thickness={gross:G17}; rho_l={rho:G17}; area unit={c.Input.Model.Identity.DisplayUnits.Length}². Required reinforcement is not provided reinforcement."));
            var status = rho < .0025 ? CheckStatus.FAIL : rho > .04 ? CheckStatus.WARNING : CheckStatus.PASS;
            Add(status, $"Required vertical reinforcement ratio={rho:G17} screened against 0.0025 minimum and 0.04 congestion threshold; provided reinforcement and full ACI applicability remain unverified.",
                rho, rho > .04 ? .04 : .0025);
        }
        return output;
    }
}
