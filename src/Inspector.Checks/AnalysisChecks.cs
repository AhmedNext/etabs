
/// <summary>
/// Phase 3 analysis checks. Inventory checks report successful extraction; engineering conclusions still require criteria.
/// </summary>
public static class AnalysisChecks
{
    // COL-014 applicability gate, required before implementing strong-column/weak-beam calculations:
    // read `seismic.system` from the preserved project.asce7-16.json settings (`Settings.Seismic`).
    // Evaluate the check only for a Special Moment Frame system. Return NOT_APPLICABLE for
    // Special reinforced concrete shear walls; missing or unrecognized systems remain NOT_VERIFIED.
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("ANA-001", "Analysis result availability",
            "Surface ETABS result-extraction errors and confirm that at least one selected result row is available. Availability only; freshness and engineering validity are not established.",
            [], Availability),
        Rule.Create("ANA-002", "Analysis case status",
            "Map Analyze.GetCaseStatus codes (1 Not run, 2 Could not start, 3 Not finished, 4 Finished). Finished states only that ETABS reports completion, not that results are current or valid.",
            ["AnalysisStatus"], CaseStatus),
        Rule.Create("ANA-007", "Story drift",
            "Compare absolute ETABS story drift with a confirmed project drift-ratio limit for the confirmed case names and directions.",
            ["AnalysisResults"], Drift),
        Rule.Create("ANA-008", "Base reaction availability",
            "Report the extracted base reaction inventory. Global equilibrium and reaction consistency are not verified without an applied-load aggregate and directional conventions.",
            ["AnalysisResults"], c => ExtendedPhase3Checks.Evaluate("ANA-008", c)),
        ExtendedPhase3Checks.Create("ANA-003", "Analysis warning and failure indicators", "Review typed ETABS Analysis Messages; distinguish information, warnings and errors.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("ANA-004", "Suspicious structural instability", "Screen modal eigenvalues for nonpositive values; no general stability conclusion.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("ANA-009", "Concrete dead-load equilibrium screening", "Compare live Dead-case vertical base reaction with concrete self-weight calculated from ETABS takeoff volumes and extracted material unit weights using a 2% tolerance.", ["AnalysisResults", "MaterialTakeoffs", "Materials", "LoadCases", "LoadPatterns"]),
        ExtendedPhase3Checks.Create("ANA-012", "P-Delta configuration and results", "Inspect nonlinear-case options and ETABS stiffness/gravity ratios; disclose unproven result lineage.", ["AnalysisDefinitions", "AnalysisResults"]),
        ExtendedPhase3Checks.Create("ANA-013", "Second-order stability indicators", "Evaluate static stability coefficient using matched story forces, elastic drift and ETABS geometric-nonlinearity settings.", ["AnalysisDefinitions", "AnalysisResults", "Stories", "LoadCases", "AnalysisStatus"]),
        ExtendedPhase3Checks.Create("ANA-014", "Stiffness modifier consistency", "Inspect object/section modifiers and combined reduction anomalies.", ["AnalysisResults"]),
        ExtendedPhase3Checks.Create("ANA-015", "Analysis/design result compatibility", "Compare design code and resolve design combinations; disclose unproven design freshness.", ["AnalysisResults"])
    ];





    private static IReadOnlyList<Observation> Availability(InspectionContext c)
    {
        var analysis = c.Input.Analysis;
        if (analysis is null) return [Rule.NotVerified("Analysis results were not read for this inspection.")];
        List<Observation> outcomes = [];
        foreach (var issue in analysis.Issues.Concat(analysis.Results?.Issues ?? []).Concat(analysis.Results?.Extended?.Issues ?? []))
            outcomes.Add(new(CheckStatus.ERROR, issue.Message, issue.Component, "AnalysisResult",
                Evidence: [new("ETABS API operation", issue.Operation ?? "analysis extraction",
                    issue.ReturnCode?.ToString(CultureInfo.InvariantCulture))]));
        if (analysis.Results is null)
        {
            outcomes.Add(Rule.NotVerified("Analysis results could not be extracted; no availability conclusion is available."));
            return outcomes;
        }
        var count = analysis.Results.StoryDrifts.Items.Count + analysis.Results.ModalParticipation.Items.Count +
            analysis.Results.BaseReactions.Items.Count;
        outcomes.Add(count > 0
            ? new(CheckStatus.PASS, "Inventory successfully extracted and available for review.",
                Actual: count, Unit: "rows", Evidence: [new("Extracted result rows", "cAnalysisResults and result tables",
                    count.ToString(CultureInfo.InvariantCulture))])
            : Rule.NotVerified("No analysis result rows were returned for ETABS's current output selection."));
        return outcomes;
    }

