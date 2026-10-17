public static class DetailingChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("DETAIL-008", "Required vs provided column reinforcement", "Join exact ETABS FrameId to schedule ElementId. Convert provided area to ETABS display-area units when configured; As_provided>=As_required PASS, otherwise FAIL. Missing/invalid inputs remain NOT_VERIFIED. Area comparison only; bar arrangement, development, laps and detailing remain separate.",
            [], Reconcile)
    ];

    private static IReadOnlyList<Observation> Reconcile(InspectionContext c)
    {
        if (c.Input.ProvidedRebar is not { Count: > 0 } schedule)
            return [new(CheckStatus.NOT_APPLICABLE,
                "External provided reinforcement schedule was not supplied; rebar detailing comparison is skipped.",
                "model", "Model", Evidence: [new("External reinforcement schedule", c.Configuration.ReinforcementSourcePath ?? "InspectionInput.ProvidedRebar",
                    string.Join("; ", c.Configuration.ReinforcementLimitations))])];
        var columns = c.Input.ConcreteColumns;
        if (columns is null || columns.Items.Count == 0)
            return [Rule.NotVerified("Column design requirements are unavailable. " + string.Join("; ", columns?.Limitations ?? []))];
        var provided = schedule.GroupBy(x => x.ElementId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var unit = c.Input.Model.Identity.DisplayUnits.Length;
        var configuredUnit = c.Configuration.Settings.ReinforcementAreaUnit;
        double factor = 1;
        if (configuredUnit is not null)
        {
            try
            {
                var sourceLengthUnit = configuredUnit.EndsWith('²') ? configuredUnit[..^1] : configuredUnit.EndsWith('2') ? configuredUnit[..^1] : "";
                factor = Math.Pow(GeometryMath.MetersPerUnit(sourceLengthUnit) / GeometryMath.MetersPerUnit(unit), 2);
            }
            catch (ConfigurationException ex) { return [Rule.NotVerified("Cannot reconcile reinforcement area units: " + ex.Message)]; }
        }
        List<Observation> output = [];
        foreach (var row in columns.Items)
        {
            List<Evidence> evidence = [
                new("Required longitudinal reinforcement", "DesignConcrete.GetSummaryResultsColumn",
                    $"frame={row.FrameId}; section={row.DesignSection}; location={row.Location:G17}; combination={row.DesignCombination}; required As={row.LongitudinalRebarArea:G17}; error={row.ErrorSummary}; warning={row.WarningSummary}"),
                new("Provided reinforcement schedule", c.Configuration.ReinforcementSourcePath ?? "InspectionInput.ProvidedRebar",
                    $"SHA256={c.Configuration.ReinforcementSha256 ?? "unavailable"}; source area unit={configuredUnit ?? unit + "² (assumed ETABS display units)"}; conversion factor={factor:G17}"),
                new("Schedule limitations", "ConfigurationInput.ReinforcementLimitations", string.Join("; ", c.Configuration.ReinforcementLimitations))
            ];
            void Add(CheckStatus status, string message, double? actual = null, double? limit = null) =>
                output.Add(new(status, $"{message} [location={row.Location:G17}]", row.FrameId, "Frame",
                    Actual: actual, Limit: limit, Unit: unit + "²", Combination: row.DesignCombination, Evidence: evidence.ToArray()));
            if (!string.IsNullOrWhiteSpace(row.ErrorSummary) || row.LongitudinalRebarArea is not { } required || !double.IsFinite(required) || required < 0)
            { Add(CheckStatus.NOT_VERIFIED, "Valid ETABS required longitudinal area is unavailable; check-mode PMM ratio cannot substitute for required steel area."); continue; }
            if (!provided.TryGetValue(row.FrameId, out var matches) || matches.Length != 1)
            { Add(CheckStatus.NOT_VERIFIED, "No unique provided reinforcement schedule entry matches the column FrameId."); continue; }
            var supplied = matches[0].LongitudinalAreaProvided * factor;
            if (matches[0].IsGeneratedBaseline)
            {
                evidence.Add(new("Generated starter schedule", "init-config", "Required ETABS area plus 15%, rounded to standard bar areas; engineer approval of the proposed bars is pending."));
                Add(CheckStatus.NOT_VERIFIED, "Generated starter reinforcement schedule requires engineering approval before provided-bar reconciliation.", supplied, required);
                continue;
            }
            if (!double.IsFinite(supplied) || supplied < 0)
            { Add(CheckStatus.NOT_VERIFIED, "Provided reinforcement area is nonfinite or negative after conversion."); continue; }
            evidence.Add(new("Required/provided comparison", "External reinforcement schedule + ETABS",
                $"ElementId={matches[0].ElementId}; As_provided={supplied:G17} {unit}²; As_required={required:G17} {unit}²; provided>=required={supplied >= required}. Schedule is an external assertion, not proof of installed bars."));
            Add(supplied >= required ? CheckStatus.PASS : CheckStatus.FAIL,
                supplied >= required ? "Scheduled provided longitudinal area meets or exceeds ETABS required area; area reconciliation only."
                    : "Scheduled provided longitudinal area is below ETABS required area.", supplied, required);
        }
        return output;
    }
}
