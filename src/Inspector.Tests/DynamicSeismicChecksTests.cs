public sealed class DynamicSeismicChecksTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeismicConfigurationAcceptsMixedCaseAndTypedFactors(bool typed)
    {
        var c=Context().Settings(s=>s with {Seismic=typed ? JsonSerializer.SerializeToElement(new {SiteClass="C",SS=.6,S1=.2,R=5}) : JsonSerializer.SerializeToElement(new {SiteClass="C",SS=.6,S1=.2,R=5,Cd=4.5,IE=1.0}),
            SeismicVerification=typed ? new("ASCE 7-16",4.5,1,.02,1,1,[],[]) : null});
        var table=Table("Load Pattern Definitions - Auto Seismic - ASCE 7-16",Row(("Name","SX"),("SiteClass","C"),("Ss",.6),("S1",.2),("R",5),("Cd",4.5),("I",1),("SDS",.504),("SD1",.2)));
        c=c with {Input=c.Input with {Analysis=new(null,new(Data<StoryDriftResult>(),Data<ModalParticipationResult>(),Data<BaseReactionResult>(),[],[],[],[]){Extended=new([table],[],[],[],null,null,[])},null,[])}};
        var rows=ExtendedPhase3Checks.Evaluate("SEIS-001",c);
        Assert.Contains(rows,r=>r.ElementId=="SX/Cd"&&r.Status==CheckStatus.PASS);
        Assert.Contains(rows,r=>r.ElementId=="SX/I"&&r.Status==CheckStatus.PASS);
    }
    private static AnalysisEvidenceTable Table(string key, params Dictionary<string,string>[] rows) =>
        new(key, rows.SelectMany(x => x.Keys).Distinct().ToArray(), rows);
    private static Dictionary<string,string> Row(params (string Key,object Value)[] values) =>
        values.ToDictionary(x => x.Key, x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)!);
    private static InspectionContext WithSeismic(InspectionContext c) => c.Settings(s => s with
    {
        Seismic = JsonSerializer.SerializeToElement(new { siteClass="C", ss=.6, s1=.2, system="Special reinforced concrete shear walls", r=5.0, cd=4.5, ie=1.0 })
    });
    private static InspectionContext WithMode(InspectionContext c, double period)
    {
        var modalTable = Table("Modal Periods And Frequencies",
            Row(("Case", "Modal"), ("Mode", 1), ("Period", Math.Round(period, 3))));
        var results = new AnalysisResultsData(Data<StoryDriftResult>(),
            Data(new ModalParticipationResult("Modal", "Mode", 1, period, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            Data<BaseReactionResult>(), [], [], [], []) { Extended = new([modalTable], [], [], [], null, true, []) };
        c=c.Validation(v => v with { LoadCases = Data(new LoadCase("Modal", "Modal", 0, null)) });
        return c with { Input = c.Input with
        {
            Analysis = new(new(Data(new AnalysisCaseStatus("Modal", AnalysisCaseState.Finished, 4))), results,
                new(Data<ResponseSpectrumCase>(), Data<GeometricNonlinearitySetting>()), [])
        }};
    }

    [Theory]
    [InlineData(.15, CheckStatus.PASS)]
    [InlineData(.18, CheckStatus.FAIL)]
    public void PeriodUsesLiveHeightModalPeriodAndConfiguredSystem(double modalPeriod, CheckStatus expected)
    {
        var c = WithMode(WithSeismic(Context()), modalPeriod);
        var result = Assert.Single(ExtendedPhase3Checks.Evaluate("SEIS-003", c));
        Assert.Equal(expected, result.Status);
        Assert.Contains("hn=3m", result.Evidence!.Single().Value);
        Assert.Contains("Cu=1.5", result.Evidence!.Single().Value);
        Assert.Equal(.0488 * Math.Pow(3, .75) * 1.5, result.Limit!.Value, 8);
    }

    [Fact]
    public void MassCheckReportsAssembledLiveLateralWeights()
    {
        var c = WithSeismic(Context());
        var table = Table("Mass Summary by Group", Row(("Group", "All"), ("MassX", 10.0), ("MassY", 12.0)));
        var evidence = new ExtendedAnalysisEvidence([table], [], [], [], null, true, []);
        c = c with { Input = c.Input with { Analysis = new(null,
            new(Data<StoryDriftResult>(), Data<ModalParticipationResult>(), Data<BaseReactionResult>(), [], [], [], []) { Extended = evidence }, null, []) } };
        var findings = ExtendedPhase3Checks.Evaluate("SEIS-002", c);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, x => Assert.Equal(CheckStatus.PASS, x.Status));
        Assert.Equal(10 * 9806.65, findings.Single(x => x.ElementId == "weight/X").Actual);
        Assert.Equal(12 * 9806.65, findings.Single(x => x.ElementId == "weight/Y").Actual);
    }

    [Fact]
    public void BaseShearCalculatesHazardCoefficientAndComparesLiveAutoPatternAndReaction()
    {
        var c = WithMode(WithSeismic(Context()), .1).Settings(s => s with
            { SeismicVerification = new("ASCE 7-16",4.5,1,.02,1,1,[new("X","EX")],[]) });
        const double mass = 10;
        const double weight = mass * 9806.65;
        const double cs = .1008;
        const double shear = weight * cs;
        var auto = Table("Load Pattern Definitions - Auto Seismic - ASCE 7-16",
            Row(("Name", "SX"), ("IsAuto", "Yes"), ("BaseShear", shear), ("WeightUsed", weight), ("CoeffUsed", cs), ("XDir", "Yes"), ("YDir", "No"),
                ("XDirPlusE", "No"), ("XDirMinusE", "No"), ("YDirPlusE", "No"), ("YDirMinusE", "No")));
        var massTable = Table("Mass Summary by Group", Row(("Group", "All"), ("MassX", mass), ("MassY", mass)));
        var ext = new ExtendedAnalysisEvidence([auto, massTable, ..c.Input.Analysis!.Results!.Extended!.Tables], [], [], [], null, true, []);
        var results = c.Input.Analysis!.Results! with
        {
            BaseReactions = Data(new BaseReactionResult("EX", "Single Value", 0, -shear, 0, 0, 0, 0, 0, new(0,0,0))),
            Extended = ext
        };
        c = c.Validation(v => v with
        {
            LoadPatterns = Data(new LoadPattern("SX", "Quake", 0)),
            LoadCases = Data(new LoadCase("EX", "LinearStatic", 0, [new("Load", "SX", 1)]), new LoadCase("Modal", "Modal", 0, null))
        });
        c = c with { Input = c.Input with { Analysis = c.Input.Analysis! with { Results = results,
            Status = new(Data(new AnalysisCaseStatus("EX", AnalysisCaseState.Finished, 4), new("Modal", AnalysisCaseState.Finished, 4))) } } };
        var findings = ExtendedPhase3Checks.Evaluate("SEIS-013", c);
        Assert.True(findings.Count == 3, string.Join(" | ", findings.Select(x => x.Message)));
        Assert.All(findings, x => Assert.Equal(CheckStatus.PASS, x.Status));
        Assert.Equal("SX/auto", findings[0].ElementId);
        Assert.Contains("SDS=0.504g", findings[0].Evidence!.Single().Value);
        Assert.Contains("Cs=0.1008", findings[0].Evidence!.Single().Value);
    }

    [Fact]
    public void BaseShearAcceptsAndEnvelopesMaxMinReactionSteps()
    {
        var c = WithMode(WithSeismic(Context()), .1).Settings(s => s with
            { SeismicVerification = new("ASCE 7-16",4.5,1,.02,1,1,[new("X","EX")],[]) });
        const double mass=10, weight=mass*9806.65, cs=.1008, shear=weight*cs;
        var auto=Table("Load Pattern Definitions - Auto Seismic - ASCE 7-16",Row(("Name","SX"),("IsAuto","Yes"),("BaseShear",shear),("WeightUsed",weight),("CoeffUsed",cs),("XDir","Yes"),("YDir","No"),
            ("XDirPlusE","No"),("XDirMinusE","No"),("YDirPlusE","No"),("YDirMinusE","No")));
        var massTable=Table("Mass Summary by Group",Row(("Group","All"),("MassX",mass),("MassY",mass)));
        var extended=new ExtendedAnalysisEvidence([auto,massTable,..c.Input.Analysis!.Results!.Extended!.Tables],[],[],[],null,true,[]);
        c=c.Validation(v=>v with {LoadPatterns=Data(new LoadPattern("SX","Quake",0)),LoadCases=Data(new LoadCase("EX","LinearStatic",0,[new("Load","SX",1)]),new LoadCase("Modal","Modal",0,null))});
        var results=c.Input.Analysis!.Results! with
        {
            BaseReactions=Data(new BaseReactionResult("EX","Max",0,-shear*.8,0,0,0,0,0,new(0,0,0)),new("EX","Min",0,shear,0,0,0,0,0,new(0,0,0))),
            Extended=extended
        };
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Results=results,
            Status=new(Data(new AnalysisCaseStatus("EX",AnalysisCaseState.Finished,4),new("Modal",AnalysisCaseState.Finished,4)))}}};
        var findings=ExtendedPhase3Checks.Evaluate("SEIS-013",c);
        Assert.Equal(CheckStatus.PASS,findings.Single(f=>f.ElementId=="SX/reaction").Status);
    }

    [Fact]
    public void DynamicPeriodMapsModeOneFromRoundedTableWhenApiLacksModeNumber()
    {
        var c=WithSeismic(Context());
        c=WithMode(c,.1);
        var results=c.Input.Analysis!.Results! with {ModalParticipation=Data(new ModalParticipationResult("Modal","",0,.1002,0,0,0,0,0,0,0,0,0,0,0,0))};
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Results=results}}};
        var findings=ExtendedPhase3Checks.Evaluate("SEIS-003",c);
        Assert.Equal(CheckStatus.PASS,Assert.Single(findings).Status);
    }
}
