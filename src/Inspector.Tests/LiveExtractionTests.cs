
public sealed class LiveEtabsFactAttribute : FactAttribute
{
    public LiveEtabsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ETABS_LIVE_TESTS") != "1")
            Skip = "Set ETABS_LIVE_TESTS=1 with ETABS 22.7.0 and a representative RC model open. Read-only test.";
    }
}

public sealed class LiveExtractionTests
{
    private static Task Verify(Action<EtabsSession, ModelData> verify) => StaRunner.Run(() =>
    {
        using var session = EtabsSession.Attach(NullLogger.Instance);
        var data = new ModelExtractor(NullLogger<ModelExtractor>.Instance).Extract(session);
        Assert.Empty(data.Issues);
        Assert.True(data.IsConsistent);
        verify(session, data);
        Assert.True(ModelConsistency.SameContext(data.Identity, session.ReadIdentity()));
        return true;
    });

    [LiveEtabsFact]
    public Task BulkCountsAndCoordinatesMatchIndividualObjectReads() => Verify((session, data) =>
    {
        var model = session.ModelForVerification;
        int count = 0; string[] names = [];
        Assert.Equal(0, model.PointObj.GetNameList(ref count, ref names));
        Assert.Equal(names.Order(StringComparer.Ordinal), data.Joints!.Items.Select(j => j.Id));
        Assert.NotEmpty(data.Joints.Items);
        foreach (var joint in data.Joints.Items)
        {
            double x = 0, y = 0, z = 0;
            Assert.Equal(0, model.PointObj.GetCoordCartesian(joint.Id, ref x, ref y, ref z, "Global"));
            Assert.Equal(x, joint.Position.X, 8); Assert.Equal(y, joint.Position.Y, 8); Assert.Equal(z, joint.Position.Z, 8);
        }
        Assert.Equal(0, model.FrameObj.GetNameList(ref count, ref names));
        Assert.Equal(names.Order(StringComparer.Ordinal), data.Frames!.Items.Select(f => f.Id));
        var joints = data.Joints.Items.ToDictionary(j => j.Id);
        foreach (var frame in data.Frames.Items)
        {
            string start = "", end = "";
            Assert.Equal(0, model.FrameObj.GetPoints(frame.Id, ref start, ref end));
            Assert.Equal(start, frame.StartJoint); Assert.Equal(end, frame.EndJoint);
            Assert.Equal(joints[start].Position, frame.Start); Assert.Equal(joints[end].Position, frame.End);
        }
        Assert.Equal(0, model.AreaObj.GetNameList(ref count, ref names));
        Assert.Equal(names.Order(StringComparer.Ordinal), data.Areas!.Items.Select(a => a.Id));
        foreach (var area in data.Areas.Items)
        {
            Assert.True(area.Joints.Count >= 3);
            foreach (var joint in area.Joints) Assert.True(joints.ContainsKey(joint), $"Area {area.Id} joint {joint} missing");
        }
    });

    [LiveEtabsFact]
    public Task StoriesMatchScalarGettersAndStoryTable() => Verify((session, data) =>
    {
        var table = session.ReadTable("Story Definitions");
        Assert.Equal(table.Rows.Count, data.Stories!.Stories.Count);
        foreach (var story in data.Stories.Stories)
        {
            double elevation = 0, height = 0;
            Assert.Equal(0, session.ModelForVerification.Story.GetElevation(story.Name, ref elevation));
            Assert.Equal(0, session.ModelForVerification.Story.GetHeight(story.Name, ref height));
            Assert.Equal(elevation, story.Elevation); Assert.Equal(height, story.Height);
            var row = Assert.Single(table.Rows, r => r["Story"] == story.Name);
            MatchTablePrecision(row["Height"], story.Height);
        }
    });

    [LiveEtabsFact]
    public Task ConcreteSectionsMatchModelDefinitionTables() => Verify((session, data) =>
    {
        var rectangles = session.ReadTable("Frame Section Property Definitions - Concrete Rectangular");
        Assert.NotEmpty(rectangles.Rows);
        foreach (var row in rectangles.Rows)
        {
            var section = Assert.Single(data.FrameSections!.Items, s => s.Name == row["Name"]);
            Assert.Equal(row["Material"], section.Material);
            MatchTablePrecision(row["t3"], section.Depth!.Value);
            MatchTablePrecision(row["t2"], section.Width!.Value);
        }
        var areas = session.ReadTable("Area Section Property Definitions - Summary");
        Assert.Equal(areas.Rows.Count, data.AreaSections!.Items.Count);
        foreach (var row in areas.Rows.Where(r => r["Type"] is "Slab" or "Wall" && r["AnalType"] != "Layered"))
        {
            var section = Assert.Single(data.AreaSections.Items, s => s.Name == row["Name"]);
            Assert.Equal(row["Material"], section.Material);
            MatchTablePrecision(row["TotalThick"], section.MembraneThickness!.Value);
        }
    });

    [LiveEtabsFact]
    public Task ConcreteAndRebarStrengthsMatchModelDefinitionTables() => Verify((session, data) =>
    {
        var concrete = session.ReadTable("Material Properties - Concrete Data");
        Assert.NotEmpty(concrete.Rows);
        foreach (var row in concrete.Rows)
        {
            var material = Assert.Single(data.Materials!.Items, m => m.Name == row["Material"]);
            Assert.Equal("Concrete", material.Type);
            MatchTablePrecision(row["Fc"], material.ConcreteStrength!.Value);
            Assert.True(material.ElasticModulus > 0);
        }
        var rebar = session.ReadTable("Material Properties - Rebar Data");
        Assert.NotEmpty(rebar.Rows);
        foreach (var row in rebar.Rows)
        {
            var material = Assert.Single(data.Materials!.Items, m => m.Name == row["Material"]);
            Assert.Equal("Rebar", material.Type);
            MatchTablePrecision(row["Fy"], material.RebarYieldStrength!.Value);
            MatchTablePrecision(row["Fu"], material.RebarUltimateStrength!.Value);
        }
    });