    private static IReadOnlyList<Observation> CaseStatus(InspectionContext c)
    {
        var cases = c.Input.Analysis!.Status!.Cases;
        if (cases.Items.Count == 0)
            return [Rule.NotVerified("ETABS returned no analysis case statuses.")];
        List<Observation> outcomes = [];
        foreach (var item in cases.Items.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            var (status, title) = item.State switch
            {
                AnalysisCaseState.Finished => (CheckStatus.PASS, "ETABS reports analysis finished for this case."),
                AnalysisCaseState.NotRun => (CheckStatus.NOT_APPLICABLE, "Analysis has not been run for this case."),
                AnalysisCaseState.CouldNotStart => (CheckStatus.FAIL, "ETABS reports that analysis could not start for this case."),
                AnalysisCaseState.NotFinished => (CheckStatus.FAIL, "ETABS reports that analysis did not finish for this case."),
                _ => (CheckStatus.NOT_VERIFIED, $"ETABS returned an undocumented analysis status ({item.RawStatus}); it is not interpreted as a pass.")
            };
            outcomes.Add(new(status, title, item.Name, "AnalysisCase", Combination: item.Name,
                Actual: item.RawStatus, Limit: 4, Unit: "case status code",
                Evidence: [new("Analyze.GetCaseStatus", "cAnalyze.GetCaseStatus", item.RawStatus.ToString(CultureInfo.InvariantCulture))]));
        }
        return outcomes;
    }

    private static IReadOnlyList<Observation> Drift(InspectionContext c)
    {
        var criteria = c.Configuration.Settings.Analysis;
        if (!c.Configuration.Settings.Confirmed || criteria?.DriftRatioLimit is null ||
            criteria.DriftCases is not { Count: > 0 } || criteria.DriftDirections is not { Count: > 0 })
            return [Rule.NotVerified("A confirmed analysis.driftRatioLimit, driftCases, and driftDirections are required; no drift limit is assumed.")];
        var rows = Rows(c, criteria.DriftCases, criteria.DriftDirections);
        if (rows.Length == 0)
            return [Rule.NotVerified("No extracted ETABS story drift rows match the confirmed cases and directions; select them for output and rerun.")];
        var limit = criteria.DriftRatioLimit.Value;
        return rows.Select(row =>
        {
            var actual = Math.Abs(row.Drift);
            var pass = actual <= limit;
            return new Observation(pass ? CheckStatus.PASS : CheckStatus.FAIL,
                pass ? "Extracted story drift is within the confirmed project limit." :
                    "Extracted story drift exceeds the confirmed project limit.",
                $"{row.Story}/{row.Direction}/{row.Label}", "StoryDrift", row.Story,
                Actual: actual, Limit: limit, Unit: "ratio", Combination: Combination(row),
                Evidence: [new("ETABS story drift output point", "Results.StoryDrifts / Story Drifts table",
                    $"position X/Y/Z={row.X.ToString("G6", CultureInfo.InvariantCulture)}/{row.Y.ToString("G6", CultureInfo.InvariantCulture)}/{row.Z.ToString("G6", CultureInfo.InvariantCulture)}; direction={row.Direction}")]);
        }).ToArray();
    }

    private static IReadOnlyList<Observation> BaseReactions(InspectionContext c)
    {
        var rows = c.Input.Analysis!.Results!.BaseReactions.Items;
        return [new(CheckStatus.NOT_VERIFIED, rows.Count > 0
            ? "Base reaction rows were extracted; consistency with applied loads is not verified."
            : "No base reaction rows are available for consistency review.",
            Evidence: [new("Base reaction rows", "Results.BaseReact / Base Reactions table",
                rows.Count.ToString(CultureInfo.InvariantCulture))])];
    }

