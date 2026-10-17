
public sealed class SeismicCriteriaTests
{
    private static InspectionContext ContextFor(double drift = .002, double gravity = 1000, double shear = 100)
    {
        var c = Context().Settings(s => s with { SeismicVerification = new("ASCE 7-16", 4.5, 1, .02, 1, 1,
            [new("X", "EX", "RSX"), new("Y", "EY", "RSY")], [new("Dcase", 1)], true) });
        var cases = new[] { new LoadCase("EX", "LinearStatic", 0, []), new LoadCase("EY", "LinearStatic", 0, []), new LoadCase("Dcase", "LinearStatic", 0, []) };
        c = c.Validation(v => v with { LoadCases = Data(cases) });
        var results = new AnalysisResultsData(Data(new StoryDriftResult("L1", "EX", "Single Value", 0, "X", drift, "1", 0, 0, 3000), new("L1", "EY", "Single Value", 0, "Y", drift, "1", 0, 0, 3000)), Data<ModalParticipationResult>(),
            Data(new BaseReactionResult("EX", "Single Value", 0, 100, 0, 0, 0, 0, 0, new(0,0,0)), new("RSX", "Max", 0, 90, 0, 0, 0, 0, 0, new(0,0,0))), [], [], [], [])
            { StoryForces = Data(new StoryForceResult("L1", "EX", "Single Value", 0, "Bottom", 0, shear, 0), new("L1", "EY", "Single Value", 0, "Bottom", 0, 0, shear), new("L1", "Dcase", "Single Value", 0, "Bottom", gravity, 0, 0)) };
        var status = new AnalysisStatusData(Data(new[] { "EX", "EY", "Dcase", "RSX", "RSY" }.Select(x => new AnalysisCaseStatus(x, AnalysisCaseState.Finished, 4)).ToArray()));
        return c with { Input = c.Input with { Analysis = new(status, results, new(Data(new ResponseSpectrumCase("RSX", "M", 0, 0, 0, 0, 0, 0, 0, [new("U1", "F", 1, "Global", 0)])), Data<GeometricNonlinearitySetting>()), []) } };
    }
    [Theory]
    [InlineData(.004, CheckStatus.PASS)]
    [InlineData(.005, CheckStatus.FAIL)]
    public void DriftIsAmplifiedExactlyOnce(double elastic, CheckStatus status)
    {
        var rows = SeismicCriteriaChecks.Drift(ContextFor(elastic));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal(status, r.Status); Assert.Equal(elastic * 4.5, r.Actual); });
    }
    [Theory]
    [InlineData(.01, CheckStatus.PASS)]
    [InlineData(.0105, CheckStatus.WARNING)]
    [InlineData(.012, CheckStatus.FAIL)]
    public void StabilityBoundariesUseElasticRatio(double drift, CheckStatus status)
    {
        Assert.All(SeismicCriteriaChecks.Stability(ContextFor(drift)), r => { Assert.Equal(status, r.Status); Assert.Equal(drift * 10, r.Actual!.Value, 12); Assert.Equal(1.0/9, r.Limit); });
    }
    [Fact]
    public void StabilityUsesAbsoluteMaxMinEnvelopesForAutoSeismicResults()
    {
        var c=ContextFor(.01);
        var results=c.Input.Analysis!.Results! with
        {
            StoryDrifts=Data(new StoryDriftResult("L1","EX","Max",0,"X",-.008,"1",0,0,3000),new("L1","EX","Min",0,"X",.01,"1",0,0,3000),new("L1","EY","Single Value",0,"Y",.01,"1",0,0,3000),new("L1","EY","Max",0,"Y",.01,"1",0,0,3000)),
            StoryForces=Data(new StoryForceResult("L1","EX","Max",0,"Bottom",0,-100,0),new("L1","EX","Min",0,"Bottom",0,100,0),new("L1","EY","Max",0,"Bottom",0,0,-100),new("L1","EY","Min",0,"Bottom",0,0,100),new("L1","Dcase","Single Value",0,"Bottom",1000,0,0))
        };
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis with {Results=results}}};
        Assert.All(SeismicCriteriaChecks.Stability(c),r=>Assert.Equal(.1,r.Actual!.Value,12));
    }
    [Fact]
    public void StabilityAcceptsEtabsStepByStepAutoSeismicStoryForces()
    {
        var c=ContextFor(.01);
        var results=c.Input.Analysis!.Results! with
        {
            StoryDrifts=Data(new StoryDriftResult("L1","EX","Max",0,"X",-.01,"1",0,0,3000),new("L1","EY","Max",0,"Y",-.01,"1",0,0,3000)),
            StoryForces=Data(new StoryForceResult("L1","EX","Step By Step",1,"Bottom",0,-100,0),new("L1","EX","Step By Step",2,"Bottom",0,100,0),new("L1","EY","Step By Step",1,"Bottom",0,0,-100),new("L1","EY","Step By Step",2,"Bottom",0,0,100),new("L1","Dcase","Single Value",0,"Bottom",1000,0,0))
        };
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis with {Results=results}}};
        Assert.All(SeismicCriteriaChecks.Stability(c),r=>Assert.Equal(.1,r.Actual!.Value,12));
    }
    [Theory]
    [InlineData(.0105, CheckStatus.PASS)]
    [InlineData(.012, CheckStatus.FAIL)]
    public void ActiveGlobalPDeltaSuppressesNeglectWarningButKeepsThetaMaximum(double drift, CheckStatus expected)
    {
        var c=ContextFor(drift);
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Definitions=c.Input.Analysis.Definitions! with
            {GeometricNonlinearity=Data(new GeometricNonlinearitySetting("PDelta","NonlinearStatic",1,"P-Delta"))}}}};
        var findings=SeismicCriteriaChecks.Stability(c);
        Assert.All(findings,r=>Assert.Equal(expected,r.Status));
        if(expected==CheckStatus.PASS)
            Assert.All(findings,r=>Assert.Contains("Global P-Delta is active in ETABS solver; θ limits evaluated for reference but second-order effects are already accounted for.",r.Message));
        else Assert.All(findings,r=>Assert.Contains("θ maximum",r.Message));
    }
    [Fact]
    public void LowThetaCanPassProvisionallyWithUnapprovedGravity()
    {
        var c = ContextFor().Settings(s => s with { SeismicVerification = s.SeismicVerification! with { GravityBasisConfirmed = false } });
        var findings=SeismicCriteriaChecks.Stability(c);
        Assert.All(findings, r =>
        {
            Assert.Equal(CheckStatus.PASS, r.Status);
            Assert.Contains("Stability coefficient theta is within code limits (provisional: gravity basis uses extracted load cases, pending geotechnical certification).",r.Message);
            Assert.Contains(r.Evidence!,e=>e.Description=="Gravity-basis status"&&e.Value!.Contains("pending geotechnical certification",StringComparison.Ordinal));
        });
    }
    [Theory]
    [InlineData(.0105, CheckStatus.WARNING)]
    [InlineData(.012, CheckStatus.FAIL)]
    public void UnapprovedGravityStillReportsThetaWarningsAndFailures(double drift,CheckStatus expected)
    {
        var c=ContextFor(drift).Settings(s=>s with {SeismicVerification=s.SeismicVerification! with {GravityBasisConfirmed=false}});
        Assert.All(SeismicCriteriaChecks.Stability(c),r=>Assert.Equal(expected,r.Status));
    }
    [Fact]
    public void MissingGeometricNonlinearitySettingsCannotBeAssumedInactive()
    {
        var c=ContextFor();
        c=c with {Input=c.Input with {Analysis=c.Input.Analysis! with {Definitions=null}}};
        Assert.All(SeismicCriteriaChecks.Stability(c),r=>Assert.Equal(CheckStatus.NOT_VERIFIED,r.Status));
    }
    [Fact]
    public void ZeroShearNeverPasses() => Assert.All(SeismicCriteriaChecks.Stability(ContextFor(shear: 0)), r => Assert.Equal(CheckStatus.NOT_VERIFIED, r.Status));
    [Fact]
    public void MissingDirectionDoesNotDisappear()
    {
        var c = ContextFor();
        c = c with { Input = c.Input with { Analysis = c.Input.Analysis! with { Results = c.Input.Analysis.Results! with { StoryDrifts = Data(c.Input.Analysis.Results.StoryDrifts.Items.Take(1).ToArray()) } } } };
        Assert.Contains(SeismicCriteriaChecks.Drift(c), r => r.Status == CheckStatus.NOT_VERIFIED && r.ElementId!.EndsWith("Y"));
    }
    [Fact]
    public void SpectrumRatioAndMissingCaseAreDistinct()
    {
        var rows = SeismicCriteriaChecks.Spectrum(ContextFor());
        Assert.Equal(CheckStatus.FAIL, rows[0].Status); Assert.Equal(.9, rows[0].Actual);
        Assert.Equal(CheckStatus.NOT_VERIFIED, rows[1].Status);
    }
    [Fact]
    public void BadCriteriaAreRejected()
    {
        var s = ContextFor().Configuration.Settings;
        Assert.Throws<ConfigurationException>(() => ProjectConfiguration.Validate(s with { SeismicVerification = s.SeismicVerification! with { Cd = double.NaN } }));
        Assert.Throws<ConfigurationException>(() => ProjectConfiguration.Validate(s with { SeismicVerification = s.SeismicVerification! with { Axes = [new("X", "EX"), new("X", "EY")] } }));
    }
}


