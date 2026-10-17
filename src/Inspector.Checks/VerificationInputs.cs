using static EngineeringCalculations;

internal sealed class VerificationInputs(InspectionContext context)
{
    public InspectionInput Input => context.Input;
    public AnalysisResultsData Results => Input.Analysis?.Results ?? throw Missing("analysis results");
    public AnalysisVerificationEvidence Evidence => Results.VerificationEvidence is { InventoryComplete: true } e ? e : throw Missing("a complete table inventory");
    public static InvalidOperationException Missing(string what) => new("Missing input: " + what.TrimEnd(' ','.') + ".");
    public static InvalidOperationException Unavailable(string what) => new("Data could not be read: " + what.TrimEnd(' ','.') + ". This does not mean the model has no results.");
    public static string S(IReadOnlyDictionary<string,string> row, string key) => row.TryGetValue(key, out var value) ? value : throw Missing(key);
    public static Number N(IReadOnlyDictionary<string,string> row, string key)
    {
        var n=Displayed(S(row,key));
        return row.TryGetValue("__uncertainty_"+key,out var uncertainty)?new(n.Value,ResultTableRecovery.Parse(uncertainty)):n;
    }
    public AnalysisEvidenceTable Table(string key, params string[] fields)
    {
        var table = Evidence.Tables.SingleOrDefault(t => t.Key == key);
        if (table?.UnavailableReason is not null) throw Unavailable(key + ". " + table.UnavailableReason);
        if(table?.RecoverySource is not null&&fields.Any(f=>f is "StepType" or "StepNumber")&&(!table.Fields.Contains("StepType")||!table.Fields.Contains("StepNumber")))
        {
            var rows=table.Rows.Select(row=>
            {
                var caseName=S(row,"OutputCase");
                var staticCase=S(row,"CaseType")=="LinStatic"&&StaticCases().Any(c=>c.Name==caseName);
                var r=new Dictionary<string,string>(row,StringComparer.Ordinal);r.TryAdd("StepType",staticCase?"Single Value":"Unspecified");r.TryAdd("StepNumber",staticCase?"0":"");return (IReadOnlyDictionary<string,string>)r;
            }).ToArray();
            table=table with{Fields=table.Fields.Concat(new[]{"StepType","StepNumber"}).Distinct().ToArray(),Rows=rows};
        }
        if (table is null || table.UnavailableReason is not null || fields.Any(f => !table.Fields.Contains(f))) throw Missing("readable " + key + " with " + string.Join(", ", fields));
        return table;
    }
    public IReadOnlyList<StoryForceResult> StoryForces()
    {
        if(Results.StoryForces is {ReadSucceeded:true} data)return data.Items;
        var recovered=Evidence.Tables.SingleOrDefault(t=>t.Key=="Story Forces"&&t.RecoverySource is not null&&t.UnavailableReason is null);
        if(recovered is null)throw Unavailable("story forces");
        var table=Table("Story Forces","Story","OutputCase","StepType","StepNumber","Location","P","VX","VY");
        return table.Rows.Select(r=>new StoryForceResult(S(r,"Story"),S(r,"OutputCase"),S(r,"StepType"),S(r,"StepNumber")==""?0:N(r,"StepNumber").Value,S(r,"Location"),N(r,"P").Value,N(r,"VX").Value,N(r,"VY").Value)).ToArray();
    }
    public IReadOnlyList<StoryDriftResult> StoryDrifts()
    {
        if(Results.StoryDrifts.ReadSucceeded)return Results.StoryDrifts.Items;
        var recovered=Evidence.Tables.SingleOrDefault(t=>t.Key=="Story Drifts"&&t.RecoverySource is not null&&t.UnavailableReason is null);
        if(recovered is null)throw Unavailable("story drift results");
        var table=Table("Story Drifts","Story","OutputCase","StepType","StepNumber","Direction","Drift","Label","X","Y","Z");
        return table.Rows.Select(r=>new StoryDriftResult(S(r,"Story"),S(r,"OutputCase"),S(r,"StepType"),S(r,"StepNumber")==""?0:N(r,"StepNumber").Value,S(r,"Direction"),N(r,"Drift").Value,S(r,"Label"),N(r,"X").Value,N(r,"Y").Value,N(r,"Z").Value){Uncertainty=N(r,"Drift").Error}).ToArray();
    }
    public IReadOnlyList<LoadCase> StaticCases() => Input.Validation.LoadCases is { ReadSucceeded: true } cases
        ? cases.Items.Where(c => c.Type == "LinearStatic").ToArray() : throw Missing("load-case definitions");
    public void Finished(string name)
    {
        if (Input.Analysis?.Status?.Cases is not { ReadSucceeded: true } states ||
            states.Items.Count(c => c.Name == name && c.State == AnalysisCaseState.Finished) != 1) throw Missing("finished analysis status for " + name);
    }
    public BaseReactionResult Reaction(string name)
    {
        Finished(name);
        if (!Results.BaseReactions.ReadSucceeded)
        {
            var table=Evidence.Tables.SingleOrDefault(t=>t.Key=="Base Reactions"&&t.RecoverySource is not null&&t.UnavailableReason is null);
            if(table is not null)
            {
                var raw=table.Rows.Where(r=>S(r,"OutputCase")==name&&S(r,"CaseType")=="LinStatic"&&(!r.TryGetValue("StepType",out var st)||st is "" or "Single Value")&&(!r.TryGetValue("StepNumber",out var sn)||sn==""||ResultTableRecovery.Parse(sn)==0)).ToArray();
                if(raw.Length!=1)throw Missing("one simultaneous recovered base reaction for "+name);
                var row=raw[0];
                return new(name,"Single Value",0,N(row,"FX").Value,N(row,"FY").Value,N(row,"FZ").Value,N(row,"MX").Value,N(row,"MY").Value,N(row,"MZ").Value,new(N(row,"X").Value,N(row,"Y").Value,N(row,"Z").Value))
                {ForceUncertainty=new[]{"FX","FY","FZ"}.Max(k=>N(row,k).Error),MomentUncertainty=new[]{"MX","MY","MZ"}.Max(k=>N(row,k).Error),ReferenceUncertainty=new[]{"X","Y","Z"}.Max(k=>N(row,k).Error),RecoverySource=table.RecoverySource};
            }
            throw Unavailable("base reactions for " + name + ". " + string.Join(" ", Results.Issues.Where(i=>i.Component=="BaseReactions").Select(i=>i.Message)));
        }
        var rows = Results.BaseReactions.Items.Where(r => r.Case == name && r.StepType is "" or "Single Value" && r.StepNumber == 0).ToArray();
        return rows.Length == 1 ? rows[0] : throw Missing("one simultaneous static base-reaction row for " + name);
    }
    public Number SelfWeight()=>GeometryQuantity(false);
    public Number ElementMass()=>GeometryQuantity(true);
    private Number GeometryQuantity(bool mass)
    {
        if(!Evidence.SimpleFrameWeightGeometryVerified)throw new InvalidOperationException("Inspector limitation: the independent self-weight/mass calculation does not yet support this model's member offsets and overlaps. This does not mean ETABS self-weight is missing. "+string.Join(" ",Evidence.GeometryLimitations));
        var model=Input.Model;
        if(model.Frames is not {ReadSucceeded:true} frames||model.Areas is not {ReadSucceeded:true} areas||model.Materials is not {ReadSucceeded:true} materials||
            model.FrameSections is not {ReadSucceeded:true} frameSections||model.AreaSections is not {ReadSucceeded:true} areaSections)
            throw Missing("complete physical geometry and section/material definitions for self-weight");
        var modifiers=Results.Extended?.Modifiers??throw Missing("object and property weight modifiers");
        Number total = new(0);
        double Gamma(string? name)
        {
            var material=materials.Items.SingleOrDefault(m=>m.Name==name)??throw Missing("unique effective material for self-weight");
            var density=mass?material.MassDensity:material.UnitWeight;
            if(!double.IsFinite(density)||density<0)throw Missing("finite nonnegative weight or mass density");
            return density;
        }
        double Modifier(string kind,string name,int index)
        {
            var entry=modifiers.SingleOrDefault(m=>m.Kind==kind&&m.Name==name)??throw Missing(kind+" weight modifier for "+name);
            if(entry.Values.Count<=index||!double.IsFinite(entry.Values[index])||entry.Values[index]<0)throw Missing("valid weight modifier");
            return entry.Values[index];
        }
        foreach(var frame in frames.Items)
        {
            var assignment=Input.Validation.FrameAssignments?.Items.SingleOrDefault(a=>a.Id==frame.Id)??throw Missing("frame material overwrite and geometry type");
            if(assignment.GeometryType!="Straight")throw Missing("supported straight-frame self-weight geometry");
            var section=frameSections.Items.SingleOrDefault(s=>s.Name==frame.Section)??throw Missing("frame section "+frame.Section);
            var area=SectionGeometry.GrossArea(section);
            if(!double.IsFinite(area))throw Missing("supported rectangular or circular frame section area");
            var gamma=Gamma(string.IsNullOrWhiteSpace(assignment.MaterialOverwrite)?section.Material:assignment.MaterialOverwrite);
            total+=new Number(area*GeometryMath.Distance(frame.Start,frame.End)*gamma*Modifier("FrameObj",frame.Id,mass?6:7)*Modifier("PropFrame",section.Name,mass?6:7));
        }
        foreach(var area in areas.Items.Where(a=>!a.IsOpening))
        {
            var assignment=Input.Validation.AreaAssignments?.Items.SingleOrDefault(a=>a.Id==area.Id)??throw Missing("area material overwrite");
            var section=areaSections.Items.SingleOrDefault(s=>s.Name==area.Section)??throw Missing("homogeneous area section");
            if(section.MembraneThickness is not >0||!double.IsFinite(section.MembraneThickness.Value)||section.Limitation is not null)throw Missing("supported homogeneous shell thickness");
            var gamma=Gamma(string.IsNullOrWhiteSpace(assignment.MaterialOverwrite)?section.Material:assignment.MaterialOverwrite);
            total+=new Number(NetArea(area)*section.MembraneThickness.Value*gamma*Modifier("AreaObj",area.Id,mass?8:9)*Modifier("PropArea",section.Name,mass?8:9));
        }
        return total;
    }

