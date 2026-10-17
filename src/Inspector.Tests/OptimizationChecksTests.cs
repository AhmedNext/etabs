public sealed class OptimizationChecksTests
{
    [Theory]
    [InlineData(.0119,CheckStatus.WARNING)]
    [InlineData(.012,CheckStatus.NOT_APPLICABLE)]
    [InlineData(.02,CheckStatus.NOT_APPLICABLE)]
    public void DesignModeUsesRequiredSteelRatio(double rho,CheckStatus expected)
    {
        var c=Context();
        c=c with {Input=c.Input with {ConcreteColumns=Data(new ConcreteColumnDesignResult("F1","B","U",null,null,null,rho*300*600){DesignOption=2})}};
        var finding=Assert.Single(Run("OPT-001",c));
        Assert.Equal(expected,finding.Status);
        Assert.Equal(rho,finding.Actual!.Value,10);
    }
    private static InspectionContext Fixture(double ratio)
    {
        var c=Context();
        return c with {Input=c.Input with {
            ConcreteColumns=Data(new ConcreteColumnDesignResult("F1","B","U",ratio,null,null,null)),
            ConcretePiers=Data(new ConcretePierDesignResult("P1","L1",null,null,null,null,null,"","") {RawDemandCapacityRatio=ratio})}};
    }
    private static IReadOnlyList<Finding> Run(string id,InspectionContext c)=>OptimizationChecks.Create().Single(x=>x.Definition.Id==id).Evaluate(c);
    [Theory]
    [InlineData(.349,CheckStatus.WARNING)]
    [InlineData(.35,CheckStatus.NOT_APPLICABLE)]
    [InlineData(.8,CheckStatus.NOT_APPLICABLE)]
    [InlineData(-.1,CheckStatus.NOT_VERIFIED)]
    [InlineData(double.NaN,CheckStatus.NOT_VERIFIED)]
    public void PositiveRatioAndStrictThresholdAreRequired(double ratio,CheckStatus expected)
    {
        foreach(var id in new[]{"OPT-001","OPT-002"})Assert.Equal(expected,Assert.Single(Run(id,Fixture(ratio))).Status);
    }
    [Fact]
    public void ZeroPierDcrUsesExtractedDesignModeSteelPercent()
    {
        var c=Fixture(0);var pier=c.Input.ConcretePiers!.Items[0] with {RequiredVerticalRebarPercent=.2};
        c=c with {Input=c.Input with {ConcretePiers=Data(pier)}};
        Assert.Equal(CheckStatus.WARNING,Assert.Single(Run("OPT-002",c)).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(Run("OPT-001",c)).Status);
    }
    [Fact]
    public void PierOptimizationSkipsWhenEtabsDesignErrorIsPresent()
    {
        var c=Fixture(0);var pier=c.Input.ConcretePiers!.Items[0] with
            {RequiredVerticalRebarPercent=.2,ErrorSummary="Design Inadequate: Shear force exceeds maximum allowed"};
        c=c with {Input=c.Input with {ConcretePiers=Data(pier)}};
        var finding=Assert.Single(Run("OPT-002",c));
        Assert.Equal(CheckStatus.NOT_APPLICABLE,finding.Status);
        Assert.Contains("resolve the separate wall design failure",finding.Title);
    }
    [Fact]
    public void HighUtilizationAtAnotherStationPreventsFalseOptimizationCandidate()
    {
        var c=Fixture(.1);var column=c.Input.ConcreteColumns!.Items[0];var pier=c.Input.ConcretePiers!.Items[0];
        c=c with {Input=c.Input with {
            ConcreteColumns=Data(column,column with {Location=3000,PmmRatio=.8}),
            ConcretePiers=Data(pier,pier with {Station="Bottom",RawDemandCapacityRatio=.8})}};
        foreach(var id in new[]{"OPT-001","OPT-002"})
        {var result=Assert.Single(Run(id,c));Assert.Equal(CheckStatus.NOT_APPLICABLE,result.Status);Assert.Equal(.8,result.Actual);}
    }
    [Fact]
    public void MissingRatiosErrorsAndMissingDatasetsRemainUnverified()
    {
        var c=Fixture(.1);var column=c.Input.ConcreteColumns!.Items[0];var pier=c.Input.ConcretePiers!.Items[0];
        c=c with {Input=c.Input with {
            ConcreteColumns=Data(column,column with {Location=3000,PmmRatio=null}),
            ConcretePiers=Data(pier,pier with {Station="Bottom",ErrorSummary="design inadequate"})}};
        foreach(var id in new[]{"OPT-001","OPT-002"})
        {
            Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(Run(id,c)).Status);
            Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(Run(id,Context())).Status);
        }
    }
    [Fact]
    public void InspectAllIncludesOptimizationAndReservedCoverage()
    {
        var c=Fixture(.2);
        var report=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,"all",CheckCatalog.Implemented,CheckCatalog.All);
        foreach(var id in new[]{"OPT-001","OPT-002"})Assert.Equal(CheckStatus.WARNING,Assert.Single(report.Findings,f=>f.CheckId==id).Status);
        Assert.Equal(VerificationState.NOT_IMPLEMENTED,Assert.Single(report.Coverage,c=>c.CheckId=="OPT-003").Implementation);
    }
}
