using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record BaselineConfiguration(InspectionSettings Settings, IReadOnlyList<ProvidedRebar> Schedule,
    IReadOnlyList<string> Notes);

public static class BaselineConfigurationGenerator
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static BaselineConfiguration Generate(InspectionInput input)
    {
        if (!input.Model.IsComplete || input.Validation.Issues.Count > 0)
            throw new ConfigurationException("Complete, consistent model and load definitions are required to initialize configuration.");
        var extended = input.Analysis?.Results?.Extended;
        var rows = extended?.Tables.SingleOrDefault(t => t.Key == "Load Pattern Definitions - Auto Seismic - ASCE 7-16")?.Rows ?? [];
        List<string> notes = ["Model-derived values are confirmed as a snapshot of ETABS settings; independent engineering, site hazard and gravity-basis approval are pending.",
            "Starter bars use the envelope of required longitudinal areas plus 15%; bar layout, spacing, maximum reinforcement and constructability require engineering review."];
        var seismic = new Dictionary<string, object?> { ["siteSpecificHazardConfirmed"] = false };
        double? CommonNumber(string field)
        {
            var values = rows.Select(r => r.GetValueOrDefault(field)).Select(s =>
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? (double?)v : null).ToArray();
            if (values.Length == 0 || values.Any(v => v is null) || values.Any(v => Math.Abs(v!.Value - values[0]!.Value) > 1e-6 * Math.Max(1, Math.Abs(values[0]!.Value))))
            { notes.Add($"No unique valid auto-seismic {field} value; review the extracted pattern inventory."); return null; }
            return values[0];
        }
        foreach (var (field, key) in new[] { ("R", "r"), ("Ss", "ss"), ("S1", "s1"), ("Cd", "cd"), ("I", "ie") })
        {
            var number = CommonNumber(field);
            if (number is not null) seismic[key] = number.Value;
        }
        var sites = rows.Select(r => r.GetValueOrDefault("SiteClass")).Distinct(StringComparer.Ordinal).ToArray();
        if (sites.Length == 1 && !string.IsNullOrWhiteSpace(sites[0])) seismic["siteClass"] = sites[0];
        notes.Add("The auto-seismic table does not identify the governing seismic force-resisting-system category; set seismic.system from the approved design basis before using empirical-period or dynamic base-shear checks.");
        notes.Add("Load-case names, load patterns, combinations and direction mappings are intentionally not copied into project configuration; checks derive those from the live ETABS model.");
        var schedule = new List<ProvidedRebar>();
        var areaFactor = Math.Pow(GeometryMath.MetersPerUnit(input.Model.Identity.DisplayUnits.Length) / .001, 2);
        foreach (var group in (input.ConcreteColumns?.Items ?? []).GroupBy(r => r.FrameId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (group.Any(r => !string.IsNullOrWhiteSpace(r.ErrorSummary) || r.LongitudinalRebarArea is not > 0 || !double.IsFinite(r.LongitudinalRebarArea.Value)))
            { notes.Add($"Column {group.Key}: no complete valid required-area envelope; no starter bars generated."); continue; }
            var required = group.Max(r => r.LongitudinalRebarArea!.Value) * areaFactor;
            var circular = group.All(r => input.Model.FrameSections!.Items.Any(s => s.Name == r.DesignSection && s.Shape == "Circle"));
            schedule.Add(RoundStarter(group.Key, required, circular));
        }
        if (schedule.Count == 0) notes.Add("No usable column required areas were extracted; reinforcement.json is an empty starter schedule. Run concrete design in ETABS first if needed.");
        var code = extended?.ConcreteCode;
        var settings = new InspectionSettings(Confirmed: true, Geometry: new())
        {
            Codes = JsonSerializer.SerializeToElement(new { seismic = rows.Count > 0 ? "ASCE 7-16" : null, concrete = code }, Json),
            Seismic = JsonSerializer.SerializeToElement(seismic, Json),
            Phase3Review = new() { ReferencesConfirmed = true, ReferenceSource = "Active ETABS model snapshot; independent reference calculations not supplied", ExpectedConcreteCode = code },
            ReinforcementFile = "reinforcement.json", ReinforcementAreaUnit = "mm2",
            Reinforcement = JsonSerializer.SerializeToElement(new { source = "ETABS required-area envelope + 15%, rounded to standard metric bars", generatedBaseline = true, approvalRequired = true }, Json)
        };
        ProjectConfiguration.Validate(settings);
        return new(settings, schedule, notes);
    }

    public static ProvidedRebar RoundStarter(string id, double requiredMm2, bool circular = false)
    {
        var target = requiredMm2 * 1.15;
        if (!double.IsFinite(target) || target <= 0) throw new ConfigurationException("Starter reinforcement requires a finite positive required area.");
        var candidates = new List<ProvidedRebar>();
        foreach (var diameter in new[] { 16d, 20d, 25d, 32d, 40d })
        {
            var barArea = Math.PI * diameter * diameter / 4;
            var count = Math.Max(circular ? 6 : 4, Math.Ceiling(target / barArea / 2) * 2);
            if (!double.IsFinite(count) || count > int.MaxValue) continue;
            var total = count * barArea;
            if (total < target) { count += 2; total = count * barArea; }
            if (count > int.MaxValue) continue;
            candidates.Add(new(id, total) { IsGeneratedBaseline = true, BarCount = (int)count, BarDiameterMm = diameter });
        }
        return candidates.OrderBy(c => c.LongitudinalAreaProvided).ThenBy(c => c.BarCount).FirstOrDefault()
            ?? throw new ConfigurationException("Required area exceeds the supported starter schedule range.");
    }

    public static IReadOnlyList<string> Write(BaselineConfiguration baseline, string configurationPath, string backupDirectory)
    {
        var config = Path.GetFullPath(configurationPath);
        var schedule = Path.Combine(Path.GetDirectoryName(config)!, "reinforcement.json");
        if (config.Equals(schedule, StringComparison.OrdinalIgnoreCase)) throw new ConfigurationException("The project configuration and reinforcement schedule must have different filenames.");
        var files = new Dictionary<string, byte[]> { [config] = JsonSerializer.SerializeToUtf8Bytes(baseline.Settings, Json),
            [schedule] = JsonSerializer.SerializeToUtf8Bytes(baseline.Schedule, Json) };
        var previous = files.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        if (previous.Values.Any(b => b is not null))
        {
            Directory.CreateDirectory(backupDirectory);
            foreach (var (path, bytes) in previous.Where(p => p.Value is not null))
                File.WriteAllBytes(Path.Combine(backupDirectory, Path.GetFileName(path)), bytes!);
        }
        try { foreach (var (path, bytes) in files) File.WriteAllBytes(path, bytes); }
        catch
        {
            foreach (var (path, bytes) in previous)
                if (bytes is not null) File.WriteAllBytes(path, bytes); else if (File.Exists(path)) File.Delete(path);
            throw;
        }
        return files.Keys.ToArray();
    }
}
