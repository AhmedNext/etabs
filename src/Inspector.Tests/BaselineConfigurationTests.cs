using System.Text.Json;

public sealed class BaselineConfigurationTests
{
    private static InspectionInput Input()
    {
        var c = Context();
        var rows = new[] { "X", "Y" }.Select(axis => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>
        {
            ["Name"] = "S" + axis, [axis + "Dir"] = "Yes", ["SiteClass"] = "C", ["R"] = "5", ["Omega"] = "2.5",
            ["Ss"] = "0.6", ["S1"] = "0.2", ["Cd"] = "4.5", ["I"] = "1", ["SDS"] = "0.4", ["SD1"] = "0.2"
        }).ToArray();
        var table = new AnalysisEvidenceTable("Load Pattern Definitions - Auto Seismic - ASCE 7-16", rows.SelectMany(r => r.Keys).Distinct().ToArray(), rows);
        return c.Input with
        {
            Validation = c.Input.Validation with { LoadCases = Data(new LoadCase("EX", "LinearStatic", 0, [new("Load", "SX", 1)]),
                new LoadCase("EY", "LinearStatic", 0, [new("Load", "SY", 1)])) },
            Analysis = new(null, new AnalysisResultsData(Data<StoryDriftResult>(), Data<ModalParticipationResult>(), Data<BaseReactionResult>(), [], [], [], [])
                { Extended = new([table], [], [], [], "ACI 318-19", true, []) }, null, []),
            ConcreteColumns = Data(new ConcreteColumnDesignResult("F1", "B", "U", null, null, null, 1500) { DesignOption = 2 },
                new ConcreteColumnDesignResult("F1", "B", "U2", null, null, null, 2500) { DesignOption = 2 })
        };
    }
    [Fact]
    public void UsesWorstRequiredAreaAndExtractedParametersWithoutInventingCertification()
    {
        var baseline = BaselineConfigurationGenerator.Generate(Input());
        var row = Assert.Single(baseline.Schedule);
        Assert.True(row.LongitudinalAreaProvided >= 2500 * 1.15);
        Assert.Equal(row.BarCount!.Value * Math.PI * Math.Pow(row.BarDiameterMm!.Value, 2) / 4, row.LongitudinalAreaProvided, 8);
        Assert.True(row.IsGeneratedBaseline);
        Assert.True(baseline.Settings.Confirmed);
        Assert.Equal(.6, baseline.Settings.Seismic!.Value.GetProperty("ss").GetDouble());
        Assert.False(baseline.Settings.Seismic.Value.GetProperty("siteSpecificHazardConfirmed").GetBoolean());
        Assert.Null(baseline.Settings.SeismicVerification);
        Assert.Null(baseline.Settings.Loading);
        var json=JsonSerializer.SerializeToElement(baseline.Settings);
        Assert.False(json.TryGetProperty("project",out _));
        Assert.False(json.TryGetProperty("loading",out _));
        Assert.False(json.TryGetProperty("seismicVerification",out _));
        Assert.Contains(baseline.Notes,n=>n.Contains("set seismic.system",StringComparison.Ordinal));
    }
    [Fact]
    public void ErrorAtOneStationExcludesTheWholeColumnFromStarterBars()
    {
        var input = Input();
        input = input with { ConcreteColumns = Data(input.ConcreteColumns!.Items[0], input.ConcreteColumns.Items[1] with { ErrorSummary = "design failed" }) };
        Assert.Empty(BaselineConfigurationGenerator.Generate(input).Schedule);
    }
    [Fact]
    public void ConvertsModelAreasToSquareMillimeters()
    {
        var input = Input();
        input = input with { Model = input.Model with { Identity = input.Model.Identity with { DisplayUnits = new("N", "m", "C") } },
            ConcreteColumns = Data(input.ConcreteColumns!.Items[0] with { LongitudinalRebarArea = .0025 }) };
        var row = Assert.Single(BaselineConfigurationGenerator.Generate(input).Schedule);
        Assert.True(row.LongitudinalAreaProvided >= 2875);
    }
    [Fact]
    public void GeneratedFilesLoadAndExistingFilesAreBackedUp()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project.asce7-16.json");
            var backup = Path.Combine(directory, "backup");
            File.WriteAllText(path, "original");
            BaselineConfigurationGenerator.Write(BaselineConfigurationGenerator.Generate(Input()), path, backup);
            var loaded = ProjectConfiguration.Load(path, true);
            Assert.True(Assert.Single(loaded.ProvidedRebar!).IsGeneratedBaseline);
            Assert.Equal("original", File.ReadAllText(Path.Combine(backup, Path.GetFileName(path))));
            var context = Context() with { Input = Input() with { ProvidedRebar = loaded.ProvidedRebar }, Configuration = loaded };
            Assert.All(DetailingChecks.Create().Single().Evaluate(context), f => Assert.Equal(CheckStatus.NOT_VERIFIED, f.Status));
        }
        finally
        {
            var root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup path is outside the test output directory.");
            Directory.Delete(target, true);
        }
    }
    [Fact]
    public void CliAcceptsInitializationAndOptionalOutputConfig()
    {
        Assert.Equal("init-config", CliOptions.Parse(["init-config"]).Command);
        Assert.Equal("baseline.json", CliOptions.Parse(["init-config", "--config", "baseline.json"]).ConfigurationPath);
        Assert.Throws<ConfigurationException>(() => CliOptions.Parse(["init-config", "--details"]));
    }
}
