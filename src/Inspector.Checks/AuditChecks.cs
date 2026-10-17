public static class AuditChecks
{
    private const double Tolerance = 1e-9;

    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("AUD-001", "ACI 318-19 cracked section modifier compliance",
            "Engineering screening against the requested stiffness-modifier ranges. Effective modifiers are object modifier × section-property modifier; values do not replace engineer review of analysis assumptions.",
            ["Modifiers", "Frames", "Areas", "FrameSections", "AreaSections"], CrackedModifiers),
        Rule.Create("AUD-002", "Response spectrum dynamic base shear 100% scaling audit",
            "ASCE 7-16 12.9.1.4 screening: compare directional peak response-spectrum base reaction Vt with mapped ELF base reaction V; report the additional multiplier required to reach 100%.",
            ["AnalysisResults", "AnalysisDefinitions", "LoadCases", "AnalysisStatus"], BaseShearScaling),
        Rule.Create("AUD-003", "Accidental torsional amplification enforcement audit",
            "ASCE 7-16 12.8.4.3 screening: where extracted diaphragm max/average drift ratio R exceeds 1.2, Ax=min(3,(R/1.2)^2) and the expected accidental eccentricity is 0.05 Ax.",
            ["AnalysisResults", "AnalysisDefinitions"], TorsionalAmplification),
        Rule.Create("AUD-004", "P-Delta solver inclusion on lateral combinations",
            "Screen story stability theta=Px*|drift ratio|/|story shear|. When theta>0.10, identify whether the lateral combination dependency graph includes an active geometric-nonlinearity case; this does not validate nonlinear convergence.",
            ["AnalysisDefinitions", "AnalysisResults", "Stories", "LoadCases", "Combinations", "AnalysisStatus"], PDeltaLineage)
    ];

    private static IReadOnlyList<Observation> CrackedModifiers(InspectionContext c)
    {
        var ext = c.Input.Analysis?.Results?.Extended;
        if (ext is null) return [Rule.NotVerified("Extended modifier evidence is unavailable.")];
        var frameObjects = Modifiers(ext, "FrameObj");
        var frameProperties = Modifiers(ext, "PropFrame");
        var areaObjects = Modifiers(ext, "AreaObj");
        var areaProperties = Modifiers(ext, "PropArea");
        List<Observation> output = [];
        foreach (var frame in c.Input.Model.Frames?.Items ?? [])
        {
            var vertical = Math.Abs(frame.End.Z-frame.Start.Z) > Math.Max(Math.Abs(frame.End.X-frame.Start.X), Math.Abs(frame.End.Y-frame.Start.Y));
            var category = vertical ? "column" : "beam";
            var range = vertical ? (.65, .75) : (.30, .40);
            if (!frameObjects.TryGetValue(frame.Id, out var obj) || obj.Values.Count < 6 || !frameProperties.TryGetValue(frame.Section, out var prop) || prop.Values.Count < 6)
            { output.Add(new(CheckStatus.NOT_VERIFIED, $"Object and section-property bending modifiers are required to screen this {category}.", frame.Id, "Frame", frame.Story)); continue; }
            AddAxes(frame.Id, "Frame", frame.Story, category, range, [4,5], ["I22", "I33"], obj, prop);
        }
        var sectionMap = c.Input.Model.AreaSections?.Items.GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string,AreaSection>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in c.Input.Model.Areas?.Items ?? [])
        {
            if (area.IsOpening) continue;
            if (area.Section is null || !areaObjects.TryGetValue(area.Id, out var obj) || obj.Values.Count < 6 || !areaProperties.TryGetValue(area.Section, out var prop) || prop.Values.Count < 6)
            { output.Add(new(CheckStatus.NOT_VERIFIED, "Object and section-property area modifiers are required to screen this area.", area.Id, "Area", area.Story)); continue; }
            var isWall = area.Orientation.Contains("wall", StringComparison.OrdinalIgnoreCase) ||
                sectionMap.GetValueOrDefault(area.Section)?.Kind.Contains("wall", StringComparison.OrdinalIgnoreCase) == true;
            if (isWall)
            {
                AddAxes(area.Id, "Area", area.Story, "shear wall bending", (.30,.40), [3,4,5], ["m11","m22","m12"], obj, prop, alternateRange:(.60,.75));
                AddAxes(area.Id, "Area", area.Story, "shear wall membrane", (.30,.40), [0,1,2], ["f11","f22","f12"], obj, prop, alternateRange:(.60,.75));
            }
            else AddAxes(area.Id, "Area", area.Story, "slab", (.20,.30), [3,4,5], ["m11","m22","m12"], obj, prop, .25);
        }
        return output.Count == 0 ? [Rule.NotVerified("No frame or structural area modifier rows were available to screen.")] : output;

        void AddAxes(string id, string type, string? story, string category, (double Min,double Max) range,
            int[] indices, string[] labels, ModifierEvidence obj, ModifierEvidence prop, double? nominal = null,
            (double Min,double Max)? alternateRange = null)
        {
            for (var n=0;n<indices.Length;n++)
            {
                var index=indices[n]; var a=obj.Values[index]; var b=prop.Values[index]; var effective=a*b;
                var evidence=Ev("ETABS object and section modifiers", $"{labels[n]}: object={a:G8}; property={b:G8}; effective product={effective:G8}; expected [{range.Min:G3},{range.Max:G3}]"+(nominal is null?"":$" around {nominal:G3}"));
                if (!double.IsFinite(a)||!double.IsFinite(b)||!double.IsFinite(effective))
                { output.Add(new(CheckStatus.NOT_VERIFIED, $"{category} {labels[n]} modifier is non-finite.", id+"/"+labels[n], type, story, Actual:effective, Unit:"modifier", Evidence:[evidence])); continue; }
                if (a < 1-Tolerance && b < 1-Tolerance)
                    output.Add(new(CheckStatus.WARNING, $"Double-reduced {category}: both object and section-property {labels[n]} modifiers are below 1.0; confirm the product is intentional.", id+"/double/"+labels[n], type, story, Actual:effective, Limit:range.Min, Unit:"modifier", Evidence:[evidence]));
                var expected = InRange(effective,range.Min,range.Max) || alternateRange is { } alternate && InRange(effective,alternate.Min,alternate.Max);
                // ETABS commonly retains m12 twisting and f12 in-plane shear at 1.0 for walls.
                var standardWallShear = (category == "shear wall membrane" && labels[n] == "f12" || category == "shear wall bending" && labels[n] == "m12") && InRange(effective,.90,1.10);
                var status = effective > .90 && !standardWallShear ? CheckStatus.WARNING : expected || standardWallShear ? CheckStatus.PASS : CheckStatus.WARNING;
                var message = effective > .90 && !standardWallShear
                    ? category switch { "beam"=>"Beam modeled with uncracked stiffness (I = 1.0). ACI 318-19 Section 6.6.3.1.1 requires 0.35 Ig for lateral analysis; drift is severely underestimated.", "column"=>"Column modeled with uncracked stiffness (I = 1.0). ACI 318-19 Section 6.6.3.1.1 requires 0.70 Ig.", _=>$"{category} {labels[n]} modifier is near 1.0; the requested cracked-stiffness screening range is not met." }
                    : status == CheckStatus.PASS ? standardWallShear ? $"Shear-wall {labels[n]} twisting/shear modifier is within the standard ETABS range; 1.0 is accepted for this component." : $"{category} {labels[n]} effective modifier is within an accepted screening range." : $"{category} {labels[n]} effective modifier is outside the requested screening range; engineering review is required.";
                output.Add(new(status,message,id+"/"+labels[n],type,story,Actual:effective,Limit:nominal??range.Min,Unit:"modifier",Evidence:[evidence]));
            }
        }
    }

    private static IReadOnlyList<Observation> BaseShearScaling(InspectionContext c)
    {
        var settings=c.Configuration.Settings.SeismicVerification;
        if(!c.Configuration.Settings.Confirmed||settings is null) return [Rule.NotVerified("Confirmed X/Y seismic case mappings are required for the ELF-to-spectrum comparison.")];
        var results=c.Input.Analysis!.Results!; var finished=c.Input.Analysis!.Status?.Cases.Items.Where(x=>x.State==AnalysisCaseState.Finished).Select(x=>x.Name).ToHashSet(StringComparer.Ordinal)??[];
        List<Observation> output=[];
        foreach(var axis in settings.Axes.Where(a=>a.Direction is "X" or "Y"))
        {
            var spectrum=axis.SpectrumCase;
            var definition=c.Input.Analysis.Definitions!.ResponseSpectrumCases.Items.SingleOrDefault(x=>x.Name==spectrum);
            var direction=axis.Direction=="X"?"U1":"U2";
            if(spectrum is null||definition is null||definition.Loads.Count!=1||definition.Loads[0].Direction!=direction||definition.Loads[0].CoordinateSystem!="Global"||Math.Abs(definition.Loads[0].Angle)>Tolerance||!finished.Contains(axis.ElasticCase)||!finished.Contains(spectrum))
            { output.Add(new(CheckStatus.NOT_VERIFIED,"A unique mapped, finished ELF and unrotated global response-spectrum case are required.",axis.Direction)); continue; }
            var elf=results.BaseReactions.Items.Where(x=>x.Case==axis.ElasticCase).Select(x=>Force(x,axis.Direction)).ToArray(); var rs=results.BaseReactions.Items.Where(x=>x.Case==spectrum).Select(x=>Force(x,axis.Direction)).ToArray();
            if(elf.Length==0||rs.Length==0||elf.Concat(rs).Any(x=>!double.IsFinite(x))||elf.Max()<=0)
            { output.Add(new(CheckStatus.NOT_VERIFIED,"Finite ELF and response-spectrum base reactions with positive ELF shear are required.",axis.Direction)); continue; }
            var v=elf.Max(); var vt=rs.Max(); var ratio=vt/v; var multiplier=ratio>0?1/ratio:double.PositiveInfinity;
            if(!double.IsFinite(ratio)||!double.IsFinite(multiplier)) { output.Add(new(CheckStatus.NOT_VERIFIED,"Base-shear ratio or scaling multiplier is non-finite.",axis.Direction)); continue; }
            var forceUnit=c.Input.Model.Identity.DisplayUnits.Force;
            output.Add(new(ratio<1-1e-9?CheckStatus.FAIL:CheckStatus.PASS,
                ratio<1-1e-9?$"Response spectrum base shear is under-scaled per ASCE 7-16 12.9.1.4 (Vt = {vt:N0} {forceUnit} < V = {v:N0} {forceUnit}). Multiply ETABS response spectrum load case scale factor by {multiplier:F3}.":"Response spectrum base shear meets the 100% ELF scaling screen in ASCE 7-16 12.9.1.4.",axis.Direction,Actual:ratio,Limit:1,Unit:"ratio",Combination:spectrum,
                Evidence:[Ev("Results.BaseReactions + LoadCases.ResponseSpectrum",$"direction={axis.Direction}; Vt={vt:G17} {forceUnit}; V_ELF={v:G17} {forceUnit}; ratio={ratio:G17}; additional multiplier={multiplier:G17}; scale factor is not changed automatically.")]));
        }
        return output.Count>0?output:[Rule.NotVerified("No principal directions are mapped in seismicVerification.")];
        static double Force(BaseReactionResult row,string direction)=>Math.Abs(direction=="Y"?row.FY:row.FX);
    }

    private static IReadOnlyList<Observation> TorsionalAmplification(InspectionContext c)
    {
        var ext=c.Input.Analysis!.Results!.Extended; var torsion=ext?.Tables.SingleOrDefault(t=>t.Key=="Diaphragm Max Over Avg Drifts"); var auto=ext?.Tables.SingleOrDefault(t=>t.Key=="Load Pattern Definitions - Auto Seismic - ASCE 7-16");
        if(torsion is null||auto is null) return [Rule.NotVerified("Diaphragm max/average drift and auto-seismic eccentricity tables are required.")];
        var axes=c.Configuration.Settings.SeismicVerification?.Axes;
        if(axes is null||axes.Count==0) return [Rule.NotVerified("Confirmed seismic direction mappings are required to associate torsion ratios and auto-seismic patterns.")];
        List<Observation> output=[];
        foreach(var axis in axes.Where(a=>a.Direction is "X" or "Y"))
        {
            var ratios=torsion.Rows.Where(r=>Get(r,"OutputCase")==axis.ElasticCase&&Get(r,"Item").EndsWith(" "+axis.Direction,StringComparison.OrdinalIgnoreCase)).Select(r=>(Story:Get(r,"Story"),Value:Num(r,"Ratio"))).Where(x=>x.Story.Length>0&&x.Value.HasValue).ToArray();
            if(ratios.Length==0) { output.Add(new(CheckStatus.NOT_VERIFIED,"No diaphragm max-over-average drift ratios were found for the mapped elastic case and direction.",axis.Direction)); continue; }
            var governing=ratios.MaxBy(x=>x.Value!.Value); var r=governing.Value!.Value;
            if(r<=1.2+1e-9) { output.Add(new(CheckStatus.PASS,"No extracted story exceeds the 1.2 torsional-irregularity trigger for the mapped direction.",governing.Story+"/"+axis.Direction, "Story", Actual:r,Limit:1.2,Unit:"ratio",Combination:axis.ElasticCase,Evidence:[Ev(torsion.Key,$"story={governing.Story}; R=delta_max/delta_avg={r:G17}")])); continue; }
            var ax=Math.Min(3,Math.Pow(r/1.2,2)); var expected=.05*ax;
            var flags=axis.Direction+"Dir"; var candidates=auto.Rows.Where(row=>IsTrue(Get(row,flags)) && (IsTrue(Get(row,flags+"PlusE"))||IsTrue(Get(row,flags+"MinusE")))).Select(row=>(Name:Get(row,"Name"),Ecc:Num(row,"EccRatio"))).Where(x=>x.Name.Length>0&&x.Ecc.HasValue).ToArray();
            if(candidates.Length==0) { output.Add(new(CheckStatus.NOT_VERIFIED,"Torsional irregularity exceeds 1.2, but no mapped auto-seismic pattern with eccentricity evidence was extracted.",governing.Story+"/"+axis.Direction,Actual:r,Limit:1.2,Unit:"ratio",Evidence:[Ev(torsion.Key,$"R={r:G17}; required Ax={ax:G17}; expected eccentricity={expected:G17}")])); continue; }
            foreach(var pattern in candidates)
            {
                var insufficient=pattern.Ecc!.Value+1e-9<expected;
                var msg=insufficient?$"Torsional irregularity detected (R = {r:F3}); ASCE 7-16 12.8.4.3 requires accidental eccentricity amplified by Ax = {ax:F2} (effective eccentricity = {expected:P1}). ETABS load pattern still uses unamplified 5%.":$"Auto-seismic eccentricity for the extracted torsional irregularity meets or exceeds the required 0.05 Ax screening value; companion signs and load-case application require review.";
                output.Add(new(insufficient?CheckStatus.WARNING:CheckStatus.PASS,msg,pattern.Name+"/"+axis.Direction,"LoadPattern",governing.Story,Actual:pattern.Ecc.Value,Limit:expected,Unit:"ratio",Combination:axis.ElasticCase,Evidence:[Ev(torsion.Key+" + "+auto.Key,$"story={governing.Story}; R={r:G17}; Ax=min(3,(R/1.2)^2)={ax:G17}; expected=0.05*Ax={expected:G17}; pattern={pattern.Name}; EccRatio={pattern.Ecc:G17}")]));
            }
        }
        return output.Count>0?output:[Rule.NotVerified("No torsional directions were configured.")];
    }

    private static IReadOnlyList<Observation> PDeltaLineage(InspectionContext c)
    {
        var s=c.Configuration.Settings.SeismicVerification;
        if(!c.Configuration.Settings.Confirmed||s is null) return [Rule.NotVerified("Confirmed seismicVerification settings, including lateral and gravity case mappings, are required for P-Delta lineage screening.")];
        var active=c.Input.Analysis!.Definitions!.GeometricNonlinearity.Items.Where(x=>x.RawOption>0).Select(x=>x.Case).ToHashSet(StringComparer.Ordinal);
        if(active.Count==0) return [Rule.NotVerified("No active ETABS geometric-nonlinearity case (RawOption > 0) was extracted.")];
        var forces=c.Input.Analysis.Results!.StoryForces?.Items;
        if(forces is null) return [Rule.NotVerified("Story Forces are unavailable; stability theta cannot be calculated.")];
        var combos=c.Input.Validation.Combinations!.Items; var cases=c.Input.Validation.LoadCases!.Items;
        var caseByName=cases.ToDictionary(x=>x.Name,StringComparer.Ordinal); var comboByName=combos.ToDictionary(x=>x.Name,StringComparer.Ordinal);
        List<Observation> output=[];
        foreach(var axis in s.Axes.Where(a=>a.Direction is "X" or "Y"))
        foreach(var story in c.Input.Model.Stories!.Stories.Where(x=>x.Height>0))
        {
            var d=SeismicCriteriaChecks.SeismicEnvelope(c.Input.Analysis.Results.StoryDrifts.Items.Where(x=>x.Story==story.Name&&x.Case==axis.ElasticCase&&x.Direction==axis.Direction),x=>x.StepType);
            var v=SeismicCriteriaChecks.SeismicEnvelope(forces.Where(x=>x.Story==story.Name&&x.Case==axis.ElasticCase&&x.Location=="Bottom"),x=>x.StepType);
            var gravity=s.GravityLoads.Select(g=>(g,rows:forces.Where(x=>x.Story==story.Name&&x.Case==g.Case&&x.Location=="Bottom"&&SeismicCriteriaChecks.IsGravityStep(x.StepType)).ToArray())).ToArray();
            var id=story.Name+"/"+axis.Direction;
            if(d.Length==0||v.Length==0||gravity.Length==0||gravity.Any(x=>x.rows.Length!=1)) { output.Add(new(CheckStatus.NOT_VERIFIED,"Story drift and shear results (including Max/Min envelopes) and approved gravity-load rows are required to calculate theta.",id)); continue; }
            var px=gravity.Sum(x=>x.g.Factor*x.rows[0].P); var shear=v.Max(x=>Math.Abs(axis.Direction=="X"?x.VX:x.VY)); var drift=d.Max(x=>Math.Abs(x.Drift)); var theta=shear>0?px*drift/shear:double.NaN;
            if(!double.IsFinite(theta)||px<=0||shear<=0) { output.Add(new(CheckStatus.NOT_VERIFIED,"Finite positive gravity force and lateral story shear are required to calculate theta.",id)); continue; }
            if(theta<=.10) { output.Add(new(CheckStatus.NOT_APPLICABLE,"Theta does not exceed 0.10; this P-Delta solver-lineage trigger is not activated.",id,"Story",story.Name,Actual:theta,Limit:.10,Unit:"ratio",Combination:axis.ElasticCase)); continue; }
            var related=combos.Where(combo=>Expand(combo.Name,new HashSet<string>(StringComparer.Ordinal)).Contains(axis.ElasticCase)).ToArray();
            if(related.Length==0) { output.Add(new(CheckStatus.NOT_VERIFIED,"Theta exceeds 0.10 but no extracted design combination can be linked to the mapped lateral case; P-Delta lineage is unverified.",id,"Story",story.Name,Actual:theta,Limit:.10,Unit:"ratio",Combination:axis.ElasticCase)); continue; }
            foreach(var combo in related)
            {
                var hasPdelta=Expand(combo.Name,new HashSet<string>(StringComparer.Ordinal)).Any(active.Contains);
                output.Add(new(hasPdelta?CheckStatus.PASS:CheckStatus.WARNING,hasPdelta?"Theta exceeds 0.10 and the mapped lateral design combination includes an active geometric-nonlinearity case; nonlinear convergence and result adequacy remain separate.":$"Story stability theta={theta:F3} exceeds 0.10, but lateral combination '{combo.Name}' has no active P-Delta stiffness lineage.",id+"/"+combo.Name,"Story",story.Name,Actual:theta,Limit:.10,Unit:"ratio",Combination:combo.Name,Evidence:[Ev("Story Forces + combination dependency graph",$"Px={px:G17}; V envelope={shear:G17}; drift envelope={drift:G17}; theta=Px*|drift|/|V|={theta:G17}; accepted Single Value/Max/Min steps; active nonlinear cases={string.Join(",",active)}; combination={combo.Name}")]));
            }
        }
        return output.Count>0?output:[Rule.NotVerified("No stories or lateral directions were available for P-Delta lineage screening.")];

        HashSet<string> Expand(string name,HashSet<string> visiting)
        {
            HashSet<string> leaves=[]; if(caseByName.ContainsKey(name)){leaves.Add(name);return leaves;} if(!comboByName.TryGetValue(name,out var combo)||!visiting.Add(name))return leaves;
            foreach(var term in combo.Terms) if(term.Type.Contains("Combo",StringComparison.OrdinalIgnoreCase)||comboByName.ContainsKey(term.Name)) leaves.UnionWith(Expand(term.Name,visiting)); else if(caseByName.ContainsKey(term.Name)) leaves.Add(term.Name);
            visiting.Remove(name);return leaves;
        }
    }

    private static Dictionary<string,ModifierEvidence> Modifiers(ExtendedAnalysisEvidence ext,string kind) => ext.Modifiers.Where(x=>x.Kind.Equals(kind,StringComparison.OrdinalIgnoreCase)).GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.OrdinalIgnoreCase);
    private static bool InRange(double x,double lo,double hi)=>x>=lo-Tolerance&&x<=hi+Tolerance;
    private static string Get(IReadOnlyDictionary<string,string> row,string key)=>row.FirstOrDefault(x=>x.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value??"";
    private static double? Num(IReadOnlyDictionary<string,string> row,string key)=>double.TryParse(Get(row,key),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:null;
    private static bool IsTrue(string value)=>value.Equals("Yes",StringComparison.OrdinalIgnoreCase)||value.Equals("True",StringComparison.OrdinalIgnoreCase)||value=="1";
    private static Evidence Ev(string source,string value)=>new("Extracted ETABS evidence",source,value);
}
