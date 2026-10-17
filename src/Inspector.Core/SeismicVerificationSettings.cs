
public sealed record SeismicAxis(string Direction, string ElasticCase, string? SpectrumCase = null);
// Gravity factors are explicit calculation inputs, not a mass-source definition.
public sealed record GravityTerm(string Case, double Factor);
public sealed record SeismicVerificationSettings(string Standard, double Cd, double Ie,
    double DriftLimit, double SpectrumMinimumRatio, double Beta,
    IReadOnlyList<SeismicAxis> Axes, IReadOnlyList<GravityTerm> GravityLoads,
    bool GravityBasisConfirmed = false);
public sealed record StoryForceResult(string Story, string Case, string StepType, double StepNumber,
    string Location, double P, double VX, double VY);