    private static StoryDriftResult[] Rows(InspectionContext c, IReadOnlyList<string> cases, IReadOnlyList<string> directions) =>
        c.Input.Analysis!.Results!.StoryDrifts.Items.Where(r => cases.Contains(r.Case, StringComparer.Ordinal) &&
            directions.Contains(r.Direction, StringComparer.Ordinal))
            .OrderBy(r => r.Case, StringComparer.Ordinal).ThenBy(r => r.Story, StringComparer.Ordinal)
            .ThenBy(r => r.Direction, StringComparer.Ordinal).ThenBy(r => r.Label, StringComparer.Ordinal).ToArray();

    private static string Combination(StoryDriftResult row) =>
        $"{row.Case}/{row.StepType}/{row.StepNumber.ToString("G17", CultureInfo.InvariantCulture)}";
}


public static partial class ExtendedPhase3Checks
{
    private static IReadOnlyList<Observation> PDelta(InspectionContext c)
    {
        var definitions=c.Input.Analysis?.Definitions?.GeometricNonlinearity.Items??throw new Missing("Geometric nonlinearity definitions are unavailable.");
        var t=Table(c,"Stiffness Gravity Ratios","D/(G/H)");
        return [O(CheckStatus.PASS,"Inventory successfully extracted and available for review.",actual:definitions.Count(x=>x.RawOption>0),unit:"nonlinear cases",evidence:[Ev("StaticNonlinear.GetGeometricNonlinearity",string.Join(";",definitions.Select(x=>$"{x.Case}={x.RawOption}"))),Ev(t.Key,$"{t.Rows.Count} rows; minimum D/(G/H)={t.Rows.Min(x=>N(x,"D/(G/H)")):G17}")])];
    }
    private static IReadOnlyList<Observation> Modifiers(InspectionContext c)
    {
        var modifiers=E(c).Modifiers;if(modifiers.Count==0)throw new Missing("Object and section stiffness modifiers are unavailable.");
        List<Observation> output=[];
        foreach(var m in modifiers.Where(m=>m.Values.Any(x=>x<=0||x>1)))output.Add(O(CheckStatus.WARNING,"Nonpositive or amplified object/section modifier requires engineering review; zero can be intentional.",m.Kind+"/"+m.Name,evidence:[Ev(m.Kind+".GetModifiers",string.Join(",",m.Values))]));
        var doubleReduced=new List<string>();var dict=modifiers.ToDictionary(x=>(x.Kind,x.Name));
        foreach(var f in c.Input.Model.Frames?.Items??[])
            if(dict.TryGetValue(("FrameObj",f.Id),out var obj)&&dict.TryGetValue(("PropFrame",f.Section),out var prop)&&Enumerable.Range(0,Math.Min(obj.Values.Count,prop.Values.Count)).Any(i=>obj.Values[i]<1&&prop.Values[i]<1))doubleReduced.Add(f.Id);
        if(doubleReduced.Count>0)output.Add(O(CheckStatus.WARNING,"Both object and section modifiers reduce at least one corresponding frame property; verify combined effective stiffness.","combined",actual:doubleReduced.Count,unit:"frames",evidence:[Ev("FrameObj/PropFrame.GetModifiers",string.Join(",",doubleReduced))]));
        output.Add(O(CheckStatus.PASS,"Inventory successfully extracted and available for review.",actual:modifiers.Count,unit:"modifier vectors",evidence:[Ev("Object and section modifier getters","Frame: A,As2,As3,J,I22,I33,mass,weight; area: f11,f22,f12,m11,m22,m12,v13,v23,mass,weight")]));return output;
    }
    private static IReadOnlyList<Observation> DesignCompatibility(InspectionContext c)
    {
        var e=E(c);var expected=Ref(c).ExpectedConcreteCode??throw new Missing("Expected concrete code is required for compatibility review.");List<Observation> output=[];
        if(e.ConcreteCode is null)throw new Missing("Concrete design code was not extracted.");
        output.Add(O(e.ConcreteCode==expected?CheckStatus.PASS:CheckStatus.FAIL,"Concrete design code compared with approved code.","code",evidence:[Ev("DesignConcrete.GetCode",$"actual={e.ConcreteCode}; expected={expected}")]));
        output.Add(O(e.ConcreteResultsAvailable==false?CheckStatus.NOT_APPLICABLE:CheckStatus.NOT_VERIFIED,e.ConcreteResultsAvailable==true?"Concrete results exist; freshness and compatibility with the current analysis are not proven by availability.":e.ConcreteResultsAvailable==false?"Concrete design has not been run.":"Concrete design availability could not be read.","results"));
        var t=Table(c,"Concrete Frame Design Load Combination Data","ComboName");
        var combinations=c.Input.Validation.Combinations?.Items.ToDictionary(x=>x.Name)??throw new Missing("Load combinations are unavailable.");
        foreach(var name in t.Rows.Select(x=>S(x,"ComboName")).Distinct())output.Add(O(combinations.ContainsKey(name)?CheckStatus.PASS:CheckStatus.FAIL,"Design combination reference resolves to a current model combination.","combination/"+name));
        return output;
    }
    private static IReadOnlyList<Observation> Configuration(InspectionContext c)
    {
        var t=Table(c,"Load Pattern Definitions - Auto Seismic - ASCE 7-16","Name","SiteClass","R","Cd","I","Ss","S1","SDS","SD1");
        var meta=c.Configuration.Settings.Seismic??throw new Missing("Supplied seismic metadata is unavailable.");
        var site=Asce716Seismic.RequiredString(meta,"siteClass");var ss=Asce716Seismic.RequiredNumber(meta,"ss");var s1=Asce716Seismic.RequiredNumber(meta,"s1");
        var r=Asce716Seismic.RequiredNumber(meta,"r");var cd=Asce716Seismic.Factor(c.Configuration.Settings,"cd");var ie=Asce716Seismic.Factor(c.Configuration.Settings,"ie");
        var calculatedHazard=Asce716Seismic.CalculateHazard(site,ss,s1);List<Observation> output=[];
        var confirmedHazard=meta.TryGetProperty("siteSpecificHazardConfirmed",out var hazardConfirmation)&&hazardConfirmation.ValueKind==System.Text.Json.JsonValueKind.True;
        foreach(var row in t.Rows)
        {
            var id=S(row,"Name");
            foreach(var pair in new[]{("Ss","ss"),("S1","s1")})
            {
                var expected=pair.Item2=="ss"?ss:s1;
                var actual=N(row,pair.Item1);var matches=Math.Abs(actual-expected)<=1e-6*Math.Max(1,Math.Abs(expected));
                output.Add(Compare(pair.Item1,id,actual,expected,"g",true));
            }
            output.Add(Compare("R",id,N(row,"R"),r,null,false));output.Add(Compare("Cd",id,N(row,"Cd"),cd,null,false));output.Add(Compare("I",id,N(row,"I"),ie,null,false));
            var actualSite=S(row,"SiteClass");var siteMatches=actualSite.Equals(site,StringComparison.OrdinalIgnoreCase);
            output.Add(O(siteMatches?CheckStatus.PASS:confirmedHazard?CheckStatus.FAIL:CheckStatus.WARNING,
                siteMatches?(confirmedHazard?"ETABS site class matches the configured site-confirmed criterion.":"ETABS site class matches the configured assumption; site-specific geotechnical certification is pending."):confirmedHazard?"ETABS site class differs from the site-confirmed criterion.":"ETABS site class differs from the configured assumption; verify against geotechnical data.",id+"/SiteClass",evidence:[Ev(t.Key,$"actual={actualSite}; configured={site}; siteSpecificHazardConfirmed={confirmedHazard};{(!confirmedHazard?" site-specific hazard certification is pending;":"")} geotechnical basis not independently certified")]));
            output.Add(Compare("SDS",id,N(row,"SDS"),calculatedHazard.Sds,"g",true));output.Add(Compare("SD1",id,N(row,"SD1"),calculatedHazard.Sd1,"g",true));
        }
        return output.Count>0?output:[Rule.NotVerified("No auto-seismic ASCE 7-16 rows were extracted.")];

        Observation Compare(string field,string id,double actual,double expected,string? unit,bool hazardValue)
        {
            var matches=Math.Abs(actual-expected)<=1e-6*Math.Max(1,Math.Abs(expected));
            var confirmed=c.Configuration.Settings.Confirmed&&(!hazardValue||confirmedHazard);
            var status=matches?CheckStatus.PASS:confirmed?CheckStatus.FAIL:CheckStatus.WARNING;
            var note=hazardValue&&!confirmedHazard?" site-specific hazard certification is pending.":!c.Configuration.Settings.Confirmed?" Configuration is not confirmed.":"";
            return O(status,$"ETABS {field} compared with the configured or dynamically calculated ASCE 7-16 value.{note}",id+"/"+field,actual,expected,unit,Ev(t.Key,$"field={field}; configured hazard certification={confirmedHazard};{(hazardValue&&!confirmedHazard?" site-specific hazard certification is pending;":"")} Fa={calculatedHazard.Fa:G17}; Fv={calculatedHazard.Fv:G17}"));
        }
    }
    private static IReadOnlyDictionary<string,string> LoadRow(InspectionContext c,AnalysisEvidenceTable t,SeismicAxis axis)
    {
        var lc=c.Input.Validation.LoadCases?.Items.SingleOrDefault(x=>x.Name==axis.ElasticCase);
        var pattern=lc?.Loads is {Count:1} loads && loads[0].Type=="Load"&&loads[0].Factor==1?loads[0].Name:null;
        var rows=t.Rows.Where(x=>S(x,"Name")==pattern).ToArray();return rows.Length==1?rows[0]:throw new Missing($"{axis.ElasticCase} must map to one unit-factor ASCE 7-16 seismic pattern.");
    }
}


