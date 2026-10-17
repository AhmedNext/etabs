
public sealed class Phase2LiveTests
{
    private static Task Verify(Action<EtabsSession, InspectionInput> verify) => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        var extractor = new InspectionExtractor(new(NullLogger<ModelExtractor>.Instance), NullLogger<InspectionExtractor>.Instance);
        var input = extractor.Extract(session);
        Assert.Empty(input.Model.Issues); Assert.Empty(input.Validation.Issues);
        verify(session, input);
        return true;
    });

    [LiveEtabsFact]
    public Task LoadPatternsMatchDefinitionTable() => Verify((session, input) =>
    {
        var table = session.ReadTable("Load Pattern Definitions");
        Assert.Equal(table.Rows.Count, input.Validation.LoadPatterns!.Items.Count);
        foreach (var row in table.Rows)
        {
            var pattern = Assert.Single(input.Validation.LoadPatterns.Items, p => p.Name == row["Name"]);
            Assert.Equal(double.Parse(row["SelfWtMult"], CultureInfo.InvariantCulture), pattern.SelfWeightMultiplier);
            var tableType = row["Type"] switch { "Seismic" => "Quake", "Super Dead" => "SuperDead", _ => row["Type"] };
            Assert.Equal(tableType, pattern.Type);
        }
    });

    [LiveEtabsFact]
    public Task CombinationsAndLinearCasesMatchTables() => Verify((session, input) =>
    {
        var table = session.ReadTable("Load Combination Definitions");
        Assert.Equal(table.Rows.Select(r => r["Name"]).Distinct().Count(), input.Validation.Combinations!.Items.Count);
        foreach (var combination in input.Validation.Combinations.Items)
        {
            var rows = table.Rows.Where(r => r["Name"] == combination.Name).ToArray();
            Assert.Equal(rows.Length, combination.Terms.Count);
            foreach (var row in rows)
                Assert.Contains(combination.Terms, t => t.Name == row["LoadName"] && t.Factor == double.Parse(row["SF"], CultureInfo.InvariantCulture));
        }
        foreach (var row in session.ReadTable("Load Case Definitions - Linear Static").Rows)
        {
            var loadCase = Assert.Single(input.Validation.LoadCases!.Items, c => c.Name == row["Name"]);
            Assert.Equal("LinearStatic", loadCase.Type);
            Assert.Contains(loadCase.Loads!, l => l.Type == row["LoadType"] && l.Name == row["LoadName"] &&
                l.Factor == double.Parse(row["LoadSF"], CultureInfo.InvariantCulture));
        }
    });

    [LiveEtabsFact]
    public Task MassSourceFlagsAndContributorsMatchLegacyDefaultApi() => Verify((session, input) =>
    {
        bool self = false, added = false, loads = false; int n = 0; string[] patterns = []; double[] factors = [];
        Assert.Equal(0, session.ModelForVerification.PropMaterial.GetMassSource_1(ref self, ref added, ref loads, ref n, ref patterns, ref factors));
        var source = Assert.Single(input.Validation.MassSources!.Items, s => s.IsDefault);
        Assert.Equal(self, source.IncludeElementMass); Assert.Equal(added, source.IncludeAddedMass); Assert.Equal(loads, source.IncludeLoads);
        Assert.Equal(n, source.Loads.Count);
        for (int i = 0; i < n; i++) Assert.Contains(source.Loads, l => l.Pattern == patterns[i] && l.Factor == factors[i]);
    });

    [LiveEtabsFact]
    public Task DiaphragmsAndRestraintsMatchAssignmentTables() => Verify((session, input) =>
    {
        foreach (var row in session.ReadTable("Diaphragm Definitions").Rows)
        {
            var diaphragm = Assert.Single(input.Validation.Diaphragms!.Items, d => d.Name == row["Name"]);
            Assert.Equal(row["Rigidity"] == "Semi Rigid", diaphragm.SemiRigid);
        }
        foreach (var row in session.ReadTable("Joint Assignments - Restraints").Rows)
        {
            var joint = Assert.Single(input.Validation.JointAssignments!.Items, a => a.Id == row["UniqueName"]);
            Assert.Equal(new[] { "UX", "UY", "UZ", "RX", "RY", "RZ" }.Select(k => row[k] == "Yes"), joint.Restraints);
        }
        Assert.Equal(input.Model.Joints!.Items.Count, input.Validation.JointAssignments!.Items.Count);
        Assert.Equal(input.Model.Frames!.Items.Count, input.Validation.FrameAssignments!.Items.Count);
        Assert.Equal(input.Model.Areas!.Items.Count, input.Validation.AreaAssignments!.Items.Count);
        foreach (var row in session.ReadTable("Area Assignments - Diaphragms").Rows)
        {
            var assignment = Assert.Single(input.Validation.AreaAssignments.Items, a => a.Id == row["UniqueName"]);
            Assert.Equal(row["Diaphragm"] == "None" ? null : row["Diaphragm"], assignment.Diaphragm);
        }
        foreach (var row in session.ReadTable("Joint Assignments - Diaphragms").Rows)
        {
            var assignment = Assert.Single(input.Validation.JointAssignments.Items, a => a.Id == row["UniqueName"]);
            if (assignment.DiaphragmOption == "DefinedDiaphragm") Assert.Equal(row["Diaphragm"], assignment.Diaphragm);
        }
    });

    [LiveEtabsFact]
    public Task EndToEndInspectionIsDeterministicAndWarningsRetainLiveEvidence() => Verify((session, input) =>
    {
        var configuration = new ConfigurationInput(new(), null, null, false);
        var engine = new InspectionEngine(NullLogger<InspectionEngine>.Instance);
        var first = DiagnosisEngine.Enrich(engine.Run(input, configuration, "all", CheckCatalog.Implemented, CheckCatalog.All));
        var second = DiagnosisEngine.Enrich(engine.Run(input, configuration, "all", CheckCatalog.Implemented, CheckCatalog.All));
        Assert.DoesNotContain(first.Findings, f => f.Status is CheckStatus.ERROR or CheckStatus.SKIPPED);
        Assert.Equal(JsonSerializer.Serialize(first.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })),
            JsonSerializer.Serialize(second.Findings.Select(f => f with { Timestamp = DateTimeOffset.UnixEpoch })));
        foreach (var f in first.Findings)
        {
            Assert.NotEmpty(f.Evidence); Assert.NotEmpty(f.Recommendations); Assert.NotEmpty(f.RuleVersion);
            Assert.Contains(f.Evidence, e => e.Description == "Validation content SHA-256");
        }
        foreach (var pair in first.Findings.Where(f => f.CheckId == "GEO-001" && f.Status == CheckStatus.WARNING))
        {
            var ids = pair.ElementId!.Split('|');
            double[] Read(string id)
            {
                double x = 0, y = 0, z = 0;
                Assert.Equal(0, session.ModelForVerification.PointObj.GetCoordCartesian(id, ref x, ref y, ref z, "Global"));
                return [x, y, z];
            }
            var a = Read(ids[0]); var b = Read(ids[1]);
            var distance = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));
            Assert.Equal(distance, pair.Actual!.Value, 8); Assert.True(distance <= pair.Limit);
        }
        foreach (var id in new[] { "LOAD-001", "LOAD-002", "LOAD-006", "LOAD-007", "GEO-003", "GEO-012" })
            Assert.Equal(CheckStatus.NOT_VERIFIED, Assert.Single(first.Findings, f => f.CheckId == id).Status);
        Assert.False(first.IsComplete);
    });
}


