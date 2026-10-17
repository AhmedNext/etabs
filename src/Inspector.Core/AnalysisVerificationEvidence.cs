public sealed record AnalysisVerificationEvidence(IReadOnlyList<AnalysisTableSnapshot> Inventory,
    IReadOnlyList<AnalysisEvidenceTable> Tables, bool InventoryComplete, IReadOnlyList<ExtractionIssue> Issues)
{
    public DataSet<GravityLoadAssignment>? GravityLoads { get; init; }
    public bool SimpleFrameWeightGeometryVerified { get; init; }
    public IReadOnlyList<string> GeometryLimitations { get; init; } = [];
}
public sealed record GravityLoadAssignment(string ObjectType, string ObjectId, string Pattern, int LoadType,
    string CoordinateSystem, int Direction, double Start, double End, double ValueStart, double ValueEnd);
