public sealed class ColumnChecksTests
{
    private static InspectionContext Fixture(double? pmm = .5, double? area = 3600, int designOption = 1) =>
        Context() with { Input = Context().Input with
        { ConcreteColumns = Data(new ConcreteColumnDesignResult("F1", "B", "U", pmm, null, null, area) { DesignOption = designOption }) } };
    private static Finding Run(string id, InspectionContext c) =>
        Assert.Single(ColumnChecks.Create().Single(x => x.Definition.Id == id).Evaluate(c));

    [Theory]
    [InlineData(.949, CheckStatus.PASS)]
    [InlineData(.95, CheckStatus.WARNING)]
    [InlineData(1, CheckStatus.WARNING)]
    [InlineData(1.001, CheckStatus.FAIL)]
    public void PmmBoundaries(double ratio, CheckStatus status) => Assert.Equal(status, Run("COL-004", Fixture(pmm:ratio)).Status);

    [Fact]
    public void DesignModePassesWhenEtabsComputesPositiveRequiredArea()
    {
        var row = new ConcreteColumnDesignResult("F1", "B", "U", null, null, null, 1250) { DesignOption = 2 };
        var finding = Run("COL-004", Fixture() with { Input = Fixture().Input with { ConcreteColumns = Data(row) } });
        Assert.Equal(CheckStatus.PASS, finding.Status);
        Assert.Equal(1250, finding.Actual);
        Assert.Equal("mm²", finding.Unit);
        Assert.Contains("ETABS design-mode required area successfully computed", finding.Title);
    }
    [Theory]
    [InlineData(null, CheckStatus.NOT_VERIFIED)]
    [InlineData(double.NaN, CheckStatus.NOT_VERIFIED)]
    [InlineData(0d, CheckStatus.FAIL)]
    [InlineData(-1d, CheckStatus.FAIL)]
    public void DesignModeRequiresFinitePositiveArea(double? area, CheckStatus expected)
    {
        var row = new ConcreteColumnDesignResult("F1", "B", "U", null, null, null, area) { DesignOption = 2 };
        var c = Fixture() with { Input = Fixture().Input with { ConcreteColumns = Data(row) } };
        Assert.Equal(expected, Run("COL-004", c).Status);
    }
    [Fact]
    public void DesignModeErrorIsNotVerified()
    {
        var row = new ConcreteColumnDesignResult("F1", "B", "U", null, null, null, 1250)
            { DesignOption = 2, ErrorSummary = "design failed" };
        var c = Fixture() with { Input = Fixture().Input with { ConcreteColumns = Data(row) } };
        Assert.Equal(CheckStatus.NOT_VERIFIED, Run("COL-004", c).Status);
    }
    [Fact]
    public void CheckModeDoesNotFallBackToRequiredAreaWhenPmmIsMissing()
    {
        Assert.Equal(CheckStatus.NOT_VERIFIED, Run("COL-004", Fixture(pmm: null, area: 1250)).Status);
    }

    [Theory]
    [InlineData("COL-007", .0099, CheckStatus.FAIL)]
    [InlineData("COL-007", .01, CheckStatus.PASS)]
    [InlineData("COL-008", .04, CheckStatus.PASS)]
    [InlineData("COL-008", .0401, CheckStatus.WARNING)]
    [InlineData("COL-008", .08, CheckStatus.WARNING)]
    [InlineData("COL-008", .0801, CheckStatus.FAIL)]
    public void RectangularAreaAndStrictLimits(string id, double rho, CheckStatus status)
    {
        var finding=Run(id,Fixture(area:rho*600*300, designOption:2));
        Assert.Equal(status,finding.Status); Assert.Equal(rho,finding.Actual!.Value,12);
        Assert.Contains("Provided reinforcement remains unverified",finding.Title);
    }
    [Fact]
    public void CircularAreaUsesPiDiameterSquaredOverFour()
    {
        var c=Fixture(area:.02*Math.PI*400*400/4, designOption:2).Model(m=>m with
        {FrameSections=Data(new FrameSection("B","Circle","C25",null,null,400,null))});
        Assert.Equal(.02,Run("COL-007",c).Actual!.Value,12);
    }
    [Fact]
    public void MissingResultsRatiosAndUnknownGeometryCannotPass()
    {
        Assert.All(ColumnChecks.Create(),check=>Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(check.Evaluate(Context())).Status));
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("COL-004",Fixture(pmm:null)).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("COL-007",Fixture(area:null)).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("COL-008",Fixture().Model(m=>m with {FrameSections=Data<FrameSection>()})).Status);
    }
    [Theory]
    [InlineData("all")]
    [InlineData("design")]
    public void EngineSelectsColumnChecks(string module)
    {
        var c=Fixture();
        var report=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,module,CheckCatalog.Implemented,CheckCatalog.All);
        foreach(var id in new[]{"COL-004","COL-007","COL-008"}) Assert.Contains(report.Findings,f=>f.CheckId==id);
    }
}
