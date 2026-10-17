using System.Text.Json;

public static partial class ExtendedPhase3Checks
{
    private static IReadOnlyList<Observation> Mass(InspectionContext c)
    {
        var sources=c.Input.Validation.MassSources?.Items.Where(x=>x.IsDefault).ToArray()??[];
        if(sources.Length!=1)throw new Missing("Exactly one default mass source is required for this review.");var source=sources[0];List<Observation> output=[];
        var patterns=c.Input.Validation.LoadPatterns?.Items.ToDictionary(x=>x.Name)??throw new Missing("Load patterns are unavailable.");
        if(source.IncludeElementMass&&source.IncludeLoads&&source.Loads.Any(x=>x.Factor>0&&patterns.TryGetValue(x.Pattern,out var p)&&p.SelfWeightMultiplier>0))
            output.Add(O(CheckStatus.WARNING,"Mass source includes element self mass and loads containing self weight; potential double counting requires review.",source.Name));
        var t=Table(c,"Mass Summary by Group","Group","MassX","MassY");var rows=t.Rows.Where(x=>S(x,"Group")=="All").ToArray();if(rows.Length!=1)throw new Missing("Unique All-group assembled mass is unavailable.");
        var length=c.Input.Model.Identity.DisplayUnits.Length;var gravity=length switch{"mm"=>9806.65,"m"=>9.80665,"cm"=>980.665,"ft"=>32.1740485564,"in"=>386.088582677,_=>throw new Missing("Unsupported display length for mass-to-weight conversion.")};
        foreach(var direction in new[]{"X","Y"})
        {
            var mass=N(rows[0],"Mass"+direction);var weight=mass*gravity;
            if(!double.IsFinite(weight)||weight<=0)throw new Missing($"Assembled lateral Mass{direction} is not positive and finite.");
            output.Add(O(CheckStatus.PASS,$"ETABS assembled lateral Mass{direction} was converted to weight using gravity in the model display units; mass-source configuration was read from the live model.","weight/"+direction,weight,null,c.Input.Model.Identity.DisplayUnits.Force,
                Ev(t.Key,$"Group=All; Mass{direction}={mass:G17}; g={gravity:G17} {length}/s²; mass source={source.Name}; elementMass={source.IncludeElementMass}; addedMass={source.IncludeAddedMass}; loads={source.IncludeLoads}")));
        }
        return output;
    }
    private static IReadOnlyList<Observation> Period(InspectionContext c)
    {
        var (mode, estimate)=CalculateDynamicPeriod(c);
        var status=mode.Period<=estimate.UpperPeriod?CheckStatus.PASS:CheckStatus.FAIL;
        var certified=c.Configuration.Settings.Confirmed&&c.Configuration.Settings.Seismic is {ValueKind:JsonValueKind.Object} meta&&meta.TryGetProperty("siteSpecificHazardConfirmed",out var confirmation)&&confirmation.ValueKind==JsonValueKind.True;
        var message=status==CheckStatus.PASS?"ETABS Mode 1 period does not exceed the ASCE 7-16 CuTa upper bound calculated from live story height and configured SFRS."+
            (certified?"":" The comparison uses configured hazard/SFRS assumptions; independent hazard certification is pending."):"ETABS Mode 1 period exceeds the ASCE 7-16 CuTa upper bound calculated from live story height and configured SFRS.";
        return [O(status,message,mode.Case,mode.Period,estimate.UpperPeriod,"s",
            Ev("Modal Participating Mass Ratios + Model.Stories + configuration.seismic.system",$"T1={mode.Period:G17}s; hn={estimate.HeightMeters:G17}m; Ct={estimate.Ct:G17}; x={estimate.X:G17}; Ta={estimate.Ta:G17}s; SD1={estimate.Sd1:G17}g; Cu={estimate.Cu:G17}; CuTa={estimate.UpperPeriod:G17}s; hazard/SFRS certification={(certified?"confirmed":"pending")}"))];
    }

    private static (ModalParticipationResult Mode,Asce716Seismic.Period Period) CalculateDynamicPeriod(InspectionContext c)
    {
        var storyData=c.Input.Model.Stories??throw new Missing("Story elevations are unavailable.");
        var stories=storyData.Stories;if(stories.Count==0)throw new Missing("No story elevations are available.");
        var height=stories.Max(s=>s.Elevation)-storyData.BaseElevation;
        var toMeters=c.Input.Model.Identity.DisplayUnits.Length switch{"mm"=>.001,"cm"=>.01,"m"=>1.0,"ft"=>.3048,"in"=>.0254,_=>throw new Missing("Unsupported length unit for ASCE empirical period.")};
        var meta=c.Configuration.Settings.Seismic??throw new Missing("Seismic settings are unavailable.");
        var site=Asce716Seismic.RequiredString(meta,"siteClass");var ss=Asce716Seismic.RequiredNumber(meta,"ss");var s1=Asce716Seismic.RequiredNumber(meta,"s1");
        var hazard=Asce716Seismic.CalculateHazard(site,ss,s1);var system=Asce716Seismic.RequiredString(meta,"system");
        var calculated=Asce716Seismic.CalculatePeriod(system,height*toMeters,hazard.Sd1);
        var mode=ResolveModeOne(c);
        if(mode is null)throw new Missing("A unique finished Mode 1 period could not be matched between ETABS modal participation results and the Modal Periods And Frequencies table.");
        return (mode,calculated);
    }