public static partial class ExtendedPhase3Checks
{
    private sealed class Missing(string message):Exception(message);
    private static ExtendedAnalysisEvidence E(InspectionContext c) => c.Input.Analysis?.Results?.Extended ?? throw new Missing("Extended analysis evidence was not extracted.");
    private static Phase3ReviewSettings Ref(InspectionContext c) => c.Configuration.Settings.Confirmed && c.Configuration.Settings.Phase3Review is {ReferencesConfirmed:true} r && !string.IsNullOrWhiteSpace(r.ReferenceSource)
        ? r : throw new Missing("Approved phase3Review reference inputs and their source are required for this calculation.");
    private static AnalysisEvidenceTable Table(InspectionContext c,string key,params string[] fields)
    {
        var t=E(c).Tables.SingleOrDefault(x=>x.Key==key) ?? throw new Missing($"Required table unavailable: {key}.");
        if(t.UnavailableReason is not null)throw new Missing($"{key}: {t.UnavailableReason}");
        if(fields.Except(t.Fields).Any())throw new Missing($"{key} lacks required fields: {string.Join(",",fields.Except(t.Fields))}.");
        if(t.Rows.Count==0)throw new Missing($"{key} has no rows for the current output selection.");return t;
    }
    private static double N(IReadOnlyDictionary<string,string> r,string key) => r.TryGetValue(key,out var s)&&double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)&&double.IsFinite(n)?n:throw new Missing($"Missing, malformed or nonfinite field {key}.");
    private static string S(IReadOnlyDictionary<string,string> r,string key)=>r.TryGetValue(key,out var v)?v:throw new Missing("Missing field "+key);
    private static Evidence Ev(string source,string value)=>new("Calculation evidence",source,value);
    private static Observation O(CheckStatus status,string text,string? id=null,double? actual=null,double? limit=null,string? unit=null,params Evidence[] evidence)=>new(status,text,id,Actual:actual,Limit:limit,Unit:unit,Evidence:evidence);
    private static IReadOnlyList<SeismicAxis> Axes(InspectionContext c)=>c.Configuration.Settings.Confirmed && c.Configuration.Settings.SeismicVerification is {} s?s.Axes:throw new Missing("Confirmed X/Y seismic case mappings are required.");
    private static IReadOnlyList<Observation> Nonempty(List<Observation> rows,string missing)=>rows.Count>0?rows:[Rule.NotVerified(missing)];
    public static IInspectionCheck Create(string id,string title,string method,string[] dependencies)=>Rule.Create(id,title,method,dependencies,c=>Evaluate(id,c));
    public static IReadOnlyList<Observation> Evaluate(string id,InspectionContext c)
    {
        try {return id switch {
            "ANA-003"=>Messages(c),"ANA-004"=>Instability(c),
            "ANA-008"=>BaseReactionInventory(c),"ANA-009"=>Equilibrium(c),
            "ANA-012"=>PDelta(c),"ANA-013"=>SeismicCriteriaChecks.Stability(c),"ANA-014"=>Modifiers(c),"ANA-015"=>DesignCompatibility(c),
            "SEIS-001"=>Configuration(c),"SEIS-002"=>Mass(c),"SEIS-003"=>Period(c),"SEIS-005"=>ModeShapes(c),
            "SEIS-007"=>Torsion(c),"SEIS-008"=>SoftStory(c),"SEIS-010"=>Vertical(c),
            "SEIS-011"=>Plan(c),"SEIS-012"=>ShearDistribution(c),"SEIS-013"=>BaseShear(c),"SEIS-016"=>AccidentalTorsion(c),
            "SEIS-018"=>Overturning(c),"SEIS-019"=>Diaphragms(c),
            _=>throw new InvalidOperationException("Unknown Phase 3 check "+id)};}
        catch(Missing ex){return [Rule.NotVerified(ex.Message)];}
        catch(InvalidOperationException ex){return [Rule.NotVerified(ex.Message)];}
    }
    private static IReadOnlyList<Observation> Messages(InspectionContext c)
    {
        var t=Table(c,"Analysis Messages","Type","Message");
        var rows=t.Rows.Where(r=>!S(r,"Type").Equals("Information",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(rows.Length==0)return [O(CheckStatus.PASS,"No warning/error entries occur in the extracted Analysis Messages table; this is not a solver-log completeness or freshness conclusion.",evidence:[Ev(t.Key,$"{t.Rows.Count} entries")])];
        return rows.Select((r,i)=>O(S(r,"Type").Contains("error",StringComparison.OrdinalIgnoreCase)?CheckStatus.FAIL:CheckStatus.WARNING,
            S(r,"Message"),$"message/{i}",evidence:[Ev(t.Key,string.Join(";",r.Select(p=>$"{p.Key}={p.Value}")))])).ToArray();
    }
    private static IReadOnlyList<Observation> BaseReactionInventory(InspectionContext c)
    {
        var rows=c.Input.Analysis?.Results?.BaseReactions.Items??throw new Missing("Base reaction results are unavailable.");
        return [O(rows.Count>0?CheckStatus.PASS:CheckStatus.NOT_VERIFIED,rows.Count>0?"Inventory successfully extracted and available for review.":"No base reaction rows are available.",actual:rows.Count,unit:"reaction rows",evidence:[Ev("Results.BaseReact",$"{rows.Count} rows; result freshness is not established")])];
    }
    private static IReadOnlyList<Observation> Instability(InspectionContext c)
    {
        var t=Table(c,"Modal Periods And Frequencies","Eigenvalue","Period");
        var bad=t.Rows.Where(r=>N(r,"Eigenvalue")<=0||N(r,"Period")<=0).ToArray();
        return [O(bad.Length>0?CheckStatus.WARNING:CheckStatus.PASS,
            bad.Length>0?"Nonpositive modal eigenvalue/period indicates a possible mechanism; review solver and mode shapes.":"Inventory successfully extracted and available for review.",actual:bad.Length,unit:"modes",evidence:[Ev(t.Key,$"{t.Rows.Count} rows examined")])];
    }
    private static IReadOnlyList<Observation> JointDisplacement(InspectionContext c)
    {
        var r=Ref(c);if(r.MaximumJointDisplacement is not {} limit||r.DisplacementCases.Count==0||r.DisplacementDirections.Count==0)throw new Missing("Approved maximumJointDisplacement, displacementCases and displacementDirections are required in phase3Review.");
        var t=Table(c,"Joint Displacements","OutputCase","UniqueName","Ux","Uy","Uz");List<Observation> output=[];
        foreach(var name in r.DisplacementCases)foreach(var direction in r.DisplacementDirections)
        {
            var rows=t.Rows.Where(x=>S(x,"OutputCase")==name).ToArray();
            if(rows.Length==0){output.Add(O(CheckStatus.NOT_VERIFIED,"No joint displacement rows for requested case.",name+"/"+direction));continue;}
            var max=rows.MaxBy(x=>Math.Abs(N(x,"U"+direction.ToLowerInvariant())))!;var actual=Math.Abs(N(max,"U"+direction.ToLowerInvariant()));
            output.Add(O(actual<=limit?CheckStatus.PASS:CheckStatus.FAIL,"Maximum absolute joint translation compared with approved display-unit limit.",name+"/"+direction,actual,limit,c.Input.Model.Identity.DisplayUnits.Length,Ev(t.Key,$"joint={S(max,"UniqueName")}; rows={rows.Length}; direction={direction}")));
        }return output;
    }
    private static IReadOnlyList<Observation> Equilibrium(InspectionContext c)
    {
        var takeoffs=c.Input.MaterialTakeoffs?.Items??c.Input.Validation.MaterialTakeoffs?.Items??throw new Missing("Material takeoff is unavailable for dead-weight screening.");
        var materials=c.Input.Model.Materials?.Items??throw new Missing("Material definitions and unit weights are unavailable.");
        var concrete=materials.Where(m=>m.Type.Contains("concrete",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(concrete.Length==0)throw new Missing("No concrete materials with extracted unit weights are available.");
        var concreteNames=concrete.Select(m=>m.Name).ToHashSet(StringComparer.Ordinal);
        var rows=takeoffs.Where(t=>concreteNames.Contains(t.Material)).ToArray();
        if(rows.Length==0)throw new Missing("No concrete material takeoff quantities map to extracted materials.");
        var weights=new List<double>();
        foreach(var takeoff in rows)
        {
            var matches=concrete.Where(m=>m.Name==takeoff.Material).ToArray();
            if(matches.Length!=1||!double.IsFinite(matches[0].UnitWeight)||matches[0].UnitWeight<=0||!double.IsFinite(takeoff.TotalVolume)||takeoff.TotalVolume<0)
                throw new Missing($"Concrete unit weight or takeoff volume is missing or invalid for material {takeoff.Material}.");
            weights.Add(takeoff.TotalVolume*matches[0].UnitWeight);
        }
        var modeledConcreteWeight=weights.Sum();if(!double.IsFinite(modeledConcreteWeight)||modeledConcreteWeight<=0)throw new Missing("Calculated concrete self-weight is not positive and finite.");
        var patterns=c.Input.Validation.LoadPatterns?.Items.ToDictionary(x=>x.Name)??throw new Missing("Load pattern definitions are unavailable.");
        var cases=c.Input.Validation.LoadCases?.Items.Where(x=>x.Type=="LinearStatic"&&x.Loads is {Count:>0}&&x.Loads.All(load=>load.Type=="Load"&&patterns.TryGetValue(load.Name,out var pattern)&&pattern.Type.Contains("dead",StringComparison.OrdinalIgnoreCase))).ToArray()??[];
        var reactions=c.Input.Analysis?.Results?.BaseReactions??throw new Missing("Base reaction results are unavailable.");List<Observation> output=[];
        foreach(var loadCase in cases)
        {
            var selfWeightFactor=loadCase.Loads!.Sum(load=>load.Factor*patterns[load.Name].SelfWeightMultiplier);
            if(!double.IsFinite(selfWeightFactor)||selfWeightFactor<=0)continue;
            var caseRows=reactions.Items.Where(x=>x.Case==loadCase.Name&&x.StepType=="Single Value").ToArray();
            if(caseRows.Length!=1){output.Add(O(CheckStatus.NOT_VERIFIED,"A unique static base reaction is required for the live-model dead-load case.",loadCase.Name));continue;}
            var expected=modeledConcreteWeight*selfWeightFactor;var actual=Math.Abs(caseRows[0].FZ);var tolerance=.02*expected;
            output.Add(O(Math.Abs(actual-expected)<=tolerance?CheckStatus.PASS:CheckStatus.FAIL,
                "Dead-load vertical reaction is compared with concrete self-weight calculated from extracted takeoff volumes and material unit weights (2% screening tolerance). The check covers modeled concrete only; superimposed dead load, non-concrete materials, and other applied loads are outside this comparison.",
                loadCase.Name+"/FZ",Math.Abs(actual-expected),tolerance,c.Input.Model.Identity.DisplayUnits.Force,
                Ev("Material List 1 - By Object Type + Material Properties + Results.BaseReact",$"case={loadCase.Name}; dead-pattern self-weight factor={selfWeightFactor:G17}; modeled concrete weight={modeledConcreteWeight:G17}; expected={expected:G17}; |Fz|={actual:G17}; tolerance={tolerance:G17}; materials={string.Join(",",rows.Select(x=>$"{x.Material}:{x.TotalVolume:G17}*{materials.Single(m=>m.Name==x.Material).UnitWeight:G17}"))}")));
        }
        return output.Count>0?output:[Rule.NotVerified("No extracted linear-static case uses only dead-type load patterns with a positive self-weight multiplier; concrete-only equilibrium cannot be compared safely.")];
    }
}