    [LiveEtabsFact]
    public Task InvalidObjectApiCallFailsExplicitly() => Verify((session, _) =>
    {
        string p1 = "", p2 = "";
        var guard = new ApiGuard(NullLogger.Instance, default);
        var exception = Assert.Throws<ApiException>(() => guard.Call("FrameObj.GetPoints", () =>
            session.ModelForVerification.FrameObj.GetPoints("__inspector_nonexistent_4cf9e7__", ref p1, ref p2)));
        Assert.NotEqual(0, exception.ReturnCode);
    });

    [LiveEtabsFact]
    public Task RepeatedReadsAreDeterministicAndLeaveTheSessionUnchanged() => Verify((session, first) =>
    {
        var second = new ModelExtractor(NullLogger<ModelExtractor>.Instance).Extract(session);
        Assert.Equal(ModelConsistency.ContentFingerprint(first), ModelConsistency.ContentFingerprint(second));
        Assert.True(ModelConsistency.SameContext(first.Identity, second.Identity));
    });

    [LiveEtabsFact]
    public Task AreaBoundariesMatchConnectivityTables() => Verify((session, data) =>
    {
        var rows = new[] { "Floor Object Connectivity", "Wall Object Connectivity", "Null Area Object Connectivity" }
            .SelectMany(key => session.ReadTable(key).Rows).ToArray();
        Assert.Equal(data.Areas!.Items.Count, rows.Length);
        foreach (var row in rows)
        {
            var area = Assert.Single(data.Areas.Items, a => a.Id == row["UniqueName"]);
            Assert.Equal(row["Story"], area.Story);
            var joints = row.Where(pair => pair.Key.StartsWith("UniquePt", StringComparison.Ordinal) && !string.IsNullOrEmpty(pair.Value))
                .Select(pair => pair.Value).Order(StringComparer.Ordinal);
            Assert.Equal(joints, area.Joints.Order(StringComparer.Ordinal));
        }
    });

    [LiveEtabsFact]
    public Task AdditionalSectionTypesMatchDefinitionTables() => Verify((session, data) =>
    {
        foreach (var row in session.ReadTable("Frame Section Property Definitions - Steel I/Wide Flange").Rows)
        {
            var section = Assert.Single(data.FrameSections!.Items, s => s.Name == row["Name"]);
            MatchTablePrecision(row["t3"], section.Depth!.Value); MatchTablePrecision(row["t2"], section.Width!.Value);
            foreach (var (key, field) in new[] { ("TopFlangeThickness", "tf"), ("WebThickness", "tw"),
                ("BottomFlangeWidth", "t2b"), ("BottomFlangeThickness", "tfb") })
                MatchTablePrecision(row[field], section.AdditionalDimensions[key]);
        }
        foreach (var row in session.ReadTable("Deck Property Definitions").Rows)
        {
            var section = Assert.Single(data.AreaSections!.Items, s => s.Name == row["Name"]);
            foreach (var (key, field) in new[] { ("SlabDepth", "SlabDepth"), ("RibDepth", "RibDepth"),
                ("RibWidthTop", "RibWidthTop"), ("RibWidthBottom", "RibWidthBot"),
                ("RibSpacing", "RibSpacing"), ("DeckShearThickness", "DeckShrThk") })
                MatchTablePrecision(row[field], section.AdditionalDimensions[key]);
        }
        foreach (var auto in data.FrameSections!.Items.Where(s => s.Shape == "Auto"))
        {
            Assert.NotEmpty(auto.AutoSelectSections);
            foreach (var name in auto.AutoSelectSections) Assert.Contains(data.FrameSections.Items, s => s.Name == name);
            Assert.Null(auto.Material); Assert.Null(auto.Width); Assert.Null(auto.Depth);
        }
    });

    [LiveEtabsFact]
    public Task MaterialStiffnessAndDensitiesMatchDefinitionTable() => Verify((session, data) =>
    {
        foreach (var row in session.ReadTable("Material Properties - Basic Mechanical Properties").Rows)
        {
            var material = Assert.Single(data.Materials!.Items, m => m.Name == row["Material"]);
            MatchTablePrecision(row["UnitWeight"], material.UnitWeight);
            MatchTablePrecision(row["UnitMass"], material.MassDensity);
            MatchTablePrecision(row["E1"], material.ElasticModulus!.Value);
            if (material.PoissonRatio is { } poisson) MatchTablePrecision(row["U12"], poisson);
        }
    });

    private static void MatchTablePrecision(string text, double actual)
    {
        // Table display strings are rounded. Compare within half their last displayed decimal place.
        // This is an API representation tolerance, never an engineering design tolerance.
        var value = double.Parse(text, CultureInfo.InvariantCulture);
        var parts = text.ToLowerInvariant().Split('e');
        int exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = parts[0].IndexOf('.');
        int decimals = dot < 0 ? 0 : parts[0].Length - dot - 1;
        var tolerance = 0.5 * Math.Pow(10, exponent - decimals) + 1e-10 * Math.Max(1, Math.Abs(actual));
        Assert.InRange(actual, value - tolerance, value + tolerance);
    }
}


