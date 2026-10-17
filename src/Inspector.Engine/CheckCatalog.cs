
public static class CheckCatalog
{
    public static IReadOnlyList<IInspectionCheck> Implemented { get; } = GeometryChecks.Create()
        .Concat(LoadingChecks.Create()).Concat(AnalysisChecks.Create()).Concat(IndependentAnalysisChecks.Create()).Concat(SeismicChecks.Create()).Concat(ColumnChecks.Create()).Concat(BeamChecks.Create()).Concat(WallChecks.Create()).Concat(DetailingChecks.Create()).Concat(OptimizationChecks.Create()).Concat(SanityChecks.Create()).Concat(AuditChecks.Create()).ToArray();
    public static IReadOnlyList<CheckDefinition> All { get; } = Build();
    private static IReadOnlyList<CheckDefinition> Build()
    {
        var implemented = Implemented.Select(c => c.Definition).ToDictionary(d => d.Id);
        for (int i = 1; i <= 15; i++) Add($"GEO-{i:000}", "GEO");
        for (int i = 1; i <= 14; i++) Add($"LOAD-{i:000}", "LOAD");
        for (int i = 1; i <= 15; i++) if(i is not (5 or 6 or 10 or 11)) Add($"ANA-{i:000}", "ANA");
        for (int i = 1; i <= 20; i++) if(i is not (9 or 20)) Add($"SEIS-{i:000}", "SEIS");
        for (int i = 1; i <= 3; i++) Add($"OPT-{i:000}", "OPT");
        for (int i = 1; i <= 8; i++) Add($"SAN-{i:000}", "SAN");
        for (int i = 1; i <= 4; i++) Add($"AUD-{i:000}", "AUD");
        return implemented.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
        void Add(string id, string module)
        {
            if (!implemented.ContainsKey(id)) implemented.Add(id, new(id, module, "PRD catalog check awaiting implementation",
                "No verified method is implemented.", [], VerificationState.NOT_IMPLEMENTED));
        }
    }
}


