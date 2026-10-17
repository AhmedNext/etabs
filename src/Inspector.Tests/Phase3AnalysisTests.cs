
public sealed class Phase3AnalysisTests
{
    private static InspectionReport Run(InspectionContext c, string module = "analysis") =>
        new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, module, CheckCatalog.Implemented, CheckCatalog.All);
    private static InspectionContext WithAnalysis(InspectionContext c, AnalysisInput? analysis) =>
        c with { Input = c.Input with { Analysis = analysis } };
    private static InspectionContext WithCriteria(InspectionContext c, AnalysisSettings settings) =>
        c.Settings(s => s with { Analysis = settings });

    private static AnalysisInput Analysis(AnalysisStatusData? status = null, AnalysisResultsData? results = null,
        AnalysisDefinitionData? definitions = null, ExtractionIssue[]? issues = null) =>
        new(status, results, definitions, issues ?? []);

    private static AnalysisStatusData Status(params (string Name, AnalysisCaseState State, int Raw)[] cases) =>
        new(Data(cases.Select(x => new AnalysisCaseStatus(x.Name, x.State, x.Raw)).ToArray()));
    private static AnalysisResultsData Results(StoryDriftResult[]? drifts = null,
        ModalParticipationResult[]? modal = null, BaseReactionResult[]? reactions = null) =>
        new(Data(drifts ?? []), Data(modal ?? []), Data(reactions ?? []), [], [], [], []);
    private static AnalysisDefinitionData Definitions(ResponseSpectrumCase[]? spectra = null,
        GeometricNonlinearitySetting[]? nonlinear = null) => new(Data(spectra ?? []), Data(nonlinear ?? []));

    [Fact]
    public void DriftLimitIsAppliedOnlyToConfirmedCasesAndDirections()
    {
        var c = WithCriteria(WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(
            [new("L1", "Modal", "Max", 1, "X", 0.004, "D1", 10, 0, 3000), new("L1", "Modal", "Max", 1, "Y", 0.006, "D1", 10, 0, 3000)]))),
            new AnalysisSettings(DriftRatioLimit: 0.005, DriftCases: ["Modal"], DriftDirections: ["X", "Y"]));
        var report = Run(c);
        var drift = report.Findings.Where(f => f.CheckId == "ANA-007").ToArray();
        Assert.Equal(2, drift.Length);
        Assert.Equal(CheckStatus.PASS, Assert.Single(drift, f => f.ElementId!.EndsWith("/X/D1")).Status);
        var failure = Assert.Single(drift, f => f.ElementId!.EndsWith("/Y/D1"));
        Assert.Equal(CheckStatus.FAIL, failure.Status); Assert.Equal(0.006, failure.Actual); Assert.Equal(0.005, failure.Limit);
        Assert.Equal(ExitCode.EngineeringFailures, report.ExitCode);
    }

    [Fact]
    public void DriftHasNoConclusionWithoutConfirmedCriteria()
    {
        var c = WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(
            [new("L1", "Modal", "Max", 1, "X", 0.004, "D1", 10, 0, 3000)])));
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run(c).Findings, f => f.CheckId == "ANA-007").Status);
    }

    [Fact]
    public void RemovedLegacyDisplacementCheckIsAbsent()
    {
        var c = WithCriteria(WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(
            [new("L1", "Modal", "Max", 1, "X", 0.001, "D1", 10000, 22000, 20000) { Displacement = 10 }, new("L1", "Modal", "Max", 1, "Y", 0.001, "D2", 10000, 22000, 20000) { Displacement = -30 }]))),
            new AnalysisSettings(MaxStoryPointDisplacement: 20, DisplacementCases: ["Modal"], DisplacementDirections: ["X", "Y"]));
        Assert.DoesNotContain(CheckCatalog.Implemented, check => check.Definition.Id == "ANA-006");
    }

    [Fact]
    public void CoordinatesCannotBecomeDisplacementEvidence()
    {
        var c = WithCriteria(WithAnalysis(Context(), Analysis(results: Results(
            [new("L1", "Modal", "Max", 1, "X", 0.001, "D1", 10, 0, 3000)]))),
            new AnalysisSettings(MaxStoryPointDisplacement: 20, DisplacementCases: ["Modal"], DisplacementDirections: ["X"]));
        Assert.DoesNotContain(CheckCatalog.Implemented, check => check.Definition.Id == "ANA-006");
    }

    [Fact]
    public void CaseStatusIsMappedAndUnknownCodesAreNeverPasses()
    {
        var c = WithAnalysis(Context(), Analysis(Status(
            ("Finished", AnalysisCaseState.Finished, 4), ("NotRun", AnalysisCaseState.NotRun, 1),
            ("CouldNotStart", AnalysisCaseState.CouldNotStart, 2), ("NotFinished", AnalysisCaseState.NotFinished, 3),
            ("Weird", AnalysisCaseState.Unknown, 9))));
        var statuses = Run(c).Findings.Where(f => f.CheckId == "ANA-002").ToDictionary(f => f.ElementId!);
        Assert.Equal(CheckStatus.PASS, statuses["Finished"].Status);
        Assert.Equal(CheckStatus.NOT_APPLICABLE, statuses["NotRun"].Status);
        Assert.Equal(CheckStatus.FAIL, statuses["CouldNotStart"].Status);
        Assert.Equal(CheckStatus.FAIL, statuses["NotFinished"].Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED, statuses["Weird"].Status);
    }

    [Fact]
    public void MissingAnalysisIsExplicitRatherThanAssumed()
    {
        var report = Run(Context());
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "ANA-001").Status);
        Assert.Equal(CheckStatus.SKIPPED, Assert.Single(report.Findings, f => f.CheckId == "ANA-002").Status);
        Assert.Equal(ExitCode.Incomplete, report.ExitCode);
    }

    [Fact]
    public void ExtractionErrorsAreReportedAsErrorsNotEngineeringPasses()
    {
        var c = WithAnalysis(Context(), Analysis(issues: [new("AnalysisResults", "API failed", "Results.StoryDrifts", 1)]));
        var report = Run(c);
        Assert.Equal(CheckStatus.ERROR, Assert.Single(report.Findings, f => f.CheckId == "ANA-001" && f.Status == CheckStatus.ERROR).Status);
        Assert.False(report.HasFailures);
        Assert.Equal(ExitCode.Incomplete, report.ExitCode);
    }

    [Fact]
    public void IndependentChecksContinueAfterAnAnalysisFailure()
    {
        var c = WithAnalysis(Context(), Analysis(Status(("Finished", AnalysisCaseState.Finished, 4)), null,
            issues: [new("AnalysisResults", "API failed", "Results.StoryDrifts", 1)]));
        var report = Run(c);
        Assert.Equal(CheckStatus.ERROR, Assert.Single(report.Findings, f => f.CheckId == "ANA-001" && f.Status == CheckStatus.ERROR).Status);
        Assert.Equal(CheckStatus.PASS, Assert.Single(report.Findings, f => f.CheckId == "ANA-002").Status);
        Assert.DoesNotContain(report.Findings, f => f.CheckId == "ANA-006");
    }

    [Fact]
    public void ModalParticipationComparesTheMaximumCumulativeRatio()
    {
        var c = WithCriteria(WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(modal:
            [new("Modal", "Mode", 1, 1.2, 0.6, 0.1, 0, 0.6, 0.1, 0, 0, 0, 0, 0, 0, 0),
             new("Modal", "Mode", 2, 0.8, 0.95, 0.8, 0, 0.95, 0.8, 0, 0, 0, 0, 0, 0, 0)]))),
            new AnalysisSettings(ModalCase: "Modal", MinimumCumulativeUx: 0.9, MinimumCumulativeUy: 0.9));
        var modal = Run(c, "seismic").Findings.Where(f => f.CheckId == "SEIS-004").ToArray();
        Assert.Equal(2, modal.Length);
        Assert.Equal(CheckStatus.PASS, Assert.Single(modal, f => f.Unit == "ratio" && f.Actual == 0.95).Status);
        Assert.Equal(CheckStatus.FAIL, Assert.Single(modal, f => f.Actual == 0.8).Status);
    }

    [Fact]
    public void MissingSeismicEvidenceNeverPasses()
    {
        var c = WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(),
            Definitions([new("RSX", "Modal", 0, 1, 1, 0, 0, 0, 1, [new("X", "UBC", 1, "Global", 0)])],
                [new("NL", "NonlinearStatic", 1, "P-Delta")])));
        var report = Run(c, "seismic");
        Assert.All(report.Findings.Where(f => f.CheckId == "SEIS-007"), f => Assert.Equal(CheckStatus.NOT_VERIFIED, f.Status));
        Assert.Equal(VerificationState.VERIFIED, Assert.Single(report.Coverage, x => x.CheckId == "SEIS-007").Implementation);
        Assert.Equal(CheckStatus.PASS, Assert.Single(report.Findings, f => f.CheckId == "SEIS-014").Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "SEIS-017").Status);
    }

    [Fact]
    public void BaseReactionInventoryPassesWhenSuccessfullyExtracted()
    {
        var c = WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)), Results(reactions:
            [new("Modal", "Max", 1, 10, 0, 100, 0, 0, 0, new(0, 0, 0))])));
        Assert.Equal(CheckStatus.PASS, Assert.Single(Run(c).Findings, f => f.CheckId == "ANA-008").Status);
    }

    [Fact]
    public void AnalysisModuleExcludesDefinitionInspectionModules()
    {
        var report = Run(WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4)))), "analysis");
        Assert.DoesNotContain(report.Findings, f => f.CheckId.StartsWith("GEO", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.CheckId.StartsWith("SEIS", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalysisInspectionIsDeterministic()
    {
        var c = WithCriteria(WithAnalysis(Context(), Analysis(Status(("Modal", AnalysisCaseState.Finished, 4), ("Dead", AnalysisCaseState.NotRun, 1)), Results(
            [new("L1", "Modal", "Max", 1, "X", 0.004, "D1", 10, 0, 3000)]))),
            new AnalysisSettings(DriftRatioLimit: 0.005, DriftCases: ["Modal"], DriftDirections: ["X"]));
        var first = Run(c); var second = Run(c);
        Assert.Equal(JsonSerializer.Serialize(first.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })),
            JsonSerializer.Serialize(second.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })));
    }
}


