
/// <summary>
/// Confirmed numerical criteria for modal participation, amplified drift, spectrum ratios and stability.
/// These comparisons do not establish code adequacy of the underlying model or seismic loads.
/// </summary>
public static class SeismicChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("SEIS-004", "Modal mass participation",
            "Compare the maximum cumulative participating mass ratio ETABS returned for the confirmed modal case with the confirmed minimum cumulative UX/UY criteria; the criterion is a project requirement, not a code limit.",
            ["AnalysisResults"], ModalParticipation),
        Rule.Create("SEIS-014", "Response spectrum configuration",
            "Report the extracted response-spectrum case inventory (input function, scale factor, coordinate system, angle, modal case, combination codes). Spectrum scaling, code adequacy, and design acceptability are not verified.",
            ["AnalysisDefinitions"], ResponseSpectrum),
        Rule.Create("SEIS-017", "P-Delta stability review",
            "ASCE 7-16 supplied stability criterion: theta=Px*elasticDriftRatio/Vx; compare with 0.10 and min(0.5/(beta*Cd),0.25).",
            ["AnalysisResults", "Stories", "LoadCases", "AnalysisStatus"], SeismicCriteriaChecks.Stability),
        ExtendedPhase3Checks.Create("SEIS-001", "Seismic configuration completeness", "Compare extracted ASCE 7-16 parameters with supplied project assumptions.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-002", "Mass source consistency", "Read the live default mass source and assembled directional mass, then convert it to weight using model display units.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-003", "ASCE empirical period limit", "Calculate Ta from live story height and the configured SFRS, derive Cu from calculated SD1, then compare finished Mode 1 with CuTa.", ["AnalysisResults", "Stories", "LoadCases", "AnalysisStatus"]),
        ExtendedPhase3Checks.Create("SEIS-005", "Mode shape plausibility", "Inspect returned mode shapes and identify participating modes lacking shapes.", ["AnalysisResults"]),
        Rule.Create("SEIS-006", "Story drift criterion", "ASCE 7-16 supplied criterion: max absolute elastic ratio * Cd/Ie <= driftLimit; no claim of load adequacy.", ["AnalysisResults", "Stories", "LoadCases", "AnalysisStatus"], SeismicCriteriaChecks.Drift),
        ExtendedPhase3Checks.Create("SEIS-007", "Torsional irregularity", "Screen diaphragm max/average drift ratios; verified trigger evaluation requires approved torsion envelope.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-008", "Soft-story irregularity", "Screen story stiffness against adjacent and three-story averages; distinguish extreme triggers.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-010", "Vertical geometric irregularity", "Compare adjacent automatically calculated story bounding dimensions using the 1.3 geometric trigger.", ["Joints", "Stories"]),
        ExtendedPhase3Checks.Create("SEIS-011", "Horizontal irregularity screening", "Calculate floor opening ratios and bounding plan spans from live area polygons and joint coordinates.", ["Areas", "Joints", "Stories"]),
        ExtendedPhase3Checks.Create("SEIS-012", "Story shear distribution", "Derive signed floor-force increments from static story-bottom shears.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-013", "Dynamic base shear review", "Calculate SDS, SD1, Cs and V from configured hazard inputs, live modal period and assembled directional mass; compare with ETABS auto-seismic output and reactions.", ["AnalysisResults", "Stories", "LoadCases", "AnalysisStatus"]),
        Rule.Create("SEIS-015", "Response spectrum scaling", "ASCE 7-16 supplied criterion: directional Vt/V >= configured ratio; existing applied scale is retained.", ["AnalysisDefinitions", "AnalysisResults", "LoadCases", "AnalysisStatus"], SeismicCriteriaChecks.Spectrum),
        ExtendedPhase3Checks.Create("SEIS-016", "Accidental torsion configuration", "Inspect both eccentricity signs and magnitude for mapped seismic patterns.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-018", "Overturning indicators", "Extract base overturning moment envelopes and reported reference coordinates.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("SEIS-019", "Diaphragm-related seismic concerns", "Resolve diaphragm assignments and compare independent flexibility criteria when supplied.", ["AnalysisResults"]),
        
    ];





    private static IReadOnlyList<Observation> ModalParticipation(InspectionContext c)
    {
        var criteria = c.Configuration.Settings.Analysis;
        if (!c.Configuration.Settings.Confirmed || criteria?.ModalCase is not { } modalCase ||
            (criteria.MinimumCumulativeUx is null && criteria.MinimumCumulativeUy is null))
            return [Rule.NotVerified("A confirmed analysis.modalCase and one or both minimumCumulativeUx/minimumCumulativeUy criteria are required; no modal participation criterion is assumed.")];
        var rows = c.Input.Analysis!.Results!.ModalParticipation.Items.Where(r => r.Case == modalCase).ToArray();
        if (rows.Length == 0)
            return [Rule.NotVerified("No modal participation rows were returned for the confirmed modal case; select it for ETABS output and rerun.")];
        List<Observation> outcomes = [];
        Check("UX", criteria.MinimumCumulativeUx, rows.Max(r => r.SumUX));
        Check("UY", criteria.MinimumCumulativeUy, rows.Max(r => r.SumUY));
        return outcomes;

        void Check(string axis, double? limit, double actual)
        {
            if (limit is null) return;
            var pass = actual >= limit.Value;
            outcomes.Add(new(pass ? CheckStatus.PASS : CheckStatus.FAIL,
                pass ? $"Maximum cumulative modal {axis} participating mass ratio meets the confirmed criterion." :
                    $"Maximum cumulative modal {axis} participating mass ratio is below the confirmed criterion.",
                modalCase, "ModalCase", Actual: actual, Limit: limit.Value, Unit: "ratio", Combination: modalCase,
                Evidence: [new("Maximum cumulative participating mass ratio",
                    "Results.ModalParticipatingMassRatios / Modal Participating Mass Ratios table",
                    $"max Sum{axis}={actual.ToString("G17", CultureInfo.InvariantCulture)} across {rows.Length} returned modes for case {modalCase}")]));
        }
    }

    private static IReadOnlyList<Observation> ResponseSpectrum(InspectionContext c)
    {
        var cases = c.Input.Analysis!.Definitions!.ResponseSpectrumCases.Items;
        return [new(cases.Count > 0 ? CheckStatus.PASS : CheckStatus.NOT_VERIFIED, cases.Count > 0
            ? "Inventory successfully extracted and available for review."
            : "No response-spectrum case definitions were found.",
            Evidence: [new("Response-spectrum cases", "LoadCases.ResponseSpectrum getters + LoadCases.GetTypeOAPI",
                string.Join("; ", cases.Select(x => $"{x.Name}: modalCase={x.ModalCase}, loads={x.Loads.Count}")))])];
    }

    private static IReadOnlyList<Observation> PDelta(InspectionContext c)
    {
        var settings = c.Input.Analysis!.Definitions!.GeometricNonlinearity.Items;
        var nonlinear = settings.Where(x => x.RawOption > 0).ToArray();
        return [new(CheckStatus.NOT_VERIFIED, nonlinear.Length > 0
            ? "Geometric nonlinearity is configured in one or more nonlinear static cases; stability is not established."
            : "No P-Delta geometric nonlinearity was identified in regular nonlinear static cases; other configuration paths are not covered.",
            Actual: nonlinear.Length, Unit: "cases",
            Evidence: [new("Geometric nonlinearity settings", "LoadCases.StaticNonlinear.GetGeometricNonlinearity + LoadCases.GetTypeOAPI",
                string.Join("; ", settings.Select(x => $"{x.Case}({x.CaseType})={x.Meaning ?? x.RawOption.ToString(CultureInfo.InvariantCulture)}")))])];
    }
}