    private Coordinate[] Points(Area area)=>area.Joints.Select(id=>Input.Model.Joints?.Items.SingleOrDefault(j=>j.Id==id)?.Position??throw Missing("area joint "+id)).ToArray();
    public double NetArea(Area area)
    {
        var points=Points(area);var gross=PolygonArea(points);
        var openings=Input.Model.Areas?.Items.Where(a=>a.IsOpening&&a.Story==area.Story).ToArray()??throw Missing("opening inventory");
        if(openings.Length==0)return gross;
        if(area.Orientation!="Floor")
        {
            if(openings.Any(o=>Points(o).Max(p=>p.Z)-Points(o).Min(p=>p.Z)>1e-8))throw Missing("wall-opening intersections");
            return gross; // Horizontal floor openings do not remove finite area from a vertical wall.
        }
        static (double X0,double X1,double Y0,double Y1,double Z) Rect(Coordinate[] p)
        {
            var x0=p.Min(q=>q.X);var x1=p.Max(q=>q.X);var y0=p.Min(q=>q.Y);var y1=p.Max(q=>q.Y);var z=p[0].Z;
            if(p.Length!=4||p.Any(q=>Math.Abs(q.Z-z)>1e-8)||p.Any(q=>q.X!=x0&&q.X!=x1||q.Y!=y0&&q.Y!=y1)||p.Distinct().Count()!=4)
                throw Missing("axis-aligned rectangular floor/opening clipping geometry");
            return(x0,x1,y0,y1,z);
        }
        var bounds=Rect(points);
        var clipped=new List<(double X0,double X1,double Y0,double Y1)>();
        foreach(var opening in openings)
        {
            var cut=Rect(Points(opening));if(Math.Abs(cut.Z-bounds.Z)>1e-8)continue;
            var x0=Math.Max(bounds.X0,cut.X0);var x1=Math.Min(bounds.X1,cut.X1);var y0=Math.Max(bounds.Y0,cut.Y0);var y1=Math.Min(bounds.Y1,cut.Y1);
            if(x1>x0&&y1>y0)clipped.Add((x0,x1,y0,y1));
        }
        var xs=clipped.SelectMany(r=>new[]{r.X0,r.X1}).Distinct().Order().ToArray();double removed=0;
        for(int i=0;i+1<xs.Length;i++)
        {
            var spans=clipped.Where(r=>r.X0<xs[i+1]&&r.X1>xs[i]).Select(r=>(r.Y0,r.Y1)).OrderBy(r=>r.Y0).ToArray();
            double length=0,end=double.NegativeInfinity;
            foreach(var span in spans){length+=Math.Max(0,span.Y1-Math.Max(end,span.Y0));end=Math.Max(end,span.Y1);}
            removed+=(xs[i+1]-xs[i])*length;
        }
        if(removed>gross*(1+1e-8))throw Missing("nonnegative net loaded area");
        return Math.Max(0,gross-removed);
    }
    public Number PatternWeight(string name, bool forMass = false)
    {
        var patterns = Input.Validation.LoadPatterns is { ReadSucceeded: true } p ? p.Items.Where(x => x.Name == name).ToArray() : [];
        if (patterns.Length != 1) throw Missing("load-pattern definition " + name);
        var pattern = patterns[0];
        if (!double.IsFinite(pattern.SelfWeightMultiplier) || forMass && pattern.SelfWeightMultiplier < 0) throw Missing("nonnegative mass-producing self-weight contributions");
        if (pattern.Type is "Wind" or "Other" || pattern.Type == "Quake" && pattern.AutoSeismicCode != "") throw Missing("a supported explicit load definition for " + name);
        var inventory = Evidence.Inventory;
        string[] mandatory = ["Area Load Assignments - Uniform", "Frame Loads Assignments - Distributed", "Joint Loads Assignments - Force"];
        if (mandatory.Any(key => !inventory.Any(t => t.Key == key))) throw Missing("complete point, line, and area load-assignment inventory");
        Number weight = pattern.SelfWeightMultiplier == 0 ? new(0) : SelfWeight() * pattern.SelfWeightMultiplier;
        foreach (var entry in inventory.Where(t => !t.IsEmpty && (t.Key.Contains("Load Assignments", StringComparison.Ordinal) || t.Key.Contains("Loads Assignments", StringComparison.Ordinal))))
        {
            if (entry.Key == "Frame Loads Assignments - Open Structure Wind Parameters") continue; // Parameters are not an applied load; Wind patterns are rejected above.
            var table = Table(entry.Key, "LoadPattern");
            var rows = table.Rows.Where(r => S(r, "LoadPattern") == name).ToArray();
            if (rows.Length == 0) continue;
            if (entry.Key == "Area Load Assignments - Uniform")
            {
                var precise=Evidence.GravityLoads is {ReadSucceeded:true} read?read.Items.Where(l=>l.ObjectType=="Area"&&l.Pattern==name).ToArray():throw Missing("full-precision area load assignments");
                if(precise.Length!=rows.Length)throw Missing("matching area API/table assignment coverage");
                foreach (var row in precise)
                {
                    if (row.Direction!=10||row.CoordinateSystem!="Global") throw Missing("supported global gravity direction for area loading");
                    if (forMass && row.ValueStart < 0) throw Missing("joint-level mass integration for upward loads or deductions");
                    var area = Input.Model.Areas?.Items.SingleOrDefault(a => a.Id == row.ObjectId) ?? throw Missing("loaded area geometry");
                    if (area.IsOpening) throw Missing("a structural loaded area rather than an opening");
                    weight += new Number(row.ValueStart*NetArea(area));
                }
            }
            else if (entry.Key == "Frame Loads Assignments - Distributed")
            {
                var precise=Evidence.GravityLoads is {ReadSucceeded:true} read?read.Items.Where(l=>l.ObjectType=="Frame"&&l.Pattern==name).ToArray():throw Missing("full-precision frame load assignments");
                if(precise.Length!=rows.Length)throw Missing("matching line-load API/table assignment coverage");
                foreach (var row in precise)
                {
                    if (row.Direction!=10||row.CoordinateSystem!="Global"||row.LoadType!=1) throw Missing("supported global gravity force direction for line loading");
                    var frame = Input.Model.Frames?.Items.SingleOrDefault(f => f.Id == row.ObjectId) ?? throw Missing("loaded frame geometry");
                    if (Input.Validation.FrameAssignments?.Items.SingleOrDefault(f => f.Id == frame.Id)?.GeometryType != "Straight") throw Missing("verified straight loaded frame geometry");
                    if (forMass && (row.ValueStart < 0 || row.ValueEnd < 0)) throw Missing("joint-level mass integration for upward line loads");
                    var length = GeometryMath.Distance(frame.Start, frame.End);
                    var span=row.End-row.Start;
                    if (!double.IsFinite(span)||row.Start<0||span < 0 || row.End > length * (1+1e-8)) throw Missing("line-load limits within the member");
                    weight += new Number((row.ValueStart+row.ValueEnd)*.5*span);
                }
            }
            else throw Missing("implemented integration of " + entry.Key + " for " + name);
        }
        return weight;
    }
    public Number CaseWeight(LoadCase loadCase)
    {
        Finished(loadCase.Name);
        if (loadCase.Loads is not { Count: > 0 } loads || loads.Any(l => l.Type != "Load" || !double.IsFinite(l.Factor))) throw Missing("explicit static load components for " + loadCase.Name);
        Number weight = new(0);
        foreach (var load in loads.Where(l => l.Factor != 0)) weight += PatternWeight(load.Name) * load.Factor;
        return weight;
    }
}
