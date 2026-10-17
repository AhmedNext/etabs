
public sealed record AnalysisEvidenceTable(string Key, IReadOnlyList<string> Fields,
    IReadOnlyList<IReadOnlyDictionary<string,string>> Rows, string? UnavailableReason = null)
{
    public string? RecoverySource { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
}
public sealed record ModifierEvidence(string Kind, string Name, IReadOnlyList<double> Values);
public sealed record ModeShapeEvidence(string Joint, string Element, string Case, string StepType, double Mode,
    double UX, double UY, double UZ, double RX, double RY, double RZ);
public sealed record FrameForceEvidence(string Frame, double Station, string Element, double ElementStation,
    string Case, string StepType, double Step, double P, double V2, double V3, double T, double M2, double M3);
public sealed record ExtendedAnalysisEvidence(IReadOnlyList<AnalysisEvidenceTable> Tables,
    IReadOnlyList<ModifierEvidence> Modifiers, IReadOnlyList<ModeShapeEvidence> ModeShapes,
    IReadOnlyList<FrameForceEvidence> FrameForces, string? ConcreteCode, bool? ConcreteResultsAvailable,
    IReadOnlyList<ExtractionIssue> Issues);


