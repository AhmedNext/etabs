
public sealed class InspectionEngine(ILogger<InspectionEngine> logger)
{
    public InspectionReport Run(InspectionInput input, ConfigurationInput configuration, string module,
        IEnumerable<IInspectionCheck> checks, IEnumerable<CheckDefinition> catalog, CancellationToken cancellation = default)
    {
        if (module is not ("all" or "geometry" or "loads" or "analysis" or "seismic" or "design" or "sanity" or "audit"))
            throw new ConfigurationException($"Unsupported module: {module}");
        if (!input.Model.IsConsistent) throw new ModelChangedException("Inconsistent model data cannot be inspected.");
        bool Selected(CheckDefinition d) =>
            d.Module == "CFG" ||
            module == "all" || // Includes every registered module, including Phase 6 OPT checks.
            (module == "design" && d.Module is "COL" or "BEAM" or "WALL" or "DETAIL") ||
            (module == "geometry" && d.Module is "GEO" or "MAT") ||
            (module == "loads" && d.Module == "LOAD") ||
            (module == "analysis" && d.Module == "ANA") ||
            (module == "seismic" && d.Module == "SEIS") ||
            (module == "sanity" && d.Module == "SAN") ||
            (module == "audit" && d.Module == "AUD");
        var context = new InspectionContext(input, configuration, DateTimeOffset.UtcNow);
        var statePolicy = new ModelStatePolicy(context);
        var allChecks = checks.ToDictionary(c => c.Definition.Id, StringComparer.Ordinal);
        List<Finding> findings = []; List<CheckCoverage> coverage = [];
        var selectedDefinitions = catalog.Where(Selected).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        foreach (var definition in selectedDefinitions)
        {
            cancellation.ThrowIfCancellationRequested();
            if (definition.State is VerificationState.NOT_IMPLEMENTED or VerificationState.DISABLED)
            { coverage.Add(new(definition.Id, definition.State, 0)); continue; }
            if (statePolicy.Defer(definition) is { } reason)
            {
                coverage.Add(new(definition.Id, definition.State, 0) { DeferredReason = reason });
                continue;
            }
            var missing = definition.RequiredData.Where(key => !context.HasData(key)).ToArray();
            IReadOnlyList<Finding> results;
            if (missing.Length != 0)
                results = [Technical(definition, CheckStatus.SKIPPED, $"This check could not read the data it needs: {string.Join(", ", missing)}. This does not show that model inputs are missing.",
                    [..input.Model.Issues.Concat(input.Validation.Issues).Select(i => new Evidence(i.Component, i.Operation ?? "Extraction", i.Message))])];
            else
            {
                try
                {
                    if (!allChecks.TryGetValue(definition.Id, out var check)) throw new InvalidOperationException("Registered check implementation is missing.");
                    var checkContext = statePolicy.ForCheck(definition);
                    if ((definition.Module == "COL" || definition.Id is "OPT-001" or "DETAIL-008") &&
                        input.ConcreteColumns?.Items.Count > 0 && checkContext.Input.ConcreteColumns?.Items.Count == 0 ||
                        definition.Module == "BEAM" && input.ConcreteBeams?.Items.Count > 0 && checkContext.Input.ConcreteBeams?.Items.Count == 0)
                    {
                        coverage.Add(new(definition.Id, definition.State, 0) { DeferredReason = "ETABS design errors are reported in the grouped design finding." });
                        continue;
                    }
                    results = check.Evaluate(checkContext);
                    if (results.Count == 0) throw new InvalidOperationException("Check returned no explicit outcome.");
                    results = results.Select(f => (f.Actual is { } actual && !double.IsFinite(actual)) || (f.Limit is { } limit && !double.IsFinite(limit))
                        ? f with { Status = CheckStatus.ERROR, Kind = FindingKind.ApiUnavailable, Severity = Severity.HIGH,
                            Title = "This result contains an invalid number and could not be checked.", Actual = null, Limit = null } : f).ToArray();
                    results = results.Select(f => definition.State != VerificationState.VERIFIED && f.Status is CheckStatus.PASS or CheckStatus.FAIL
                        ? f with { Status = CheckStatus.NOT_VERIFIED, Title = "Unverified rule outcome: " + f.Title } : f).ToArray();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Inspection check {CheckId} failed", definition.Id);
                    results = [Technical(definition, CheckStatus.ERROR, "Technical check execution failure; no engineering conclusion is available.",
                        [new("Exception", "Inspection engine", ex.Message)])];
                }
            }
            findings.AddRange(results); coverage.Add(new(definition.Id, definition.State, results.Count));
        }
        findings.AddRange(statePolicy.Findings(selectedDefinitions.Any(d => d.Module is "COL" or "BEAM" or "DETAIL" || d.Id is "OPT-001" or "ANA-015" or "SAN-005")));
        if (findings.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != findings.Count)
            throw new InvalidOperationException("Duplicate finding identifiers were generated.");
        return new("phase2-inspection-1", module, input, configuration, findings.OrderBy(f => f.CheckId, StringComparer.Ordinal)
            .ThenBy(f => f.ElementId, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal).ToArray(), coverage, context.Timestamp);

        Finding Technical(CheckDefinition d, CheckStatus status, string title, IReadOnlyList<Evidence> evidence) =>
            new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.Id + ":" + status)))[..20], d.Id, d.Module,
                title, status, status == CheckStatus.ERROR ? Severity.HIGH : Severity.INFORMATIONAL, null, null, null, null, null,
                null, null, null, null, null, d.RuleVersion, d.Method, evidence, [], [],
                "Review the stated read or execution error and rerun this check.", context.Timestamp) { Kind = FindingKind.ApiUnavailable };
    }
}


