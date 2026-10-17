
public sealed class ExtendedPhase3ReferenceTests
{
    private static AnalysisEvidenceTable Table(string key,params Dictionary<string,string>[] rows)=>new(key,rows.SelectMany(x=>x.Keys).Distinct().ToArray(),rows);
    private static Dictionary<string,string> Row(params (string Key,object Value)[] values)=>values.ToDictionary(x=>x.Key,x=>Convert.ToString(x.Value,CultureInfo.InvariantCulture)!);
    private static InspectionContext Fixture(params AnalysisEvidenceTable[] tables)
    {
        var c=Context().Settings(s=>s with { Phase3Review=new(){ReferencesConfirmed=true,ReferenceSource="Independent test fixture",ExpectedConcreteCode="ACI 318-19"},SeismicVerification=new("ASCE 7-16",4.5,1,.02,1,1,[new("X","EX"),new("Y","EY")],[]) });
        c=c.Model(m=>m with {Stories=new(0,[new("L1",3000,3000,true,"None"),new("L2",6000,3000,true,"None"),new("L3",9000,3000,true,"None"),new("L4",12000,3000,true,"None")])});
        var results=new AnalysisResultsData(Data<StoryDriftResult>(),Data<ModalParticipationResult>(),Data<BaseReactionResult>(),[],[],[],[])
            {Extended=new(tables,[],[],[],"ACI 318-19",true,[])};
        return c with {Input=c.Input with {Analysis=new(null,results,new(Data<ResponseSpectrumCase>(),Data<GeometricNonlinearitySetting>()),[])}};
    }
    private static InspectionContext References(InspectionContext c,Func<Phase3ReviewSettings,Phase3ReviewSettings> f)=>c.Settings(s=>s with {Phase3Review=f(s.Phase3Review!)});
    private static InspectionContext Results(InspectionContext c,Func<AnalysisResultsData,AnalysisResultsData> f)=>c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Results=f(c.Input.Analysis.Results!)}}};
    private static InspectionContext Extended(InspectionContext c,Func<ExtendedAnalysisEvidence,ExtendedAnalysisEvidence> f)=>Results(c,r=>r with {Extended=f(r.Extended!)});
    [Theory]
    [InlineData("ANA-003")][InlineData("ANA-004")][InlineData("ANA-008")][InlineData("ANA-009")][InlineData("ANA-012")][InlineData("ANA-013")][InlineData("ANA-014")][InlineData("ANA-015")]
    [InlineData("SEIS-001")][InlineData("SEIS-002")][InlineData("SEIS-003")][InlineData("SEIS-005")][InlineData("SEIS-007")][InlineData("SEIS-008")][InlineData("SEIS-010")][InlineData("SEIS-011")][InlineData("SEIS-012")][InlineData("SEIS-013")][InlineData("SEIS-016")][InlineData("SEIS-018")]
    public void MissingEvidenceNeverPasses(string id)=>Assert.All(ExtendedPhase3Checks.Evaluate(id,Context()),o=>Assert.Equal(CheckStatus.NOT_VERIFIED,o.Status));
    [Theory]
    [InlineData("Information",CheckStatus.PASS)][InlineData("Warning",CheckStatus.WARNING)][InlineData("Error",CheckStatus.FAIL)]
    public void SolverMessageTypesStayDistinct(string type,CheckStatus expected)
    {var c=Fixture(Table("Analysis Messages",Row(("Type",type),("Message","test"))));Assert.Equal(expected,Assert.Single(ExtendedPhase3Checks.Evaluate("ANA-003",c)).Status);}
    [Fact]
    public void MatchingProvisionalAutoSeismicParametersPassWithCertificationEvidence()
    {
        using var seismic=JsonDocument.Parse("""{"siteClass":"C","r":5.0,"cd":4.5,"ie":1.0,"omega0":2.5,"ss":0.6,"s1":0.2,"siteSpecificHazardConfirmed":false}""");
        var c=Fixture(Table("Load Pattern Definitions - Auto Seismic - ASCE 7-16",
            Row(("Name","SX"),("SiteClass","C"),("R",5.0),("Cd",4.5),("I",1.0),("Omega",2.5),("Ss",.6),("S1",.2),("SDS",.528),("SD1",.2933)),
            Row(("Name","SY"),("SiteClass","C"),("R",5.0),("Cd",4.5),("I",1.0),("Omega",2.5),("Ss",.6),("S1",.2),("SDS",.528),("SD1",.2933))))
            .Settings(s=>s with {Seismic=seismic.RootElement.Clone()})
            .Validation(v=>v with {LoadCases=Data(new LoadCase("EX","LinearStatic",0,[new("Load","SX",1)]),new LoadCase("EY","LinearStatic",0,[new("Load","SY",1)]))});
        var findings=ExtendedPhase3Checks.Evaluate("SEIS-001",c);
        foreach(var field in new[]{"Ss","S1","SiteClass"})
        {
            var matching=findings.Where(f=>f.ElementId!.EndsWith("/"+field,StringComparison.Ordinal)).ToArray();
            Assert.Equal(2,matching.Length);
            Assert.All(matching,f=>
            {
                Assert.Equal(CheckStatus.PASS,f.Status);
                Assert.Contains(f.Evidence!,e=>e.Value!.Contains("site-specific hazard certification is pending",StringComparison.Ordinal));
            });
        }
    }
    [Fact]
    public void EquilibriumUsesExtractedConcreteSelfWeightAndDeadReaction()
    {
        var c=Fixture();
        c=c with {Input=c.Input with {MaterialTakeoffs=Data(new MaterialTakeoffResult("Floor","C25",216,9_000_000)),Analysis=new(null,
            new AnalysisResultsData(Data<StoryDriftResult>(),Data<ModalParticipationResult>(),Data(new BaseReactionResult("Dcase","Single Value",0,0,0,-216,0,0,0,new(0,0,0))),[],[],[],[]),null,[])}};
        var outcome=Assert.Single(ExtendedPhase3Checks.Evaluate("ANA-009",c));Assert.Equal(CheckStatus.PASS,outcome.Status);Assert.Equal(0,outcome.Actual);
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Results=c.Input.Analysis.Results! with {BaseReactions=Data(new BaseReactionResult("Dcase","Single Value",0,0,0,-230,0,0,0,new(0,0,0)))}}}};
        Assert.Equal(CheckStatus.FAIL,Assert.Single(ExtendedPhase3Checks.Evaluate("ANA-009",c)).Status);
    }
    [Fact]
    public void OrphanedJsonDependentRulesAreAbsent()
    {
        foreach(var id in new[]{"ANA-005","ANA-006","ANA-010","ANA-011","SEIS-009","SEIS-020"})
        {Assert.DoesNotContain(CheckCatalog.Implemented,x=>x.Definition.Id==id);Assert.DoesNotContain(CheckCatalog.All,x=>x.Id==id);}
    }
    [Theory]
    [InlineData(1.3,CheckStatus.NOT_VERIFIED)][InlineData(1.31,CheckStatus.WARNING)]
    public void VerticalDimensionBoundaryIsStrict(double ratio,CheckStatus expected)
    {
        var c=Fixture();
        var joints=new List<Joint>();
        foreach(var story in c.Input.Model.Stories!.Stories)
        {
            var width=story.Name=="L2"?10*ratio:10;
            joints.AddRange([
                new(story.Name+"-A",new(0,0,story.Elevation)),
                new(story.Name+"-B",new(width,0,story.Elevation)),
                new(story.Name+"-C",new(width,20,story.Elevation)),
                new(story.Name+"-D",new(0,20,story.Elevation))]);
        }
        c=c.Model(m=>m with {Joints=Data(joints.ToArray())});
        var observation=Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-010",c),o=>o.ElementId=="L1/X");
        Assert.Equal(expected,observation.Status);
        Assert.Contains("calculated automatically from bounding joint coordinates",observation.Evidence!.Single().Value);
        var yObservation=Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-010",c),o=>o.ElementId=="L1/Y");
        Assert.Equal(CheckStatus.NOT_VERIFIED,yObservation.Status);
        Assert.Equal(1,yObservation.Actual);
    }
    [Theory]
    [InlineData(.5,CheckStatus.NOT_VERIFIED)][InlineData(.51,CheckStatus.WARNING)]
    public void PlanOpeningRatioUsesLiveAreaAndJointGeometry(double openingRatio,CheckStatus expected)
    {
        var c=Fixture();var side=3000*Math.Sqrt(openingRatio);var holeJoints=new[]{new Joint("H1",new(0,0,3000)),new Joint("H2",new(side,0,3000)),new Joint("H3",new(side,side,3000)),new Joint("H4",new(0,side,3000))};
        c=c.Model(m=>m with {Joints=Data([..m.Joints!.Items,..holeJoints]),Areas=Data(m.Areas!.Items[0],new Area("Opening","Opening","L1",null,"Floor",true,["H1","H2","H3","H4"]))});
        var outcome=Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-011",c),o=>o.ElementId=="L1");Assert.Equal(expected,outcome.Status);Assert.Equal(openingRatio,(double)outcome.Actual!,5);Assert.Contains("automatically calculated from polygon joint coordinates",outcome.Evidence!.Single().Value);
    }
    [Theory]
    [InlineData(.7,CheckStatus.NOT_VERIFIED)][InlineData(.69,CheckStatus.WARNING)]
    public void SoftStoryTriggerDoesNotClaimFullCompliance(double ratio,CheckStatus expected)
    {
        var c=Fixture(Table("Story Stiffness",Row(("Story","L1"),("OutputCase","EX"),("StiffX",100*ratio),("StiffY",0)),Row(("Story","L2"),("OutputCase","EX"),("StiffX",100),("StiffY",0))));
        Assert.Equal(expected,Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-008",c),o=>o.ElementId=="L1/X").Status);
    }
    [Theory]
    [InlineData(false,CheckStatus.WARNING)][InlineData(true,CheckStatus.WARNING)]
    public void TorsionRequiresConfirmedApplicableEnvelope(bool confirmed,CheckStatus expected)
    {
        var c=References(Fixture(Table("Diaphragm Max Over Avg Drifts",Row(("Story","L1"),("OutputCase","EX"),("Item","Diaph D1 X"),("Ratio",1.21)))),r=>r with {TorsionEnvelopeConfirmed=confirmed});
        Assert.Equal(expected,Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-007",c)).Status);
    }
    [Fact]
    public void ExtractedDiaphragmInventoryPassesAvailabilityCheck()
    {
        var c=Fixture().Validation(v=>v with {Diaphragms=Data(new Diaphragm("D1",false)),AreaAssignments=Data<AreaAssignment>(),JointAssignments=Data<JointAssignment>()});
        Assert.Equal(CheckStatus.PASS,Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-019",c)).Status);
    }
    [Fact]
    public void MissingModesAreReportedAndStaticRowsAreNotModes()
    {
        var c=Fixture(Table("Modal Periods And Frequencies",Row(("Case","M"),("Mode",1),("Period",1)),Row(("Case","M"),("Mode",2),("Period",.5))))
            .Validation(v=>v with {LoadCases=Data(new LoadCase("M","Modal",1,null))});
        c=Results(c,r=>r with {ModalParticipation=Data(new ModalParticipationResult("M","",0,1,0,0,0,0,0,0,0,0,0,0,0,0))});
        c=Extended(c,e=>e with {ModeShapes=[new("A","A","M","mOdE",1,1,0,0,0,0,0),new("A","A","EX","Single Value",0,1,0,0,0,0,0)]});
        var rows=ExtendedPhase3Checks.Evaluate("SEIS-005",c);Assert.Equal(2,rows.Count);Assert.Contains(rows,x=>x.ElementId=="missing/M/2");Assert.DoesNotContain(rows,x=>x.ElementId!.StartsWith("EX/"));
    }
    [Fact]
    public void MalformedTablesCannotPass()
    {var c=Fixture(Table("Modal Periods And Frequencies",Row(("Eigenvalue","NaN"),("Period",1))));Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(ExtendedPhase3Checks.Evaluate("ANA-004",c)).Status);}
}


