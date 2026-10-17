using static EngineeringCalculations;
using static VerificationInputs;

/// <summary>Input-derived comparisons are separate from identities between ETABS results.</summary>
public static class IndependentAnalysisChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Create("ANA-101", "Gravity load balance", "Integrate physical frame/shell self-weight with effective materials, actual weight modifiers, and net floor openings; add supported area/line gravity loads, apply linear-static case factors, and compare signed vertical base reactions. Unsupported geometry or assignments block validation.", GravityBalance),
        Create("ANA-102", "Support reaction resultants", "Sum simultaneous global joint forces and M + (r-r0) cross F, then compare all six components with base reactions. Result consistency only; not applied-load equilibrium.", ReactionSum),
        Create("ANA-103", "Building mass from mass-source inputs", "Compute mass from the actual source's weighted gravity loads divided by standard gravity in current length units; compare active lateral mass. Unsupported added-mass definitions block the calculation.", BuildingMass),
        Create("ANA-104", "Story drift from joint displacements", "Recompute drift=(upper displacement-lower displacement)/story height at each reported governing point for simultaneous linear-static results. Compare within propagated display rounding. Does not independently certify the maximum over unobserved mesh points.", Drift),
        Create("ANA-105", "Lateral story shear and base reactions", "Compare bottom-story shear magnitudes with base-reaction magnitudes for simultaneous finished linear-static cases. Signs and independent applied lateral-load resultants remain unverified. Response-spectrum and envelope extrema are never summed.", Lateral),
        Create("ANA-106", "Modal mechanics verification", "Check T=2*pi/sqrt(lambda) against the modal table within display rounding. This identity does not independently validate model stiffness, modal completeness, or eigenvectors.", Modal),
        Create("ANA-107", "Story stability calculation", "Compute theta=P*abs(delta)/(abs(V)*h) only with confirmed gravity-case factors, simultaneous signed story forces, matched drift, and finished statuses. No stability acceptance limit is invented.", Stability)
    ];
    private static IInspectionCheck Create(string id, string title, string method, Func<InspectionContext,IReadOnlyList<Observation>> evaluate) =>
        Rule.Create(id, title, method, ["AnalysisResults"], c =>
        {
            try { return evaluate(c); }
            catch (InvalidOperationException ex) { return [MissingObservation(ex.Message)]; }
        });
    private static Observation MissingObservation(string message, string? name = null) =>
        new(CheckStatus.NOT_VERIFIED, message.StartsWith("Missing input:", StringComparison.Ordinal) || message.StartsWith("Data could not be read:", StringComparison.Ordinal) || message.StartsWith("Inspector limitation:",StringComparison.Ordinal) ? message : "Missing input: " + message,
            name, Combination: name) { Kind = message.StartsWith("Data could not be read:", StringComparison.Ordinal) ? FindingKind.ApiUnavailable : message.StartsWith("Inspector limitation:",StringComparison.Ordinal)?FindingKind.ReviewRequired:FindingKind.MissingVerificationInput };
    private static Observation Compared(string title, string name, Number expected, Number actual, CalculationValidation scope, string method, string? unit = null)
    {
        var agrees = expected.Agrees(actual);
        return new(agrees ? CheckStatus.PASS : CheckStatus.WARNING,
            title + (agrees ? " agrees within source precision." : " does not agree; review loads, results, and calculation scope."), name, Combination: name,
            Actual: actual.Value, Limit: expected.Value, Unit: unit,
            Evidence: [new("Calculation", method, $"calculated={expected.Value:G17}; ETABS={actual.Value:G17}; combined display-rounding bound={expected.Error+actual.Error:G17}; floating allowance={1e-8*Math.Max(1,Math.Max(Math.Abs(expected.Value),Math.Abs(actual.Value))):G17}. Numerical precision comparison, not an engineering code limit.")])
        { CalculationValidation = agrees ? scope : CalculationValidation.Unverified, Kind = agrees ? FindingKind.Information : FindingKind.ReviewRequired };
    }
    private static IReadOnlyList<Observation> ForCases(VerificationInputs v, Func<LoadCase,Observation> check)
    {
        var cases = v.StaticCases();
        if (cases.Count == 0) return [Rule.NotApplicable("No linear-static cases are defined for this comparison.")];
        var outcomes=new List<Observation>();var missing=new List<(string Case,string Message)>();var unrun=new List<string>();
        foreach(var loadCase in cases)
        {
            if(v.Input.Analysis?.Status?.Cases.Items.Any(s=>s.Name==loadCase.Name&&s.State==AnalysisCaseState.NotRun)==true)
            {unrun.Add(loadCase.Name);continue;}
            try { outcomes.Add(check(loadCase)); }
            catch (InvalidOperationException ex) { missing.Add((loadCase.Name,ex.Message)); }
        }
        string GroupReason((string Case,string Message) item)
        {
            var text=item.Message.Replace(item.Case,"the affected case",StringComparison.Ordinal);
            var suffix=text.LastIndexOf(" for ",StringComparison.Ordinal);
            return suffix<0?text:text[..suffix]+" for the affected cases.";
        }
        foreach(var group in missing.GroupBy(GroupReason))
        {
            var message=group.Count()==1?group.First().Message:group.Key+" ("+group.Count()+" cases)";
            outcomes.Add(MissingObservation(message,group.Count()==1?group.First().Case:null) with
            {Evidence=[new("Affected cases","Read-only analysis verification",string.Join(", ",group.Select(m=>m.Case)))]});
        }
        if(unrun.Count>0)outcomes.Add(new Observation(CheckStatus.NOT_APPLICABLE,$"Analysis has not been run for {unrun.Count} cases; this calculation was deferred.",
            Evidence:[new("Affected cases","Analyze.GetCaseStatus",string.Join(", ",unrun))]){Kind=FindingKind.OperationNotPerformed});
        return outcomes;
    }
    private static IReadOnlyList<Observation> GravityBalance(InspectionContext c)
    {
        var v = new VerificationInputs(c);
        return ForCases(v, loadCase =>
        {
            var weight = v.CaseWeight(loadCase);
            var reaction = v.Reaction(loadCase.Name);
            return Compared("Calculated vertical load and base reaction", loadCase.Name, weight, new(reaction.FZ,reaction.ForceUncertainty),
                CalculationValidation.IndependentLoadBalance, "sum(Ag*L*gamma*weight modifiers) + sum(net shell area*t*gamma*weight modifiers), multiplied by pattern self-weight; plus integral(q dA)+integral(w ds); then actual case factors", c.Input.Model.Identity.DisplayUnits.Force);
        });
    }
    private static IReadOnlyList<Observation> ReactionSum(InspectionContext c)
    {
        var v = new VerificationInputs(c);
        var table = v.Table("Joint Reactions", "UniqueName", "OutputCase", "StepType", "StepNumber", "FX", "FY", "FZ", "MX", "MY", "MZ");
        return ForCases(v, loadCase =>
        {
            var reaction = v.Reaction(loadCase.Name);
            var rows = table.Rows.Where(r => S(r,"OutputCase") == loadCase.Name && S(r,"StepType") is "" or "Single Value" && (S(r,"StepNumber") == "" || N(r,"StepNumber").Value == 0)).ToArray();
            if (rows.Length == 0 || rows.Select(r => S(r,"UniqueName")).Distinct().Count() != rows.Length) throw Missing("unique simultaneous joint reactions for " + loadCase.Name);
            Number[] sum = new Number[6];
            foreach (var row in rows)
            {
                var point = c.Input.Model.Joints?.Items.SingleOrDefault(j => j.Id == S(row,"UniqueName"))?.Position ?? throw Missing("reaction point global coordinates");
                var fx=N(row,"FX"); var fy=N(row,"FY"); var fz=N(row,"FZ");
                var dx=point.X-reaction.ReferencePoint.X; var dy=point.Y-reaction.ReferencePoint.Y; var dz=point.Z-reaction.ReferencePoint.Z;
                sum[0]+=fx; sum[1]+=fy; sum[2]+=fz;
                sum[3]+=N(row,"MX")+fz*dy-fy*dz;
                sum[4]+=N(row,"MY")+fx*dz-fz*dx;
                sum[5]+=N(row,"MZ")+fy*dx-fx*dy;
                sum[3]+=new Number(0,reaction.ReferenceUncertainty*(Math.Abs(fz.Value)+fz.Error+Math.Abs(fy.Value)+fy.Error));
                sum[4]+=new Number(0,reaction.ReferenceUncertainty*(Math.Abs(fx.Value)+fx.Error+Math.Abs(fz.Value)+fz.Error));
                sum[5]+=new Number(0,reaction.ReferenceUncertainty*(Math.Abs(fy.Value)+fy.Error+Math.Abs(fx.Value)+fx.Error));
            }
            double[] actual=[reaction.FX,reaction.FY,reaction.FZ,reaction.MX,reaction.MY,reaction.MZ];
            string[] names=["FX","FY","FZ","MX","MY","MZ"];
            var bad=Enumerable.Range(0,6).Where(i=>!sum[i].Agrees(new(actual[i],i<3?reaction.ForceUncertainty:reaction.MomentUncertainty))).ToArray();
            return new Observation(bad.Length==0?CheckStatus.PASS:CheckStatus.WARNING,
                bad.Length==0?"All six support-reaction resultants agree with base reactions within table precision.":"Support-reaction resultants disagree with base reactions; review the listed components.",
                loadCase.Name, Combination:loadCase.Name,
                Evidence:Enumerable.Range(0,6).Select(i=>new Evidence(names[i],"sum(F); sum(M + r cross F)",$"sum={sum[i].Value:G17}; base={actual[i]:G17}; rounding={sum[i].Error:G17}")).ToArray())
            { CalculationValidation=bad.Length==0?CalculationValidation.ResultConsistency:CalculationValidation.Unverified };
        });
    }
    private static IReadOnlyList<Observation> BuildingMass(InspectionContext c)
    {
        List<Observation> output=[];
        try{output.AddRange(IndependentBuildingMass(c));}
        catch(InvalidOperationException ex){output.Add(MissingObservation(ex.Message));}
        try
        {
            var v=new VerificationInputs(c);
            var sources=c.Input.Validation.MassSources is {ReadSucceeded:true} data?data.Items:throw Missing("mass-source definitions");
            if(sources.Count!=1||!sources[0].IsDefault||sources[0].IncludeElementMass||!sources[0].IncludeLoads)throw Missing("one load-derived default mass source for reaction/mass consistency");
            var source=sources[0];
            if(source.IncludeAddedMass&&new[]{"Joint Assignments - Additional Mass","Frame Assignments - Additional Mass","Area Assignments - Additional Mass"}
                .Any(k=>!v.Evidence.Inventory.Any(t=>t.Key==k&&t.IsEmpty)))throw Missing("additional-mass totals for reaction/mass consistency");
            var sourceRows=v.Table("Mass Source Definition","Name","IncLateral").Rows.Where(r=>S(r,"Name")==source.Name).ToArray();
            if(!sourceRows.Any(r=>S(r,"IncLateral")=="Yes"))throw Missing("an enabled lateral mass source");
            var loads=v.Evidence.GravityLoads is {ReadSucceeded:true} precise?precise.Items:throw Missing("precise load assignments for mass-source deduction screening");
            var sourceNames=source.Loads.Select(l=>l.Pattern).ToHashSet(StringComparer.Ordinal);
            foreach(var entry in v.Evidence.Inventory.Where(t=>!t.IsEmpty&&(t.Key.Contains("Load Assignments",StringComparison.Ordinal)||t.Key.Contains("Loads Assignments",StringComparison.Ordinal))))
            {
                if(entry.Key is "Area Load Assignments - Uniform" or "Frame Loads Assignments - Distributed" or "Frame Loads Assignments - Open Structure Wind Parameters")continue;
                if(v.Table(entry.Key,"LoadPattern").Rows.Any(r=>sourceNames.Contains(S(r,"LoadPattern"))))throw Missing("supported mass-source load integration for "+entry.Key);
            }
            Number weight=new(0);
            foreach(var term in source.Loads.Where(t=>t.Factor!=0))
            {
                if(term.Factor<0||!double.IsFinite(term.Factor)||loads.Any(l=>l.Pattern==term.Pattern&&(l.Direction!=10||l.CoordinateSystem!="Global"||l.ValueStart<0||l.ValueEnd<0)))
                    throw Missing("joint-level assembly for upward, nongravity, or negative mass-source contributions");
                var pattern=c.Input.Validation.LoadPatterns?.Items.SingleOrDefault(p=>p.Name==term.Pattern)??throw Missing("mass-source load pattern");
                if(pattern.SelfWeightMultiplier<0||pattern.Type is not ("Dead" or "SuperDead" or "Live" or "ReducibleLive"))throw Missing("supported gravity mass-source pattern classification");
                var matching=v.StaticCases().Where(l=>l.Loads is {Count:1}&&l.Loads[0].Type=="Load"&&l.Loads[0].Name==term.Pattern&&l.Loads[0].Factor>0&&double.IsFinite(l.Loads[0].Factor)).ToArray();
                if(matching.Length!=1)throw Missing("one single-pattern static case for mass-source pattern "+term.Pattern);
                var reaction=v.Reaction(matching[0].Name);
                if(reaction.FZ<0)throw Missing("positive gravity reaction for mass-source pattern "+term.Pattern);
                weight+=new Number(reaction.FZ,reaction.ForceUncertainty)*(term.Factor/matching[0].Loads![0].Factor);
            }
            if(source.Loads.Count==0||weight.Value<=0)throw Missing("positive load-derived mass contributions");
            var expected=weight*(1/Gravity(c.Input.Model.Identity.DisplayUnits.Length));
            var totals=v.Table("Mass Summary by Group","Group","MassX","MassY").Rows.Where(r=>S(r,"Group")=="All").ToArray();
            if(totals.Length!=1)throw Missing("one assembled mass total");
            foreach(var axis in new[]{"X","Y"})output.Add(Compared("Reaction-derived mass-source total in "+axis,source.Name+"/reaction/"+axis,expected,N(totals[0],"Mass"+axis),
                CalculationValidation.ResultConsistency,"M=sum(source factor*single-pattern vertical reaction/case factor)/g. ETABS-result consistency only; this does not independently validate self-weight or applied loads.",c.Input.Model.Identity.DisplayUnits.Force+"·s²/"+c.Input.Model.Identity.DisplayUnits.Length));
        }
        catch(InvalidOperationException ex){output.Add(MissingObservation(ex.Message,"reaction/mass consistency"));}
        return output;
    }
    private static IReadOnlyList<Observation> IndependentBuildingMass(InspectionContext c)
    {
        var v=new VerificationInputs(c);
        if(c.Input.Validation.MassSources is not {ReadSucceeded:true} sources || sources.Items.Count!=1 || !sources.Items[0].IsDefault)
            throw Missing("one unambiguous mass source and its case lineage");
        var source=sources.Items[0];
        if(source.IncludeAddedMass)
        {
            string[] added=["Joint Assignments - Additional Mass","Frame Assignments - Additional Mass","Area Assignments - Additional Mass"];
            if(added.Any(k=>!v.Evidence.Inventory.Any(t=>t.Key==k&&t.IsEmpty))) throw Missing("complete integration of assigned additional masses");
        }
        Number mass=new(0);
        if(source.IncludeElementMass)mass+=v.ElementMass();
        if(source.IncludeLoads)
        {
            if(source.Loads.Count==0) throw Missing("mass-source pattern contributions");
            foreach(var term in source.Loads.Where(t=>t.Factor!=0))
            {
                if(term.Factor<0||!double.IsFinite(term.Factor))throw Missing("joint-level mass integration for negative mass-source factors");
                mass+=v.PatternWeight(term.Pattern,true)*(term.Factor/Gravity(c.Input.Model.Identity.DisplayUnits.Length));
            }
        }
        var expected=mass;
        if(expected.Value<=0) throw Missing("positive assembled mass after contributions and deductions");
        var definition=v.Table("Mass Source Definition","Name","IncLateral","IncVertical").Rows.Where(r=>S(r,"Name")==source.Name).ToArray();
        if(definition.Length==0||definition.Select(r=>S(r,"IncLateral")).FirstOrDefault(s=>s!="")!="Yes") throw Missing("enabled lateral mass source directions");
        var totals=v.Table("Mass Summary by Group","Group","MassX","MassY").Rows.Where(r=>S(r,"Group")=="All").ToArray();
        if(totals.Length!=1) throw Missing("one All-group assembled mass total");
        return new[]{"X","Y"}.Select(axis=>Compared("Input-derived building mass in "+axis,source.Name+"/"+axis,expected,N(totals[0],"Mass"+axis),
            CalculationValidation.IndependentLoadBalance,"M=sum(net element volume*material mass density*mass modifiers) + sum(source factor*pattern gravity weight)/g; additional masses are excluded only with empty-assignment inventory evidence",c.Input.Model.Identity.DisplayUnits.Force+"·s²/"+c.Input.Model.Identity.DisplayUnits.Length)).ToArray();
    }
    private static IReadOnlyList<Observation> Drift(InspectionContext c)
    {
        var v=new VerificationInputs(c);
        var displacements=v.Table("Joint Displacements","UniqueName","Label","Story","OutputCase","StepType","StepNumber","Ux","Uy");
        var recoveredDrifts=v.StoryDrifts();
        var stories=c.Input.Model.Stories??throw Missing("story elevations and base elevation");
        return ForCases(v,loadCase=>
        {
            v.Finished(loadCase.Name);
            var drifts=recoveredDrifts.Where(r=>r.Case==loadCase.Name&&r.StepType is "" or "Single Value"&&r.StepNumber==0).ToArray();
            if(drifts.Length==0)throw Missing("simultaneous story drift rows for "+loadCase.Name);
            int failed=0; List<Evidence> evidence=[];
            foreach(var row in drifts)
            {
                if(row.Direction is not ("X" or "Y"))throw Missing("supported drift direction");
                var story=stories.Stories.SingleOrDefault(s=>s.Name==row.Story)??throw Missing("unique story elevation");
                var lower=stories.Stories.Where(s=>s.Elevation<story.Elevation).Select(s=>s.Elevation).DefaultIfEmpty(stories.BaseElevation).Max();
                var height=story.Elevation-lower;
                if(height<=0)throw Missing("positive story height");
                var topRows=displacements.Rows.Where(r=>S(r,"Story")==row.Story&&S(r,"Label")==row.Label&&S(r,"OutputCase")==loadCase.Name&&S(r,"StepType") is "" or "Single Value").ToArray();
                if(topRows.Length!=1)throw Missing("a unique governing-point displacement for "+row.Story+"/"+row.Label);
                var topPoint=c.Input.Model.Joints?.Items.SingleOrDefault(j=>j.Id==S(topRows[0],"UniqueName"))??throw Missing("governing-point geometry");
                var tolerance=1e-8*Math.Max(1,Math.Abs(story.Elevation));
                if(Math.Abs(topPoint.Position.Z-story.Elevation)>tolerance)throw Missing("a governing point at the reported story elevation");
                var bottomPoints=c.Input.Model.Joints!.Items.Where(j=>Math.Abs(j.Position.X-topPoint.Position.X)<=tolerance&&Math.Abs(j.Position.Y-topPoint.Position.Y)<=tolerance&&Math.Abs(j.Position.Z-lower)<=tolerance).ToArray();
                if(bottomPoints.Length!=1)throw Missing("a unique vertically aligned lower joint for "+row.Story+"/"+row.Label);
                var bottomRows=displacements.Rows.Where(r=>S(r,"UniqueName")==bottomPoints[0].Id&&S(r,"OutputCase")==loadCase.Name&&S(r,"StepType") is "" or "Single Value").ToArray();
                Number bottom;
                if(bottomRows.Length==1)bottom=N(bottomRows[0],"U"+row.Direction.ToLowerInvariant());
                else
                {
                    // A restraint does not prove zero motion when ground displacement loads exist.
                    var ground=v.Evidence.Inventory.SingleOrDefault(t=>t.Key=="Joint Loads Assignments - Ground Displacement");
                    var assignment=c.Input.Validation.JointAssignments?.Items.SingleOrDefault(a=>a.Id==bottomPoints[0].Id);
                    var dof=row.Direction=="X"?0:1;
                    if(ground?.IsEmpty!=true||assignment?.Restraints.Count<=dof||assignment is null||!assignment.Restraints[dof])throw Missing("lower-joint displacement or a verified stationary restraint");
                    bottom=new(0);
                }
                var delta=N(topRows[0],"U"+row.Direction.ToLowerInvariant())-bottom;
                var ratio=delta*(1/height);
                var actual=new Number(Math.Abs(row.Drift),row.Uncertainty);
                if(!new Number(Math.Abs(ratio.Value),ratio.Error).Agrees(actual))failed++;
                evidence.Add(new(row.Story+"/"+row.Direction,"abs(u_top-u_bottom)/height",$"calculated={Math.Abs(ratio.Value):G17}; ETABS={Math.Abs(row.Drift):G17}; height={height:G17}; rounding={ratio.Error:G17}"));
            }
            return new Observation(failed==0?CheckStatus.PASS:CheckStatus.WARNING,
                failed==0?$"All {drifts.Length} reported governing-point drifts agree with displacement differences within table precision.":$"{failed} of {drifts.Length} governing-point drift calculations disagree; review the paired points and result conventions.",
                loadCase.Name,Combination:loadCase.Name,Evidence:evidence)
            {CalculationValidation=failed==0?CalculationValidation.ResultConsistency:CalculationValidation.Unverified};
        });
    }
    private static IReadOnlyList<Observation> Lateral(InspectionContext c)
    {
        var v=new VerificationInputs(c);
        var stories=c.Input.Model.Stories??throw Missing("story elevations");
        var lowest=stories.Stories.OrderBy(s=>s.Elevation).FirstOrDefault()??throw Missing("lowest story");
        var forces=v.StoryForces();
        return ForCases(v,loadCase=>
        {
            var reaction=v.Reaction(loadCase.Name);
            if(Math.Abs(reaction.FX)+Math.Abs(reaction.FY)<1e-8*Math.Max(1,Math.Abs(reaction.FZ)))return Rule.NotApplicable("This case has no significant horizontal resultant for the lateral comparison.");
            var rows=forces.Where(f=>f.Story==lowest.Name&&f.Case==loadCase.Name&&f.Location=="Bottom"&&f.StepType is "" or "Single Value"&&f.StepNumber==0).ToArray();
            if(rows.Length!=1)throw Missing("one simultaneous lowest-story force row for "+loadCase.Name);
            var table=v.Table("Story Forces","Story","OutputCase","StepType","Location","VX","VY");
            var raw=table.Rows.Where(r=>S(r,"Story")==lowest.Name&&S(r,"OutputCase")==loadCase.Name&&S(r,"Location")=="Bottom"&&S(r,"StepType") is "" or "Single Value").ToArray();
            if(raw.Length!=1)throw Missing("one simultaneous story-force row retaining display precision");
            var x=N(raw[0],"VX");var y=N(raw[0],"VY");
            var agrees=new Number(Math.Abs(x.Value),x.Error).Agrees(new(Math.Abs(reaction.FX),reaction.ForceUncertainty))&&new Number(Math.Abs(y.Value),y.Error).Agrees(new(Math.Abs(reaction.FY),reaction.ForceUncertainty));
            return new Observation(agrees?CheckStatus.PASS:CheckStatus.WARNING,
                agrees?"Lowest-story shear magnitudes agree with base-reaction magnitudes within display precision. Signs and applied lateral loads remain unverified.":"Lowest-story shear magnitudes and base-reaction magnitudes disagree; review base-level loads and result conventions.",
                loadCase.Name,Combination:loadCase.Name,Evidence:[new("Magnitude residuals","abs(V_bottom)-abs(R_base)",$"X residual={Math.Abs(x.Value)-Math.Abs(reaction.FX):G17}; Y residual={Math.Abs(y.Value)-Math.Abs(reaction.FY):G17}; rounding X/Y={x.Error:G17}/{y.Error:G17}")])
            {CalculationValidation=agrees?CalculationValidation.ResultConsistency:CalculationValidation.Unverified};
        });
    }
    private static IReadOnlyList<Observation> Modal(InspectionContext c)
    {
        var v=new VerificationInputs(c);
        var cases=c.Input.Validation.LoadCases?.Items.Where(x=>x.Type=="Modal").ToArray()??throw Missing("modal-case definitions");
        if(cases.Length==0)return [Rule.NotApplicable("No modal analysis cases are defined.")];
        var table=v.Table("Modal Periods And Frequencies","Case","Mode","Period","Eigenvalue");
        List<Observation> output=[];
        foreach(var modal in cases)
        {
            try
            {
                v.Finished(modal.Name);
                var rows=table.Rows.Where(r=>S(r,"Case")==modal.Name).ToArray();
                if(rows.Length==0)throw Missing("modal periods and eigenvalues for "+modal.Name);
                var modeIds=rows.Select(r=>N(r,"Mode").Value).ToArray();
                if(modeIds.Any(m=>m<=0||Math.Floor(m)!=m)||modeIds.Distinct().Count()!=modeIds.Length)throw Missing("unique positive modal numbers");
                int bad=0,nonpositive=0;
                foreach(var row in rows)
                {
                    var lambda=N(row,"Eigenvalue");var period=N(row,"Period");
                    if(lambda.Value+lambda.Error<=0){nonpositive++;continue;}
                    if(lambda.Value-lambda.Error<=0)throw Missing("full-precision eigenvalues near zero; displayed precision cannot establish a positive frequency");
                    var expected=2*Math.PI/Math.Sqrt(lambda.Value);
                    var error=Math.Max(Math.Abs(2*Math.PI/Math.Sqrt(lambda.Value-lambda.Error)-expected),Math.Abs(2*Math.PI/Math.Sqrt(lambda.Value+lambda.Error)-expected));
                    if(!new Number(expected,error).Agrees(period))bad++;
                }
                output.Add(new Observation(bad==0&&nonpositive==0?CheckStatus.PASS:CheckStatus.WARNING,
                    nonpositive>0?$"ETABS reports {nonpositive} nonpositive modal eigenvalues. Review rigid-body motion and stability; a real positive period cannot be calculated.":
                    bad==0?$"Period and eigenvalue identities agree for {rows.Length} modes. This verifies output consistency, not the model's dynamic stiffness.":$"Period and eigenvalue identities disagree for {bad} modes.",modal.Name,
                    Evidence:[new("Modal identity","CSI Analysis Reference Manual 19.2, Eq. 19-2","T=2*pi/sqrt(lambda); propagated printed-value precision")])
                {CalculationValidation=bad==0&&nonpositive==0?CalculationValidation.ResultConsistency:CalculationValidation.Unverified});
            }
            catch(InvalidOperationException ex){output.Add(MissingObservation(ex.Message,modal.Name));}
        }
        output.Add(MissingObservation("Missing input: assembled stiffness and case-specific mass matrices with constraint mapping. Full-model eigenvalue and mode-shape validation remains unverified."));
        return output;
    }
    private static IReadOnlyList<Observation> Stability(InspectionContext c)
    {
        var settings=c.Configuration.Settings.SeismicVerification;
        if(!c.Configuration.Settings.Confirmed||settings is not {GravityBasisConfirmed:true,GravityLoads.Count:>0,Axes.Count:>0})
            return [MissingObservation("Missing input: confirmed gravity-force factors and lateral-case mappings for stability.")];
        var v=new VerificationInputs(c);
        var forces=v.StoryForces();
        var stories=c.Input.Model.Stories??throw Missing("story heights");
        List<Observation> output=[];
        foreach(var axis in settings.Axes)
        {
            try
            {
                if(axis.Direction is not ("X" or "Y"))throw Missing("a supported global lateral direction");
                v.Finished(axis.ElasticCase);
                if(!v.StaticCases().Any(x=>x.Name==axis.ElasticCase))throw Missing("a simultaneous linear-static lateral case");
                double maximum=0;string governing="";
                foreach(var story in stories.Stories)
                {
                    double gravity=0;
                    foreach(var term in settings.GravityLoads)
                    {
                        v.Finished(term.Case);
                        var rows=forces.Where(f=>f.Story==story.Name&&f.Case==term.Case&&f.Location=="Bottom"&&f.StepType is "" or "Single Value"&&f.StepNumber==0).ToArray();
                        if(rows.Length!=1||term.Factor<0||!double.IsFinite(term.Factor))throw Missing("simultaneous gravity story forces and nonnegative factors");
                        gravity+=rows[0].P*term.Factor;
                    }
                    var shear=forces.Where(f=>f.Story==story.Name&&f.Case==axis.ElasticCase&&f.Location=="Bottom"&&f.StepType is "" or "Single Value"&&f.StepNumber==0).ToArray();
                    var drift=v.StoryDrifts().Where(d=>d.Story==story.Name&&d.Case==axis.ElasticCase&&d.Direction==axis.Direction&&d.StepType is "" or "Single Value"&&d.StepNumber==0).ToArray();
                    if(shear.Length!=1||drift.Length!=1||story.Height<=0)throw Missing("matched simultaneous shear, drift, and height for "+story.Name);
                    if(gravity<0)throw Missing("a verified compression-positive gravity-force convention");
                    var theta=StabilityTheta(gravity,drift[0].Drift*story.Height,axis.Direction=="X"?shear[0].VX:shear[0].VY,story.Height);
                    if(theta>maximum){maximum=theta;governing=story.Name;}
                }
                output.Add(new Observation(CheckStatus.NOT_VERIFIED,"Story stability demand was calculated. Missing input: verified P-Delta stiffness lineage and project acceptance criteria; stability is not certified.",
                    axis.ElasticCase,Story:governing,Actual:maximum,Unit:"theta",Evidence:[new("Maximum first-order stability demand","P*abs(delta)/(abs(V)*h)",$"theta={maximum:G17}; gravity basis is confirmed; governing story={governing}")]));
            }
            catch(InvalidOperationException ex){output.Add(MissingObservation(ex.Message,axis.ElasticCase));}
        }
        return output;
    }
}
