/// <summary>Defers checks whose operations have not happened and preserves the reason in coverage.</summary>
internal sealed class ModelStatePolicy(InspectionContext context)
{
    private readonly ModelStateAssessment state = context.State;
    private readonly Dictionary<string, (Finding Finding, HashSet<string> Checks)> summaries = new(StringComparer.Ordinal);

    public string? Defer(CheckDefinition definition)
    {
        var input = context.Input;
        if (definition.Id is "SEIS-003" or "SEIS-004" or "SEIS-005" or "SAN-002" or "SAN-008" or "ANA-004" &&
            input.Validation.LoadCases is { ReadSucceeded: true, Limitations.Count: 0 } modalCases &&
            modalCases.Items.All(c => c.Type != "Modal") && input.Analysis?.Results?.ModalParticipation.Items.Count is not > 0 &&
            string.IsNullOrWhiteSpace(context.Configuration.Settings.Analysis?.ModalCase))
            return Summary("STATE-MODAL", definition, CheckStatus.NOT_APPLICABLE,
                "No modal cases are defined or required by the inspection settings.", FindingKind.Information);
        if (definition.Id is "SEIS-014" or "SEIS-015" or "AUD-002" &&
            input.Validation.LoadCases is { ReadSucceeded: true, Limitations.Count: 0 } cases &&
            cases.Items.All(c => c.Type != "ResponseSpectrum") &&
            input.Analysis?.Definitions?.ResponseSpectrumCases.Items.Count is not > 0 &&
            context.Configuration.Settings.SeismicVerification?.Axes.Any(a => !string.IsNullOrWhiteSpace(a.SpectrumCase)) != true)
            return Summary("STATE-SPECTRUM", definition, CheckStatus.NOT_APPLICABLE,
                "No response-spectrum cases are defined or required by the inspection settings.", FindingKind.Information);
        var column = definition.Module == "COL" || definition.Id is "OPT-001" or "DETAIL-008" or "SAN-005";
        var beam = definition.Module == "BEAM";
        if (column || beam || definition.Id == "ANA-015")
        {
            if (state.ConcreteDesign is OperationState.NotRun or OperationState.NotApplicable)
                return Summary("STATE-DESIGN", definition, CheckStatus.NOT_APPLICABLE,
                    state.ConcreteDesign == OperationState.NotRun ? "Concrete design has not been run." : "No frames require concrete design.",
                    state.ConcreteDesign == OperationState.NotRun ? FindingKind.OperationNotPerformed : FindingKind.Information);
            var type = column ? "Column" : "Beam";
            if ((column || beam) && state.DesignCoverageKnown && input.ConcreteDesignStatus?.Members?.Items
                .Any(m => m.Orientation == type && ModelStateAssessment.RequiresConcreteDesign(m, input) == true) != true)
                return Summary("STATE-DESIGN-" + type.ToUpperInvariant(), definition, CheckStatus.NOT_APPLICABLE,
                    $"No {type.ToLowerInvariant()}s require concrete design.", FindingKind.Information);
            if ((column && input.ConcreteColumns?.Items.Count is not > 0) || (beam && input.ConcreteBeams?.Items.Count is not > 0))
                return Summary("STATE-DESIGN-DATA", definition, CheckStatus.SKIPPED,
                    state.ConcreteDesign == OperationState.NotRead ? "Concrete design data was not read for this inspection."
                        : "Concrete design data could not be fully read. Missing results do not show that members failed design.",
                    state.ConcreteDesign == OperationState.NotRead ? FindingKind.Information : FindingKind.ApiUnavailable);
        }
        if (definition.Id is "WALL-008" or "WALL-009" or "OPT-002" or "SAN-006" && input.ConcretePiers?.Items.Count is not > 0)
        {
            var noWalls = input.Model.Areas is { ReadSucceeded: true, Limitations.Count: 0 } areas &&
                areas.Items.All(a => a.IsOpening || a.Orientation is "Floor" or "Null");
            return Summary("STATE-WALL-DESIGN", definition, noWalls ? CheckStatus.NOT_APPLICABLE : CheckStatus.SKIPPED,
                noWalls ? "No wall objects require a wall design review." : "Wall design results are unavailable. Wall design completion could not be confirmed.",
                noWalls ? FindingKind.Information : FindingKind.ApiUnavailable);
        }
        if (state.Analysis is OperationState.NotRun or OperationState.NotApplicable &&
            (definition.RequiredData.Contains("AnalysisResults") || definition.RequiredData.Contains("AnalysisStatus") || definition.Id == "ANA-001"))
            return Summary("STATE-ANALYSIS", definition, CheckStatus.NOT_APPLICABLE,
                state.Analysis == OperationState.NotRun ? "Analysis has not been run." : "No analysis cases are defined.",
                FindingKind.OperationNotPerformed);
        return null;
    }

