public enum OperationState { NotRead, Unknown, NotRun, Partial, Failed, Complete, ResultsAvailable, NotApplicable }
public sealed record DesignCoverageGroup(string ElementType, string Story, string Reason, IReadOnlyList<string> Elements);

/// <summary>Describes observed state, never historical execution or design adequacy.</summary>
public sealed record ModelStateAssessment(OperationState Analysis, OperationState ConcreteDesign,
    IReadOnlyList<DesignCoverageGroup> DesignGroups, bool DesignCoverageKnown)
{
    public static ModelStateAssessment Assess(InspectionInput input)
    {
        var analysis = AssessAnalysis(input);
        var columns = input.ConcreteColumns;
        var beams = input.ConcreteBeams;
        var status = input.ConcreteDesignStatus;
        var hasRows = columns?.Items.Count > 0 || beams?.Items.Count > 0;
        var available = status?.ResultsAvailable ?? input.Analysis?.Results?.Extended?.ConcreteResultsAvailable;
        if (status is null && columns is null && beams is null && available is null)
            return new(analysis, OperationState.NotRead, [], false);

        List<DesignCoverageGroup> groups = [];
        var frames = input.Model.Frames?.Items.ToDictionary(f => f.Id, StringComparer.Ordinal) ?? [];
        var members = status?.Members;
        var coverageKnown = members is { ReadSucceeded: true, Limitations.Count: 0 } && input.Model.Frames is not null &&
            members.Items.Count == frames.Count &&
            members.Items.Select(m => m.FrameId).Distinct(StringComparer.Ordinal).Count() == frames.Count &&
            members.Items.All(m => frames.ContainsKey(m.FrameId));
        var eligible = new List<ConcreteDesignMember>();
        foreach (var member in members?.Items ?? [])
        {
            var required = RequiresConcreteDesign(member, input);
            if (required is null || required == true && member.Orientation is not ("Column" or "Beam")) coverageKnown = false;
            if (required == true && member.Orientation is "Column" or "Beam") eligible.Add(member);
        }

        if (coverageKnown && eligible.Count == 0 && !hasRows)
            return new(analysis, OperationState.NotApplicable, [], true);
        // Only the dedicated availability getter can establish absence of retained design results.
        // Empty arrays, an API exception, and model lock state cannot establish this.
        if (available == false && !hasRows)
            return new(analysis, OperationState.NotRun, [], coverageKnown);
        if (available == false && hasRows)
            return new(analysis, OperationState.Unknown, [], false);

        var columnIds = columns?.Items.Select(r => r.FrameId).ToHashSet(StringComparer.Ordinal) ?? [];
        var beamIds = beams?.Items.Select(r => r.FrameId).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var group in eligible.Where(m => !(m.Orientation == "Column" ? columnIds : beamIds).Contains(m.FrameId))
            .GroupBy(m => (m.Orientation, Story: frames.GetValueOrDefault(m.FrameId)?.Story ?? "Unknown")))
        {
            var readSucceeded = group.Key.Orientation == "Column" ? columns?.ReadSucceeded == true : beams?.ReadSucceeded == true;
            groups.Add(new(group.Key.Orientation, group.Key.Story, readSucceeded ? "MissingResults" : "ApiUnavailable",
                group.Select(m => m.FrameId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
        AddErrors(columns?.Items.Select(r => (r.FrameId, r.ErrorSummary)) ?? [], "Column");
        AddErrors(beams?.Items.Select(r => (r.FrameId, r.ErrorSummary)) ?? [], "Beam");
        var state = !hasRows ? OperationState.Unknown : groups.Count > 0 ? OperationState.Partial :
            coverageKnown && columns?.ReadSucceeded != false && beams?.ReadSucceeded != false ? OperationState.Complete : OperationState.ResultsAvailable;
        return new(analysis, state, groups, coverageKnown);

        void AddErrors(IEnumerable<(string FrameId, string Error)> rows, string type)
        {
            foreach (var group in rows.Where(r => !string.IsNullOrWhiteSpace(r.Error))
                .GroupBy(r => frames.GetValueOrDefault(r.FrameId)?.Story ?? "Unknown"))
                groups.Add(new(type, group.Key, "DesignError",
                    group.Select(r => r.FrameId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
        }
    }

    public static bool? RequiresConcreteDesign(ConcreteDesignMember member, InspectionInput input)
    {
        if (member.Procedure == 2) return true;
        if (member.Procedure is 1 or 3 or 4 or 7 or 13) return false;
        if (member.Procedure != 0) return null;
        // Program-determined design follows the effective assigned material, not member direction or name.
        if (input.Validation.FrameAssignments is not { ReadSucceeded: true, Limitations.Count: 0 } assignments ||
            assignments.Items.Count(a => a.Id == member.FrameId) != 1) return null;
        var frame = input.Model.Frames?.Items.SingleOrDefault(f => f.Id == member.FrameId);
        var section = input.Model.FrameSections?.Items.SingleOrDefault(s => s.Name == frame?.Section);
        var overwrite = input.Validation.FrameAssignments?.Items.SingleOrDefault(a => a.Id == member.FrameId)?.MaterialOverwrite;
        var materialName = string.IsNullOrWhiteSpace(overwrite) ? section?.Material : overwrite;
        var material = input.Model.Materials?.Items.SingleOrDefault(m => m.Name == materialName);
        return material is null ? null : material.Type == "Concrete";
    }

    private static OperationState AssessAnalysis(InspectionInput input)
    {
        if (input.Analysis is null) return OperationState.NotRead;
        var cases = input.Analysis.Status?.Cases;
        if (cases is null || !cases.ReadSucceeded || cases.Limitations.Count > 0) return OperationState.Unknown;
        var states = cases.Items.Select(c => c.State).ToArray();
        if (states.Length == 0) return input.Validation.LoadCases is { ReadSucceeded: true, Items.Count: 0 }
            ? OperationState.NotApplicable : OperationState.Unknown;
        // A partial status list must never claim all model cases are finished or unrun.
        var defined = input.Validation.LoadCases;
        var coverageKnown = defined is { ReadSucceeded: true, Limitations.Count: 0 } &&
            defined.Items.Select(c => c.Name).ToHashSet(StringComparer.Ordinal).SetEquals(cases.Items.Select(c => c.Name));
        if (states.Any(s => s is AnalysisCaseState.CouldNotStart or AnalysisCaseState.NotFinished)) return OperationState.Failed;
        if (states.Any(s => s == AnalysisCaseState.Unknown)) return OperationState.Unknown;
        if (states.All(s => s == AnalysisCaseState.NotRun)) return coverageKnown ? OperationState.NotRun : OperationState.Unknown;
        if (states.All(s => s == AnalysisCaseState.Finished)) return coverageKnown ? OperationState.Complete : OperationState.ResultsAvailable;
        return OperationState.Partial;
    }
}