public static class SeismicCriteriaChecks
{
    private static SeismicVerificationSettings? Criteria(InspectionContext c) =>
        c.Configuration.Settings.Confirmed ? c.Configuration.Settings.SeismicVerification : null;
    private static bool Ready(InspectionContext c, string name) =>
        c.Input.Analysis?.Status?.Cases.Items.Any(x => x.Name == name && x.State == AnalysisCaseState.Finished) == true &&
        c.Input.Validation.LoadCases?.Items.Any(x => x.Name == name && x.Type == "LinearStatic") == true;
    private static Evidence Basis(string value) => new("User-supplied ASCE 7-16 criterion; numerical comparison only", "configuration.seismicVerification", value);

    public static IReadOnlyList<Observation> Drift(InspectionContext c)
    {
        if (Criteria(c) is not { } s) return [Rule.NotVerified("Confirmed seismicVerification criteria are required.")];
        List<Observation> findings = [];
        foreach (var axis in s.Axes)
        foreach (var story in c.Input.Model.Stories!.Stories.Where(x => x.Height > 0))
        {
            var id = $"{story.Name}/{axis.ElasticCase}/{axis.Direction}";
            var rows = c.Input.Analysis!.Results!.StoryDrifts.Items.Where(r => r.Story == story.Name && r.Case == axis.ElasticCase && r.Direction == axis.Direction).ToArray();
            if (!Ready(c, axis.ElasticCase) || rows.Length == 0 || rows.Any(r => !double.IsFinite(r.Drift)))
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "Finished linear-static elastic drift results are missing or invalid.", id)); continue; }
            var elastic = rows.Max(r => Math.Abs(r.Drift));
            var design = elastic * s.Cd / s.Ie;
            if (!double.IsFinite(design)) { findings.Add(new(CheckStatus.NOT_VERIFIED, "Amplified drift is nonfinite.", id)); continue; }
            findings.Add(new(design <= s.DriftLimit ? CheckStatus.PASS : CheckStatus.FAIL,
                "Amplified drift compared with supplied limit; load adequacy, torsion and diaphragm coverage require separate verification.", id, "Story", story.Name,
                Actual: design, Limit: s.DriftLimit, Unit: "ratio", Combination: axis.ElasticCase,
                Evidence: [Basis($"12.8.6 / Table 12.12-1: {elastic:G17} * Cd={s.Cd} / Ie={s.Ie}; limit={s.DriftLimit}"),
                    new("Elastic story drift envelope", "Results.StoryDrifts", $"{rows.Length} rows; {axis.Direction}")]));
        }
        return findings.Count > 0 ? findings : [Rule.NotVerified("No positive-height stories were returned.")];
    }

    public static IReadOnlyList<Observation> Spectrum(InspectionContext c)
    {
        if (Criteria(c) is not { } s) return [Rule.NotVerified("Confirmed seismicVerification criteria are required.")];
        List<Observation> findings = [];
        foreach (var axis in s.Axes)
        {
            var definition = c.Input.Analysis!.Definitions!.ResponseSpectrumCases.Items.FirstOrDefault(x => x.Name == axis.SpectrumCase);
            if (definition is null || !Ready(c, axis.ElasticCase) ||
                c.Input.Analysis.Status?.Cases.Items.Any(x => x.Name == axis.SpectrumCase && x.State == AnalysisCaseState.Finished) != true)
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "No mapped, finished response-spectrum case and ELF reference pair is available.", axis.Direction)); continue; }
            // Mixed/rotated directional combinations cannot establish a directional base-shear ratio.
            var expected = axis.Direction == "X" ? "U1" : "U2";
            if (definition.Loads.Count != 1 || definition.Loads[0].Direction != expected || definition.Loads[0].CoordinateSystem != "Global" || definition.Loads[0].Angle != 0)
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "Spectrum case must contain one unrotated global direction for this comparison.", axis.Direction)); continue; }
            var reactions = c.Input.Analysis.Results!.BaseReactions.Items;
            var elf = reactions.Where(x => x.Case == axis.ElasticCase).ToArray();
            var rs = reactions.Where(x => x.Case == axis.SpectrumCase).ToArray();
            double Force(BaseReactionResult x) => Math.Abs(axis.Direction == "X" ? x.FX : x.FY);
            if (elf.Length == 0 || rs.Length == 0 || elf.Concat(rs).Any(x => !double.IsFinite(Force(x))) || elf.Max(Force) <= 0)
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "Base reaction results are missing, nonfinite, or the ELF reference is zero.", axis.Direction)); continue; }
            var ratio = rs.Max(Force) / elf.Max(Force);
            if (!double.IsFinite(ratio)) { findings.Add(new(CheckStatus.NOT_VERIFIED, "Base-shear ratio is nonfinite.", axis.Direction)); continue; }
            findings.Add(new(ratio >= s.SpectrumMinimumRatio ? CheckStatus.PASS : CheckStatus.FAIL,
                "Spectrum/ELF base-shear ratio compared with supplied criterion; ELF code adequacy remains a separate check.", axis.Direction,
                Actual: ratio, Limit: s.SpectrumMinimumRatio, Unit: "ratio", Combination: axis.SpectrumCase,
                Evidence: [Basis($"12.9.1.4: Vt={rs.Max(Force):G17}; V={elf.Max(Force):G17}; required multiplier={(ratio > 0 ? Math.Max(1, s.SpectrumMinimumRatio / ratio).ToString("G17") : "undefined: zero dynamic shear")}")]));
        }
        return findings;
    }

    public static IReadOnlyList<Observation> Stability(InspectionContext c)
    {
        if (Criteria(c) is not { } s) return [Rule.NotVerified("Confirmed seismicVerification criteria are required; geometric nonlinearity settings alone do not establish stability.")];
        if (s.GravityLoads.Count == 0) return [Rule.NotVerified("Stability requires an explicit total vertical design-load basis Px; mass-source factors are not substituted.")];
        var geometricNonlinearityData = c.Input.Analysis?.Definitions?.GeometricNonlinearity;
        if (geometricNonlinearityData is null)
            return [Rule.NotVerified("ETABS geometric-nonlinearity settings are unavailable; P-Delta inclusion cannot be determined.")];
        var geometricNonlinearity = geometricNonlinearityData.Items;
        var globalPDeltaActive = geometricNonlinearity.Any(x => x.RawOption > 0);
        var forces = c.Input.Analysis!.Results!.StoryForces?.Items;
        if (forces is null) return [Rule.NotVerified("Story Forces results were not extracted.")];
        List<Observation> findings = [];
        foreach (var axis in s.Axes)
        foreach (var story in c.Input.Model.Stories!.Stories.Where(x => x.Height > 0))
        {
            var id = $"{story.Name}/{axis.ElasticCase}/{axis.Direction}";
            var drift = SeismicEnvelope(c.Input.Analysis.Results.StoryDrifts.Items.Where(x => x.Story == story.Name && x.Case == axis.ElasticCase && x.Direction == axis.Direction), x => x.StepType);
            var shear = SeismicEnvelope(forces.Where(x => x.Story == story.Name && x.Case == axis.ElasticCase && x.Location == "Bottom"), x => x.StepType);
            var gravity = s.GravityLoads.Select(g => (g, rows: forces.Where(x => x.Story == story.Name && x.Case == g.Case && x.Location == "Bottom" && IsGravityStep(x.StepType)).ToArray())).ToArray();
            if (!Ready(c, axis.ElasticCase) || drift.Length == 0 || shear.Length == 0 || gravity.Any(g => !Ready(c, g.g.Case) || g.rows.Length != 1))
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "Unique finished static story drift, shear and gravity results are required.", id)); continue; }
            var px = gravity.Sum(g => g.g.Factor * g.rows[0].P);
            var vx = shear.Max(x => Math.Abs(axis.Direction == "X" ? x.VX : x.VY));
            var elasticDrift = drift.Max(x => Math.Abs(x.Drift));
            if (px <= 0 || vx <= 0 || !double.IsFinite(px) || !double.IsFinite(vx) || !double.IsFinite(elasticDrift))
            { findings.Add(new(CheckStatus.NOT_VERIFIED, "Positive compression Px and nonzero story shear with finite drift are required.", id)); continue; }
            // Delta/h = elastic drift ratio * Cd/Ie, so Cd/Ie cancels in theta.
            var theta = px * elasticDrift / vx;
            if (!double.IsFinite(theta)) { findings.Add(new(CheckStatus.NOT_VERIFIED, "Stability coefficient is nonfinite.", id)); continue; }
            var maximum = Math.Min(0.5 / (s.Beta * s.Cd), 0.25);
            var status = theta > maximum ? CheckStatus.FAIL :
                theta > 0.10 && !globalPDeltaActive ? CheckStatus.WARNING : CheckStatus.PASS;
            var provisionalLowTheta = !s.GravityBasisConfirmed && theta <= maximum && theta <= 0.10;
            findings.Add(new(status,
                provisionalLowTheta ? "Stability coefficient theta is within code limits (provisional: gravity basis uses extracted load cases, pending geotechnical certification)." :
                theta > maximum ? globalPDeltaActive
                    ? "Stability coefficient exceeds θ maximum; P-Delta is active in the ETABS solver, but the ASCE 7-16 stability limit is still exceeded."
                    : "Stability coefficient exceeds θ maximum; second-order effects must be included and the stability limit is exceeded." :
                globalPDeltaActive ? "Global P-Delta is active in ETABS solver; θ limits evaluated for reference but second-order effects are already accounted for." :
                theta > 0.10 ? "P-Delta effects must be accounted for; this comparison does not verify their inclusion." : "Stability coefficient is within the supplied neglect threshold and theta maximum.",
                id, "Story", story.Name, Actual: theta, Limit: maximum, Unit: "ratio", Combination: axis.ElasticCase,
                Evidence: [Basis($"12.8.7: Px={px:G17}, Vx envelope={vx:G17}, elastic drift envelope={elasticDrift:G17}; theta=Px*elasticRatio/Vx; beta={s.Beta}; neglect=0.10"),
                    new("Rounded display story forces", "DatabaseTables:Story Forces", string.Join(" + ", s.GravityLoads.Select(g => $"{g.Factor}*{g.Case}"))),
                    new("Gravity-basis status", "configuration.seismicVerification.gravityBasisConfirmed",
                        s.GravityBasisConfirmed ? "confirmed" : "provisional; extracted load cases used, pending geotechnical certification"),
                    new("ETABS geometric nonlinearity", "LoadCases.StaticNonlinear.GetGeometricNonlinearity", globalPDeltaActive
                        ? string.Join("; ", geometricNonlinearity.Where(x => x.RawOption > 0).Select(x => $"{x.Case}({x.CaseType})={x.Meaning ?? x.RawOption.ToString(CultureInfo.InvariantCulture)}"))
                        : "No extracted nonlinear static case has RawOption > 0.") ]));
        }
        return findings.Count > 0 ? findings : [Rule.NotVerified("No positive-height stories were returned.")];
    }

    internal static bool IsGravityStep(string? stepType) =>
        string.IsNullOrWhiteSpace(stepType) || string.Equals(stepType, "Single Value", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSeismicEnvelopeStep(string? stepType) =>
        string.Equals(stepType, "Max", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(stepType, "Min", StringComparison.OrdinalIgnoreCase) ||
        // ETABS 22.7 can expose static auto-seismic Story Forces as multiple
        // Step By Step rows instead of Max/Min. Envelope those rows as well.
        string.Equals(stepType, "Step By Step", StringComparison.OrdinalIgnoreCase);

    internal static T[] SeismicEnvelope<T>(IEnumerable<T> rows, Func<T, string?> stepType)
    {
        var materialized = rows.ToArray();
        var extrema = materialized.Where(x => IsSeismicEnvelopeStep(stepType(x))).ToArray();
        // Older ETABS result configurations may return a single static row; retain that fallback.
        return extrema.Length > 0 ? extrema : materialized.Where(x => IsGravityStep(stepType(x))).ToArray();
    }
}


