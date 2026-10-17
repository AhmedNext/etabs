
public static class GeometryChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("GEO-001", "Coincident joints", "Pair distances <= confirmed tolerance or model bounding diagonal * 1e-8; coincidence is a review warning.",
            ["Joints"], Coincident),
        Rule.Create("GEO-002", "Object connectivity components", "Connected components of the frame/structural area/link joint graph; multiple components require review, not an instability conclusion.",
            ["Joints", "Frames", "Areas", "Links"], Components),
        Rule.Create("GEO-003", "Short frame screening", "Global endpoint distance compared with the explicitly confirmed project short-frame threshold; no default engineering threshold.",
            ["Frames"], ShortFrames),
        Rule.Create("GEO-004", "Section dimensions", "Known physical section dimensions must be positive; auto-select lists have no single geometry; limited section variants remain unverified.",
            ["FrameSections", "AreaSections"], Dimensions),
        Rule.Create("GEO-006", "Unreferenced joints", "Find joints not referenced by extracted frames, structural area objects or links; restraints and other intentional uses require review.",
            ["Joints", "Frames", "Areas", "Links", "JointAssignments"], Unreferenced),
        Rule.Create("GEO-007", "Frame end references", "Each frame end must reference an extracted joint; coincident endpoints are flagged against the numerical/model tolerance.",
            ["Joints", "Frames"], FrameEnds),
        Rule.Create("GEO-009", "Frame releases", "Report assigned end releases and partial fixity for review without declaring an intentional release a design failure.",
            ["FrameAssignments"], Releases),
        Rule.Create("GEO-010", "Area boundary connectivity", "Non-opening structural areas need at least three distinct defined boundary joints; mesh/load-path adequacy is outside this criterion.",
            ["Joints", "Areas"], AreaBoundaries),
        Rule.Create("GEO-011", "Story elevation consistency", "For one tower, each height must be positive and elevation must equal previous elevation plus height within the model numerical tolerance.",
            ["Joints", "Stories"], Stories),
        Rule.Create("GEO-012", "Diaphragm assignments", "Check definition references and inherited shell assignments; floor diaphragm completeness requires a confirmed project requirement.",
            ["Joints", "Areas", "JointAssignments", "AreaAssignments", "Diaphragms"], Diaphragms),
        Rule.Create("GEO-015", "Section and material references", "Assigned structural sections and effective material names must exist; openings/null areas are exempt from structural property assignment.",
            ["Frames", "Areas", "Materials", "FrameSections", "AreaSections", "FrameAssignments", "AreaAssignments"], References)
    ];

    private static IReadOnlyList<Observation> Coincident(InspectionContext c)
    {
        var joints = c.Input.Model.Joints!.Items.OrderBy(j => j.Position.X).ThenBy(j => j.Id, StringComparer.Ordinal).ToArray();
        if (joints.Length < 2) return [Rule.NotApplicable("Fewer than two joints are available for coincidence screening.")];
        var tolerance = GeometryMath.ModelTolerance(c); var unit = c.Input.Model.Identity.DisplayUnits.Length;
        var geometry = c.Configuration.Settings.Geometry;
        var intentional = c.Configuration.Settings.Confirmed ? geometry?.IntentionalCoincidentJoints ?? [] : [];
        var connected = StructuralJointIds(c);
        var subNanometer = GeometryMath.ConvertLength(new(1e-6, "mm"), unit);
        var screeningTolerance = Math.Max(tolerance, subNanometer);
        List<Observation> results = [];
        for (int i = 0; i < joints.Length; i++)
            for (int j = i + 1; j < joints.Length && joints[j].Position.X - joints[i].Position.X <= screeningTolerance; j++)
            {
                var distance = GeometryMath.Distance(joints[i].Position, joints[j].Position);
                if (distance > screeningTolerance) continue;
                var known = intentional.Contains(joints[i].Id, StringComparer.Ordinal) && intentional.Contains(joints[j].Id, StringComparer.Ordinal);
                var generatedReference = !connected.Contains(joints[i].Id) || !connected.Contains(joints[j].Id);
                var numericalNoise = distance < subNanometer;
                var ignored = known || generatedReference || numericalNoise;
                var reason = known ? "Coincident pair is explicitly recorded as intentional." :
                    generatedReference ? "Pair includes an unreferenced calculation/reference joint; coincidence is not treated as a structural duplicate." :
                    numericalNoise ? "Pair separation is below 1e-6 mm and is treated as numerical precision noise." :
                    "Coincident or near-coincident joints require connectivity review.";
                results.Add(new(ignored ? CheckStatus.NOT_APPLICABLE : CheckStatus.WARNING, reason,
                    string.Join("|", new[] { joints[i].Id, joints[j].Id }.Order(StringComparer.Ordinal)), "JointPair", Position: joints[i].Position,
                    Actual: distance, Limit: ignored && numericalNoise ? subNanometer : tolerance, Unit: unit,
                    Evidence: [new("Second joint coordinates", "PointObj.GetAllPoints", joints[j].Position.ToString()),
                        new("Structural connectivity classification", "FrameObj/AreaObj/LinkObj connectivity", generatedReference ? "unreferenced calculation/reference joint present" : "both joints structurally referenced"),
                        new("Tolerance source", c.Configuration.Settings.Confirmed && c.Configuration.Settings.Geometry?.CoincidentTolerance is not null
                            ? c.Configuration.Path ?? "project.json" : "Numerical screen: bounding diagonal * 1e-8", tolerance.ToString("G17", CultureInfo.InvariantCulture), unit)]));
                if (results.Count == 10000)
                    return [..results, Rule.NotVerified("Coincidence output reached 10,000 pairs; further pairs were not evaluated. Review model/configuration before rerunning.")];
            }
        return results.Count == 0 ? [Rule.Pass("No joint pair lies within the stated numerical coincidence tolerance.")] : results;
    }
    private static bool IsDiaphragmAssigned(JointAssignment? assignment) => assignment is not null &&
        assignment.DiaphragmOption == "DefinedDiaphragm" && !string.IsNullOrWhiteSpace(assignment.Diaphragm);
    private static HashSet<string> StructuralJointIds(InspectionContext c) => c.Input.Model.Frames!.Items
        .SelectMany(f => new[] { f.StartJoint, f.EndJoint })
        .Concat(c.Input.Model.Areas!.Items.Where(StructuralArea).SelectMany(a => a.Joints))
        .Concat(c.Input.Validation.Links!.Items.SelectMany(l => new[] { l.StartJoint, l.EndJoint }))
        .ToHashSet(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, HashSet<string>> Graph(InspectionContext c)
    {
        var graph = c.Input.Model.Joints!.Items.ToDictionary(j => j.Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        void Connect(IReadOnlyList<string> ids)
        {
            for (int i = 1; i < ids.Count; i++)
                if (graph.ContainsKey(ids[0]) && graph.ContainsKey(ids[i])) { graph[ids[0]].Add(ids[i]); graph[ids[i]].Add(ids[0]); }
        }
        foreach (var f in c.Input.Model.Frames!.Items) Connect([f.StartJoint, f.EndJoint]);
        foreach (var a in c.Input.Model.Areas!.Items.Where(StructuralArea)) Connect(a.Joints);
        foreach (var l in c.Input.Validation.Links!.Items) Connect([l.StartJoint, l.EndJoint]);
        return graph;
    }
    private static bool StructuralArea(Area area) => !area.IsOpening && area.Orientation != "Null";
    private static IReadOnlyList<Observation> Components(InspectionContext c)
    {
        var graph = Graph(c); var visited = new HashSet<string>(StringComparer.Ordinal); List<List<string>> components = [];
        foreach (var id in graph.Keys.Order(StringComparer.Ordinal))
        {
            if (visited.Contains(id) || graph[id].Count == 0) continue;
            List<string> group = []; Queue<string> queue = new(); queue.Enqueue(id); visited.Add(id);
            while (queue.TryDequeue(out var next))
            {
                group.Add(next);
                foreach (var adjacent in graph[next].Order(StringComparer.Ordinal)) if (visited.Add(adjacent)) queue.Enqueue(adjacent);
            }
            components.Add(group);
        }
        if (components.Count == 0) return [Rule.NotApplicable("No connected structural object graph is available.")];
        if (c.Input.Validation.Links!.Limitations.Count > 0) return [Rule.NotVerified("Link acquisition is unverified; graph components cannot be fully verified.")];
        if (components.Count == 1) return [Rule.Pass("The extracted structural object graph has one connected component; solver stability and mesh connectivity are not evaluated.")];
        var intentional = c.Configuration.Settings.Confirmed ? c.Configuration.Settings.Geometry?.IntentionalDisconnectedElements ?? [] : [];
        return components.Select(group =>
        {
            var groupIds = group.ToHashSet(StringComparer.Ordinal);
            var members = c.Input.Model.Frames!.Items.Where(f => groupIds.Contains(f.StartJoint)).Select(f => "Frame:" + f.Id)
                .Concat(c.Input.Model.Areas!.Items.Where(a => StructuralArea(a) && a.Joints.Any(groupIds.Contains)).Select(a => "Area:" + a.Id))
                .Concat(c.Input.Validation.Links.Items.Where(l => groupIds.Contains(l.StartJoint)).Select(l => "Link:" + l.Id)).Order(StringComparer.Ordinal).ToArray();
            var known = members.Length > 0 && members.All(id => intentional.Contains(id, StringComparer.Ordinal));
            return new Observation(known ? CheckStatus.NOT_APPLICABLE : CheckStatus.WARNING,
                known ? "Disconnected component is explicitly recorded as intentional." : "Separate structural object component requires review.",
                group.Order(StringComparer.Ordinal).First(), "Component", Position: c.Input.Model.Joints!.Items.First(j => j.Id == group[0]).Position,
                Actual: components.Count, Unit: "components", Evidence: [new("Component members", "FrameObj/AreaObj/LinkObj connectivity", string.Join(", ", members)),
                    new("Component joints", "Object connectivity", string.Join(", ", group.Order(StringComparer.Ordinal)))]);
        }).ToArray();
    }
    private static IReadOnlyList<Observation> ShortFrames(InspectionContext c)
    {
        if (!c.Configuration.Settings.Confirmed || c.Configuration.Settings.Geometry?.ShortFrameThreshold is not { } criterion)
            return [Rule.NotVerified("A confirmed short-frame threshold with length units is required.")];
        var frames = c.Input.Model.Frames!.Items;
        if (frames.Count == 0) return [Rule.NotApplicable("No frames are defined.")];
        var threshold = GeometryMath.ConvertLength(criterion, c.Input.Model.Identity.DisplayUnits.Length);
        var results = frames.Select(f => (f, length: GeometryMath.Distance(f.Start, f.End))).Where(x => x.length < threshold)
            .Select(x => new Observation(CheckStatus.WARNING, "Frame length is below the confirmed project screening threshold.", x.f.Id, "Frame", x.f.Story,
                x.f.Start, x.length, threshold, c.Input.Model.Identity.DisplayUnits.Length,
                [new("Project threshold", c.Configuration.Path ?? "project.json", criterion.ToString())])).ToArray();
        return results.Length == 0 ? [Rule.Pass("All extracted frames meet the confirmed minimum screening length.")] : results;
    }
    private static IReadOnlyList<Observation> Dimensions(InspectionContext c)
    {
        List<Observation> results = []; int physical = 0;
        void Dimension(string id, string type, IEnumerable<double?> values, string? limitation)
        {
            if (limitation is not null) { results.Add(new(CheckStatus.NOT_VERIFIED, limitation, id, type)); return; }
            var dimensions = values.ToArray(); physical++;
            if (dimensions.Length == 0 || dimensions.Any(v => v is null)) results.Add(new(CheckStatus.NOT_VERIFIED, "Physical section dimensions are unavailable.", id, type));
            else if (dimensions.Any(v => v <= 0 || !double.IsFinite(v!.Value))) results.Add(new(CheckStatus.FAIL, "Section has a nonpositive or invalid physical dimension.", id, type,
                Actual: dimensions.Min(), Limit: 0, Unit: c.Input.Model.Identity.DisplayUnits.Length,
                Evidence: [new("Section dimensions", "ETABS property definition", string.Join(", ", dimensions))]));
        }
        foreach (var s in c.Input.Model.FrameSections!.Items)
        {
            if (s.Shape == "Auto") continue;
            Dimension("FrameSection:" + s.Name, "FrameSection", s.Shape == "Circle" ? [s.Diameter] :
                new double?[] { s.Depth, s.Width }.Concat(s.AdditionalDimensions.Values.Select(v => (double?)v)), s.Limitation);
        }
        foreach (var s in c.Input.Model.AreaSections!.Items)
            Dimension("AreaSection:" + s.Name, "AreaSection", new double?[] { s.MembraneThickness }.Concat(s.AdditionalDimensions.Values.Select(v => (double?)v)), s.Limitation);
        return results.Count > 0 ? results : physical == 0 ? [Rule.NotApplicable("No physical sections are defined.")] : [Rule.Pass("All interpreted physical section dimensions are positive; this is a geometry criterion only.")];
    }
    private static IReadOnlyList<Observation> Unreferenced(InspectionContext c)
    {
        var used = StructuralJointIds(c);
        var assignments = c.Input.Validation.JointAssignments!.Items.ToDictionary(j => j.Id);
        var geometry = c.Configuration.Settings.Geometry;
        var intentional = c.Configuration.Settings.Confirmed ? geometry?.IntentionalUnreferencedJoints ?? [] : [];
        var tolerance = GeometryMath.ModelTolerance(c);
        var diaphragmPoints = assignments.Values.Where(IsDiaphragmAssigned).Select(a => a.Id)
            .Where(id => c.Input.Model.Joints!.Items.Any(j => j.Id == id)).ToHashSet(StringComparer.Ordinal);
        var joints = c.Input.Model.Joints!.Items.ToDictionary(j => j.Id, StringComparer.Ordinal);
        var results = c.Input.Model.Joints!.Items.Where(j => !used.Contains(j.Id)).Select(j =>
        {
            var assignment = assignments.GetValueOrDefault(j.Id);
            var coincidesWithDiaphragmPoint = diaphragmPoints.Any(id => id != j.Id && GeometryMath.Distance(j.Position, joints[id].Position) <= tolerance);
            var exempt = intentional.Contains(j.Id, StringComparer.Ordinal) || IsDiaphragmAssigned(assignment) || coincidesWithDiaphragmPoint;
            var reason = intentional.Contains(j.Id, StringComparer.Ordinal) ? "Unreferenced joint is explicitly whitelisted as intentional." :
                IsDiaphragmAssigned(assignment) ? "Unreferenced joint has an explicit diaphragm assignment and is treated as a diaphragm reference point." :
                coincidesWithDiaphragmPoint ? "Unreferenced joint coincides with a diaphragm-assigned reference point within the model tolerance." :
                "Joint is unreferenced by the extracted structural frame/area/link graph; review restraints, springs or intentional reference-point use.";
            return new Observation(exempt ? CheckStatus.NOT_APPLICABLE : CheckStatus.WARNING, reason, j.Id, "Joint", Position: j.Position,
                Evidence: [new("Joint diaphragm/restraint assignment", "PointObj.GetDiaphragm/GetRestraint", assignment?.ToString() ?? "assignment unavailable"),
                    new("Coincident diaphragm point", "Joint coordinates and diaphragm assignments", coincidesWithDiaphragmPoint ? "yes" : "no")]);
        }).ToArray();
        if (c.Input.Validation.Links!.Limitations.Count > 0) return [Rule.NotVerified("Link acquisition is unverified; unreferenced-joint screening is incomplete.")];
        return results.Length == 0 ? [Rule.Pass("All extracted joints are referenced by a structural frame, area or link.")] : results;
    }
    private static IReadOnlyList<Observation> FrameEnds(InspectionContext c)
    {
        var joints = c.Input.Model.Joints!.Items.ToDictionary(j => j.Id); var tolerance = GeometryMath.ModelTolerance(c); List<Observation> results = [];
        foreach (var frame in c.Input.Model.Frames!.Items)
        {
            if (!joints.ContainsKey(frame.StartJoint) || !joints.ContainsKey(frame.EndJoint))
                results.Add(new(CheckStatus.FAIL, "Frame references a joint absent from the extracted inventory.", frame.Id, "Frame", frame.Story, frame.Start,
                    Evidence: [new("Endpoint references", "FrameObj.GetAllFrames", frame.StartJoint + ", " + frame.EndJoint)]));
            else if (GeometryMath.Distance(frame.Start, frame.End) <= tolerance)
                results.Add(new(CheckStatus.WARNING, "Frame endpoints are coincident within the model tolerance; review short links and intentional modeling.", frame.Id, "Frame", frame.Story, frame.Start,
                    GeometryMath.Distance(frame.Start, frame.End), tolerance, c.Input.Model.Identity.DisplayUnits.Length));
        }
        return results.Count == 0 ? c.Input.Model.Frames.Items.Count == 0 ? [Rule.NotApplicable("No frames are defined.")] : [Rule.Pass("Frame end references exist and endpoint distances exceed the numerical tolerance.")] : results;
    }
    private static IReadOnlyList<Observation> Releases(InspectionContext c)
    {
        var result = c.Input.Validation.FrameAssignments!.Items.Where(f => f.StartReleases.Any(v => v) || f.EndReleases.Any(v => v))
            .Select(f => new Observation(CheckStatus.WARNING, "Frame end releases are assigned; verify intended continuity and partial fixity.", f.Id, "Frame",
                Evidence: [new("I-end release flags U1,U2,U3,R1,R2,R3", "FrameObj.GetReleases", string.Join(",", f.StartReleases)),
                    new("J-end release flags U1,U2,U3,R1,R2,R3", "FrameObj.GetReleases", string.Join(",", f.EndReleases)),
                    new("I/J partial fixity", "FrameObj.GetReleases", string.Join(",", f.StartPartialFixity) + " / " + string.Join(",", f.EndPartialFixity))])).ToArray();
        return result.Length == 0 ? [Rule.Pass("No frame end release flags are assigned; structural continuity is not otherwise verified.")] : result;
    }
    private static IReadOnlyList<Observation> AreaBoundaries(InspectionContext c)
    {
        var ids = c.Input.Model.Joints!.Items.Select(j => j.Id).ToHashSet(StringComparer.Ordinal); List<Observation> results = [];
        var areas = c.Input.Model.Areas!.Items.Where(StructuralArea).ToArray();
        foreach (var a in areas)
        {
            if (a.Joints.Count < 3 || a.Joints.Distinct(StringComparer.Ordinal).Count() < 3 || a.Joints.Any(p => !ids.Contains(p)))
                results.Add(new(CheckStatus.FAIL, "Structural area boundary has fewer than three distinct defined joints or an undefined reference.", a.Id, "Area", a.Story,
                    Evidence: [new("Boundary joint identifiers", "AreaObj.GetPoints", string.Join(",", a.Joints))]));
        }
        return results.Count > 0 ? results : areas.Length == 0 ? [Rule.NotApplicable("No structural areas are defined.")] : [Rule.Pass("Structural area boundaries have at least three distinct defined joints; analysis mesh connectivity is not evaluated.")];
    }
    private static IReadOnlyList<Observation> Stories(InspectionContext c)
    {
        var stories = c.Input.Model.Stories!; double previous = stories.BaseElevation, tolerance = GeometryMath.ModelTolerance(c); List<Observation> results = [];
        foreach (var story in stories.Stories.OrderBy(s => s.Elevation).ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            var expected = previous + story.Height;
            if (story.Height <= 0 || Math.Abs(expected - story.Elevation) > tolerance)
                results.Add(new(CheckStatus.FAIL, "Story height or elevation increment is inconsistent with the preceding level.", story.Name, "Story", story.Name,
                    Actual: story.Elevation, Limit: expected, Unit: c.Input.Model.Identity.DisplayUnits.Length,
                    Evidence: [new("Height", "Story.GetStories_2", story.Height.ToString("G17", CultureInfo.InvariantCulture)),
                        new("Previous elevation", "Story.GetStories_2", previous.ToString("G17", CultureInfo.InvariantCulture))]));
            previous = story.Elevation;
        }
        return results.Count > 0 ? results : stories.Stories.Count == 0 ? [Rule.NotApplicable("No stories are defined.")] : [Rule.Pass("Story heights are positive and match successive elevation increments within the model tolerance.")];
    }
    private static IReadOnlyList<Observation> Diaphragms(InspectionContext c)
    {
        var definitions = c.Input.Validation.Diaphragms!.Items.Select(d => d.Name).ToHashSet(StringComparer.Ordinal); List<Observation> results = [];
        var assignments = c.Input.Validation.AreaAssignments!.Items.ToDictionary(a => a.Id);
        foreach (var a in c.Input.Validation.AreaAssignments.Items.Where(a => a.Diaphragm is not null && !definitions.Contains(a.Diaphragm)))
            results.Add(new(CheckStatus.FAIL, "Area references an undefined diaphragm.", a.Id, "Area", Evidence: [new("Diaphragm", "AreaObj.GetDiaphragm", a.Diaphragm)]));
        foreach (var j in c.Input.Validation.JointAssignments!.Items.Where(j => j.DiaphragmOption == "DefinedDiaphragm" && (j.Diaphragm is null || !definitions.Contains(j.Diaphragm))))
            results.Add(new(CheckStatus.FAIL, "Joint references an undefined diaphragm.", j.Id, "Joint", Evidence: [new("Diaphragm", "PointObj.GetDiaphragm", j.Diaphragm)]));
        // Shell inheritance is not an explicit missing diaphragm assignment at the joint.
        if (!c.Configuration.Settings.Confirmed || c.Configuration.Settings.Geometry?.RequireFloorDiaphragms is null)
            results.Add(Rule.NotVerified("Floor diaphragm completeness requires a confirmed project requirement; shell-inherited joint assignments are not treated as missing."));
        else if (c.Configuration.Settings.Geometry.RequireFloorDiaphragms == true)
            foreach (var area in c.Input.Model.Areas!.Items.Where(a => StructuralArea(a) && a.Orientation == "Floor"))
                if (!assignments.TryGetValue(area.Id, out var assignment)) results.Add(new(CheckStatus.NOT_VERIFIED, "Floor diaphragm assignment was not extracted.", area.Id, "Area", area.Story));
                else if (assignment.Diaphragm is null) results.Add(new(CheckStatus.WARNING, "Floor has no diaphragm despite the confirmed project requirement.", area.Id, "Area", area.Story));
        return results.Count == 0 ? [Rule.Pass("Diaphragm references exist and the confirmed floor-assignment criterion is satisfied.")] : results;
    }
    private static IReadOnlyList<Observation> References(InspectionContext c)
    {
        if (c.Input.Validation.FrameAssignments!.Limitations.Count > 0 || c.Input.Validation.AreaAssignments!.Limitations.Count > 0)
            return [Rule.NotVerified("Material overwrite acquisition is unverified for this model; effective material-reference validation is incomplete.")];
        var frames = c.Input.Model.FrameSections!.Items.ToDictionary(s => s.Name); var areas = c.Input.Model.AreaSections!.Items.ToDictionary(s => s.Name);
        var materials = c.Input.Model.Materials!.Items.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var fa = c.Input.Validation.FrameAssignments.Items.ToDictionary(a => a.Id); var aa = c.Input.Validation.AreaAssignments.Items.ToDictionary(a => a.Id);
        List<Observation> results = [];
        foreach (var frame in c.Input.Model.Frames!.Items)
        {
            if (!frames.TryGetValue(frame.Section, out var section)) { results.Add(new(CheckStatus.FAIL, "Frame references an undefined section.", frame.Id, "Frame", frame.Story)); continue; }
            if (!fa.TryGetValue(frame.Id, out var assignment)) { results.Add(new(CheckStatus.NOT_VERIFIED, "Frame material assignment was not extracted.", frame.Id, "Frame", frame.Story)); continue; }
            var material = assignment.MaterialOverwrite ?? section.Material;
            if (section.Shape == "Auto") results.Add(new(CheckStatus.NOT_VERIFIED, "Auto-select member effective section/material interpretation requires additional verification.", frame.Id, "Frame", frame.Story));
            else if (material is null) results.Add(new(CheckStatus.NOT_VERIFIED, "Effective frame material is unavailable.", frame.Id, "Frame", frame.Story));
            else if (!materials.Contains(material)) results.Add(new(CheckStatus.FAIL, "Frame references an undefined effective material.", frame.Id, "Frame", frame.Story,
                Evidence: [new("Effective material", "Section definition / material overwrite table", material)]));
        }
        foreach (var area in c.Input.Model.Areas!.Items.Where(StructuralArea))
        {
            if (area.Section is null || !areas.TryGetValue(area.Section, out var section)) { results.Add(new(CheckStatus.FAIL, "Structural area references an undefined section.", area.Id, "Area", area.Story)); continue; }
            if (!aa.TryGetValue(area.Id, out var assignment)) { results.Add(new(CheckStatus.NOT_VERIFIED, "Area material assignment was not extracted.", area.Id, "Area", area.Story)); continue; }
            var material = assignment.MaterialOverwrite ?? section.Material;
            if (material is null) results.Add(new(CheckStatus.NOT_VERIFIED, "Effective area material is unavailable.", area.Id, "Area", area.Story));
            else if (!materials.Contains(material)) results.Add(new(CheckStatus.FAIL, "Area references an undefined effective material.", area.Id, "Area", area.Story,
                Evidence: [new("Effective material", "Section definition / material overwrite table", material)]));
        }
        return results.Count == 0 ? [Rule.Pass("All inspected structural objects reference defined sections and effective material names; material suitability is not checked.")] : results;
    }
}


