using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

public static class SvgPlanGenerator
{
    public sealed record FloorPlan(string Story, double Elevation, string FileName, string SvgMarkup);
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private const string Fail = "#dc2626", Warning = "#f59e0b", Neutral = "#d1d5db";

    public static void GeneratePlans(InspectionReport report, string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var plans = RenderPlans(report);
        Directory.CreateDirectory(outputDirectory);
        foreach (var plan in plans)
            File.WriteAllText(Path.Combine(outputDirectory, plan.FileName), plan.SvgMarkup, new UTF8Encoding(false));
    }

    public static IReadOnlyList<FloorPlan> RenderPlans(InspectionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        List<FloorPlan> plans = [];
        var model = report.Input.Model;
        var joints = (model.Joints?.Items ?? []).ToDictionary(j => j.Id, StringComparer.Ordinal);
        var frames = model.Frames?.Items ?? [];
        var areas = model.Areas?.Items ?? [];
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Joint"] = joints.Keys.ToHashSet(StringComparer.Ordinal),
            ["Frame"] = frames.Select(f => f.Id).ToHashSet(StringComparer.Ordinal),
            ["Area"] = areas.Select(a => a.Id).ToHashSet(StringComparer.Ordinal)
        };
        var statuses = new Dictionary<(string Type, string Id), CheckStatus>();
        foreach (var finding in report.Findings)
        {
            if (finding.ElementType == "FrameGroup")
            {
                foreach (var member in finding.AffectedElements)
                    if (names["Frame"].Contains(member)) Add("Frame", member, finding.Status);
                continue;
            }
            var id = finding.ElementId;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var type = finding.ElementType == "Point" ? "Joint" : finding.ElementType;
            if (type == "JointPair")
            {
                foreach (var joint in id.Split('|').Where(joints.ContainsKey)) Add("Joint", joint, finding.Status);
                continue;
            }
            if (type is not null && names.TryGetValue(type, out var typed))
            {
                if (typed.Contains(id)) Add(type, id, finding.Status);
                else if (id.StartsWith(type + ":", StringComparison.OrdinalIgnoreCase) && typed.Contains(id[(type.Length + 1)..]))
                    Add(type, id[(type.Length + 1)..], finding.Status);
                continue;
            }
            if (type is not null) continue; // A story, section or pier ID is not an object ID.
            var colon = id.IndexOf(':');
            if (colon > 0)
            {
                var prefix = names.Keys.FirstOrDefault(k => k.Equals(id[..colon], StringComparison.OrdinalIgnoreCase));
                if (prefix is not null && names[prefix].Contains(id[(colon + 1)..])) Add(prefix, id[(colon + 1)..], finding.Status);
            }
            else
            {
                var matches = names.Where(n => n.Value.Contains(id)).Select(n => n.Key).ToArray();
                if (matches.Length == 1) Add(matches[0], id, finding.Status);
            }
        }

        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var story in (model.Stories?.Stories ?? []).OrderBy(s => s.Elevation))
        {
            var storyFrames = frames.Where(f => f.Story == story.Name).ToArray();
            var storyAreas = areas.Where(a => a.Story == story.Name).OrderBy(a => a.IsOpening).ToArray();
            var tolerance = Math.Max(1e-6, Math.Abs(story.Elevation) * 1e-9);
            var storyJoints = joints.Values.Where(j => Math.Abs(j.Position.Z - story.Elevation) <= tolerance && Finite(j.Position)).ToArray();
            var polygons = storyAreas.Select(a => (Area: a, Points: a.Joints.Select(id => joints.GetValueOrDefault(id)?.Position).ToArray()))
                .Where(a => a.Points.Length >= 3 && a.Points.All(p => p is not null && Finite(p))).ToArray();
            var lines = storyFrames.Select(f => (Frame: f,
                Start: joints.GetValueOrDefault(f.StartJoint)?.Position ?? f.Start,
                End: joints.GetValueOrDefault(f.EndJoint)?.Position ?? f.End))
                .Where(f => Finite(f.Start) && Finite(f.End)).ToArray();
            var points = storyJoints.Select(j => j.Position).Concat(lines.SelectMany(f => new[] { f.Start, f.End }))
                .Concat(polygons.SelectMany(a => a.Points.Select(p => p!))).ToArray();
            var minX = points.Length == 0 ? 0 : points.Min(p => p.X);
            var maxX = points.Length == 0 ? 1 : points.Max(p => p.X);
            var minY = points.Length == 0 ? 0 : points.Min(p => p.Y);
            var maxY = points.Length == 0 ? 1 : points.Max(p => p.Y);
            var span = Math.Max(maxX - minX, maxY - minY);
            if (!double.IsFinite(span)) throw new InvalidOperationException("Floor-plan coordinate range is nonfinite.");
            if (span <= 0) span = 1;
            var padX = Math.Max(maxX - minX, span * .1) * .1;
            var padY = Math.Max(maxY - minY, span * .1) * .1;
            var stroke = span / 400;
            var root = new XElement(Svg + "svg",
                new XAttribute("viewBox", $"{N(minX - padX)} {N(-maxY - padY)} {N(maxX - minX + 2 * padX)} {N(maxY - minY + 2 * padY)}"),
                new XAttribute("width", "100%"), new XAttribute("height", "100%"),
                new XAttribute("preserveAspectRatio", "xMidYMid meet"), new XAttribute("role", "img"),
                new XAttribute("aria-labelledby", "plan-title plan-description"),
                new XElement(Svg + "title", new XAttribute("id", "plan-title"), story.Name + " floor plan"),
                new XElement(Svg + "desc", new XAttribute("id", "plan-description"),
                    $"Story {story.Name}; elevation {N(story.Elevation)} {model.Identity.DisplayUnits.Length}. Global X/Y plan; positive Y is upward. Red FAIL, orange WARNING, gray PASS/other/no finding. Gray does not establish safety. Only direct frame/area/joint findings and joint pairs are mapped; pier, story and section findings are not mapped to objects. Omitted invalid geometry: {storyAreas.Length - polygons.Length} areas, {storyFrames.Length - lines.Length} frames."),
                new XElement(Svg + "metadata", $"Model: {model.Identity.FilePath}; ETABS: {model.Identity.EtabsVersion}; inspection: {report.Timestamp:O}"));
            var areaGroup = new XElement(Svg + "g", new XAttribute("id", "areas"));
            foreach (var (area, vertices) in polygons)
            {
                var color = Color("Area", area.Id);
                var polygon = Shape("polygon", "Area", area.Id,
                    new XAttribute("points", string.Join(" ", vertices.Select(p => $"{N(p!.X)},{N(-p.Y)}"))),
                    new XAttribute("fill", area.IsOpening ? "#ffffff" : color),
                    new XAttribute("fill-opacity", area.IsOpening ? "1" : ".23"),
                    new XAttribute("stroke", color), new XAttribute("stroke-width", N(stroke)));
                if (area.IsOpening) polygon.Add(new XAttribute("stroke-dasharray", $"{N(stroke * 3)} {N(stroke * 2)}"));
                areaGroup.Add(polygon);
            }
            root.Add(areaGroup);
            var frameGroup = new XElement(Svg + "g", new XAttribute("id", "frames"), new XAttribute("stroke-linecap", "round"));
            foreach (var (frame, start, end) in lines)
                frameGroup.Add(Shape("line", "Frame", frame.Id,
                    new XAttribute("x1", N(start.X)), new XAttribute("y1", N(-start.Y)),
                    new XAttribute("x2", N(end.X)), new XAttribute("y2", N(-end.Y)),
                    new XAttribute("stroke", Color("Frame", frame.Id)), new XAttribute("stroke-width", N(stroke * 1.5))));
            root.Add(frameGroup);
            var jointGroup = new XElement(Svg + "g", new XAttribute("id", "joints"));
            foreach (var joint in storyJoints)
                jointGroup.Add(Shape("circle", "Joint", joint.Id,
                    new XAttribute("cx", N(joint.Position.X)), new XAttribute("cy", N(-joint.Position.Y)),
                    new XAttribute("r", N(stroke * (Color("Joint", joint.Id) == Neutral ? .6 : 2))), new XAttribute("fill", Color("Joint", joint.Id))));
            root.Add(jointGroup);
            if (points.Length == 0) root.Add(new XElement(Svg + "text", new XAttribute("x", "0"), new XAttribute("y", "-.5"), new XAttribute("font-size", ".06"), "No drawable geometry at this story."));
            var filename = SafeName(story.Name);
            if (!usedFiles.Add(filename + ".svg"))
            {
                filename += "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(story.Name)))[..10];
                int suffix = 1; var stem = filename;
                while (!usedFiles.Add(filename + ".svg")) filename = stem + "_" + suffix++;
            }
            plans.Add(new(story.Name, story.Elevation, filename + ".svg", root.ToString(SaveOptions.DisableFormatting)));
        }
        return plans;

        void Add(string type, string id, CheckStatus status)
        {
            if (!statuses.TryGetValue((type, id), out var previous) || Rank(status) > Rank(previous)) statuses[(type, id)] = status;
        }
        string Color(string type, string id) => statuses.GetValueOrDefault((type, id), CheckStatus.NOT_VERIFIED) switch
        { CheckStatus.FAIL => Fail, CheckStatus.WARNING => Warning, _ => Neutral };
        XElement Shape(string tag, string type, string id, params XAttribute[] attributes)
        {
            var status = statuses.TryGetValue((type, id), out var found) ? found.ToString() : "NO_FINDING";
            return new XElement(Svg + tag, attributes, new XAttribute("data-element-type", type),
                new XAttribute("data-element-id", id), new XAttribute("data-status", status),
                new XElement(Svg + "title", $"{type} {id}: {status}"));
        }
    }

    private static int Rank(CheckStatus status) => status switch
    { CheckStatus.FAIL => 6, CheckStatus.WARNING => 5, CheckStatus.ERROR => 4, CheckStatus.NOT_VERIFIED => 3, CheckStatus.SKIPPED => 2, CheckStatus.PASS => 1, _ => 0 };
    private static bool Finite(Coordinate p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static string N(double value) => value == 0 ? "0" : value.ToString("G17", CultureInfo.InvariantCulture);
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).Take(100).ToArray()).TrimEnd('.', ' ');
        var stem = safe.Split('.')[0];
        if (string.IsNullOrWhiteSpace(safe) || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            Enumerable.Range(1, 9).Any(i => stem.Equals("COM" + i, StringComparison.OrdinalIgnoreCase) || stem.Equals("LPT" + i, StringComparison.OrdinalIgnoreCase))) safe = "Story_" + safe;
        return safe;
    }
}
