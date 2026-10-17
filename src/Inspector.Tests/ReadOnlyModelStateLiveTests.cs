public sealed class ReadOnlyModelStateLiveTests
{
    [LiveEtabsFact]
    public Task InspectionLeavesModelDefinitionsResultsAndOutputSelectionUnchanged() => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        var identity = session.ReadIdentity();
        var beforeSelection = Selection();
        var beforeStatuses = JsonSerializer.Serialize(session.ReadAnalysisStatus().Cases.Items);
        var beforeDesign = session.ReadConcreteDesignStatus();
        var beforeFileHash = FileHash(identity.FilePath);
        var beforeGeometry = ModelConsistency.ContentFingerprint(new ModelExtractor(NullLogger<ModelExtractor>.Instance).Extract(session));
        var input = new InspectionExtractor(new(NullLogger<ModelExtractor>.Instance), NullLogger<InspectionExtractor>.Instance)
            .Extract(session, readAnalysis: true, readDesign: true);
        var report = new InspectionEngine(NullLogger<InspectionEngine>.Instance)
            .Run(input, new(new(), null, null, false), "all", CheckCatalog.Implemented, CheckCatalog.All);
        Assert.True(ModelConsistency.SameContext(identity, session.ReadIdentity()));
        Assert.Equal(beforeSelection, Selection());
        Assert.Equal(beforeStatuses, JsonSerializer.Serialize(session.ReadAnalysisStatus().Cases.Items));
        Assert.Equal(beforeDesign.ResultsAvailable, session.ReadConcreteDesignStatus().ResultsAvailable);
        Assert.Equal(beforeFileHash, FileHash(identity.FilePath));
        Assert.Equal(beforeGeometry, ModelConsistency.ContentFingerprint(input.Model));
        Assert.Equal(beforeGeometry, ModelConsistency.ContentFingerprint(new ModelExtractor(NullLogger<ModelExtractor>.Instance).Extract(session)));
        Assert.NotNull(input.ConcreteDesignStatus);
        foreach (var pattern in input.Validation.LoadPatterns?.Items.Where(p => p.Type == "Quake") ?? [])
        {
            string code = "";
            Assert.Equal(0, session.ModelForVerification.LoadPatterns.GetAutoSeismicCode(pattern.Name, ref code));
            Assert.Equal(code, pattern.AutoSeismicCode);
        }
        Assert.All(report.Findings.Where(f => f.CheckId == "STATE-DESIGN-COVERAGE"), f => Assert.NotEmpty(f.AffectedElements));
        if (beforeDesign.ResultsAvailable == false)
        {
            Assert.Equal(OperationState.NotRun, report.ModelState.ConcreteDesign);
            Assert.Single(report.Findings, f => f.Title == "Concrete design has not been run.");
            Assert.DoesNotContain(report.Findings, f => f.Module is "COL" or "BEAM");
        }
        return true;

        string Selection()
        {
            var model = session.ModelForVerification;
            var selected = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (var item in session.ReadAnalysisStatus().Cases.Items)
            {
                bool value = false;
                Assert.Equal(0, model.Results.Setup.GetCaseSelectedForOutput(item.Name, ref value));
                selected.Add("case:" + item.Name, value);
            }
            int count = 0; string[] names = [];
            Assert.Equal(0, model.RespCombo.GetNameList(ref count, ref names));
            foreach (var name in names)
            {
                bool value = false;
                Assert.Equal(0, model.Results.Setup.GetComboSelectedForOutput(name, ref value));
                selected.Add("combination:" + name, value);
            }
            return JsonSerializer.Serialize(selected);
        }
        static string FileHash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
    });
}
