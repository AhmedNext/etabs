
public sealed record Phase3ReviewSettings
{
    public bool ReferencesConfirmed { get; init; }
    public string? ReferenceSource { get; init; }
    public string? ModalCase { get; init; }
    public double? MinimumModalParticipation { get; init; }
    public double? MaximumJointDisplacement { get; init; }
    public IReadOnlyList<string> DisplacementCases { get; init; } = [];
    public IReadOnlyList<string> DisplacementDirections { get; init; } = [];
    public IReadOnlyList<CombinationReference> RequiredSeismicCombinations { get; init; } = [];
    public double? MemberForceOutlierRatio { get; init; }
    public double? MaximumFrameForceJump { get; init; }
    public bool TorsionEnvelopeConfirmed { get; init; }
    public double? MaximumDiaphragmFlexibilityRatio { get; init; }
    public double? DiaphragmFlexibilityRatio { get; init; }
    public string? ExpectedConcreteCode { get; init; }
}

public sealed record CombinationReference(string Name,IReadOnlyList<CaseLoad> Terms);


