using System.Diagnostics;

public sealed class ModelExtractor(ILogger<ModelExtractor> logger)
{
    public ModelData Extract(IModelSession session, CancellationToken cancellation = default,
        Action<string>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var first = ExtractOnce(session, cancellation, progress);
        progress?.Invoke("Consistency verification (second read)");
        var second = ExtractOnce(session, cancellation);
        if (ModelConsistency.ContentFingerprint(first) != ModelConsistency.ContentFingerprint(second))
            throw new ModelChangedException("Model contents changed between consecutive reads. Discarding the extraction; retry while ETABS is idle.");
        var combinedIssues = first.Issues.Concat(second.Issues).Distinct().ToArray();
        return first with { Issues = combinedIssues, Duration = timer.Elapsed };
    }

    private ModelData ExtractOnce(IModelSession session, CancellationToken cancellation = default,
        Action<string>? progress = null)
    {
        var timer = Stopwatch.StartNew();
        var identity = session.ReadIdentity();
        VersionPolicy.Validate(identity.EtabsVersion);
        var issues = new List<ExtractionIssue>();

        T? Read<T>(string component, Func<T> read) where T : class
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ModelConsistency.SameContext(identity, session.ReadIdentity()))
                throw new ModelChangedException("Model identity, lock state, or units changed during extraction. Discard this extraction and retry.");
            progress?.Invoke(component);
            try { return read(); }
            catch (ApiException ex)
            {
                logger.LogError(ex, "Extraction failed for {Component}", component);
                issues.Add(new(component, ex.Message, ex.Operation, ex.ReturnCode));
                return null;
            }
        }

        var stories = Read("Stories", session.ReadStories);
        var joints = Read("Joints", session.ReadJoints);
        var frames = Read("Frames", session.ReadFrames);
        var areas = Read("Areas", session.ReadAreas);
        var materials = Read("Materials", session.ReadMaterials);
        var frameSections = Read("Frame sections", session.ReadFrameSections);
        var areaSections = Read("Area sections", session.ReadAreaSections);
        cancellation.ThrowIfCancellationRequested();
        if (!ModelConsistency.SameContext(identity, session.ReadIdentity()))
            throw new ModelChangedException("ETABS changed context during extraction. No combined model data may be used.");

        timer.Stop();
        logger.LogInformation("Extraction finished in {Milliseconds} ms; {IssueCount} API failures", timer.ElapsedMilliseconds, issues.Count);
        return new(identity, stories, joints, frames, areas, materials, frameSections, areaSections,
            issues, true, timer.Elapsed);
    }
}


