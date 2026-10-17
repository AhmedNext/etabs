public sealed class WallChecksTests
{
    private static InspectionContext Fixture(double? area = 1500, double? thickness = 300, string error = "")
    {
        var c=Context();
        return c with {Input=c.Input with {ConcretePiers=Data(new ConcretePierDesignResult("P1","L1",null,null,null,area,.75,error,"")
            {DesignType="Uniform",Station="Top",PierLeg="Top Leg 1",LegLength=2000,LegThickness=thickness})}};
    }
    private static Finding Run(string id,InspectionContext c)=>Assert.Single(WallChecks.Create().Single(x=>x.Definition.Id==id).Evaluate(c));
    [Theory]
    [InlineData(.00249,CheckStatus.FAIL)]
    [InlineData(.0025,CheckStatus.PASS)]
    [InlineData(.04,CheckStatus.PASS)]
    [InlineData(.04001,CheckStatus.WARNING)]
    public void RatioBoundariesUseMatchingPierGrossArea(double ratio,CheckStatus expected)
    {
        var result=Run("WALL-009",Fixture(ratio*2000*300));
        Assert.Equal(expected,result.Status);Assert.Equal(ratio,result.Actual!.Value,12);
        Assert.Equal("L1",result.Story);Assert.Equal("Pier",result.ElementType);
    }
    [Fact]
    public void AvailabilityDoesNotClaimAdequacyAndMissingDataIsUnverified()
    {
        Assert.Equal(CheckStatus.PASS,Run("WALL-008",Fixture(error:"shear exceeds maximum")).Status);
        Assert.All(WallChecks.Create(),check=>Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(check.Evaluate(Context())).Status));
        var c=Fixture(); c=c with {Input=c.Input with {ConcretePiers=Data<ConcretePierDesignResult>()}};
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("WALL-008",c).Status);
        Assert.True(c.HasData("ConcretePiers"));
    }
    [Fact]
    public void MissingGeometryAreaAndDesignErrorsCannotPass()
    {
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("WALL-009",Fixture(thickness:null)).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("WALL-009",Fixture(area:null)).Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED,Run("WALL-009",Fixture(area:double.NaN)).Status);
        Assert.Equal(CheckStatus.FAIL,Run("WALL-009",Fixture(error:"design inadequate")).Status);
    }
    [Fact]
    public void EtabsMaximumShearErrorIsAnAciFailure()
    {
        const string error="Shear force exceeds maximum allowed for ACI 318-19";
        var finding=Assert.Single(WallChecks.Create().Single(x=>x.Definition.Id=="WALL-009").Evaluate(Fixture(error:error)));
        Assert.Equal(CheckStatus.FAIL,finding.Status);
        Assert.Contains(error,finding.Title);
    }
    [Fact]
    public void MultipleLegsAreNotSilentlySummedAsNonoverlappingGeometry()
    {
        var c=Fixture();var row=c.Input.ConcretePiers!.Items[0];
        c=c with {Input=c.Input with {ConcretePiers=Data(row,row with {PierLeg="Top Leg 2"})}};
        var results=WallChecks.Create().Single(x=>x.Definition.Id=="WALL-009").Evaluate(c);
        Assert.Equal(2,results.Count);Assert.All(results,r=>Assert.Equal(CheckStatus.NOT_VERIFIED,r.Status));
    }
    [Theory]
    [InlineData("all")]
    [InlineData("design")]
    public void WallChecksRunFromBothInspectionCommands(string module)
    {
        var c=Fixture();
        var report=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,module,CheckCatalog.Implemented,CheckCatalog.All);
        foreach(var id in new[]{"WALL-008","WALL-009"})Assert.Contains(report.Findings,f=>f.CheckId==id);
    }
}
