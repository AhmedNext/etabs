
public sealed class Phase2GeometryTests
{
    // Only definition-inspection modules can be satisfied by this reference model; the ANA/SEIS modules require
    // analysis results and confirmed project criteria and are covered by the Phase 3 reference tests.
    public static IEnumerable<object[]> RuleIds => CheckCatalog.Implemented
        .Where(c => c.Definition.Module is "CFG" or "GEO" or "MAT" or "LOAD")
        .Select(c => new object[] { c.Definition.Id });
    private static IReadOnlyList<Finding> Run(string id, InspectionContext c) => Assert.Single(CheckCatalog.Implemented, r => r.Definition.Id == id).Evaluate(c);
    [Theory, MemberData(nameof(RuleIds))]
    public void IndependentReferenceModelSatisfiesEachImplementedCriterion(string id)
    {
        var result = Assert.Single(Run(id, Context()));
        Assert.Equal(id == "LOAD-014" ? CheckStatus.NOT_APPLICABLE : CheckStatus.PASS, result.Status);
        Assert.NotEmpty(result.Evidence); Assert.NotEmpty(result.CalculationMethod);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1.0001, false)]
    public void CoincidenceBoundaryUsesTheConfirmedTolerance(double offset, bool warning)
    {
        var c = Context().Model(m => m with {
            Joints = Data([..m.Joints!.Items, new("E", new(offset, 0, 3000))]),
            Frames = Data([..m.Frames!.Items, new Frame("F2", "L1", "B", "E", "D", new(offset, 0, 3000), new(0, 3000, 3000), 0)]) });
        Assert.Equal(offset == 0 ? CheckStatus.NOT_APPLICABLE : warning ? CheckStatus.WARNING : CheckStatus.PASS,
            Assert.Single(Run("GEO-001", c)).Status);
    }
    [Fact]
    public void IntentionalCoincidentPairIsNotAConfirmedFailure()
    {
        var c = Context().Model(m => m with { Joints = Data([..m.Joints!.Items, new("E", new(0, 0, 3000))]) })
            .Settings(s => s with { Geometry = s.Geometry! with { IntentionalCoincidentJoints = ["A", "E"] } });
        Assert.Equal(CheckStatus.NOT_APPLICABLE, Assert.Single(Run("GEO-001", c)).Status);
    }
    [Fact]
    public void SubNanometerCoincidenceIsNumericalNoise()
    {
        var c = Context().Model(m => m with {
            Joints = Data([..m.Joints!.Items, new("E", new(0.0000005, 0, 3000))]),
            Frames = Data([..m.Frames!.Items, new Frame("F2", "L1", "B", "E", "D", new(0.0000005, 0, 3000), new(0, 3000, 3000), 0)]) });
        var result = Assert.Single(Run("GEO-001", c));
        Assert.Equal(CheckStatus.NOT_APPLICABLE, result.Status);
        Assert.Contains("numerical precision noise", result.Title, StringComparison.Ordinal);
    }
    [Fact]
    public void UnreferencedCalculationJointIsExcludedFromCoincidenceWarning()
    {
        var c = Context().Model(m => m with { Joints = Data([..m.Joints!.Items, new("COM", new(0, 0, 3000))]) });
        var result = Assert.Single(Run("GEO-001", c));
        Assert.Equal(CheckStatus.NOT_APPLICABLE, result.Status);
        Assert.Contains("unreferenced calculation/reference joint", result.Title, StringComparison.Ordinal);
    }
    [Fact]
    public void ConfirmedCustomCoincidenceToleranceIsRespected()
    {
        var c = Context().Model(m => m with {
            Joints = Data([..m.Joints!.Items, new("E", new(1.5, 0, 3000))]),
            Frames = Data([..m.Frames!.Items, new Frame("F2", "L1", "B", "E", "D", new(1.5, 0, 3000), new(0, 3000, 3000), 0)]) })
            .Settings(s => s with { Geometry = s.Geometry! with { CoincidentTolerance = new(2, "mm") } });
        Assert.Equal(CheckStatus.WARNING, Assert.Single(Run("GEO-001", c)).Status);
    }
    [Fact]
    public void DisconnectedObjectsAreWarningsWithMemberEvidence()
    {
        var c = Context().Model(m => m with {
            Joints = Data([..m.Joints!.Items, new("E", new(10000, 0, 3000)), new("F", new(10000, 3000, 3000))]),
            Frames = Data([..m.Frames!.Items, new("F2", "L1", "B", "E", "F", new(10000, 0, 3000), new(10000, 3000, 3000), 0)]) });
        var results = Run("GEO-002", c);
        Assert.Equal(2, results.Count); Assert.All(results, r => Assert.Equal(CheckStatus.WARNING, r.Status));
        Assert.Contains(results.SelectMany(f => f.Evidence), e => e.Value?.Contains("Frame:F2", StringComparison.Ordinal) == true);
    }
    [Theory]
    [InlineData(2999.999, false)]
    [InlineData(3000, false)]
    [InlineData(3000.001, true)]
    public void ShortFrameBoundaryHasAnInclusiveAllowedLimit(double threshold, bool warning)
    {
        var c = Context().Settings(s => s with { Geometry = s.Geometry! with { ShortFrameThreshold = new(threshold, "mm") } });
        Assert.Equal(warning ? CheckStatus.WARNING : CheckStatus.PASS, Assert.Single(Run("GEO-003", c)).Status);
    }
    [Fact]
    public void MissingLengthCriterionDoesNotPass() => Assert.Equal(CheckStatus.NOT_VERIFIED,
        Assert.Single(Run("GEO-003", Context().Settings(s => s with { Geometry = new() }))).Status);
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositivePhysicalDimensionFails(double depth)
    {
        var c = Context().Model(m => m with { FrameSections = Data(m.FrameSections!.Items[0] with { Depth = depth }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-004", c)).Status);
    }
    [Fact]
    public void UnsupportedSectionCannotPassDimensions()
    {
        var c = Context().Model(m => m with { FrameSections = Data(m.FrameSections!.Items[0] with { Limitation = "Layered/unverified" }) });
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run("GEO-004", c)).Status);
    }
    [Fact]
    public void AutoSelectDefinitionHasNoInventedPhysicalDimensions()
    {
        var c = Context().Model(m => m with { FrameSections = Data([..m.FrameSections!.Items, new("Auto", "Auto", null, null, null, null, null)]) });
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run("GEO-004", c)).Status);
    }
    [Fact]
    public void UnreferencedJointIsOnlyAScreeningWarning()
    {
        var c = Context().Model(m => m with { Joints = Data([..m.Joints!.Items, new("unused", new(0, 0, 9000))]) });
        var finding = Assert.Single(Run("GEO-006", c)); Assert.Equal(CheckStatus.WARNING, finding.Status); Assert.Equal("unused", finding.ElementId);
    }
    [Fact]
    public void IntentionalUnreferencedJointIsMarkedNotApplicable()
    {
        var c = Context().Model(m => m with { Joints = Data([..m.Joints!.Items, new("unused", new(0, 0, 9000))]) })
            .Settings(s => s with { Geometry = s.Geometry! with { IntentionalUnreferencedJoints = ["unused"] } });
        var finding = Assert.Single(Run("GEO-006", c));
        Assert.Equal(CheckStatus.NOT_APPLICABLE, finding.Status);
        Assert.Contains("whitelisted", finding.Title, StringComparison.Ordinal);
    }
    [Fact]
    public void UnreferencedJointWithExplicitDiaphragmIsNotFlaggedAsWarning()
    {
        var c = Context().Model(m => m with { Joints = Data([..m.Joints!.Items, new("COM", new(6000, 6000, 3000))]) })
            .Validation(v => v with { JointAssignments = Data([..v.JointAssignments!.Items,
                new JointAssignment("COM", new bool[6], "DefinedDiaphragm", "D1")]) });
        var finding = Assert.Single(Run("GEO-006", c));
        Assert.Equal(CheckStatus.NOT_APPLICABLE, finding.Status);
        Assert.Contains("diaphragm assignment", finding.Title, StringComparison.Ordinal);
    }
    [Fact]
    public void MissingFrameJointIsAReferenceFailure()
    {
        var c = Context().Model(m => m with { Frames = Data(m.Frames!.Items[0] with { EndJoint = "unknown" }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-007", c)).Status);
    }
    [Fact]
    public void EndReleaseIsAReviewWarningWithDegreesOfFreedom()
    {
        var c = Context().Validation(v => v with { FrameAssignments = Data(v.FrameAssignments!.Items[0] with { StartReleases = [false, false, false, false, true, false] }) });
        var result = Assert.Single(Run("GEO-009", c)); Assert.Equal(CheckStatus.WARNING, result.Status);
        Assert.Contains(result.Evidence, e => e.Description.Contains("I-end", StringComparison.Ordinal));
    }
    [Fact]
    public void RepeatedAreaBoundaryJointsFail()
    {
        var c = Context().Model(m => m with { Areas = Data(m.Areas!.Items[0] with { Joints = ["A", "B", "A"] }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-010", c)).Status);
    }
    [Fact]
    public void OpeningAreasAreExemptFromStructuralBoundaryCriterion()
    {
        var c = Context().Model(m => m with { Areas = Data(m.Areas!.Items[0] with { Joints = ["A", "B"], IsOpening = true }) });
        Assert.Equal(CheckStatus.NOT_APPLICABLE, Assert.Single(Run("GEO-010", c)).Status);
    }
    [Fact]
    public void InconsistentStoryElevationFails()
    {
        var c = Context().Model(m => m with { Stories = new(0, [new("L1", 3005, 3000, true, "None")]) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-011", c)).Status);
    }
    [Fact]
    public void ShellInheritedJointDiaphragmsAreHandled()
    {
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run("GEO-012", Context())).Status);
        var c = Context().Validation(v => v with { AreaAssignments = Data(new AreaAssignment("S1", null, null)) });
        Assert.Equal(CheckStatus.WARNING, Assert.Single(Run("GEO-012", c)).Status);
    }
    [Fact]
    public void UndefinedExplicitDiaphragmFails()
    {
        var c = Context().Validation(v => v with { JointAssignments = Data(new JointAssignment("A", new bool[6], "DefinedDiaphragm", "unknown")) });
        Assert.Contains(Run("GEO-012", c), f => f.Status == CheckStatus.FAIL);
    }
    [Fact]
    public void MissingFloorRequirementDoesNotPassCompleteness()
    {
        var c = Context().Settings(s => s with { Geometry = s.Geometry! with { RequireFloorDiaphragms = null } });
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run("GEO-012", c)).Status);
    }
    [Fact]
    public void MissingSectionOrMaterialFailsReferenceCriterion()
    {
        var c = Context().Model(m => m with { FrameSections = Data(m.FrameSections!.Items[0] with { Material = "missing" }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-015", c)).Status);
        c = Context().Model(m => m with { Frames = Data(m.Frames!.Items[0] with { Section = "missing" }) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(Run("GEO-015", c)).Status);
    }
    [Theory]
    [InlineData(1000, "mm", "m", 1)]
    [InlineData(1, "in", "mm", 25.4)]
    [InlineData(1, "ft", "mm", 304.8)]
    [InlineData(100, "cm", "m", 1)]
    public void ReferenceLengthConversions(double value, string from, string to, double expected) =>
        Assert.Equal(expected, GeometryMath.ConvertLength(new(value, from), to), 10);
    [Fact]
    public void IndependentThreeFourFiveDistance() => Assert.Equal(5, GeometryMath.Distance(new(1, 1, 1), new(4, 5, 1)));
    [Fact]
    public void UnrepresentableDistanceIsAnErrorRatherThanPassing()
    {
        Assert.Throws<InvalidOperationException>(() => GeometryMath.Distance(new(double.MaxValue, 0, 0), new(-double.MaxValue, 0, 0)));
        Assert.Throws<ConfigurationException>(() => GeometryMath.ConvertLength(new(double.MaxValue, "m"), "mm"));
    }
}



