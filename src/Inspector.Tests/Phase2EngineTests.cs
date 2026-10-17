
public sealed class Phase2EngineTests
{
    private static InspectionReport Run(InspectionContext c, string module = "all") => new InspectionEngine(NullLogger<InspectionEngine>.Instance)
        .Run(c.Input, c.Configuration, module, CheckCatalog.Implemented, CheckCatalog.All);
    [Fact]
    public void MissingDependencySkipsOnlyDependentChecks()
    {
        var report = Run(Context().Validation(v => v with { LoadPatterns = null, Issues = [new("LoadPatterns", "API failed", "GetNameList", 3)] }));
        Assert.All(report.Findings.Where(f => f.CheckId == "LOAD-001"), f => Assert.Equal(CheckStatus.SKIPPED, f.Status));
        Assert.Equal(CheckStatus.PASS, Assert.Single(report.Findings, f => f.CheckId == "GEO-011").Status);
    }
    [Fact]
    public void TechnicalFailureDoesNotBecomeEngineeringFailureOrStopOtherRules()
    {
        var c = Context(); var bad = Rule.Create("GEO-901", "Throw", "Test failure boundary", [], _ => throw new InvalidOperationException("controlled"));
        var good = Assert.Single(CheckCatalog.Implemented, r => r.Definition.Id == "GEO-011");
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [bad, good], [bad.Definition, good.Definition]);
        Assert.Equal(CheckStatus.ERROR, Assert.Single(report.Findings, f => f.CheckId == "GEO-901").Status);
        Assert.Equal(CheckStatus.PASS, Assert.Single(report.Findings, f => f.CheckId == "GEO-011").Status);
        Assert.False(report.HasFailures);
    }
    [Fact]
    public void UnverifiedRuleCannotIssueOfficialPassOrFail()
    {
        var c = Context(); var definition = new CheckDefinition("GEO-901", "GEO", "Unverified", "Unverified", [], VerificationState.IMPLEMENTED_UNVERIFIED);
        var rule = new Rule(definition, _ => [new(CheckStatus.FAIL, "Unverified calculation")]);
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, "all", [rule], [definition]);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings).Status);
        Assert.Equal(ExitCode.Incomplete, report.ExitCode);
    }
    [Fact]
    public void ModuleSelectionRunsIndependentChecksAndCoverageExposesUnimplementedRules()
    {
        var report = Run(Context(), "geometry");
        Assert.DoesNotContain(report.Findings, f => f.Module == "LOAD");
        Assert.Contains(report.Coverage, c => c.CheckId == "GEO-005" && c.Implementation == VerificationState.NOT_IMPLEMENTED);
        Assert.False(report.IsComplete);
    }
    [Fact]
    public void DiagnosisPreservesEvidenceAndEngineeringStatus()
    {
        var report = Run(Context().Validation(v => v with { Combinations = Data<LoadCombination>() }));
        var enriched = DiagnosisEngine.Enrich(report);
        foreach (var original in report.Findings)
        {
            var result = Assert.Single(enriched.Findings, f => f.Id == original.Id);
            Assert.Equal(original.Status, result.Status); Assert.Equal(original.Evidence, result.Evidence); Assert.NotEmpty(result.Recommendations);
        }
        Assert.Equal(ExitCode.EngineeringFailures, enriched.ExitCode);
    }
    [Fact]
    public void SameInputProducesStableFindingIdsAndValues()
    {
        var first = Run(Context()); var second = Run(Context());
        Assert.Equal(JsonSerializer.Serialize(first.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })),
            JsonSerializer.Serialize(second.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })));
    }
    [Fact]
    public void CancellationStopsRuleScheduling()
    {
        var c = Context();
        Assert.Throws<OperationCanceledException>(() => new InspectionEngine(NullLogger<InspectionEngine>.Instance)
            .Run(c.Input, c.Configuration, "all", CheckCatalog.Implemented, CheckCatalog.All, new CancellationToken(true)));
    }
    [Theory]
    [InlineData("{\"schemaVersion\":\"2.0\"}")]
    [InlineData("{\"confirmed\":true,\"confirmed\":false}")]
    [InlineData("{\"geometry\":{\"shortFrameThreshold\":{\"value\":-1,\"unit\":\"mm\"}}}")]
    [InlineData("{\"geometry\":{\"coincidentTolerance\":{\"value\":1,\"unit\":\"banana\"}}}")]
    [InlineData("{\"loading\":{\"requiredCombinations\":[\"A\",\"A\"]}}")]
    [InlineData("{\"executeScript\":\"untrusted\"}")]
    [InlineData("{\"loading\":{\"expectedPatterns\":[null]}}")]
    [InlineData("{\"etabs\":{\"requiredVersionPrefix\":\"22.5\"}}")]
    public void InvalidConfigurationFailsBeforeAnyEtabsWork(string json)
    {
        var path = Path.GetTempFileName();
        try { File.WriteAllText(path, json); Assert.Throws<ConfigurationException>(() => ProjectConfiguration.Load(path, true)); }
        finally { File.Delete(path); }
    }
    [Fact]
    public void MissingConfigurationIsExplicitlyUnconfirmed()
    {
        var input = ProjectConfiguration.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.False(input.WasFound); Assert.False(input.Settings.Confirmed);
    }
    [Fact]
    public void PrdFutureSettingsArePreservedWithoutActivatingCodeChecks()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":\"1.0\",\"etabs\":{\"requiredVersionPrefix\":\"22.7\",\"connectionMode\":\"AttachOnly\"},\"codes\":{\"loadStandard\":null}}");
            var config = ProjectConfiguration.Load(path, true);
            Assert.False(config.Settings.Confirmed); Assert.NotNull(config.Settings.Codes);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void InspectArgumentsAcceptConfigAndRejectUnsupportedModule()
    {
        var options = CliOptions.Parse(["inspect", "loads", "--config", "project.json", "--details"]);
        Assert.Equal("loads", options.Module); Assert.True(options.ExplicitConfiguration); Assert.True(options.Details);
        Assert.Throws<ConfigurationException>(() => CliOptions.Parse(["inspect", "unsupported"]));
    }
}


