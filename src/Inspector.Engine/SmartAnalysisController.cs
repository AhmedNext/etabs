
public sealed record AnalysisRunResult(bool WasStarted, string Message);

/// <summary>Compatibility entry point. Inspection never starts analysis.</summary>
public sealed class SmartAnalysisController
{
    public AnalysisRunResult Run(IAnalysisSession session, AnalysisAuthorization authorization,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellation.ThrowIfCancellationRequested();
        return new(false, "This inspector is read-only. Run analysis in ETABS when needed.");
    }
}


