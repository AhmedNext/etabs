
public enum CapabilityState { SUPPORTED, UNSUPPORTED, UNTESTED, FAILED }

public sealed record ApiCapability(string Id, IReadOnlyList<string> Features, string Api,
    string MinimumRequiredVersion, string? VerifiedTargetVersion, string InputRequirements,
    IReadOnlyList<string> ResultFields, string Limitations, CapabilityState State, string TestStatus);


