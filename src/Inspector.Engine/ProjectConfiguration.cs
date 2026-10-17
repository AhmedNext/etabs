
public static partial class ProjectConfiguration
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static ConfigurationInput Load(string path, bool explicitlyRequested = false)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            if (explicitlyRequested) throw new ConfigurationException($"Project configuration not found: {fullPath}");
            return new(new(), fullPath, null, false);
        }
        if (new FileInfo(fullPath).Length > 1_048_576) throw new ConfigurationException("Project configuration exceeds 1 MiB.");
        var bytes = File.ReadAllBytes(fullPath);
        try
        {
            using var json = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(json.RootElement);
            var settings = JsonSerializer.Deserialize<InspectionSettings>(bytes, Options) ?? throw new ConfigurationException("Project configuration is null.");
            Validate(settings);
            var configuration = new ConfigurationInput(settings, fullPath, Convert.ToHexString(SHA256.HashData(bytes)), true);
            return LoadReinforcement(configuration);
        }
        catch (JsonException ex) { throw new ConfigurationException($"Invalid project configuration: {ex.Message}"); }
    }
    private static ConfigurationInput LoadReinforcement(ConfigurationInput configuration)
    {
        if (configuration.Settings.ReinforcementFile is not { } file) return configuration;
        var path = Path.GetFullPath(file, Path.GetDirectoryName(configuration.Path!)!);
        if (!File.Exists(path)) return configuration with
        {
            ReinforcementSourcePath = path,
            ReinforcementLimitations = ["Optional reinforcement schedule was not found: " + path]
        };
        try
        {
            if (new FileInfo(path).Length > 16_777_216) throw new ConfigurationException("Reinforcement schedule exceeds 16 MiB.");
            var bytes = File.ReadAllBytes(path);
            using var json = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(json.RootElement);
            var rows = JsonSerializer.Deserialize<List<ProvidedRebar>>(bytes, Options)
                ?? throw new ConfigurationException("Reinforcement schedule must be a JSON array, not null.");
            if (rows.Any(row => row is null || string.IsNullOrWhiteSpace(row.ElementId) ||
                !double.IsFinite(row.LongitudinalAreaProvided) || row.LongitudinalAreaProvided < 0))
                throw new ConfigurationException("Reinforcement rows require an elementId and a finite nonnegative longitudinalAreaProvided.");
            if (rows.Select(row => row.ElementId).Distinct(StringComparer.Ordinal).Count() != rows.Count)
                throw new ConfigurationException("Reinforcement schedule contains duplicate element IDs; one total longitudinal area per column is required.");
            return configuration with { ProvidedRebar = rows.ToArray(), ReinforcementSourcePath = path,
                ReinforcementSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new ConfigurationException("Cannot load reinforcement schedule: " + ex.Message); }
    }
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ConfigurationException($"Duplicate configuration field '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }
    public static void Validate(InspectionSettings settings)
    {
        if (settings.RebarEstimation is { } estimation)
        {
            if (estimation.DefaultRates is null || estimation.ByObjectType is null || estimation.ByObjectType.Keys.Any(string.IsNullOrWhiteSpace))
                throw new ConfigurationException("Rebar estimation requires defaultRates and valid object-type names.");
            foreach (var rates in estimation.ByObjectType.Values.Prepend(estimation.DefaultRates))
                if (rates is null || new[] { rates.LowKgPerM3, rates.BaseKgPerM3, rates.HighKgPerM3 }.Any(v => !double.IsFinite(v) || v <= 0) ||
                    rates.LowKgPerM3 > rates.BaseKgPerM3 || rates.BaseKgPerM3 > rates.HighKgPerM3)
                    throw new ConfigurationException("Rebar estimation rates must be finite positive kg/m³ with low <= base <= high.");
        }
        if (settings.ReinforcementFile is { } reinforcementFile &&
            (string.IsNullOrWhiteSpace(reinforcementFile) || !System.IO.Path.GetExtension(reinforcementFile).Equals(".json", StringComparison.OrdinalIgnoreCase)))
            throw new ConfigurationException("reinforcementFile must identify a .json schedule.");
        if (settings.ReinforcementAreaUnit is { } areaUnit && areaUnit is not ("mm2" or "cm2" or "m2" or "in2" or "ft2" or "mm²" or "cm²" or "m²" or "in²" or "ft²"))
            throw new ConfigurationException("reinforcementAreaUnit supports mm2, cm2, m2, in2 or ft2 (also using ²).");
        ValidatePhase3(settings.Phase3Review);
        if (settings.SeismicVerification is { } seismic)
        {
            if (seismic.Standard != "ASCE 7-16" ||
                new[] { seismic.Cd, seismic.Ie, seismic.DriftLimit, seismic.SpectrumMinimumRatio, seismic.Beta }
                    .Any(v => !double.IsFinite(v) || v <= 0) || seismic.Beta > 1 || seismic.DriftLimit >= 1)
                throw new ConfigurationException("Seismic verification requires ASCE 7-16 and finite positive criteria; beta <= 1 and driftLimit < 1.");
            if (seismic.Axes is null || seismic.GravityLoads is null || seismic.Axes.Count != 2 ||
                seismic.Axes.Any(a => a is null) || seismic.GravityLoads.Any(g => g is null) ||
                !seismic.Axes.Select(a => a.Direction).Order().SequenceEqual(new[] { "X", "Y" }) ||
                seismic.Axes.Any(a => string.IsNullOrWhiteSpace(a.ElasticCase) || a.SpectrumCase is not null && string.IsNullOrWhiteSpace(a.SpectrumCase)) ||
                seismic.GravityLoads.Any(g => string.IsNullOrWhiteSpace(g.Case) || !double.IsFinite(g.Factor) || g.Factor <= 0) ||
                seismic.GravityLoads.Select(g => g.Case).Distinct().Count() != seismic.GravityLoads.Count ||
                seismic.GravityBasisConfirmed && seismic.GravityLoads.Count == 0)
                throw new ConfigurationException("Seismic verification requires distinct X/Y case mappings and valid unique gravity loads.");
        }
        if (settings.SchemaVersion != "1.0") throw new ConfigurationException("Unsupported project schemaVersion; expected 1.0.");
        if (settings.Etabs is { } etabs && (etabs.RequiredVersionPrefix is not ("22.7" or "22.7.0") || etabs.ConnectionMode != "AttachOnly"))
            throw new ConfigurationException("Phase 2 requires ETABS 22.7.0 and AttachOnly connection mode.");
        foreach (var length in new[] { settings.Geometry?.CoincidentTolerance, settings.Geometry?.ShortFrameThreshold })
        {
            if (length is null) continue;
            if (!double.IsFinite(length.Value) || length.Value <= 0) throw new ConfigurationException("Length criteria must be finite and positive.");
            GeometryMath.MetersPerUnit(length.Unit);
        }
        static void Names(IEnumerable<string?>? names, string field)
        {
            if (names is null) return;
            var all = names.ToArray();
            if (all.Any(string.IsNullOrWhiteSpace) || all.Distinct(StringComparer.Ordinal).Count() != all.Length)
                throw new ConfigurationException($"{field} contains empty or duplicate identifiers.");
        }
        Names(settings.Geometry?.IntentionalCoincidentJoints, "intentionalCoincidentJoints");
        Names(settings.Geometry?.IntentionalUnreferencedJoints, "intentionalUnreferencedJoints");
        Names(settings.Geometry?.IntentionalDisconnectedElements, "intentionalDisconnectedElements");
        Names(settings.Loading?.RequiredCombinations, "requiredCombinations");
        Names(settings.Loading?.ExpectedPatterns?.Select(p => p?.Name), "expectedPatterns");
        Names(settings.Analysis?.DriftCases, "analysis.driftCases");
        Names(settings.Analysis?.DriftDirections, "analysis.driftDirections");
        Names(settings.Analysis?.DisplacementCases, "analysis.displacementCases");
        Names(settings.Analysis?.DisplacementDirections, "analysis.displacementDirections");
        if (settings.Analysis?.DriftRatioLimit is { } drift && (!double.IsFinite(drift) || drift <= 0))
            throw new ConfigurationException("analysis.driftRatioLimit must be finite and positive.");
        if (settings.Analysis?.DriftDirections?.Any(d => d is not ("X" or "Y")) == true)
            throw new ConfigurationException("analysis.driftDirections supports only exact X and Y directions.");
        if (settings.Analysis?.MaxStoryPointDisplacement is { } displacement && (!double.IsFinite(displacement) || displacement <= 0))
            throw new ConfigurationException("analysis.maxStoryPointDisplacement must be finite and positive in current ETABS display length units.");
        if (settings.Analysis?.DisplacementDirections?.Any(d => d is not ("X" or "Y")) == true)
            throw new ConfigurationException("analysis.displacementDirections supports only exact X and Y directions.");
        if (settings.Analysis?.ModalCase is { } modalCase && string.IsNullOrWhiteSpace(modalCase))
            throw new ConfigurationException("analysis.modalCase cannot be empty.");
        foreach (var ratio in new[] { settings.Analysis?.MinimumCumulativeUx, settings.Analysis?.MinimumCumulativeUy })
            if (ratio is { } value && (!double.IsFinite(value) || value <= 0 || value > 1))
                throw new ConfigurationException("Modal cumulative mass ratio criteria must be greater than 0 and at most 1.");
        foreach (var pattern in settings.Loading?.ExpectedPatterns ?? [])
        {
            if (pattern.SelfWeightMultiplier is { } factor && !double.IsFinite(factor))
                throw new ConfigurationException("Expected self-weight multipliers must be finite.");
            if (pattern.Type is { } type && !PatternTypes.Contains(type)) throw new ConfigurationException($"Unknown load pattern type '{type}'.");
        }
    }
    private static readonly HashSet<string> PatternTypes = new(StringComparer.Ordinal)
    {
        "Dead", "SuperDead", "Live", "ReduceLive", "Quake", "Wind", "Snow", "Other", "Temperature", "Rooflive",
        "Notional", "PatternLive", "QuakeDrift", "QuakeVerticalOnly", "Prestress", "Shrinkage", "Creep", "Settlement"
    };
}




