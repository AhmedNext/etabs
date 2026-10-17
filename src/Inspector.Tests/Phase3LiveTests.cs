
public sealed class Phase3LiveTests
{
    private static Task Verify(Action<EtabsSession, InspectionInput> verify) => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        var extractor = new InspectionExtractor(new(NullLogger<ModelExtractor>.Instance), NullLogger<InspectionExtractor>.Instance);
        var input = extractor.Extract(session, readAnalysis: true);
        Assert.Empty(input.Model.Issues); Assert.Empty(input.Validation.Issues);
        Assert.NotNull(input.Analysis);
        verify(session, input);
        return true;
    });

    private static InspectionReport Run(InspectionInput input, string module = "analysis") =>
        new InspectionEngine(NullLogger<InspectionEngine>.Instance)
            .Run(input, new ConfigurationInput(new(), null, null, false), module, CheckCatalog.Implemented, CheckCatalog.All);

    [LiveEtabsFact]
    public Task AnalysisCaseStatusMatchesRawGetCaseStatus() => Verify((session, input) =>
    {
        var statuses = input.Analysis!.Status!.Cases.Items;
        Assert.NotEmpty(statuses);
        int count = 0; string[] names = []; int[] codes = [];
        Assert.Equal(0, session.ModelForVerification.Analyze.GetCaseStatus(ref count, ref names, ref codes));
        Assert.Equal(count, statuses.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(codes[i], Assert.Single(statuses, s => s.Name == names[i]).RawStatus);
    });

    [LiveEtabsFact]
    public Task UnconfirmedAndUnsupportedChecksAreNeverReportedAsPasses() => Verify((session, input) =>
    {
        var report = Run(input);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "ANA-007").Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "ANA-006").Status);
        foreach (var id in new[] { "ANA-004", "ANA-005", "ANA-008", "ANA-009", "ANA-010", "ANA-011",
            "ANA-012", "ANA-013", "ANA-014", "ANA-015" })
            Assert.DoesNotContain(report.Findings, f => f.CheckId == id && f.Status == CheckStatus.PASS);
        Assert.All(report.Coverage.Where(c => c.CheckId is "ANA-009" or "ANA-015"),
            c => Assert.Equal(VerificationState.VERIFIED, c.Implementation));
    });

    [LiveEtabsFact]
    public Task AnalysisAvailabilityErrorsStayTechnicalAndCarryEvidence() => Verify((session, input) =>
    {
        var report = Run(input);
        Assert.All(report.Findings.Where(f => f.Status == CheckStatus.ERROR), f =>
        {
            Assert.Equal("ANA-001", f.CheckId); Assert.NotEmpty(f.Evidence);
        });
        Assert.False(report.HasFailures);
        Assert.False(report.IsComplete);
    });

    [LiveEtabsFact]
    public Task ModalFallbackRowsAreCountedAndNeverBecomePasses() => Verify((session, input) =>
    {
        var results = input.Analysis!.Results;
        Assert.NotNull(results);
        Assert.NotEmpty(results.ModalParticipation.Items);
        var table = session.ReadTable("Modal Participating Mass Ratios");
        Assert.Equal(table.Rows.Count, results.ModalParticipation.Items.Count);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(Run(input, "seismic").Findings, f => f.CheckId == "SEIS-004").Status);
    });

    [LiveEtabsFact]
    public Task SeismicDefinitionsAreExtractedAndInventoried() => Verify((session, input) =>
    {
        Assert.NotNull(input.Analysis!.Definitions);
        var report = Run(input, "seismic");
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "SEIS-014").Status);
        Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(report.Findings, f => f.CheckId == "SEIS-017").Status);
        Assert.DoesNotContain(report.Findings, f => f.Status == CheckStatus.PASS);
    });

    [LiveEtabsFact]
    public Task AnalysisInspectionIsDeterministicOnTheLiveModel() => Verify((session, input) =>
    {
        var first = Run(input); var second = Run(input);
        Assert.Equal(JsonSerializer.Serialize(first.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })),
            JsonSerializer.Serialize(second.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })));
        foreach (var finding in first.Findings)
        {
            Assert.NotEmpty(finding.Evidence); Assert.NotEmpty(finding.RuleVersion); Assert.NotEmpty(finding.CalculationMethod);
        }
    });
}


