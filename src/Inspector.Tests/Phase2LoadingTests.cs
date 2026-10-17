
public sealed class Phase2LoadingTests
{
    private static IReadOnlyList<Finding> Run(string id, InspectionContext c) => Assert.Single(CheckCatalog.Implemented, r => r.Definition.Id == id).Evaluate(c);
    [Theory]
    [InlineData("LOAD-001")]
    [InlineData("LOAD-002")]
    [InlineData("LOAD-006")]
    [InlineData("LOAD-007")]
    public void UnconfirmedProjectExpectationsNeverPass(string id) => Assert.Equal(CheckStatus.NOT_VERIFIED,
        Assert.Single(Run(id, Context().Settings(s => s with { Confirmed = false }))).Status);
    [Fact]
    public void ConfirmedMissingPatternFails()
    {
        var c = Context().Validation(v => v with { LoadPatterns = Data(new LoadPattern("L", "Live", 0)) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-001", c)).Status);
    }
    [Theory]
    [InlineData(1.0000000001, CheckStatus.PASS)]
    [InlineData(1.000001, CheckStatus.FAIL)]
    public void SelfWeightUsesNumericalComparisonTolerance(double factor, CheckStatus status)
    {
        var c = Context().Validation(v => v with { LoadPatterns = Data(new LoadPattern("D", "Dead", factor), new LoadPattern("L", "Live", 0)) });
        Assert.Equal(status, Assert.Single(Run("LOAD-002", c)).Status);
    }
    [Fact]
    public void MultipleSelfWeightPatternsAreNotAutomaticallyFailures()
    {
        var c = Context().Settings(s => s with { Confirmed = false })
            .Validation(v => v with { LoadPatterns = Data(new LoadPattern("D", "Dead", 1), new LoadPattern("L", "Live", 1)) });
        var result = Run("LOAD-002", c); Assert.DoesNotContain(result, r => r.Status == CheckStatus.FAIL);
        Assert.Contains(result, r => r.Status == CheckStatus.WARNING);
    }
    [Fact]
    public void NamesDoNotDetermineLoadClassification()
    {
        var c = Context().Validation(v => v with { LoadPatterns = Data(new LoadPattern("D", "Other", 1), new LoadPattern("L", "Live", 0)) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-006", c)).Status);
    }
    [Fact]
    public void MissingRequiredCombinationFails()
    {
        var c = Context().Validation(v => v with { Combinations = Data<LoadCombination>() });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-007", c)).Status);
    }
    [Fact]
    public void CombinationCyclesAndUndefinedReferencesFail()
    {
        var c = Context().Validation(v => v with { Combinations = Data(new LoadCombination("A", 0, [new("LoadCombo", "B", 0, 1)]),
            new LoadCombination("B", 0, [new("LoadCombo", "A", 0, 1)])) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-008", c)).Status);
        c = Context().Validation(v => v with { Combinations = Data(new LoadCombination("A", 0, [new("LoadCase", "missing", 0, 1)])) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-008", c)).Status);
    }
    [Fact]
    public void NestedAcyclicCombinationsAndNegativeFactorsAreValid()
    {
        var c = Context().Validation(v => v with { Combinations = Data([..v.Combinations!.Items,
            new("ENVELOPE", 1, [new("LoadCombo", "ULS", 0, -1)])]) });
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run("LOAD-008", c)).Status);
    }
    [Fact]
    public void UnknownCombinationTypeCannotPass()
    {
        var c = Context().Validation(v => v with { Combinations = Data(v.Combinations!.Items[0] with { Type = 99 }) });
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run("LOAD-008", c)).Status);
    }
    [Fact]
    public void ZeroCombinationFactorsAreWarnings()
    {
        var c = Context().Validation(v => v with { Combinations = Data(new LoadCombination("Z", 0, [new("LoadCase", "Dcase", 0, 0)])) });
        Assert.Equal(CheckStatus.WARNING, Assert.Single(Run("LOAD-008", c)).Status);
    }
    [Fact]
    public void UnknownActiveMassPatternFails()
    {
        var c = Context().Validation(v => v with { MassSources = Data(v.MassSources!.Items[0] with { Loads = [new("missing", 1)] }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-009", c)).Status);
    }
    [Fact]
    public void DisabledLoadSourceDoesNotActivateStoredMassRows()
    {
        var c = Context().Validation(v => v with { MassSources = Data(v.MassSources!.Items[0] with { IncludeLoads = false, Loads = [new("missing", 1)] }) });
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run("LOAD-009", c)).Status);
    }
    [Fact]
    public void SelfMassOverlapUsesActualFactorsAndRemainsAWarning()
    {
        var c = Context().Validation(v => v with { MassSources = Data(v.MassSources!.Items[0] with { IncludeElementMass = true, Loads = [new("D", 0.4), new("L", 0.25)] }) });
        var finding = Assert.Single(Run("LOAD-010", c));
        Assert.Equal(CheckStatus.WARNING, finding.Status); Assert.Equal(0.4, finding.Actual);
        Assert.Contains(finding.Evidence, e => e.Value?.Contains("D: 0.4", StringComparison.Ordinal) == true);
    }
    [Fact]
    public void AddedMassAloneDoesNotEstablishDuplication() => Assert.Equal(CheckStatus.PASS,
        Assert.Single(Run("LOAD-010", Context())).Status);
    [Fact]
    public void UnsupportedCaseDefinitionRemainsUnverified()
    {
        var c = Context().Validation(v => v with { LoadCases = Data(new LoadCase("M", "Modal", 0, null)) });
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run("LOAD-013", c)).Status);
    }
    [Fact]
    public void AccelerationComponentsAreNotPatternReferences()
    {
        var c = Context().Validation(v => v with { LoadCases = Data(new LoadCase("A", "LinearStatic", 0, [new("Accel", "UX", 9806.65)])) });
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run("LOAD-013", c)).Status);
    }
    [Fact]
    public void UndefinedStaticLoadPatternFails()
    {
        var c = Context().Validation(v => v with { LoadCases = Data(new LoadCase("A", "LinearStatic", 0, [new("Load", "UX", 1)])) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("LOAD-013", c)).Status);
    }
    [Fact]
    public void NegativeDensityAndInconsistentRebarStrengthsFail()
    {
        var c = Context().Model(m => m with { Materials = Data(m.Materials!.Items[0] with { UnitWeight = -1 }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("MAT-001", c)).Status);
        c = Context().Model(m => m with { Materials = Data(new Material("R", "Rebar", 3, 1, 1, 200000, null, null, 500, 400)) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("MAT-001", c)).Status);
    }
    [Fact]
    public void ZeroDensityIsAReviewCondition()
    {
        var c = Context().Model(m => m with { Materials = Data(m.Materials!.Items[0] with { UnitWeight = 0 }) });
        Assert.Equal(CheckStatus.WARNING, Assert.Single(Run("MAT-001", c)).Status);
    }
    [Fact]
    public void MassTableContinuationFlagsAreReadPerSource()
    {
        var table = DatabaseTable.Parse("Mass Source Definition", 1,
            ["Name", "IsDefault", "SourceSelf", "SourceAdded", "SourceLoads", "LoadPattern", "Multiplier"], 2,
            ["M", "Yes", "No", "Yes", "Yes", "D", "1", "M", "", "", "", "", "L", "0.25"]);
        var mass = Assert.Single(MassSourceTable.Parse(table));
        Assert.True(mass.IsDefault); Assert.False(mass.IncludeElementMass); Assert.Equal(2, mass.Loads.Count);
    }
    [Theory]
    [InlineData("")]
    [InlineData("Maybe")]
    public void MissingOrUnknownMassFlagsAreNotDefaulted(string flag)
    {
        var table = DatabaseTable.Parse("Mass", 1, ["Name", "IsDefault", "SourceSelf", "SourceAdded", "SourceLoads", "LoadPattern", "Multiplier"],
            1, ["M", flag, "No", "Yes", "Yes", "D", "1"]);
        Assert.Throws<ApiException>(() => MassSourceTable.Parse(table));
    }
}


