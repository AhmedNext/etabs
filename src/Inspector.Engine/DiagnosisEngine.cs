
public static class DiagnosisEngine
{
    public static InspectionReport Enrich(InspectionReport report) => report with
    {
        Findings = report.Findings.Select(f =>
        {
            var actions = f.Kind == FindingKind.OperationNotPerformed
                ? new[] { "Run this operation in ETABS when it is needed, then inspect again." }
                : f.Kind == FindingKind.ApiUnavailable
                ? new[] { "Review the data-read details in ETABS. Unavailable API data does not mean that model inputs are missing." }
                : f.Status switch
            {
                CheckStatus.ERROR or CheckStatus.SKIPPED => new[] { "Review the data-read error and inspect again when the data is available." },
                CheckStatus.NOT_VERIFIED => new[] { "Supply and confirm the stated missing project information or validate the unsupported acquisition/method before claiming compliance." },
                CheckStatus.PASS or CheckStatus.NOT_APPLICABLE => new[] { "Retain the applicability, criterion, and source evidence for engineering review." },
                _ => f.CheckId switch
                {
                    "GEO-001" or "GEO-002" or "GEO-006" or "GEO-007" or "GEO-010" => ["Review the identified joints, members, releases, constraints and intended separation in ETABS before changing connectivity."],
                    "GEO-003" => ["Review the cited frame length against the confirmed project threshold and its intended modeling purpose."],
                    "GEO-004" or "GEO-015" or "MAT-001" => ["Review the cited section/material definition and any object overwrite; verify the intended units and property assignment."],
                    "GEO-009" => ["Review the recorded release degrees of freedom and partial-fixity values against the intended load path."],
                    "GEO-011" => ["Compare base elevation, each story height, and successive story elevations with the project's level schedule."],
                    "GEO-012" => ["Review shell-inherited and explicit diaphragm assignments against the intended floor behavior and confirmed project requirement."],
                    "LOAD-010" => ["Review the cited element-mass flag and each weighted self-weight contribution; check intended mass deductions and added mass separately."],
                    "LOAD-008" => ["Review the cited combination members and dependency graph; confirm intended factors and remove recursive/undefined references through engineer-approved edits."],
                    _ => ["Review the cited load/mass definition against the confirmed project requirement, then rerun this check after approved corrections."]
                }
            };
            string[] causes = (f.Status is CheckStatus.FAIL or CheckStatus.WARNING)
                ? ["Hypothesis: the cited definition differs from the intended model; an intentional modeling condition may also explain a screening warning."] : [];
            return f with { Recommendations = actions, PossibleCauses = causes };
        }).ToArray()
    };
}


