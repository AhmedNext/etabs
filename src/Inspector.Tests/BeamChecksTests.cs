public sealed class BeamChecksTests
{
    private static InspectionContext Fixture(double top = 1000, double bottom = 800, bool effectiveDepth = true, string error = "")
    {
        var c=Context();
        c=c with {Input=c.Input with {ConcreteBeams=Data(new ConcreteBeamDesignResult("F1","B",300,top,bottom,.5,50,"TopU","ShearU",error,"")
            {BottomFlexuralCombo="BottomU"})}};
        return c.Model(m=>m with {FrameSections=Data(m.FrameSections!.Items[0] with
            {AdditionalDimensions=effectiveDepth?new Dictionary<string,double>{{"EffectiveDepth",550}}:new Dictionary<string,double>()})});
    }
    private static IReadOnlyList<Finding> Run(string id, InspectionContext c) => BeamChecks.Create().Single(x=>x.Definition.Id==id).Evaluate(c);
    [Theory]
    [InlineData("BEAM-004",1000,CheckStatus.PASS)]
    [InlineData("BEAM-004",0,CheckStatus.NOT_VERIFIED)]
    [InlineData("BEAM-005",800,CheckStatus.PASS)]
    [InlineData("BEAM-005",double.NaN,CheckStatus.NOT_VERIFIED)]
    public void DemandAvailabilityRequiresPositiveFiniteArea(string id,double area,CheckStatus expected)
    {
        var finding=Assert.Single(Run(id,Fixture(area,area)));
        Assert.Equal(expected,finding.Status);
        Assert.Equal(id=="BEAM-004"?"TopU":"BottomU",finding.LoadCaseOrCombination);
    }
    [Theory]
    [InlineData(.0032,CheckStatus.FAIL)]
    [InlineData(.0033,CheckStatus.PASS)]
    [InlineData(.0034,CheckStatus.PASS)]
    public void MinimumRatioUsesEffectiveDepthAndChecksBothFaces(double ratio,CheckStatus expected)
    {
        var rows=Run("BEAM-006",Fixture(ratio*300*550,ratio*300*550));
        Assert.Equal(2,rows.Count);
        Assert.All(rows,r=>{Assert.Equal(expected,r.Status); Assert.Equal(ratio,r.Actual!.Value,12);});
    }
    [Fact]
    public void MissingDepthUsesDisclosedFallbackWhileDesignErrorsRemainUnverified()
    {
        Assert.All(Run("BEAM-006",Fixture(effectiveDepth:false)),r=>
        {
            Assert.Equal(CheckStatus.PASS,r.Status);
            Assert.Contains(r.Evidence,e=>e.Description=="Assumed effective depth" && e.Value!.Contains("d=540"));
        });
        foreach(var check in BeamChecks.Create())
        {
            Assert.All(check.Evaluate(Fixture(error:"design failed")),r=>Assert.Equal(CheckStatus.NOT_VERIFIED,r.Status));
            Assert.Equal(CheckStatus.NOT_VERIFIED,Assert.Single(check.Evaluate(Context())).Status);
        }
    }
    [Theory]
    [InlineData("all")]
    [InlineData("design")]
    public void EngineIncludesBeamChecks(string module)
    {
        var c=Fixture();
        var report=new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input,c.Configuration,module,CheckCatalog.Implemented,CheckCatalog.All);
        foreach(var id in new[]{"BEAM-004","BEAM-005","BEAM-006"}) Assert.Contains(report.Findings,f=>f.CheckId==id);
        Assert.True(c.HasData("ConcreteBeams"));
    }
}
