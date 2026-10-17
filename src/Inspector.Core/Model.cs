
public sealed record UnitContext(string Force, string Length, string Temperature)
{
    public string Stress => $"{Force}/{Length}²";
    public string UnitWeight => $"{Force}/{Length}³";
    // CSi uses consistent force/length/second mass units, not a kilogram assumption.
    public string MassDensity => $"{Force}·s²/{Length}⁴";
}

public sealed record ModelIdentity(string EtabsVersion, string FilePath, bool IsLocked,
    UnitContext DisplayUnits, UnitContext DatabaseUnits, DateTimeOffset ReadAt);
public sealed record Coordinate(double X, double Y, double Z);
public sealed record Story(string Name, double Elevation, double Height, bool IsMaster, string SimilarTo);
public sealed record StoryData(double BaseElevation, IReadOnlyList<Story> Stories)
{
    public SourceEvidence? Source { get; init; }
}
public sealed record Joint(string Id, Coordinate Position);
public sealed record Frame(string Id, string Story, string Section, string StartJoint, string EndJoint,
    Coordinate Start, Coordinate End, double LocalAxisAngle);
public sealed record Area(string Id, string Label, string Story, string? Section, string Orientation,
    bool IsOpening, IReadOnlyList<string> Joints);
public sealed record Material(string Name, string Type, int SymmetryType, double UnitWeight,
    double MassDensity, double? ElasticModulus, double? PoissonRatio, double? ConcreteStrength,
    double? RebarYieldStrength, double? RebarUltimateStrength);
public sealed record FrameSection(string Name, string Shape, string? Material,
    double? Depth, double? Width, double? Diameter, string? Limitation)
{
    public IReadOnlyDictionary<string, double> AdditionalDimensions { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> AutoSelectSections { get; init; } = [];
    public string? AutoStartSection { get; init; }
}
public sealed record AreaSection(string Name, string Kind, string ShellType, string? Material,
    double? MembraneThickness, string? Limitation)
{
    public IReadOnlyDictionary<string, double> AdditionalDimensions { get; init; } = new Dictionary<string, double>();
}
public sealed record SourceEvidence(string Operation, DateTimeOffset ReadAt, UnitContext Units,
    string CoordinateSystem = "Global");
public sealed record DataSet<T>(IReadOnlyList<T> Items, IReadOnlyList<SourceEvidence> Sources,
    IReadOnlyList<string> Limitations)
{
    // A failed read is different from a successful read containing no records.
    // Limitations can describe a valid dataset and are not an error flag.
    public bool ReadSucceeded { get; init; } = true;
}
public sealed record ExtractionIssue(string Component, string Message, string? Operation, int? ReturnCode);
public sealed record ModelData(ModelIdentity Identity, StoryData? Stories, DataSet<Joint>? Joints,
    DataSet<Frame>? Frames, DataSet<Area>? Areas, DataSet<Material>? Materials,
    DataSet<FrameSection>? FrameSections, DataSet<AreaSection>? AreaSections,
    IReadOnlyList<ExtractionIssue> Issues, bool IsConsistent, TimeSpan Duration)
{
    public bool IsComplete => IsConsistent && Issues.Count == 0 && Stories is not null &&
        Joints is not null && Frames is not null && Areas is not null && Materials is not null &&
        FrameSections is not null && AreaSections is not null &&
        new[] { Joints.Limitations.Count, Frames.Limitations.Count, Areas.Limitations.Count,
            Materials.Limitations.Count, FrameSections.Limitations.Count, AreaSections.Limitations.Count }.All(n => n == 0);
}