    /// <summary>
    /// ETABS 22.7's modal participation API can return blank StepType and zero
    /// StepNumber for every mode. Resolve Mode 1 against its database table row,
    /// matching on case and the period rounded for display; use explicit API mode
    /// numbering first when a version supplies it.
    /// </summary>
    internal static ModalParticipationResult? ResolveModeOne(InspectionContext c)
    {
        var apiRows=c.Input.Analysis?.Results?.ModalParticipation.Items;
        var modalCases=c.Input.Validation.LoadCases?.Items
            .Where(x=>string.Equals(x.Type,"Modal",StringComparison.OrdinalIgnoreCase))
            .Select(x=>x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(apiRows is null||modalCases is null||modalCases.Count==0)return null;
        bool IsFinished(string name)=>c.Input.Analysis?.Status?.Cases.Items
            .Any(x=>string.Equals(x.Name,name,StringComparison.OrdinalIgnoreCase)&&x.State==AnalysisCaseState.Finished)==true;
        var explicitRows=apiRows.Where(x=>modalCases.Contains(x.Case)&&
            string.Equals(x.StepType,"Mode",StringComparison.OrdinalIgnoreCase)&&
            Math.Abs(x.StepNumber-1)<1e-8&&double.IsFinite(x.Period)&&x.Period>0&&IsFinished(x.Case)).ToArray();
        if(explicitRows.Length==1)return explicitRows[0];

        var table=c.Input.Analysis?.Results?.Extended?.Tables
            .SingleOrDefault(x=>x.Key.Equals("Modal Periods And Frequencies",StringComparison.OrdinalIgnoreCase));
        if(table is null||table.UnavailableReason is not null||
           !table.Fields.Contains("Case",StringComparer.OrdinalIgnoreCase)||
           !table.Fields.Contains("Mode",StringComparer.OrdinalIgnoreCase)||
           !table.Fields.Contains("Period",StringComparer.OrdinalIgnoreCase))return null;
        var modeRows=table.Rows.Where(r=>TryNumber(r,"Mode",out var n)&&Math.Abs(n-1)<1e-8&&
            r.TryGetValue("Case",out var name)&&modalCases.Contains(name)).ToArray();
        var matches=new List<ModalParticipationResult>();
        foreach(var row in modeRows)
        {
            if(!row.TryGetValue("Case",out var caseName)||!TryNumber(row,"Period",out var period)||period<=0)continue;
            var source=row.TryGetValue("Period",out var rawPeriod)?rawPeriod:string.Empty;
            var decimals=source.Contains('.')?source.Length-source.IndexOf('.')-1:0;
            var tolerance=Math.Max(1e-6,0.500001*Math.Pow(10,-decimals));
            matches.AddRange(apiRows.Where(x=>string.Equals(x.Case,caseName,StringComparison.OrdinalIgnoreCase)&&
                double.IsFinite(x.Period)&&x.Period>0&&Math.Abs(x.Period-period)<=tolerance&&IsFinished(x.Case)));
        }
        var distinct=matches.Distinct().ToArray();
        return distinct.Length==1?distinct[0]:null;

        static bool TryNumber(IReadOnlyDictionary<string,string> row,string key,out double number)
        {
            number=0;
            return row.TryGetValue(key,out var value)&&double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&double.IsFinite(number);
        }
    }
    private static IReadOnlyList<Observation> ModeShapes(InspectionContext c)
    {
        var modalCases=c.Input.Validation.LoadCases?.Items.Where(x=>x.Type=="Modal").Select(x=>x.Name).ToHashSet()??[];
        var periods=Table(c,"Modal Periods And Frequencies","Case","Mode","Period").Rows.Where(x=>modalCases.Contains(S(x,"Case"))).ToArray();
        if(periods.Length==0)throw new Missing("No modal periods were returned for defined modal cases.");
        var modes=E(c).ModeShapes.Where(x=>modalCases.Contains(x.Case)&&string.Equals(x.StepType,"Mode",StringComparison.OrdinalIgnoreCase)&&periods.Any(p=>S(p,"Case")==x.Case&&Math.Abs(N(p,"Mode")-x.Mode)<1e-8)).ToArray();List<Observation> output=[];
        foreach(var g in modes.GroupBy(x=>(x.Case,x.Mode)))
        {var zero=g.All(x=>x.UX==0&&x.UY==0&&x.UZ==0&&x.RX==0&&x.RY==0&&x.RZ==0);output.Add(O(zero?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,zero?"All returned mode-shape components are zero.":"Nonzero mode shape extracted; mass orthogonality and mechanism classification require stiffness/mass matrices and restraint review.",$"{g.Key.Case}/{g.Key.Mode}",actual:g.Count(),unit:"joint rows",evidence:[Ev("Results.ModeShape","All group; selected modal outputs") ]));}
        foreach(var mode in periods.Where(x=>!modes.Any(m=>m.Case==S(x,"Case")&&Math.Abs(m.Mode-N(x,"Mode"))<1e-8)))
            output.Add(O(CheckStatus.NOT_VERIFIED,"Modal period exists but no corresponding mode-shape rows were extracted under current output options.",$"missing/{S(mode,"Case")}/{N(mode,"Mode"):G17}",N(mode,"Period"),unit:"s",evidence:[Ev("Modal Periods And Frequencies","Case/Mode/Period enumerates expected modes")]));
        return output;
    }
    private static IReadOnlyList<Observation> Torsion(InspectionContext c)
    {
        var t=Table(c,"Diaphragm Max Over Avg Drifts","Story","OutputCase","Item","Ratio");List<Observation> output=[];
        var approved=c.Configuration.Settings.Phase3Review is {ReferencesConfirmed:true,TorsionEnvelopeConfirmed:true,ReferenceSource:not null};
        foreach(var axis in Axes(c))foreach(var group in t.Rows.Where(x=>S(x,"OutputCase")==axis.ElasticCase&&S(x,"Item").EndsWith(" "+axis.Direction,StringComparison.Ordinal)).GroupBy(x=>S(x,"Story")))
        {var ratio=group.Max(x=>N(x,"Ratio"));output.Add(O(ratio>1.2?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,
            approved?(ratio>1.4?"Extreme torsional drift-ratio trigger (>1.4).":"Torsional max/average ratio compared with 1.2 trigger."):"Preliminary diaphragm max/average drift indicator; both eccentricity signs, edge drifts and diaphragm applicability must be established.",group.Key+"/"+axis.Direction,ratio,1.2,"ratio",Ev(t.Key,$"{group.Count()} rows; approved envelope={approved}")));}
        return Nonempty(output,"No diaphragm drift ratios match the seismic cases.");
    }
    private static IReadOnlyList<Observation> SoftStory(InspectionContext c)
    {
        var t=Table(c,"Story Stiffness","Story","OutputCase","StiffX","StiffY");var stories=c.Input.Model.Stories?.Stories.OrderBy(x=>x.Elevation).ToArray()??throw new Missing("Story elevations are unavailable.");List<Observation> output=[];
        foreach(var axis in Axes(c))
        {
            double? Stiff(string story){var rows=t.Rows.Where(x=>S(x,"Story")==story&&S(x,"OutputCase")==axis.ElasticCase).ToArray();return rows.Length==1?N(rows[0],"Stiff"+axis.Direction):null;}
            for(int i=0;i<stories.Length-1;i++)
            {var current=Stiff(stories[i].Name);var above=Stiff(stories[i+1].Name);if(current is null||above is null||current<=0||above<=0){output.Add(O(CheckStatus.NOT_VERIFIED,"Missing or nonpositive directional story stiffness.",stories[i].Name+"/"+axis.Direction));continue;}
             var ratio=current.Value/above.Value;var avgRatio=(double?)null;
             if(i+3<stories.Length){var upper=stories.Skip(i+1).Take(3).Select(x=>Stiff(x.Name)).ToArray();if(upper.All(x=>x>0))avgRatio=current/upper.Average(x=>x!.Value);}
             var trigger=ratio<.7||avgRatio<.8;var extreme=ratio<.6||avgRatio<.7;
             output.Add(O(trigger?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,"Preliminary stiffness irregularity: "+(extreme?"extreme soft-story threshold triggered.":trigger?"soft-story threshold triggered.":"neither evaluated threshold triggered; applicability/exceptions and stiffness definition remain to be verified."),stories[i].Name+"/"+axis.Direction,ratio,.7,"ratio",Ev(t.Key,$"K/above={ratio:G17}; K/average3={avgRatio}; thresholds .7/.8, extreme .6/.7; one case and step schema only")));
            }
        }return Nonempty(output,"At least two story stiffness results are required.");
    }
    private static IReadOnlyList<Observation> Vertical(InspectionContext c)
    {
        var stories=c.Input.Model.Stories?.Stories.OrderBy(x=>x.Elevation).ToArray()
            ??throw new Missing("Story elevations are unavailable.");
        var joints=c.Input.Model.Joints?.Items
            ??throw new Missing("Joint coordinates are unavailable for automatic story-dimension calculation.");
        if(stories.Length<2)throw new Missing("At least two stories are required for an adjacent-story comparison.");

        var dimensions=stories.Select(story=>(Story:story,Joints:joints.Where(j=>
        {
            var tolerance=Math.Max(1e-6,Math.Abs(story.Elevation)*1e-9);
            return Math.Abs(j.Position.Z-story.Elevation)<=tolerance;
        }).ToArray())).ToArray();
        List<Observation> output=[];
        for(int i=0;i<dimensions.Length-1;i++)
        foreach(var direction in new[]{"X","Y"})
        {
            var lower=dimensions[i];var upper=dimensions[i+1];
            var lowerDimension=lower.Joints.Length==0?0:direction=="X"
                ?lower.Joints.Max(j=>j.Position.X)-lower.Joints.Min(j=>j.Position.X)
                :lower.Joints.Max(j=>j.Position.Y)-lower.Joints.Min(j=>j.Position.Y);
            var upperDimension=upper.Joints.Length==0?0:direction=="X"
                ?upper.Joints.Max(j=>j.Position.X)-upper.Joints.Min(j=>j.Position.X)
                :upper.Joints.Max(j=>j.Position.Y)-upper.Joints.Min(j=>j.Position.Y);
            var id=lower.Story.Name+"/"+direction;
            if(lower.Joints.Length==0||upper.Joints.Length==0||lowerDimension<=0||upperDimension<=0)
            {
                output.Add(O(CheckStatus.NOT_VERIFIED,"Story bounding dimensions could not be calculated from unique joint coordinates at both adjacent elevations.",id,
                    evidence:[Ev("Model.Joints + Model.Stories",$"automatic bounding-coordinate calculation; {lower.Story.Name}: elevation={lower.Story.Elevation:G17}, joints={lower.Joints.Length}, {direction} span={lowerDimension:G17}; {upper.Story.Name}: elevation={upper.Story.Elevation:G17}, joints={upper.Joints.Length}, {direction} span={upperDimension:G17}")]));
                continue;
            }

            var ratio=Math.Max(lowerDimension,upperDimension)/Math.Min(lowerDimension,upperDimension);
            output.Add(O(ratio>1.3?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,
                ratio>1.3
                    ?"Automatically calculated adjacent-story bounding dimension ratio exceeds 1.3; potential ASCE 7-16 vertical geometric irregularity requires SFRS extent and exception review."
                    :"Automatically calculated adjacent-story bounding dimension ratio does not exceed 1.3; full ASCE 7-16 compliance is not verified because floor bounds are a proxy for SFRS extent.",
                id,ratio,1.3,"ratio",Ev("Model.Joints + Model.Stories",
                    $"dimensions calculated automatically from bounding joint coordinates at story elevations; direction={direction}; {lower.Story.Name}={lowerDimension:G17} ({lower.Joints.Length} joints), {upper.Story.Name}={upperDimension:G17} ({upper.Joints.Length} joints); no JSON lateral-dimension reference used")));
        }
        return output;
    }
    private static IReadOnlyList<Observation> Adjacent(InspectionContext c,(string Story,string Direction,double Value)[] values,bool larger,double limit,string message)
    {
        var stories=c.Input.Model.Stories?.Stories.OrderBy(x=>x.Elevation).ToArray()??throw new Missing("Story elevations are unavailable.");List<Observation> output=[];
        foreach(var direction in new[]{"X","Y"})for(int i=0;i<stories.Length-1;i++)
        {var a=values.Where(x=>x.Story==stories[i].Name&&x.Direction==direction).ToArray();var b=values.Where(x=>x.Story==stories[i+1].Name&&x.Direction==direction).ToArray();
         var id=stories[i].Name+"/"+direction;if(a.Length!=1||b.Length!=1){output.Add(O(CheckStatus.NOT_VERIFIED,"Missing unique reference values for adjacent stories.",id));continue;}
         var ratio=larger?Math.Max(a[0].Value,b[0].Value)/Math.Min(a[0].Value,b[0].Value):a[0].Value/b[0].Value;
         output.Add(O(larger?(ratio>limit?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED):(ratio<limit?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED),message,id,ratio,limit,"ratio",Ev("phase3Review independent references",$"{a[0].Value:G17}/{b[0].Value:G17}")));}
        return Nonempty(output,"At least two stories and reference values are required.");
    }
    private static IReadOnlyList<Observation> Plan(InspectionContext c)
    {
        var stories=c.Input.Model.Stories?.Stories??throw new Missing("Story elevations are unavailable.");
        var areas=c.Input.Model.Areas?.Items??throw new Missing("Area geometry is unavailable.");
        var joints=c.Input.Model.Joints?.Items.ToDictionary(x=>x.Id)??throw new Missing("Joint coordinates are unavailable.");
        List<Observation> output=[];
        foreach(var story in stories)
        {
            var floorAreas=areas.Where(a=>a.Story==story.Name&&a.Orientation.Equals("Floor",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(floorAreas.Length==0){output.Add(O(CheckStatus.NOT_VERIFIED,"No horizontal floor areas were extracted at this story; opening and plan-span screening is unavailable.",story.Name));continue;}
            var geometry=floorAreas.Select(a=>(Area:a,Coordinates:a.Joints.Where(joints.ContainsKey).Select(id=>joints[id].Position).ToArray())).ToArray();
            if(geometry.Any(x=>x.Coordinates.Length<3)){output.Add(O(CheckStatus.NOT_VERIFIED,"At least one floor/opening object has fewer than three extracted joint coordinates; polygon areas cannot be established.",story.Name));continue;}
            var gross=geometry.Where(x=>!x.Area.IsOpening).Sum(x=>PolygonArea(x.Coordinates));
            var opening=geometry.Where(x=>x.Area.IsOpening).Sum(x=>PolygonArea(x.Coordinates));
            if(!double.IsFinite(gross)||!double.IsFinite(opening)||gross<=0){output.Add(O(CheckStatus.NOT_VERIFIED,"Positive finite gross non-opening floor area is required to calculate the diaphragm opening ratio.",story.Name));continue;}
            var ratio=opening/gross;
            var points=geometry.Where(x=>!x.Area.IsOpening).SelectMany(x=>x.Coordinates).ToArray();
            var spanX=points.Max(p=>p.X)-points.Min(p=>p.X);var spanY=points.Max(p=>p.Y)-points.Min(p=>p.Y);
            var evidence=Ev("Model.Areas + Model.Joints",$"story={story.Name}; gross floor area={gross:G17}; opening area={opening:G17}; opening/gross={ratio:G17}; bounding spans X={spanX:G17}, Y={spanY:G17} {c.Input.Model.Identity.DisplayUnits.Length}; floor objects={geometry.Count(x=>!x.Area.IsOpening)}; openings={geometry.Count(x=>x.Area.IsOpening)}; automatically calculated from polygon joint coordinates");
            var exceedsOpeningTrigger=ratio>.5+1e-9;
            output.Add(O(exceedsOpeningTrigger?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,
                exceedsOpeningTrigger?"Automatically calculated diaphragm opening ratio exceeds 0.50; potential ASCE 7-16 Type 3 discontinuity requires diaphragm-level applicability and force-transfer review.":"Automatically calculated diaphragm opening ratio does not exceed 0.50; stiffness discontinuity, reentrant corners, and full diaphragm compliance remain unverified.",story.Name,ratio,.5,"ratio",evidence));
        }
        return Nonempty(output,"Story data are unavailable.");
    }

    private static double PolygonArea(IReadOnlyList<Coordinate> points)
    {
        double twice=0;
        for(var i=0;i<points.Count;i++){var a=points[i];var b=points[(i+1)%points.Count];twice+=a.X*b.Y-b.X*a.Y;}
        return Math.Abs(twice)/2;
    }
}


public static partial class ExtendedPhase3Checks
{
    private static IReadOnlyList<Observation> ShearDistribution(InspectionContext c)
    {
        var rows=c.Input.Analysis?.Results?.StoryForces?.Items??throw new Missing("Story Forces are unavailable.");var stories=c.Input.Model.Stories?.Stories.OrderBy(x=>x.Elevation).ToArray()??throw new Missing("Story elevations unavailable.");List<Observation> output=[];
        foreach(var axis in Axes(c))for(int i=0;i<stories.Length;i++)
        {var current=SeismicCriteriaChecks.SeismicEnvelope(rows.Where(x=>x.Case==axis.ElasticCase&&x.Story==stories[i].Name&&x.Location=="Bottom"),x=>x.StepType);var above=i+1<stories.Length?SeismicCriteriaChecks.SeismicEnvelope(rows.Where(x=>x.Case==axis.ElasticCase&&x.Story==stories[i+1].Name&&x.Location=="Bottom"),x=>x.StepType):[];
         if(current.Length==0||i+1<stories.Length&&above.Length==0){output.Add(O(CheckStatus.NOT_VERIFIED,"Missing static story-bottom shear results, including Max/Min envelope steps.",stories[i].Name+"/"+axis.Direction));continue;}
         double V(StoryForceResult x)=>Math.Abs(axis.Direction=="X"?x.VX:x.VY);var currentEnvelope=current.Max(V);var aboveEnvelope=above.Length>0?above.Max(V):0;var force=currentEnvelope-aboveEnvelope;
         output.Add(O(CheckStatus.PASS,"Floor lateral-force increment was derived from the extracted story-shear envelope and is available for review; comparison with the theoretical Cvx distribution is reported separately by SAN-008.",stories[i].Name+"/"+axis.Direction,force,unit:c.Input.Model.Identity.DisplayUnits.Force,evidence:[Ev("Story Forces",$"max |Vbottom(current)|={currentEnvelope:G17}; max |Vbottom(above)|={aboveEnvelope:G17}; accepted Single Value, Max/Min and Step By Step rows; independent Cvx comparison is SAN-008")]));}
        return Nonempty(output,"No matching story shears available.");
    }
    private static IReadOnlyList<Observation> BaseShear(InspectionContext c)
    {
        var t=Table(c,"Load Pattern Definitions - Auto Seismic - ASCE 7-16","Name","IsAuto","BaseShear","WeightUsed","CoeffUsed","XDir","XDirPlusE","XDirMinusE","YDir","YDirPlusE","YDirMinusE");
        var massTable=Table(c,"Mass Summary by Group","Group","MassX","MassY");var massRows=massTable.Rows.Where(x=>S(x,"Group")=="All").ToArray();if(massRows.Length!=1)throw new Missing("Unique All-group assembled lateral mass is unavailable.");
        var length=c.Input.Model.Identity.DisplayUnits.Length;var gravity=length switch{"mm"=>9806.65,"m"=>9.80665,"cm"=>980.665,"ft"=>32.1740485564,"in"=>386.088582677,_=>throw new Missing("Unsupported display length for mass-to-weight conversion.")};
        var meta=c.Configuration.Settings.Seismic??throw new Missing("Seismic hazard settings are unavailable.");var site=Asce716Seismic.RequiredString(meta,"siteClass");
        var ss=Asce716Seismic.RequiredNumber(meta,"ss");var s1=Asce716Seismic.RequiredNumber(meta,"s1");var r=Asce716Seismic.RequiredNumber(meta,"r");var ie=Asce716Seismic.Factor(c.Configuration.Settings,"ie");
        var hazard=Asce716Seismic.CalculateHazard(site,ss,s1);var (periodResult,period)=CalculateDynamicPeriod(c);
        var designPeriod=Math.Min(periodResult.Period,period.UpperPeriod);List<Observation> output=[];
        var seismicAxes=Axes(c);
        foreach(var row in t.Rows.Where(x=>IsTrue(S(x,"IsAuto"))))
        {
            var directionFlags=new[]{("X",IsTrue(S(row,"XDir"))||IsTrue(S(row,"XDirPlusE"))||IsTrue(S(row,"XDirMinusE"))),("Y",IsTrue(S(row,"YDir"))||IsTrue(S(row,"YDirPlusE"))||IsTrue(S(row,"YDirMinusE")))}.Where(x=>x.Item2).ToArray();
            if(directionFlags.Length!=1){output.Add(O(CheckStatus.NOT_VERIFIED,"Auto-seismic pattern does not identify exactly one active global direction.",S(row,"Name")));continue;}
            var direction=directionFlags[0].Item1;var pattern=S(row,"Name");
            var axes=seismicAxes.Where(x=>x.Direction==direction).ToArray();
            if(axes.Length!=1){output.Add(O(CheckStatus.NOT_VERIFIED,"A unique configured elastic seismic case is required to compare the auto-seismic pattern reaction.",pattern));continue;}
            var candidates=c.Input.Validation.LoadCases?.Items.Where(x=>string.Equals(x.Name,axes[0].ElasticCase,StringComparison.OrdinalIgnoreCase)&&string.Equals(x.Type,"LinearStatic",StringComparison.OrdinalIgnoreCase)).ToArray()??[];
            if(candidates.Length!=1){output.Add(O(CheckStatus.NOT_VERIFIED,"A unique mapped linear-static elastic case is required to compare base reactions.",pattern));continue;}
            var reactionRows=SeismicCriteriaChecks.SeismicEnvelope(c.Input.Analysis?.Results?.BaseReactions.Items.Where(x=>string.Equals(x.Case,candidates[0].Name,StringComparison.OrdinalIgnoreCase))??[],x=>x.StepType);
            if(reactionRows.Length==0){output.Add(O(CheckStatus.NOT_VERIFIED,"Static base reaction results, including Max/Min envelope steps, are required for the mapped auto-seismic case.",pattern));continue;}
            var mass=N(massRows[0],"Mass"+direction);var weight=mass*gravity;
            var cs=Asce716Seismic.ResponseCoefficient(hazard.Sds,hazard.Sd1,designPeriod,r,ie,s1);var calculatedShear=cs*weight;
            var etabsShear=N(row,"BaseShear");var etabsWeight=N(row,"WeightUsed");var etabsCs=N(row,"CoeffUsed");var reaction=reactionRows.Max(x=>Math.Abs(direction=="X"?x.FX:x.FY));
            var tolerance=1e-5*Math.Max(calculatedShear,1e-9);
            var detail=$"ASCE 7-16 11.4.5 and 12.8.1.1; site={site}; Fa={hazard.Fa:G17}; Fv={hazard.Fv:G17}; SDS={hazard.Sds:G17}g; SD1={hazard.Sd1:G17}g; Tdesign={designPeriod:G17}s; R={r:G17}; Ie={ie:G17}; Cs={cs:G17}; W={weight:G17} {c.Input.Model.Identity.DisplayUnits.Force}; Vcalc={calculatedShear:G17}; ETABS pattern={pattern}, Cs={etabsCs:G17}, W={etabsWeight:G17}, V={etabsShear:G17}; reaction={reaction:G17}; numerical comparison tolerance=1e-5 relative";
            output.Add(O(Math.Abs(calculatedShear-etabsShear)<=tolerance?CheckStatus.PASS:CheckStatus.FAIL,"ASCE 7-16 theoretical base shear from the extracted assembled lateral mass is compared with ETABS auto-seismic base shear using a 1e-5 relative numerical tolerance.",pattern+"/auto",Math.Abs(calculatedShear-etabsShear),tolerance,c.Input.Model.Identity.DisplayUnits.Force,Ev(t.Key+" + "+massTable.Key,detail)));
            output.Add(O(Math.Abs(calculatedShear-reaction)<=tolerance?CheckStatus.PASS:CheckStatus.FAIL,"ASCE 7-16 theoretical base shear is compared with the mapped ETABS static base reaction absolute envelope using a 1e-5 relative numerical tolerance.",pattern+"/reaction",Math.Abs(calculatedShear-reaction),tolerance,c.Input.Model.Identity.DisplayUnits.Force,Ev("Results.BaseReact + dynamic ASCE 7-16 calculation",detail)));
            var autoProduct=etabsWeight*etabsCs;output.Add(O(Math.Abs(autoProduct-etabsShear)<=tolerance?CheckStatus.PASS:CheckStatus.FAIL,"ETABS auto-seismic base shear is checked against its reported coefficient times reported seismic weight.",pattern+"/internal",Math.Abs(autoProduct-etabsShear),tolerance,c.Input.Model.Identity.DisplayUnits.Force,Ev(t.Key,detail)));
        }
        return output.Count>0?output:[O(CheckStatus.NOT_VERIFIED,"No ASCE 7-16 auto-seismic patterns were extracted.")];
    }

    private static bool IsTrue(string value)=>value.Equals("Yes",StringComparison.OrdinalIgnoreCase)||value.Equals("True",StringComparison.OrdinalIgnoreCase)||value=="1"||value.Equals("X",StringComparison.OrdinalIgnoreCase)||value.Equals("Y",StringComparison.OrdinalIgnoreCase);
    private static IReadOnlyList<Observation> AccidentalTorsion(InspectionContext c)
    {
        var t=Table(c,"Load Pattern Definitions - Auto Seismic - ASCE 7-16","Name","EccRatio","XDirPlusE","XDirMinusE","YDirPlusE","YDirMinusE");List<Observation> output=[];
        foreach(var axis in Axes(c))
        {var r=LoadRow(c,t,axis);var plus=S(r,axis.Direction+"DirPlusE")=="Yes";var minus=S(r,axis.Direction+"DirMinusE")=="Yes";var ratio=N(r,"EccRatio");
         output.Add(O(!plus||!minus||ratio<.05?CheckStatus.WARNING:CheckStatus.NOT_VERIFIED,"Configured eccentricity coverage: "+$"positive={plus}, negative={minus}; applicability, separate companion patterns and Ax amplification require review.",axis.Direction,ratio,.05,"ratio",Ev(t.Key,"Per mapped pattern; no assumption that unselected companion patterns supply missing signs")));}
        return output;
    }
    private static IReadOnlyList<Observation> Overturning(InspectionContext c)
    {
        var axes=Axes(c);List<Observation> output=[];
        foreach(var axis in axes)
        {var rows=c.Input.Analysis!.Results!.BaseReactions.Items.Where(x=>x.Case==axis.ElasticCase).ToArray();if(rows.Length==0){output.Add(O(CheckStatus.NOT_VERIFIED,"No base reactions for overturning review.",axis.Direction));continue;}
         var moment=rows.Max(x=>Math.Abs(axis.Direction=="X"?x.MY:x.MX));
         output.Add(O(CheckStatus.NOT_VERIFIED,"Overturning moment envelope at the reported base reference; resisting gravity moments, foundation geometry and uplift capacities are needed for adequacy.",axis.Direction,moment,unit:c.Input.Model.Identity.DisplayUnits.Force+"*"+c.Input.Model.Identity.DisplayUnits.Length,evidence:[Ev("Results.BaseReact",$"{rows.Length} rows; reference={rows[0].ReferencePoint}")]));}
        return output;
    }
    private static IReadOnlyList<Observation> Diaphragms(InspectionContext c)
    {
        var definitions=c.Input.Validation.Diaphragms?.Items??throw new Missing("Diaphragm definitions are unavailable.");var areas=c.Input.Validation.AreaAssignments?.Items??throw new Missing("Area diaphragm assignments are unavailable.");var joints=c.Input.Validation.JointAssignments?.Items??throw new Missing("Joint diaphragm assignments are unavailable.");List<Observation> output=[];
        var names=definitions.Select(x=>x.Name).ToHashSet();foreach(var area in areas.Where(x=>x.Diaphragm is not null&&!names.Contains(x.Diaphragm)))output.Add(O(CheckStatus.FAIL,"Area refers to an undefined diaphragm.",area.Id));
        foreach(var joint in joints.Where(x=>x.Diaphragm is not null&&!names.Contains(x.Diaphragm)))output.Add(O(CheckStatus.FAIL,"Joint refers to an undefined diaphragm.",joint.Id));
        var r=c.Configuration.Settings.Phase3Review;
        if(r is {ReferencesConfirmed:true,DiaphragmFlexibilityRatio:{} actual,MaximumDiaphragmFlexibilityRatio:{} limit}&&!string.IsNullOrWhiteSpace(r.ReferenceSource))output.Add(O(actual<=limit?CheckStatus.PASS:CheckStatus.FAIL,"Independent diaphragm deformation/story-drift ratio compared with approved flexibility criterion.","flexibility",actual,limit,"ratio",Ev("phase3Review",r.ReferenceSource)));
        else output.Add(O(CheckStatus.PASS,"Inventory successfully extracted and available for review.",actual:definitions.Count,unit:"diaphragms",evidence:[Ev("Diaphragm.GetDiaphragm + PointObj.GetDiaphragm + AreaObj.GetDiaphragm",string.Join(";",definitions.Select(x=>$"{x.Name}:semiRigid={x.SemiRigid}; joint assignments={joints.Count}; area assignments={areas.Count}")))]));return output;
    }
    private static IReadOnlyList<Observation> Combinations(InspectionContext c)
    {
        var refs=Ref(c).RequiredSeismicCombinations;if(refs.Count==0)throw new Missing("Approved seismic combination coefficient templates are required, including orthogonal/vertical effects and redundancy/overstrength where applicable.");
        var combos=c.Input.Validation.Combinations?.Items.ToDictionary(x=>x.Name)??throw new Missing("Load combinations unavailable.");List<Observation> output=[];
        foreach(var reference in refs)
        {
            if(!combos.ContainsKey(reference.Name)){output.Add(O(CheckStatus.FAIL,"Required seismic combination is absent.",reference.Name));continue;}
            var actual=Expand(reference.Name,new HashSet<string>());var expected=reference.Terms.GroupBy(x=>x.Name).ToDictionary(x=>x.Key,x=>x.Sum(y=>y.Factor));
            var equal=actual.Keys.Union(expected.Keys).All(k=>Math.Abs(actual.GetValueOrDefault(k)-expected.GetValueOrDefault(k))<=1e-8);
            output.Add(O(equal?CheckStatus.PASS:CheckStatus.FAIL,"Expanded linear-add combination coefficients compared with approved seismic template; envelopes and nonlinear combination rules cannot be treated as sums.",reference.Name,evidence:[Ev("RespCombo + phase3Review",string.Join(";",actual.OrderBy(x=>x.Key).Select(x=>$"{x.Key}={x.Value:G17}")))]));
        }return output;
        Dictionary<string,double> Expand(string name,HashSet<string> visiting)
        {
            if(!visiting.Add(name))throw new Missing("Combination reference cycle detected: "+name);
            if(!combos.TryGetValue(name,out var combo)||combo.Type!=0)throw new Missing("Only defined linear-add combinations can be expanded: "+name);
            Dictionary<string,double> terms=[];
            foreach(var term in combo.Terms)
            {if(term.Mode!=0)throw new Missing("Nonzero combination term mode cannot be reduced as a linear sum.");
             if(term.Type=="LoadCase")terms[term.Name]=terms.GetValueOrDefault(term.Name)+term.Factor;
             else if(term.Type=="LoadCombo"||term.Type=="LoadCombination")foreach(var nested in Expand(term.Name,visiting))terms[nested.Key]=terms.GetValueOrDefault(nested.Key)+term.Factor*nested.Value;
             else throw new Missing("Unknown combination term type "+term.Type);}
            visiting.Remove(name);return terms;
        }
    }
}


