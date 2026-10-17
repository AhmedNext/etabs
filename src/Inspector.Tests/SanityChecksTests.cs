public sealed class SanityChecksTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    [InlineData(-9999)]
    public void WindScreenSkipsInsignificantDirection(double fy)
    {
        var c=WithResults(Base(),reactions:[new("WindX","Single Value",0,11250,fy,0,0,0,0,new(0,0,0))]);
        var findings=Check("SAN-004").Evaluate(c);
        Assert.Equal(CheckStatus.PASS,Assert.Single(findings,f=>f.ElementId=="WindX/X").Status);
        Assert.Equal(CheckStatus.NOT_APPLICABLE,Assert.Single(findings,f=>f.ElementId=="WindX/Y").Status);
    }
    private static DataSet<T> Set<T>(params T[] values) => new(values, [], []);
    private static IInspectionCheck Check(string id) => SanityChecks.Create().Single(x => x.Definition.Id == id);
    private static Finding Run(string id, InspectionContext c) => Assert.Single(Check(id).Evaluate(c));
    private static InspectionContext Base() => Phase2Fixture.Context();

    private static InspectionContext WithResults(InspectionContext c,
        BaseReactionResult[]? reactions = null, ModalParticipationResult[]? modes = null,
        FrameForceEvidence[]? frameForces = null, StoryForceResult[]? storyForces = null,
        AnalysisEvidenceTable[]? tables = null, AnalysisCaseStatus[]? statuses = null) =>
        c with { Input = c.Input with { Analysis = new(
            statuses is null ? null : new AnalysisStatusData(Set(statuses)),
            new AnalysisResultsData(Set<StoryDriftResult>(), Set(modes ?? []), Set(reactions ?? []), [], [], [], [])
            {
                StoryForces = storyForces is null ? null : Set(storyForces),
                Extended = new(tables ?? [], [], [], frameForces ?? [], null, null, [])
            }, null, []) } };

    private static InspectionContext OneColumn(InspectionContext c) => c.Model(m => m with
    {
        Frames = Set(new Frame("C1", "L1", "B", "A", "A", new(0, 0, 0), new(0, 0, 3000), 0)),
        Stories = new StoryData(0, [new Story("L1", 3000, 3000, true, "None")])
    });

    [Theory]
    [InlineData(108000, CheckStatus.PASS)]
    [InlineData(160000, CheckStatus.PASS)]
    [InlineData(200000, CheckStatus.WARNING)]
    public void San001ComparesTributaryEstimateToLiveGravityAxialForce(double actualN, CheckStatus expected)
    {
        var c = OneColumn(Base());
        c = WithResults(c, frameForces: [new("C1", 0, "C1", 0, "Dcase", "Single Value", 0, actualN, 0, 0, 0, 0, 0)]);
        var finding = Run("SAN-001", c);
        Assert.Equal(expected, finding.Status);
        Assert.Equal(.40, finding.Limit);
        Assert.Contains("q_floor=12", Assert.Single(finding.Evidence!, e => e.Description == "Sanity-check calculation").Value);
    }

    [Theory]
    [InlineData(.1, CheckStatus.PASS)]
    [InlineData(.04, CheckStatus.WARNING)]
    public void San002UsesSystemSpecificPointOneNBand(double period, CheckStatus expected)
    {
        var c = Base().Settings(s => s with { Seismic = System.Text.Json.JsonDocument.Parse("{\"system\":\"Special concrete moment frame\"}").RootElement });
        c = WithResults(c, modes: [new("Modal", "Mode", 1, period, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)],
            statuses: [new("Modal", AnalysisCaseState.Finished, 4)]);
        c = c.Validation(v => v with { LoadCases = Set(new LoadCase("Modal", "Modal", 0, null)) });
        Assert.Equal(expected, Run("SAN-002", c).Status);
    }

    [Theory]
    [InlineData(-90000, CheckStatus.PASS)]
    [InlineData(-50000, CheckStatus.WARNING)]
    public void San003ScreensDeadWeightPerFloorArea(double fzN, CheckStatus expected)
    {
        var c = WithResults(Base(), reactions: [new("Dcase", "Single Value", 0, 0, 0, fzN, 0, 0, 0, new(0, 0, 0))]);
        var finding = Run("SAN-003", c);
        Assert.Equal(expected, finding.Status);
        Assert.Equal("kN/m²", finding.Unit);
    }

    [Theory]
    [InlineData(11250, CheckStatus.PASS)]
    [InlineData(30000, CheckStatus.WARNING)]
    public void San004ComparesWindReactionToSilhouetteEstimate(double fxN, CheckStatus expected)
    {
        var c = Base().Validation(v => v with
        {
            LoadPatterns = Set(new LoadPattern("W", "Wind", 0)),
            LoadCases = Set(new LoadCase("W1", "LinearStatic", 0, [new("Load", "W", 1)]))
        });
        c = WithResults(c, reactions: [new("W1", "Single Value", 0, fxN, fxN, 0, 0, 0, 0, new(0, 0, 0))]);
        var findings=Check("SAN-004").Evaluate(c);
        Assert.All(findings, finding => Assert.Equal(expected, finding.Status));
        Assert.Contains(findings.SelectMany(f=>f.Evidence!),e=>(e.Value??"").Contains("H=3m")&&(e.Value??"").Contains("Vest=11250 N"));
    }

    [Theory]
    [InlineData(900000, CheckStatus.PASS)]
    [InlineData(2000000, CheckStatus.WARNING)]
    public void San005UsesConcreteStrengthAndSectionArea(double axialN, CheckStatus expected)
    {
        var c = OneColumn(Base()) with { Input = OneColumn(Base()).Input with
        {
            ConcreteColumns = Set(new ConcreteColumnDesignResult("C1", "B", "D+L", .3, null, null, 1500)),
            Model = OneColumn(Base()).Input.Model with { Materials = Set(new Material("C25", "Concrete", 0, .000024, 2.446e-9, 25000, .2, 25, null, null)) }
        } };
        c = WithResults(c, frameForces: [new("C1", 0, "C1", 0, "Dcase", "Single Value", 0, axialN, 0, 0, 0, 0, 0)]);
        Assert.Equal(expected, Run("SAN-005", c).Status);
    }

    [Theory]
    [InlineData(1000000, CheckStatus.PASS)]
    [InlineData(2000000, CheckStatus.FAIL)]
    public void San006ChecksPierShearStressAgainstConcreteCeiling(double vuN, CheckStatus expected)
    {
        var c = Base();
        c = c.Model(m => m with
        {
            Areas = Set(new Area("A1", "P1", "L1", "SW", "Wall", false, ["A", "B", "C", "D"])),
            AreaSections = Set(new AreaSection("SW", "Wall", "ShellThick", "C25", 200, null))
        });
        var pier = new ConcretePierDesignResult("P1", "L1", null, null, null, 100, 2, "", "") { LegLength = 3000, LegThickness = 200 };
        var pierForces = new AnalysisEvidenceTable("Pier Forces", ["Story", "Pier", "Vu"],
            [new Dictionary<string, string> { ["Story"] = "L1", ["Pier"] = "P1", ["Vu"] = vuN.ToString(System.Globalization.CultureInfo.InvariantCulture) }]);
        c = c with { Input = c.Input with { ConcretePiers = Set(pier) } };
        c = WithResults(c, tables: [pierForces]);
        var finding = Run("SAN-006", c);
        Assert.Equal(expected, finding.Status);
        Assert.Equal("MPa", finding.Unit);
    }

    [Theory]
    [InlineData(1000000000, CheckStatus.WARNING)]
    [InlineData(2000000000, CheckStatus.FAIL)]
    public void San007ChecksGlobalOverturningRatio(double myNmm, CheckStatus expected)
    {
        var c = WithResults(Base(), reactions: [
            new("Dcase", "Single Value", 0, 0, 0, -1000000, 0, 0, 0, new(1500, 1500, 0)),
            new("EQX", "Single Value", 0, 0, 0, 0, 0, myNmm, 0, new(1500, 1500, 0))]);
        Assert.Equal(expected, Run("SAN-007", c).Status);
    }

    [Theory]
    [InlineData(10000, CheckStatus.PASS)]
    [InlineData(15000, CheckStatus.WARNING)]
    public void San008ComparesFloorForceWithCvx(double storyShearN, CheckStatus expected)
    {
        var table = new AnalysisEvidenceTable("Mass Summary by Story", ["Story", "MassX", "MassY"],
            [new Dictionary<string, string> { ["Story"] = "L1", ["MassX"] = "10", ["MassY"] = "10" }]);
        var c = WithResults(Base(), reactions: [new("EQX", "Single Value", 0, 10000, 0, 0, 0, 0, 0, new(0, 0, 0))],
            modes: [new("Modal", "Mode", 1, .4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)],
            storyForces: [new("L1", "EQX", "Single Value", 0, "Bottom", 0, storyShearN, 0)],
            tables: [table, new AnalysisEvidenceTable("Modal Periods And Frequencies", ["Case", "Mode", "Period"],
                [new Dictionary<string, string> { ["Case"] = "Modal", ["Mode"] = "1", ["Period"] = ".4" }])],
            statuses: [new("Modal", AnalysisCaseState.Finished, 4)]);
        c = c.Validation(v => v with { LoadCases = Data(new LoadCase("Modal", "Modal", 0, null)) });
        var finding = Run("SAN-008", c);
        Assert.Equal(expected, finding.Status);
    }

    [Fact]
    public void SanityChecksAreInCatalogAndSelectableAsModule()
    {
        Assert.Equal(8, SanityChecks.Create().Count);
        Assert.All(Enumerable.Range(1, 8), i => Assert.Contains(CheckCatalog.All, x => x.Id == $"SAN-{i:000}"));
        Assert.Equal("sanity", CliOptions.Parse(["inspect", "sanity"]).Module);
        var c = Base();
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance)
            .Run(c.Input, c.Configuration, "sanity", CheckCatalog.Implemented, CheckCatalog.All);
        Assert.All(report.Findings.Where(f => f.CheckId.StartsWith("SAN-", StringComparison.Ordinal)), f => Assert.Equal("SAN", f.Module));
    }
}
