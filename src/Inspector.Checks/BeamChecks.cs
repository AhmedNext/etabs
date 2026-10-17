public static class BeamChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("BEAM-004", "Required top reinforcement", "Positive finite ETABS required top area confirms demand availability only; provided reinforcement is not verified.",
            ["ConcreteBeams"], c => Evaluate(c, "BEAM-004")),
        Rule.Create("BEAM-005", "Required bottom reinforcement", "Positive finite ETABS required bottom area confirms demand availability only; provided reinforcement is not verified.",
            ["ConcreteBeams"], c => Evaluate(c, "BEAM-005")),
        Rule.Create("BEAM-006", "Minimum longitudinal reinforcement screening", "Approximate required-area screening: rho=As/(bw*d), rho<0.0033 FAIL. Uses explicit effective depth or a disclosed h minus 60 mm screening assumption; does not verify provided reinforcement or the full material-dependent ACI minimum.",
            ["ConcreteBeams", "FrameSections"], c => Evaluate(c, "BEAM-006"))
    ];

    private static IReadOnlyList<Observation> Evaluate(InspectionContext c, string id)
    {
        var data = c.Input.ConcreteBeams;
        if (data is null)
            return [Rule.NotVerified("Concrete beam design summary results are unavailable. " + string.Join(" ", data?.Limitations ?? []))];
        if (data.Items.Count == 0)
            return [Rule.NotVerified("Concrete beam design summary contains no element rows. " + string.Join(" ", data.Limitations))];
        List<Observation> output = [];
        foreach (var row in data.Items)
        foreach (var face in id == "BEAM-004" ? new[] { "top" } : id == "BEAM-005" ? new[] { "bottom" } : new[] { "top", "bottom" })
        {
            var area = face == "top" ? row.TopRebarArea : row.BottomRebarArea;
            var combo = face == "top" ? row.FlexuralCombo : row.BottomFlexuralCombo;
            List<Evidence> evidence = [
                new("Beam design summary", "DesignConcrete.GetSummaryResultsBeam + GetDesignSection",
                    $"frame={row.FrameId}; section={row.DesignSection}; location={row.Location:G17}; face={face}; required As={area:G17}; combo={combo}; errors={row.ErrorSummary}; warnings={row.WarningSummary}"),
                new("Extraction limitations", "ConcreteBeams.Limitations", string.Join("; ", data.Limitations))
            ];
            void Add(CheckStatus status, string message, double? actual = null, double? limit = null) =>
                output.Add(new(status, $"{message} [face={face}; location={row.Location:G17}]", row.FrameId, "Frame",
                    Actual: actual, Limit: limit, Unit: id == "BEAM-006" ? "ratio" : c.Input.Model.Identity.DisplayUnits.Length + "²",
                    Evidence: evidence.ToArray(), Combination: combo));
            if (!string.IsNullOrWhiteSpace(row.ErrorSummary) || !double.IsFinite(area) || area < 0)
            { Add(CheckStatus.NOT_VERIFIED, "ETABS design error or missing/invalid required reinforcement area; no member adequacy conclusion is available."); continue; }
            if (id != "BEAM-006")
            {
                Add(area > 0 ? CheckStatus.PASS : CheckStatus.NOT_VERIFIED,
                    area > 0 ? "Positive required reinforcement area returned; demand availability only, provided reinforcement remains unverified."
                        : "No positive required reinforcement area was returned.", area);
                continue;
            }
            var sections = c.Input.Model.FrameSections?.Items.Where(s => s.Name == row.DesignSection).ToArray() ?? [];
            var section = sections.Length == 1 ? sections[0] : null;
            if (section is not { Shape: "Rectangular", Width: > 0, Depth: > 0 } ||
                !double.IsFinite(section.Width.Value) || !double.IsFinite(section.Depth.Value))
            { Add(CheckStatus.NOT_VERIFIED, "Unique matching rectangular FrameSection with finite positive web width and overall depth is required."); continue; }
            // d is measured from the compression face to the tension-bar centroid; overall h is not d.
            var key = face == "top" ? "TopEffectiveDepth" : "BottomEffectiveDepth";
            var explicitDepth = section.AdditionalDimensions.TryGetValue(key, out var depth) ||
                section.AdditionalDimensions.TryGetValue("EffectiveDepth", out depth);
            if (!explicitDepth)
            {
                var allowance = .060 / GeometryMath.MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
                depth = section.Depth.Value > allowance ? section.Depth.Value - allowance : .9 * section.Depth.Value;
                evidence.Add(new("Assumed effective depth", "BEAM-006 screening fallback", $"d={depth:G17}; h minus assumed 60 mm tension-bar centroid allowance (0.9h for shallow sections); actual bar cover and arrangement require confirmation."));
            }
            if (!double.IsFinite(depth) || depth <= 0 || depth >= section.Depth.Value)
            { Add(CheckStatus.NOT_VERIFIED, "Effective depth d is unavailable or invalid in the matching FrameSection; overall section depth cannot substitute for tension-bar centroid depth."); continue; }
            var webArea = section.Width.Value * depth;
            var rho = area / webArea;
            if (!double.IsFinite(webArea) || webArea <= 0 || !double.IsFinite(rho))
            { Add(CheckStatus.NOT_VERIFIED, "Required reinforcement ratio cannot be calculated from finite positive bw*d."); continue; }
            evidence.Add(new("Approximate minimum-ratio screening", "Model.FrameSections.AdditionalDimensions + ConcreteBeams",
                $"bw={section.Width:G17}; effective d={depth:G17}; required As={area:G17}; rho={rho:G17}; threshold=0.0033. No fc/fy-dependent ACI minimum or provided-bar compliance is established."));
            Add(rho < .0033 ? CheckStatus.FAIL : CheckStatus.PASS,
                $"Required {face} reinforcement ratio={rho:G17} screened against approximate minimum 0.0033; provided reinforcement remains unverified.", rho, .0033);
        }
        return output;
    }
}
