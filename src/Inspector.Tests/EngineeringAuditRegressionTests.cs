public sealed class EngineeringAuditRegressionTests
{
    [Fact]
    public void SiteELowHazardUsesAsce716CoefficientRatherThanOlderEdition()
    {
        var hazard=Asce716Seismic.CalculateHazard("E",.25,.1);
        Assert.Equal(4.2,hazard.Fv);
        Assert.Equal(.28,hazard.Sd1,12);
        Assert.Throws<InvalidOperationException>(()=>Asce716Seismic.CalculateHazard("E",.8,.1));
        Assert.Throws<InvalidOperationException>(()=>Asce716Seismic.CalculateHazard("E",.25,.15));
    }
    [Theory]
    [InlineData(2, .125)]
    [InlineData(4, .125)]
    [InlineData(8, .03125)]
    public void ResponseCoefficientHandlesBothSidesOfLongPeriodTransition(double period, double expected)
        => Assert.Equal(expected, Asce716Seismic.ResponseCoefficient(.5, 2, period, 4, 1, .2, 4), 12);

    [Fact]
    public void ResponseCoefficientRejectsMissingOrInvalidTransitionPeriod()
        => Assert.Throws<InvalidOperationException>(() => Asce716Seismic.ResponseCoefficient(.5, .2, 1, 4, 1, .2, 0));

    [Theory]
    [InlineData("Rectangular", 300, 600, 0, 180000)]
    [InlineData("Circle", 0, 0, 400, 125663.70614359173)]
    [InlineData("Rectangular", .3, .6, 0, .18)]
    public void GrossAreaUsesActualSectionShape(string shape, double width, double depth, double diameter, double area)
        => Assert.Equal(area, SectionGeometry.GrossArea(new("S", shape, "C25", depth, width, diameter, null)), 8);

    [Theory]
    [InlineData("I", 300, 600, 0)]
    [InlineData("Tube", 300, 600, 0)]
    [InlineData("Auto", 300, 600, 0)]
    [InlineData("Circle", 300, 600, 0)]
    [InlineData("Rectangular", double.NaN, 600, 0)]
    public void UnsupportedOrInvalidGeometryIsNotABoundingRectangle(string shape, double width, double depth, double diameter)
        => Assert.True(double.IsNaN(SectionGeometry.GrossArea(new("S", shape, "C25", depth, width, diameter, null))));

    [Theory]
    [InlineData("Concrete crushing at boundary")]
    [InlineData("Shear capacity exceeded")]
    [InlineData("Shear force exceeds maximum allowed for ACI 318-19")]
    [InlineData("Design Inadequate")]
    public void WallDesignErrorsFailEvenWithoutReinforcementGeometry(string error)
    {
        var c = Context();
        c = c with { Input = c.Input with { ConcretePiers = Data(new ConcretePierDesignResult("P1", "L1", null, null, null, null, null, error, "")) } };
        var finding = Assert.Single(WallChecks.Create().Single(x => x.Definition.Id == "WALL-009").Evaluate(c));
        Assert.Equal(CheckStatus.FAIL, finding.Status);
        Assert.Contains(error, finding.Title);
    }

    [Fact]
    public void CheckModePierCapacityFailureDoesNotNeedUniformRebarGeometry()
    {
        var c=Context();
        c=c with {Input=c.Input with {ConcretePiers=Data(new ConcretePierDesignResult("P1","L1",null,null,1.1,null,null,"","") {DesignType="Check",RawDemandCapacityRatio=1.1})}};
        var row=Assert.Single(WallChecks.Create().Single(x=>x.Definition.Id=="WALL-009").Evaluate(c));
        Assert.Equal(CheckStatus.FAIL,row.Status); Assert.Equal(1.1,row.Actual);
    }

    [Fact]
    public void CheckModeColumnAreaCannotBeUsedAsRequiredSteel()
    {
        var c = Context();
        c = c with { Input = c.Input with { ConcreteColumns = Data(new ConcreteColumnDesignResult("F1", "B", "U", .5, null, null, 0) { DesignOption = 1 }) } };
        foreach (var id in new[] { "COL-007", "COL-008" })
            Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(ColumnChecks.Create().Single(x => x.Definition.Id == id).Evaluate(c)).Status);
    }

    [Fact]
    public void BadNumericalRowCannotDiscardAnIndependentStructuralFailure()
    {
        var c = Context();
        var rule = Rule.Create("WALL-999", "Regression", "Independent row outcomes", [], _ =>
            [new(CheckStatus.FAIL, "ETABS crushing failure", "P1"), new(CheckStatus.NOT_VERIFIED, "Invalid ratio", "P2", Actual: double.NaN)]);
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [rule], [rule.Definition]);
        Assert.Contains(report.Findings, f => f.ElementId == "P1" && f.Status == CheckStatus.FAIL);
        Assert.Contains(report.Findings, f => f.ElementId == "P2" && f.Status == CheckStatus.ERROR && f.Actual is null);
    }

    [Fact]
    public void UnsupportedShapeCannotGenerateOptimizationCandidate()
    {
        var c = Context().Model(m => m with { FrameSections = Data(new FrameSection("B", "I", "C25", 600, 300, null, null)) });
        c = c with { Input = c.Input with { ConcreteColumns = Data(new ConcreteColumnDesignResult("F1", "B", "U", null, null, null, 1000) { DesignOption = 2 }) } };
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(OptimizationChecks.Create().Single(x => x.Definition.Id == "OPT-001").Evaluate(c)).Status);
    }

    [Fact]
    public void NonSquareOverturningUsesDimensionInLoadingDirection()
    {
        var c = Context().Model(m => m with { Joints = Data(new Joint("A",new(0,0,0)),new Joint("B",new(10000,2000,3000))) });
        var reactions = Data(new BaseReactionResult("Dcase", "Single Value", 0, 0, 0, -1000000, 0, 0, 0, new(5000,1000,0)),
            new BaseReactionResult("EQX", "Single Value", 0, 0, 0, 0, 0, 1000000000, 0, new(5000,1000,0)));
        c = c with { Input = c.Input with { Analysis = new(null, new(Data<StoryDriftResult>(), Data<ModalParticipationResult>(), reactions, [], [], [], []), null, []) } };
        var row = Assert.Single(SanityChecks.Create().Single(x => x.Definition.Id == "SAN-007").Evaluate(c));
        Assert.Equal(4.5, row.Actual!.Value, 10); // 0.9 * 1000 kN * 10 m / 2 / 1000 kNm
    }

    [Fact]
    public void NonFiniteMaterialDoesNotPassDefinitionCheck()
    {
        var c = Context().Model(m => m with { Materials = Data(m.Materials!.Items[0] with { UnitWeight = double.NaN }) });
        var check = LoadingChecks.Create().Single(x => x.Definition.Id == "MAT-001");
        // Invalid model numbers are also rejected by the model fingerprint before rule evaluation.
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [check], [check.Definition]);
        Assert.Equal(CheckStatus.ERROR, Assert.Single(report.Findings).Status);
    }
}
