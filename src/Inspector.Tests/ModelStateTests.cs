public sealed class ModelStateTests
{
    private static InspectionReport Inspect(InspectionContext c, string module = "all") =>
        new InspectionEngine(NullLogger<InspectionEngine>.Instance).Run(c.Input, c.Configuration, module, CheckCatalog.Implemented, CheckCatalog.All);

    private static InspectionContext DesignModel(int count = 60, bool? available = true)
    {
        var c = Context();
        var template = c.Input.Model.Frames!.Items[0];
        var frames = Enumerable.Range(1, count).Select(i => template with { Id = "F" + i, Story = i <= count / 2 ? "L1" : "L2" }).ToArray();
        return c with { Input = c.Input with
        {
            Model = c.Input.Model with { Frames = Data(frames) },
            ConcreteDesignStatus = new(available, Data(frames.Select(f => new ConcreteDesignMember(f.Id, "Beam", 2)).ToArray()), []),
            ConcreteColumns = Data<ConcreteColumnDesignResult>(), ConcreteBeams = Data<ConcreteBeamDesignResult>()
        } };
    }

    private static ConcreteBeamDesignResult Beam(string id, string error = "") =>
        new(id, "B", 0, 1200, 1200, .2, 0, "ULS", "ULS", error, "") { BottomFlexuralCombo = "ULS" };

    [Theory]
    [InlineData("all")]
    [InlineData("design")]
    [InlineData("sanity")]
    public void UnrunDesignProducesOneInformationalMessage(string module)
    {
        var c = DesignModel(1200, false);
        var report = Inspect(c, module);
        var message = Assert.Single(report.Findings, f => f.CheckId.StartsWith("STATE-DESIGN", StringComparison.Ordinal));
        Assert.Equal("Concrete design has not been run.", message.Title);
        Assert.Equal(Severity.INFORMATIONAL, message.Severity);
        Assert.Equal(FindingKind.OperationNotPerformed, message.Kind);
        Assert.Empty(message.AffectedElements);
        Assert.DoesNotContain(report.Findings, f => f.Module is "BEAM" or "COL" or "DETAIL");
        Assert.All(report.Coverage.Where(c => c.CheckId.StartsWith("BEAM-", StringComparison.Ordinal)), c => Assert.NotNull(c.DeferredReason));
    }

    [Fact]
    public void EmptyResultsAndLockedModelDoNotProveDesignWasNotRun()
    {
        var c = DesignModel(10, null);
        var report = Inspect(c, "design");
        Assert.Equal(OperationState.Unknown, report.ModelState.ConcreteDesign);
        Assert.DoesNotContain(report.Findings, f => f.Title == "Concrete design has not been run.");
        Assert.Contains(report.Findings, f => f.Kind == FindingKind.ApiUnavailable);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public void FailedSummaryReadDoesNotBecomeMissingMemberDesign()
    {
        var c = DesignModel();
        c = c with { Input = c.Input with { ConcreteBeams = new([], [], ["API failed"]) { ReadSucceeded = false } } };
        var report = Inspect(c, "design");
        Assert.DoesNotContain(report.Findings, f => f.Status == CheckStatus.FAIL);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("have no design results", StringComparison.Ordinal));
        Assert.All(report.Findings.Where(f => f.CheckId == "STATE-DESIGN-COVERAGE"), f => Assert.Equal(FindingKind.ApiUnavailable, f.Kind));
    }

    [Fact]
    public void PartialDesignIsGroupedByStoryAndTypeWithExactMemberIds()
    {
        var c = DesignModel(1000);
        c = c with { Input = c.Input with { ConcreteBeams = Data(Beam("F1")) } };
        var report = Inspect(c, "design");
        Assert.Equal(OperationState.Partial, report.ModelState.ConcreteDesign);
        var groups = report.Findings.Where(f => f.CheckId == "STATE-DESIGN-COVERAGE").ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal(999, groups.Sum(f => f.AffectedElements.Count));
        Assert.DoesNotContain(groups.SelectMany(f => f.AffectedElements), id => id == "F1");
        Assert.All(groups, f => Assert.Equal(CheckStatus.WARNING, f.Status));
        Assert.Contains(report.Findings, f => f.Module == "BEAM" && f.ElementId == "F1");
        Assert.DoesNotContain(report.Findings, f => f.CheckId == "STATE-DESIGN-DATA");
    }

    [Fact]
    public void DesignErrorsAcrossStationsAreReportedOncePerStoryAndType()
    {
        var c = DesignModel(20);
        var rows = Enumerable.Range(1, 20).SelectMany(i => new[] { Beam("F" + i, "Design error " + i), Beam("F" + i, "Another error") with { Location = 5 } }).ToArray();
        c = c with { Input = c.Input with { ConcreteBeams = Data(rows) } };
        var report = Inspect(c, "design");
        var errors = report.Findings.Where(f => f.Status == CheckStatus.FAIL).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Equal(20, errors.Sum(f => f.AffectedElements.Count));
        Assert.DoesNotContain(report.Findings, f => f.Module == "BEAM");
    }

    [Fact]
    public void NoDesignAndSteelMembersAreExcludedFromConcreteCoverage()
    {
        var c = DesignModel(3);
        c = c with { Input = c.Input with
        {
            ConcreteDesignStatus = new(true, Data(new ConcreteDesignMember("F1", "Beam", 2), new("F2", "Beam", 7), new("F3", "Beam", 1)), []),
            ConcreteBeams = Data(Beam("F1"))
        } };
        var state = c.State;
        Assert.Equal(OperationState.Complete, state.ConcreteDesign);
        Assert.Empty(state.DesignGroups);
    }

    [Fact]
    public void ProgramDeterminedProcedureUsesMaterialRatherThanFrameDirection()
    {
        var c = DesignModel(1);
        c = c with { Input = c.Input with { ConcreteDesignStatus = new(true, Data(new ConcreteDesignMember("F1", "Beam", 0)), []), ConcreteBeams = Data(Beam("F1")) } };
        Assert.Equal(OperationState.Complete, c.State.ConcreteDesign);
        c = c.Model(m => m with { Materials = Data(m.Materials!.Items[0] with { Type = "Steel" }) });
        c = c with { Input = c.Input with { ConcreteBeams = Data<ConcreteBeamDesignResult>() } };
        Assert.Equal(OperationState.NotApplicable, c.State.ConcreteDesign);
    }

    [Fact]
    public void UnknownProcedureOrIncompleteMemberInventoryNeverClaimsCompleteCoverage()
    {
        var c = DesignModel(2);
        c = c with { Input = c.Input with { ConcreteDesignStatus = new(true, Data(new ConcreteDesignMember("F1", "Beam", 999)), []), ConcreteBeams = Data(Beam("F1")) } };
        Assert.Equal(OperationState.ResultsAvailable, c.State.ConcreteDesign);
        Assert.False(c.State.DesignCoverageKnown);
    }

    [Fact]
    public void GeometryAndConfirmedMissingInputsAreStillCheckedWhenDesignIsUnrun()
    {
        var c = DesignModel(1, false).Validation(v => v with { Combinations = Data<LoadCombination>() });
        var report = Inspect(c);
        Assert.Contains(report.Findings, f => f.CheckId == "LOAD-007" && f.Status == CheckStatus.FAIL && f.Kind == FindingKind.MissingInput);
        Assert.Contains(report.Findings, f => f.Module == "GEO");
    }

    [Fact]
    public void UnrunAnalysisDefersDependentResultsWithoutCallingThemErrors()
    {
        var c = Context();
        var cases = c.Input.Validation.LoadCases!.Items.Select(x => new AnalysisCaseStatus(x.Name, AnalysisCaseState.NotRun, 1)).ToArray();
        c = c with { Input = c.Input with { Analysis = new(new(Data(cases)), null, null, []) } };
        var report = Inspect(c, "analysis");
        Assert.Equal(OperationState.NotRun, report.ModelState.Analysis);
        var message = Assert.Single(report.Findings, f => f.CheckId == "STATE-ANALYSIS");
        Assert.Equal("Analysis has not been run.", message.Title);
        Assert.DoesNotContain(report.Findings, f => f.Status is CheckStatus.FAIL or CheckStatus.ERROR);
    }

    [Fact]
    public void IncompleteStatusInventoryDoesNotClaimAnalysisWasNotRun()
    {
        var c = Context();
        c = c with { Input = c.Input with { Analysis = new(new(Data(new AnalysisCaseStatus("Dcase", AnalysisCaseState.NotRun, 1))), null, null, []) } };
        Assert.Equal(OperationState.Unknown, c.State.Analysis);
    }

    [Fact]
    public void AnActualAnalysisFailureIsPreserved()
    {
        var c = Context();
        c = c with { Input = c.Input with { Analysis = new(new(Data(new AnalysisCaseStatus("Dcase", AnalysisCaseState.CouldNotStart, 2))), null, null, []) } };
        var report = Inspect(c, "analysis");
        Assert.Equal(OperationState.Failed, report.ModelState.Analysis);
        Assert.Contains(report.Findings, f => f.CheckId == "ANA-002" && f.Status == CheckStatus.FAIL);
    }

    [Fact]
    public void MissingMassSourceIsOnlyAProblemWhenDefinedCasesNeedMass()
    {
        var c = Context().Validation(v => v with { MassSources = Data<MassSource>() });
        var check = LoadingChecks.Create().Single(x => x.Definition.Id == "LOAD-009");
        Assert.Equal(CheckStatus.NOT_APPLICABLE, Assert.Single(check.Evaluate(c)).Status);
        c = c.Validation(v => v with { LoadCases = Data(new LoadCase("M", "Modal", 0, null)) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(check.Evaluate(c)).Status);
        c = c.Validation(v => v with { MassSources = new([], [], ["API unavailable"]) { ReadSucceeded = false } });
        var report = Inspect(c, "loads");
        Assert.DoesNotContain(report.Findings, f => f.CheckId == "LOAD-009" && f.Status == CheckStatus.FAIL);
    }

    [Theory]
    [InlineData(null, CheckStatus.NOT_VERIFIED)]
    [InlineData("", CheckStatus.NOT_APPLICABLE)]
    [InlineData("ASCE 7-16", CheckStatus.FAIL)]
    public void StaticQuakePatternDoesNotImplyAutomaticMassLoading(string? code, CheckStatus expected)
    {
        var c = Context().Validation(v => v with
        {
            MassSources = Data<MassSource>(), LoadPatterns = Data(new LoadPattern("Q", "Quake", 0) { AutoSeismicCode = code }),
            LoadCases = Data(new LoadCase("EQ", "LinearStatic", 0, [new("Load", "Q", 1)]))
        });
        Assert.Equal(expected, Assert.Single(LoadingChecks.Create().Single(x => x.Definition.Id == "LOAD-009").Evaluate(c)).Status);
    }

    [Fact]
    public void StaticAccelerationRequiresMassEvenWithoutDynamicCases()
    {
        var c = Context().Validation(v => v with { MassSources = Data<MassSource>(),
            LoadCases = Data(new LoadCase("A", "LinearStatic", 0, [new("Accel", "UX", 1)])) });
        Assert.Equal(CheckStatus.FAIL, Assert.Single(LoadingChecks.Create().Single(x => x.Definition.Id == "LOAD-009").Evaluate(c)).Status);
    }

    [Fact]
    public void NoCombinationsOrDiaphragmsAreErrorsWithoutARequirement()
    {
        var c = Context().Settings(s => s with { Loading = new(), Geometry = new(RequireFloorDiaphragms: false) })
            .Validation(v => v with { Combinations = Data<LoadCombination>(), Diaphragms = Data<Diaphragm>(),
                AreaAssignments = Data(new AreaAssignment("S1", null, null)),
                JointAssignments = Data(v.JointAssignments!.Items.Select(j => j with { Diaphragm = null, DiaphragmOption = "None" }).ToArray()) });
        var report = Inspect(c);
        Assert.DoesNotContain(report.Findings, f => f.Status == CheckStatus.FAIL && f.CheckId is "LOAD-007" or "LOAD-008" or "GEO-012");
    }

    [Theory]
    [InlineData(AnalysisAuthorization.Granted)]
    [InlineData(AnalysisAuthorization.NotGranted)]
    public void ReadOnlyInspectorCannotStartAnalysis(AnalysisAuthorization authorization)
    {
        var session = new AnalysisTrap();
        Assert.False(new SmartAnalysisController().Run(session, authorization).WasStarted);
        Assert.Equal(0, session.RunCalls);
        Assert.Throws<ConfigurationException>(() => CliOptions.Parse(["run-analysis"]));
    }

    [Fact]
    public void MissingCasesAreOnlyReportedWhenRequiredByConfirmedSettings()
    {
        var c = Context().Settings(s => s with { Analysis = new(ModalCase: "MissingModal") });
        var report = Inspect(c, "loads");
        Assert.Contains(report.Findings, f => f.CheckId == "LOAD-014" && f.Status == CheckStatus.FAIL && f.Kind == FindingKind.MissingInput);
        c = c.Settings(s => s with { Confirmed = false });
        report = Inspect(c, "loads");
        Assert.DoesNotContain(report.Findings, f => f.CheckId == "LOAD-014" && f.Status == CheckStatus.FAIL);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("No Message", "")]
    [InlineData(" no message ", "")]
    [InlineData("Shear failure", "Shear failure")]
    public void EmptyDesignMessageSentinelsAreNotDesignErrors(string? message, string expected) =>
        Assert.Equal(expected, EtabsSession.NormalizeDesignMessage(message));

    private sealed class AnalysisTrap : IAnalysisSession
    {
        public int RunCalls { get; private set; }
        public void RunAnalysis() { RunCalls++; throw new InvalidOperationException("Mutation attempted"); }
        public AnalysisStatusData ReadAnalysisStatus() => throw new NotSupportedException();
        public AnalysisResultsData ReadSelectedAnalysisResults() => throw new NotSupportedException();
        public AnalysisDefinitionData ReadAnalysisDefinitions() => throw new NotSupportedException();
    }
}