    public InspectionContext ForCheck(CheckDefinition definition)
    {
        // The grouped state finding carries all design errors. Do not repeat it at each station in each rule.
        if (definition.Module is not ("COL" or "BEAM" or "DETAIL") && definition.Id != "OPT-001") return context;
        var columns = context.Input.ConcreteColumns;
        var beams = context.Input.ConcreteBeams;
        var badColumns = columns?.Items.Where(r => !string.IsNullOrWhiteSpace(r.ErrorSummary)).Select(r => r.FrameId).ToHashSet(StringComparer.Ordinal) ?? [];
        var badBeams = beams?.Items.Where(r => !string.IsNullOrWhiteSpace(r.ErrorSummary)).Select(r => r.FrameId).ToHashSet(StringComparer.Ordinal) ?? [];
        return context with { Input = context.Input with
        {
            ConcreteColumns = columns is null ? null : columns with { Items = columns.Items.Where(r => !badColumns.Contains(r.FrameId)).ToArray() },
            ConcreteBeams = beams is null ? null : beams with { Items = beams.Items.Where(r => !badBeams.Contains(r.FrameId)).ToArray() }
        } };
    }

    public IReadOnlyList<Finding> Findings(bool includeDesign)
    {
        var output = summaries.Values.Select(s => s.Finding with { RelatedCheckIds = s.Checks.Order(StringComparer.Ordinal).ToArray() }).ToList();
        if (!includeDesign || state.ConcreteDesign is OperationState.NotRun or OperationState.NotApplicable or OperationState.NotRead) return output;
        foreach (var group in state.DesignGroups)
        {
            var error = group.Reason == "DesignError";
            var unavailable = group.Reason == "ApiUnavailable";
            var label = group.ElementType == "Column" ? "columns" : "beams";
            var title = error ? $"ETABS reports a design error for {group.Elements.Count} {label} on {group.Story}."
                : unavailable ? $"Design results could not be read for {group.Elements.Count} {label} on {group.Story}."
                : $"Concrete design is incomplete: {group.Elements.Count} {label} on {group.Story} have no design results.";
            output.Add(Create("STATE-DESIGN-COVERAGE", title, error ? CheckStatus.FAIL : unavailable ? CheckStatus.SKIPPED : CheckStatus.WARNING,
                error ? FindingKind.ModelProblem : unavailable ? FindingKind.ApiUnavailable : FindingKind.OperationNotPerformed,
                group.Story + ":" + group.ElementType + ":" + group.Reason) with
            {
                Story = group.Story, ElementType = "FrameGroup", AffectedElements = group.Elements,
                Actual = group.Elements.Count, Unit = "members",
                Evidence = [new("Affected members", "Concrete design coverage", string.Join(", ", group.Elements)),
                    new("Coverage reason", "Concrete design summaries + frame design assignments", group.Reason),
                    new("ETABS design messages", "Concrete design summary error field", string.Join("; ",
                        (context.Input.ConcreteColumns?.Items.Where(r => group.Elements.Contains(r.FrameId)).Select(r => r.ErrorSummary) ?? [])
                        .Concat(context.Input.ConcreteBeams?.Items.Where(r => group.Elements.Contains(r.FrameId)).Select(r => r.ErrorSummary) ?? [])
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal)))]
            });
        }
        if (!state.DesignCoverageKnown && (context.Input.ConcreteColumns?.Items.Count > 0 || context.Input.ConcreteBeams?.Items.Count > 0))
            output.Add(Create("STATE-DESIGN-COVERAGE", "Concrete design results are available. Full member coverage could not be confirmed.",
                CheckStatus.NOT_VERIFIED, FindingKind.ApiUnavailable, "unknown-coverage"));
        return output;
    }

    private string Summary(string id, CheckDefinition definition, CheckStatus status, string title, FindingKind kind)
    {
        if (!summaries.TryGetValue(id, out var summary))
        {
            summary = (Create(id, title, status, kind, id), new(StringComparer.Ordinal));
            summaries.Add(id, summary);
        }
        summary.Checks.Add(definition.Id);
        return title;
    }

    private Finding Create(string id, string title, CheckStatus status, FindingKind kind, string key) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + key)))[..20], id, "STATE", title,
            status, status == CheckStatus.FAIL ? Severity.HIGH : status == CheckStatus.WARNING ? Severity.MEDIUM : Severity.INFORMATIONAL,
            null, null, null, null, null, null, null, null, null, null, "1.0", "Assess model state before evaluating dependent checks.",
            [new("Analysis state", "Analyze.GetCaseStatus", state.Analysis.ToString()),
                new("Concrete design availability", "DesignConcrete.GetResultsAvailable", context.Input.ConcreteDesignStatus?.ResultsAvailable?.ToString() ?? "Unknown"),
                new("State limitations", "ETABS read-only inspection", "Availability describes retained results; historical execution and result freshness are not proven."),
                ..context.Input.ConcreteDesignStatus?.Issues.Select(i => new Evidence(i.Component, i.Operation ?? "Extraction", i.Message)) ?? []],
            [], [], "Review the reported state and any read limitations in ETABS.", context.Timestamp) { Kind = kind };
}
