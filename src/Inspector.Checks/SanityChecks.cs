using System.Globalization;
using System.Text.Json;

/// <summary>
/// Order-of-magnitude engineering screens. These are hand-check comparisons, not design/code checks.
/// Their assumed pressures and screening bands are disclosed in each rule's method/evidence.
/// </summary>
public static class SanityChecks
{
    private const double FloorLoadKnPerSquareMeter = 12.0;
    private const double WindPressureKnPerSquareMeter = 1.25;

    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("SAN-001", "Tributary area column axial load check",
            "Compare the largest extracted service-gravity column axial force at the lowest occupied story with a tributary-area estimate using an explicit 12 kN/m² screening allowance and story count. This is not a load combination or design check.",
            ["Frames", "Stories", "FrameSections", "Areas", "Joints", "AnalysisResults"], TributaryColumnLoad),
        Rule.Create("SAN-002", "0.1N fundamental period rule of thumb",
            "Compare finished ETABS Mode 1 with 0.075N seconds for shear-wall/dual systems or 0.10N seconds for moment frames; this is an engineering plausibility screen, not an ASCE period limit.",
            ["Stories", "AnalysisResults"], PeriodRule),
        Rule.Create("SAN-003", "Building gravity weight density check",
            "Compare extracted Dead-case vertical base reaction divided by summed non-opening floor area with broad reinforced-concrete commercial screening bands (8 to 18 kN/m²); this does not establish complete dead load.",
            ["Stories", "Areas", "Joints", "AnalysisResults"], GravityDensity),
        Rule.Create("SAN-004", "Wind base shear silhouette area check",
            "Compare extracted wind base reactions with projected building silhouette times an explicit 1.25 kN/m² screening pressure; this is not a site wind design calculation.",
            ["Stories", "Joints", "AnalysisResults"], WindSilhouette),
        Rule.Create("SAN-005", "Column base axial stress ratio limit",
            "Screen the largest extracted gravity axial force in lowest-story designed columns against fc'Ag; ratios over 0.40 warn and ratios below 0.08 are reported not applicable as lightly stressed. This is not an ACI interaction check.",
            ["ConcreteColumns", "FrameSections", "Materials", "AnalysisResults", "Frames", "Stories"], ColumnStress),
        Rule.Create("SAN-006", "Shear wall maximum shear stress ceiling",
            "Compare ETABS Pier Forces shear demand divided by extracted pier leg length times thickness with 0.495√fc' MPa (phi=0.75). Missing Vu or unambiguous pier/material mapping is not verified.",
            ["ConcretePiers", "Materials", "Areas", "AreaSections", "AnalysisResults"], WallShearStress),
        Rule.Create("SAN-007", "Global foundation overturning factor of safety",
            "Screen overturning moment from seismic/wind base reactions against 0.9 times Dead reaction times half the orthogonal footprint. This simplified global screen does not replace foundation/uplift design.",
            ["Stories", "Joints", "AnalysisResults"], Overturning),
        Rule.Create("SAN-008", "ASCE 7 vertical story force distribution Cvx verification",
            "Compare theoretical Cvx story force shares from live story mass, elevations and ASCE 7-16 exponent k with increments derived from ETABS story shears. Missing story mass or force increments are reported not verified.",
            ["Stories", "AnalysisResults"], VerticalForceDistribution)
    ];

    private static IReadOnlyList<Observation> TributaryColumnLoad(InspectionContext c)
    {
        var frames = LowestColumns(c);
        if (frames.Length == 0) return [Rule.NotVerified("No vertical frame columns were found at the lowest occupied story.")];
        var forces = c.Input.Analysis?.Results?.Extended?.FrameForces ?? [];
        var points = frames.Select(f => (Frame: f, X: (f.Start.X + f.End.X) / 2, Y: (f.Start.Y + f.End.Y) / 2)).ToArray();
        var unit = c.Input.Model.Identity.DisplayUnits.Length;
        var toMeters = MetersPerUnit(unit);
        var fallbackArea = FloorAreaByStory(c).GetValueOrDefault(frames[0].Story);
        if (fallbackArea <= 0) fallbackArea = BoundingFootprint(c);
        var storyCount = AboveGroundStories(c).Count;
        if (storyCount == 0) return [Rule.NotVerified("Above-ground story count is unavailable for the tributary-load estimate.")];
        List<Observation> output = [];
        foreach (var point in points)
        {
            var neighbors = NearestSpans(point, points);
            var usedFallback = neighbors is null;
            var tributaryArea = usedFallback ? fallbackArea / frames.Length :
                .5 * (neighbors!.Value.Left + neighbors.Value.Right) * .5 * (neighbors.Value.Bottom + neighbors.Value.Top) * toMeters * toMeters;
            if (!double.IsFinite(tributaryArea) || tributaryArea <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "A positive tributary area could not be derived for this column.", point.Frame.Id, point.Frame.Story)); continue; }
            var service = forces.Where(x => x.Frame == point.Frame.Id && IsGravityCase(c, x.Case)).ToArray();
            if (service.Length == 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "No extracted Dead/Live/service gravity axial-force result was found for this column.", point.Frame.Id, point.Frame.Story)); continue; }
            var actual = service.Max(x => Math.Abs(x.P)) * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
            if (!double.IsFinite(actual) || actual <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Extracted gravity axial force is not positive and finite.", point.Frame.Id, point.Frame.Story)); continue; }
            var estimate = tributaryArea * FloorLoadKnPerSquareMeter * storyCount;
            var deviation = Math.Abs(estimate - actual) / actual;
            output.Add(new(deviation > .40 ? CheckStatus.WARNING : CheckStatus.PASS,
                deviation > .40
                    ? "Column axial load deviates significantly from tributary area hand calculation; inspect slab meshing, transfer slabs, or beam releases."
                    : "Column axial load is within the tributary area hand-calculation screening band.",
                point.Frame.Id, "Frame", point.Frame.Story, Actual: deviation, Limit: .40, Unit: "ratio",
                Evidence: [Ev("Results.FrameForce + live model geometry",
                    $"P_est={estimate:G6} kN; P_actual={actual:G6} kN; Atrib={tributaryArea:G6} m² ({(usedFallback ? "floor footprint / column count fallback" : "nearest adjacent column spacings")}); q_floor={FloorLoadKnPerSquareMeter:G4} kN/m² screening allowance; N={storyCount}; cases={string.Join(",",service.Select(x=>x.Case).Distinct(StringComparer.Ordinal))}")]));
        }
        return output;
    }

    private static IReadOnlyList<Observation> PeriodRule(InspectionContext c)
    {
        var stories = AboveGroundStories(c);
        if (stories.Count == 0) return [Rule.NotVerified("No above-ground stories are available for the 0.1N period screen.")];
        var meta = c.Configuration.Settings.Seismic;
        if (meta is not { ValueKind: JsonValueKind.Object } || !meta.Value.TryGetProperty("system", out var systemValue) || systemValue.ValueKind != JsonValueKind.String)
            return [Rule.NotVerified("The structural system is not identified in seismic settings; the rule-of-thumb period cannot be selected.")];
        var system = systemValue.GetString() ?? "";
        var isWallOrDual = system.Contains("wall", StringComparison.OrdinalIgnoreCase) || system.Contains("dual", StringComparison.OrdinalIgnoreCase);
        var isMoment = system.Contains("moment", StringComparison.OrdinalIgnoreCase);
        if (!isWallOrDual && !isMoment) return [Rule.NotVerified($"Structural system '{system}' does not map to the 0.1N screen categories.")];
        var expected = (isWallOrDual ? .075 : .10) * stories.Count;
        var mode = ExtendedPhase3Checks.ResolveModeOne(c);
        if (mode is null) return [Rule.NotVerified("A unique finished Mode 1 period could not be matched between ETABS modal participation results and the Modal Periods And Frequencies table.")];
        var ratio = mode.Period / expected;
        var unusual = ratio < .5 || ratio > 1.75;
        return [new(unusual ? CheckStatus.WARNING : CheckStatus.PASS,
            unusual ? "Fundamental period is outside the 0.5 to 1.75 times 0.1N plausibility band; review restraints, stiffness, and modal setup."
                : "Fundamental period is within the 0.1N plausibility band.", mode.Case, "ModalCase",
            Actual: ratio, Limit: 1.75, Unit: "Tactual/Tapprox",
            Evidence: [Ev("Results.ModalParticipatingMassRatios + seismic.system",
                $"T1={mode.Period:G6}s; Tapprox={expected:G6}s; ratio={ratio:G6}; system={system}; above-ground stories={stories.Count}; screening range=0.5..1.75")])];
    }

    private static IReadOnlyList<Observation> GravityDensity(InspectionContext c)
    {
        var floorArea = FloorAreaByStory(c).Values.Sum();
        if (!double.IsFinite(floorArea) || floorArea <= 0) return [Rule.NotVerified("Non-opening floor slab area could not be calculated from the model polygons.")];
        var reactions = c.Input.Analysis?.Results?.BaseReactions.Items.Where(r => r.StepType == "Single Value" && IsDeadCase(c, r.Case)).ToArray() ?? [];
        if (reactions.Length == 0) return [Rule.NotVerified("No extracted Dead-case vertical base reaction is available.")];
        var result = reactions.OrderByDescending(r => Math.Abs(r.FZ)).First();
        var weight = Math.Abs(result.FZ) * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
        var density = weight / floorArea;
        var warning = density < 8 || density > 18;
        return [new(warning ? CheckStatus.WARNING : CheckStatus.PASS,
            density < 8 ? "Average building gravity weight density is unusually low; check missing dead/finishes load or mass-source setup."
            : density > 18 ? "Average building gravity weight density is unusually high; check for double-counted self mass or load assignments."
            : "Average building gravity weight density is within the broad reinforced-concrete commercial screening range.",
            result.Case, "BaseReaction", Actual: density, Limit: density < 8 ? 8 : 18, Unit: "kN/m²",
            Evidence: [Ev("Results.BaseReact + non-opening floor polygons", $"case={result.Case}; |Fz|={weight:G6} kN; total floor area={floorArea:G6} m²; density={density:G6} kN/m²; screening band=8..18 kN/m²")])];
    }

    private static IReadOnlyList<Observation> WindSilhouette(InspectionContext c)
    {
        var (bx, by) = PlanDimensions(c);
        var height = (c.Input.Model.Stories!.Stories.Select(s => s.Elevation).DefaultIfEmpty(c.Input.Model.Stories.BaseElevation).Max() - c.Input.Model.Stories.BaseElevation) * MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
        var reactions = c.Input.Analysis?.Results?.BaseReactions.Items.Where(r => (SeismicCriteriaChecks.IsSeismicEnvelopeStep(r.StepType) || SeismicCriteriaChecks.IsGravityStep(r.StepType)) && IsWindCase(c, r.Case)).ToArray() ?? [];
        if (height <= 0 || bx <= 0 || by <= 0) return [Rule.NotVerified("Positive model height and two plan dimensions are required for the wind silhouette screen.")];
        if (reactions.Length == 0) return [Rule.NotVerified("No extracted Wind-case base reactions are available.")];
        List<Observation> output = [];
        foreach (var group in reactions.GroupBy(r => r.Case, StringComparer.Ordinal))
        foreach (var direction in new[] { "X", "Y" })
        {
            var reaction = group.First();
            var estimate = (direction == "X" ? by : bx) * height * WindPressureKnPerSquareMeter * 1000;
            var actual = group.Max(r => Math.Abs(direction == "X" ? r.FX : r.FY)) * ForceToN(c.Input.Model.Identity.DisplayUnits.Force);
            if (actual <= 10000) { output.Add(new(CheckStatus.NOT_APPLICABLE, "Wind reaction is 10 kN or less; silhouette screening is not applicable to this direction.", reaction.Case + "/" + direction, "BaseReaction", Actual: actual, Limit: 10000, Unit: "N")); continue; }
            if (estimate <= 0 || actual <= 0) { output.Add(Item(CheckStatus.NOT_VERIFIED, "Wind estimate or extracted base shear is not positive.", reaction.Case + "/" + direction)); continue; }
            var ratio = actual / estimate;
            var unusual = ratio < .5 || ratio > 2.0;
            output.Add(new(unusual ? CheckStatus.WARNING : CheckStatus.PASS,
                unusual ? "ETABS wind base shear is outside 0.5 to 2.0 times the silhouette hand estimate; review wind exposure, pressure, and load application."
                    : "ETABS wind base shear is within the silhouette hand-estimate screening band.",
                reaction.Case + "/" + direction, "BaseReaction", Actual: ratio, Limit: 2.0, Unit: "Vactual/Vestimate",
                Evidence: [Ev("Results.BaseReact + joint bounding box",
                    $"case={reaction.Case}; direction={direction}; H={height:G6}m; Bx={bx:G6}m; By={by:G6}m; qwind={WindPressureKnPerSquareMeter*1000:G4} N/m²; Vest={estimate:G6} N; VETABS={actual:G6} N; ratio={ratio:G6}")]));
        }
        return output;
    }

    private static IReadOnlyList<Observation> ColumnStress(InspectionContext c)
    {
        var columns = LowestColumns(c);
        if (columns.Length == 0) return [Rule.NotVerified("No lowest-story vertical frame columns were found.")];
        var designedIds = c.Input.ConcreteColumns?.Items.Select(x => x.FrameId).ToHashSet(StringComparer.Ordinal) ?? [];
        var frames = columns.Where(x => designedIds.Contains(x.Id)).ToArray();
        if (frames.Length == 0) return [Rule.NotVerified("No lowest-story columns match extracted concrete design results.")];
        var sections = c.Input.Model.FrameSections!.Items.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var materials = c.Input.Model.Materials!.Items.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var forces = c.Input.Analysis?.Results?.Extended?.FrameForces ?? [];
        List<Observation> output = [];
        foreach (var frame in frames)
        {
            if (!sections.TryGetValue(frame.Section, out var matches) || matches.Length != 1 || matches[0].Material is not { } materialName ||
                !materials.TryGetValue(materialName, out var mat) || mat.Length != 1 || mat[0].ConcreteStrength is not { } fc || fc <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Unique section geometry and assigned concrete strength are required for the axial stress ratio.", frame.Id, frame.Story)); continue; }
            var section = matches[0];
            var area = SectionAreaSquareMeters(section, MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length));
            var concreteStrength = fc * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force) / Math.Pow(MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length), 2);
            var axial = forces.Where(x => x.Frame == frame.Id && IsGravityCase(c, x.Case)).Select(x => Math.Abs(x.P)).DefaultIfEmpty(0).Max() * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
            if (area <= 0 || concreteStrength <= 0 || axial <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Positive finite section area, concrete strength, and extracted service axial force are required.", frame.Id, frame.Story)); continue; }
            var ratio = axial / (concreteStrength * area);
            var status = ratio > .40 ? CheckStatus.WARNING : ratio < .08 ? CheckStatus.NOT_APPLICABLE : CheckStatus.PASS;
            output.Add(new(status, ratio > .40
                ? "Column axial stress ratio exceeds 0.40 fc'Ag; review section size, axial load, and seismic flexure interaction."
                : ratio < .08 ? "Column is lightly stressed by this axial-only screen; candidate for section optimization review." : "Column axial stress ratio is within the axial-only screening band.",
                frame.Id, "Frame", frame.Story, Actual: ratio, Limit: .40, Unit: "P/(fc'Ag)",
                Evidence: [Ev("FrameForce + PropFrame + PropMaterial",
                    $"P={axial:G6} kN; fc'={concreteStrength:G6} kN/m²; Ag={area:G6} m²; ratio={ratio:G6}; bending interaction is not included") ]));
        }
        return output;
    }

    private static IReadOnlyList<Observation> WallShearStress(InspectionContext c)
    {
        var piers = c.Input.ConcretePiers?.Items ?? [];
        if (piers.Count == 0) return [Rule.NotVerified("Concrete pier design results are unavailable.")];
        var table = c.Input.Analysis?.Results?.Extended?.Tables.SingleOrDefault(x => x.Key == "Pier Forces");
        if (table is null || table.UnavailableReason is not null || table.Rows.Count == 0)
            return [Rule.NotVerified("ETABS Pier Forces table is unavailable; required reinforcement per unit length cannot substitute for ultimate shear demand Vu.")];
        string? Field(params string[] names) => names.FirstOrDefault(name => table.Fields.Contains(name, StringComparer.OrdinalIgnoreCase));
        var pierField = Field("Pier", "PierName"); var storyField = Field("Story", "StoryName");
        var directShearField = Field("Vu", "Shear", "ShearForce"); var v2Field = Field("V2"); var v3Field = Field("V3");
        if (pierField is null || storyField is null || directShearField is null && v2Field is null && v3Field is null)
            return [Rule.NotVerified("Pier Forces table lacks pier/story identity or shear force fields; SAN-006 cannot match Vu safely.")];
        var areaSections = c.Input.Model.AreaSections?.Items.ToDictionary(x => x.Name, StringComparer.Ordinal) ?? new Dictionary<string, AreaSection>(StringComparer.Ordinal);
        var materialGroups = c.Input.Model.Materials!.Items.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        var lengthScale = MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
        var forceScale = ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
        var output = new List<Observation>();
        foreach (var pier in piers.GroupBy(x => (x.PierName, x.StoryName, x.PierLeg)).Select(x => x.First()))
        {
            var matchingRows = table.Rows.Where(row => row.GetValueOrDefault(pierField)?.Equals(pier.PierName, StringComparison.OrdinalIgnoreCase) == true &&
                row.GetValueOrDefault(storyField)?.Equals(pier.StoryName, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (matchingRows.Length == 0 || pier.LegLength is not { } length || pier.LegThickness is not { } thickness || length <= 0 || thickness <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Pier-force rows and positive ETABS pier leg dimensions are required to calculate average shear stress.", pier.PierName, pier.StoryName)); continue; }
            var shearValues = new List<double>();
            foreach (var row in matchingRows)
            {
                void Read(string? name)
                {
                    if (name is not null && double.TryParse(row.GetValueOrDefault(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) shearValues.Add(Math.Abs(value));
                }
                Read(directShearField); Read(v2Field); Read(v3Field);
            }
            if (shearValues.Count == 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Pier-force shear demand is missing or nonnumeric.", pier.PierName, pier.StoryName)); continue; }
            var matchingAreas = (c.Input.Model.Areas?.Items ?? []).Where(a => a.Story == pier.StoryName &&
                (a.Label.Equals(pier.PierName, StringComparison.OrdinalIgnoreCase) || a.Id.Equals(pier.PierName, StringComparison.OrdinalIgnoreCase)))
                .Where(a => a.Section is not null && areaSections.ContainsKey(a.Section)).ToArray();
            var materialNames = matchingAreas.Select(a => areaSections[a.Section!].Material).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
            if (materialNames.Length != 1 || !materialGroups.TryGetValue(materialNames[0]!, out var concreteMatches) || concreteMatches.Length != 1 ||
                concreteMatches[0].ConcreteStrength is not { } strength || strength <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "A unique concrete material and fc' could not be mapped from model areas assigned to this pier.", pier.PierName, pier.StoryName)); continue; }
            var vuKn = shearValues.Max() * forceScale;
            var acv = length * lengthScale * thickness * lengthScale;
            var tauMpa = vuKn / acv / 1000;
            var fcMpa = strength * forceScale / (lengthScale * lengthScale) / 1000;
            if (tauMpa < 0 || fcMpa <= 0 || !double.IsFinite(tauMpa) || !double.IsFinite(fcMpa))
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Converted pier shear stress or concrete strength is invalid.", pier.PierName, pier.StoryName)); continue; }
            var limit = .75 * .66 * Math.Sqrt(fcMpa);
            var fail = tauMpa > limit;
            output.Add(new(fail ? CheckStatus.FAIL : CheckStatus.PASS,
                fail ? "Shear wall shear stress exceeds ACI 318-19 absolute ceiling phi*0.66*sqrt(fc'); increasing rebar cannot fix this, wall thickness must be increased."
                    : "Shear wall average shear stress is below the ACI 318-19 absolute ceiling; this does not verify wall detailing or reinforcement sufficiency.",
                pier.PierName, "Pier", pier.StoryName, Actual: tauMpa, Limit: limit, Unit: "MPa",
                Evidence: [Ev("Pier Forces + pier design summary + AreaSection/Material definitions",
                    $"pier={pier.PierName}; story={pier.StoryName}; Vu={vuKn:G6} kN; Lw={length * lengthScale:G6}m; tw={thickness * lengthScale:G6}m; Acv={acv:G6}m²; fc'={fcMpa:G6}MPa; tau={tauMpa:G6}MPa; limit=0.495√fc'={limit:G6}MPa") ]));
        }
        return output.Count > 0 ? output : [Rule.NotVerified("No pier-level shear demand could be evaluated.")];
    }

    private static IReadOnlyList<Observation> Overturning(InspectionContext c)
    {
        var (bx, by) = PlanDimensions(c);
        var dead = c.Input.Analysis?.Results?.BaseReactions.Items.Where(x => x.StepType == "Single Value" && IsDeadCase(c, x.Case)).ToArray() ?? [];
        var overturning = c.Input.Analysis?.Results?.BaseReactions.Items.Where(x => x.StepType == "Single Value" && (IsWindCase(c, x.Case) || IsSeismicCase(x.Case))).ToArray() ?? [];
        if (dead.Length == 0 || overturning.Length == 0 || bx <= 0 || by <= 0)
            return [Rule.NotVerified("Dead reactions, seismic/wind overturning reactions, and positive plan dimensions are required for the global overturning screen.")];
        var weight = dead.Max(x => Math.Abs(x.FZ)) * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
        if (weight <= 0) return [Rule.NotVerified("Dead-case vertical base reaction is not positive.")];
        var centerX = (c.Input.Model.Joints!.Items.Min(x => x.Position.X) + c.Input.Model.Joints.Items.Max(x => x.Position.X)) / 2;
        var centerY = (c.Input.Model.Joints.Items.Min(x => x.Position.Y) + c.Input.Model.Joints.Items.Max(x => x.Position.Y)) / 2;
        var lengthScale = MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
        var output = new List<Observation>();
        foreach (var reaction in overturning)
        {
            var offsetX = (reaction.ReferencePoint.X - centerX) * lengthScale;
            var offsetY = (reaction.ReferencePoint.Y - centerY) * lengthScale;
            // Translate the reported base moment to the plan center before using it as overturning demand.
            var momentScale = ForceToKn(c.Input.Model.Identity.DisplayUnits.Force) * lengthScale;
            var mx = reaction.MX * momentScale + offsetY * reaction.FZ * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
            var my = reaction.MY * momentScale - offsetX * reaction.FZ * ForceToKn(c.Input.Model.Identity.DisplayUnits.Force);
            var cases = new[] { ("X", .9 * weight * bx / 2, Math.Abs(my)), ("Y", .9 * weight * by / 2, Math.Abs(mx)) };
            foreach (var (direction, resisting, demand) in cases)
            {
                if (demand <= 0) continue;
                var fs = resisting / demand;
                var status = fs < 1 ? CheckStatus.FAIL : fs < 1.5 ? CheckStatus.WARNING : CheckStatus.PASS;
                output.Add(new(status, fs < 1 ? "Global overturning safety factor is below 1.0; review uplift and foundation stability." :
                    fs < 1.5 ? "Global overturning safety factor is below 1.5; verify hold-down anchors or pile tension capacity." :
                    "Global overturning safety factor is at least 1.5 under the simplified global screen.",
                    reaction.Case + "/" + direction, "BaseReaction", Actual: fs, Limit: 1.5, Unit: "factor",
                    Evidence: [Ev("Results.BaseReact + live plan bounding box", $"case={reaction.Case}; direction={direction}; Wdead={weight:G6} kN; Bx={bx:G6}m; By={by:G6}m; Mres={resisting:G6} kN·m; Moverturn={demand:G6} kN·m; FS={fs:G6}")]));
            }
        }
        return output.Count > 0 ? output : [Rule.NotVerified("No positive seismic/wind overturning base moments were available.")];
    }

    private static IReadOnlyList<Observation> VerticalForceDistribution(InspectionContext c)
    {
        var evidence = c.Input.Analysis?.Results?.Extended?.Tables.SingleOrDefault(x => x.Key == "Mass Summary by Story");
        if (evidence is null || evidence.UnavailableReason is not null || evidence.Rows.Count == 0)
            return [Rule.NotVerified("Mass Summary by Story is unavailable; story seismic weights cannot be derived.")];
        var stories = AboveGroundStories(c).OrderBy(x => x.Elevation).ToArray();
        var storyForces = c.Input.Analysis?.Results?.StoryForces?.Items ?? [];
        var mode = ExtendedPhase3Checks.ResolveModeOne(c);
        if (stories.Length == 0 || mode is null || storyForces.Count == 0)
            return [Rule.NotVerified("Stories, finished Mode 1 period, and ETABS story-force results are required for Cvx comparison.")];
        var t1 = mode.Period;
        var k = t1 <= .5 ? 1 : t1 >= 2.5 ? 2 : 1 + (t1 - .5) / 2.0;
        var rows = evidence.Rows;
        string Field(string row, params string[] candidates) => candidates.FirstOrDefault(name => rows[0].ContainsKey(name)) ?? "";
        var storyField = Field("", "Story", "StoryName");
        var massXField = Field("", "MassX", "UXMass", "WeightX", "UX");
        var massYField = Field("", "MassY", "UYMass", "WeightY", "UY");
        if (storyField.Length == 0 || massXField.Length == 0 && massYField.Length == 0)
            return [Rule.NotVerified("Mass Summary by Story lacks recognizable story and directional mass fields.")];
        var output = new List<Observation>();
        var caseNames = storyForces.Select(x => x.Case).Distinct(StringComparer.Ordinal).Where(IsSeismicCase).ToArray();
        foreach (var caseName in caseNames)
        foreach (var direction in new[] { "X", "Y" })
        {
            var forceRows = storyForces.Where(x => x.Case == caseName && x.Location.Equals("Bottom",StringComparison.OrdinalIgnoreCase)).ToArray();
            forceRows = SeismicCriteriaChecks.SeismicEnvelope(forceRows,x=>x.StepType);
            if (forceRows.Length == 0) continue;
            var masses = new List<(Story Story, double Mass)>();
            foreach (var story in stories)
            {
                var matching = rows.Where(r => r.TryGetValue(storyField, out var name) && name == story.Name).ToArray();
                var field = direction == "X" ? massXField : massYField;
                if (field.Length == 0 || matching.Length != 1 || !double.TryParse(matching[0].GetValueOrDefault(field), NumberStyles.Float, CultureInfo.InvariantCulture, out var mass) || !double.IsFinite(mass) || mass < 0)
                { masses.Clear(); break; }
                masses.Add((story, mass));
            }
            if (masses.Count != stories.Length || masses.Sum(x => x.Mass) <= 0)
            { output.Add(Item(CheckStatus.NOT_VERIFIED, "Story mass is missing or invalid for this seismic direction.", caseName + "/" + direction)); continue; }
            var sum = masses.Sum(x => x.Mass * Math.Pow((x.Story.Elevation - c.Input.Model.Stories!.BaseElevation) * MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length), k));
            var baseReaction = SeismicCriteriaChecks.SeismicEnvelope(c.Input.Analysis!.Results!.BaseReactions.Items.Where(x => x.Case == caseName),x=>x.StepType);
            if (baseReaction.Length == 0 || sum <= 0) { output.Add(Item(CheckStatus.NOT_VERIFIED, "Seismic base reaction envelope or positive Cvx denominator is unavailable.", caseName + "/" + direction)); continue; }
            var baseShear = baseReaction.Max(x=>Math.Abs(direction == "X" ? x.FX : x.FY));
            if (baseShear <= 0) continue; // The load case does not act in this orthogonal direction.
            double Shear(StoryForceResult r) => direction == "X" ? r.VX : r.VY;
            for (var i = 0; i < stories.Length; i++)
            {
                var current = forceRows.Where(x => x.Story == stories[i].Name).ToArray();
                var above = i + 1 < stories.Length ? forceRows.Where(x => x.Story == stories[i + 1].Name).ToArray() : [];
                var increments = current.Select(row =>
                {
                    if(i==stories.Length-1)return Math.Abs(Shear(row));
                    var match=above.Where(x=>string.Equals(x.StepType,row.StepType,StringComparison.OrdinalIgnoreCase)&&Math.Abs(x.StepNumber-row.StepNumber)<1e-8).ToArray();
                    return match.Length==1?Math.Abs(Shear(row)-Shear(match[0])):double.NaN;
                }).Where(double.IsFinite).ToArray();
                if (current.Length == 0 || i + 1 < stories.Length && increments.Length==0)
                { output.Add(Item(CheckStatus.NOT_VERIFIED, "Matching adjacent story-shear steps are required to derive the ETABS floor force increment.", stories[i].Name + "/" + caseName)); continue; }
                var wxhk = masses[i].Mass * Math.Pow((stories[i].Elevation - c.Input.Model.Stories!.BaseElevation) * MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length), k);
                var theoretical = wxhk / sum * baseShear;
                var actual = increments.DefaultIfEmpty(0).Max();
                if (theoretical <= 0) { output.Add(Item(CheckStatus.NOT_VERIFIED, "Theoretical floor force is zero and cannot be used as a deviation denominator.", stories[i].Name + "/" + caseName)); continue; }
                var deviation = Math.Abs(actual - theoretical) / theoretical;
                output.Add(new(deviation > .20 ? CheckStatus.WARNING : CheckStatus.PASS,
                    deviation > .20 ? "ETABS story-force increment differs from the Cvx hand distribution by more than 20%; review diaphragm, mass, and force output."
                        : "ETABS story-force increment is within 20% of the Cvx hand distribution.",
                    stories[i].Name + "/" + direction, "Story", stories[i].Name,
                    Actual: deviation, Limit: .20, Unit: "ratio",
                    Evidence: [Ev("Mass Summary by Story + Story Forces + BaseReact",
                        $"case={caseName}; direction={direction}; T1={t1:G6}s; k={k:G6}; Cvx={wxhk / sum:G6}; Ftheory={theoretical:G6}; FETABS={actual:G6}; deviation={deviation:P2}") ]));
            }
        }
        return output.Count > 0 ? output : [Rule.NotVerified("No seismic case names could be matched to story-force and base-reaction results.")];
    }

    private static Frame[] LowestColumns(InspectionContext c)
    {
        var storyData = c.Input.Model.Stories;
        if (storyData is null || storyData.Stories.Count == 0) return [];
        var lowest = storyData.Stories.Where(s => s.Elevation > storyData.BaseElevation + 1e-8).OrderBy(s => s.Elevation).FirstOrDefault();
        if (lowest is null) return [];
        return c.Input.Model.Frames?.Items.Where(f => (f.Story == lowest.Name || f.Story.Equals("Base", StringComparison.OrdinalIgnoreCase)) &&
            Math.Abs(f.Start.X - f.End.X) + Math.Abs(f.Start.Y - f.End.Y) <= 1e-6 && Math.Abs(f.Start.Z - f.End.Z) > 1e-6).ToArray() ?? [];
    }

    private static List<Story> AboveGroundStories(InspectionContext c) => c.Input.Model.Stories?.Stories
        .Where(s => s.Elevation > c.Input.Model.Stories.BaseElevation + 1e-8).ToList() ?? [];

    private static Dictionary<string, double> FloorAreaByStory(InspectionContext c)
    {
        var joints = c.Input.Model.Joints?.Items.ToDictionary(x => x.Id, x => x.Position, StringComparer.Ordinal) ?? new Dictionary<string, Coordinate>(StringComparer.Ordinal);
        var sections = c.Input.Model.AreaSections?.Items.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal) ?? new Dictionary<string, AreaSection>(StringComparer.Ordinal);
        var scale = MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
        return (c.Input.Model.Areas?.Items ?? []).Where(a => !a.IsOpening && IsFloor(a, sections))
            .Select(a => (a.Story, Area: PolygonArea(a.Joints, joints, scale))).Where(x => x.Area > 0)
            .GroupBy(x => x.Story, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(x => x.Area), StringComparer.Ordinal);
    }

    private static bool IsFloor(Area area, IReadOnlyDictionary<string, AreaSection> sections) =>
        area.Orientation.Contains("floor", StringComparison.OrdinalIgnoreCase) ||
        area.Section is { } section && sections.TryGetValue(section, out var data) &&
            (data.Kind.Contains("slab", StringComparison.OrdinalIgnoreCase) || data.Kind.Contains("floor", StringComparison.OrdinalIgnoreCase));

    private static double PolygonArea(IReadOnlyList<string> ids, IReadOnlyDictionary<string, Coordinate> joints, double scale)
    {
        if (ids.Count < 3 || ids.Any(id => !joints.ContainsKey(id))) return 0;
        var sum = 0.0;
        for (var i = 0; i < ids.Count; i++)
        {
            var a = joints[ids[i]]; var b = joints[ids[(i + 1) % ids.Count]];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) * .5 * scale * scale;
    }

    private static double BoundingFootprint(InspectionContext c)
    {
        var (x, y) = PlanDimensions(c); return x * y;
    }

    private static (double Bx, double By) PlanDimensions(InspectionContext c)
    {
        var points = c.Input.Model.Joints?.Items.Select(x => x.Position).ToArray() ?? [];
        if (points.Length == 0) return (0, 0);
        var scale = MetersPerUnit(c.Input.Model.Identity.DisplayUnits.Length);
        return ((points.Max(x => x.X) - points.Min(x => x.X)) * scale, (points.Max(x => x.Y) - points.Min(x => x.Y)) * scale);
    }

    private static (double Left, double Right, double Bottom, double Top)? NearestSpans(
        (Frame Frame, double X, double Y) center, (Frame Frame, double X, double Y)[] columns)
    {
        var others = columns.Where(x => x.Frame.Id != center.Frame.Id).ToArray();
        double Nearest(Func<(Frame Frame, double X, double Y), bool> filter, Func<(Frame Frame, double X, double Y), double> delta) =>
            others.Where(filter).Select(x => Math.Abs(delta(x))).DefaultIfEmpty(0).Min();
        var left = Nearest(x => x.X < center.X && Math.Abs(x.Y - center.Y) <= Math.Abs(x.X - center.X) * .25, x => center.X - x.X);
        var right = Nearest(x => x.X > center.X && Math.Abs(x.Y - center.Y) <= Math.Abs(x.X - center.X) * .25, x => x.X - center.X);
        var bottom = Nearest(x => x.Y < center.Y && Math.Abs(x.X - center.X) <= Math.Abs(x.Y - center.Y) * .25, x => center.Y - x.Y);
        var top = Nearest(x => x.Y > center.Y && Math.Abs(x.X - center.X) <= Math.Abs(x.Y - center.Y) * .25, x => x.Y - center.Y);
        if (left <= 0 || right <= 0 || bottom <= 0 || top <= 0) return null;
        return (left, right, bottom, top);
    }

    private static bool IsDeadCase(InspectionContext c, string name)
    {
        var loadCase = c.Input.Validation.LoadCases?.Items.SingleOrDefault(x => x.Name == name);
        if (loadCase?.Loads is { Count: > 0 } loads)
        {
            var patterns = c.Input.Validation.LoadPatterns?.Items.ToDictionary(x => x.Name, StringComparer.Ordinal);
            if (patterns is not null) return loads.All(x => patterns.TryGetValue(x.Name, out var pattern) && pattern.Type.Contains("dead", StringComparison.OrdinalIgnoreCase));
        }
        return name.Contains("dead", StringComparison.OrdinalIgnoreCase) || name.Equals("D", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGravityCase(InspectionContext c, string name) => IsDeadCase(c, name) ||
        name.Contains("live", StringComparison.OrdinalIgnoreCase) || name.Contains("gravity", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("service", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, "(^|[^A-Z])L([^A-Z]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsWindCase(InspectionContext c, string name)
    {
        var loadCase = c.Input.Validation.LoadCases?.Items.SingleOrDefault(x => x.Name == name);
        if (loadCase is { Type: "LinearStatic", Loads: { Count: > 0 } loads })
        {
            var patterns = c.Input.Validation.LoadPatterns?.Items.ToDictionary(x => x.Name, StringComparer.Ordinal);
            if (patterns is not null && loads.All(x => patterns.TryGetValue(x.Name, out var pattern) && pattern.Type.Contains("wind", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return name.Contains("wind", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(name, "(^|[^A-Z])W([^A-Z]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsSeismicCase(string name) => name.Contains("seis", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("quake", StringComparison.OrdinalIgnoreCase) || name.StartsWith("EQ", StringComparison.OrdinalIgnoreCase) ||
        System.Text.RegularExpressions.Regex.IsMatch(name, "(^|[^A-Z])E(X|Y)?([^A-Z]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static double SectionAreaSquareMeters(FrameSection section, double metersPerUnit)
    {
        if (section.Shape.Contains("circle", StringComparison.OrdinalIgnoreCase) && section.Diameter is { } d && d > 0)
            return Math.PI * Math.Pow(d * metersPerUnit, 2) / 4;
        if (section.Width is { } width && section.Depth is { } depth && width > 0 && depth > 0)
            return width * depth * metersPerUnit * metersPerUnit;
        return 0;
    }

    private static double MetersPerUnit(string unit) => GeometryMath.MetersPerUnit(unit);

    private static double ForceToKn(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "kn" => 1, "n" => .001, "mn" => 1000, "kip" or "kips" => 4.4482216152605,
        "lb" or "lbs" => .0044482216152605, "tf" or "tonf" => 9.80665,
        _ => throw new InvalidOperationException($"Unsupported force unit '{unit}' for kN sanity checks.")
    };

    private static double ForceToN(string unit) => ForceToKn(unit) * 1000;

    private static Observation Item(CheckStatus status, string message, string? id = null, string? story = null) =>
        new(status, message, id, ElementType: "Frame", Story: story);

    private static Evidence Ev(string source, string value) => new("Sanity-check calculation", source, value);
}
