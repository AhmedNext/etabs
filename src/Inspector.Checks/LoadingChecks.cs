
public static class LoadingChecks
{
    public static IReadOnlyList<IInspectionCheck> Create() =>
    [
        Rule.Create("CFG-001", "Project assumptions", "Project-specific criteria are usable only from a schema-valid configuration explicitly marked confirmed.", [], Configuration),
        Rule.Create("MAT-001", "Material definition consistency", "Physical elastic/strength values must be positive where applicable; density zero is a modeling review, negative density is invalid.", ["Materials"], Materials),
        Rule.Create("LOAD-001", "Expected load patterns", "Compare actual exact pattern identifiers with confirmed project expectations; no default dead/live names are inferred.", ["LoadPatterns"], ExpectedPatterns),
        Rule.Create("LOAD-002", "Self-weight multipliers", "Compare confirmed pattern self-weight factors using relative numerical tolerance 1e-9; unconfigured negative factors or multiple nonzero patterns are screening warnings only.", ["LoadPatterns"], SelfWeight),
        Rule.Create("LOAD-006", "Load pattern classifications", "Compare actual API classifications with confirmed project type expectations, never infer type from names.", ["LoadPatterns"], PatternTypes),
        Rule.Create("LOAD-007", "Required combinations", "Compare exact combination names with confirmed project requirements; no code combinations are synthesized.", ["Combinations"], RequiredCombinations),
        Rule.Create("LOAD-008", "Combination references", "Check case/combo references and detect recursive cycles; negative factors are valid, empty or all-zero combinations need review.", ["Combinations", "LoadCases"], Combinations),
        Rule.Create("LOAD-009", "Mass source definitions", "Validate source selection flags, default count, active pattern references and finite nonnegative source factors; no seismic weight compliance is inferred.", ["MassSources", "LoadPatterns"], MassSources),
        Rule.Create("LOAD-010", "Potential duplicated self mass", "Screen simultaneous element self mass and positive weighted pattern self-weight contributions; added mass is not assumed duplicated.", ["MassSources", "LoadPatterns"], DuplicateMass),
        Rule.Create("LOAD-013", "Linear static case references", "Validate Load references to defined patterns and Accel direction names in linear static cases; other case definitions remain unverified.", ["LoadCases", "LoadPatterns"], Cases),
        Rule.Create("LOAD-014", "Required analysis cases", "Check only case or combination names explicitly required by confirmed inspection settings.", ["LoadCases"], RequiredCases)
    ];
    private static IReadOnlyList<Observation> Configuration(InspectionContext c) =>
        !c.Configuration.WasFound ? [Rule.NotVerified("project.json was not found; only independent definition/numerical screening checks can be verified.")] :
        !c.Configuration.Settings.Confirmed ? [Rule.NotVerified("Project criteria are unconfirmed; set confirmed=true only after engineering review of the supplied assumptions.")] :
        [new(CheckStatus.PASS, "Project configuration is schema-valid and explicitly confirmed; code-dependent checks remain outside Phase 2.",
            Evidence: [new("Configuration SHA-256", c.Configuration.Path ?? "project.json", c.Configuration.Sha256)])];
    private static IReadOnlyList<Observation> Materials(InspectionContext c)
    {
        List<Observation> results = [];
        foreach (var material in c.Input.Model.Materials!.Items)
        {
            var source = new Evidence("Material properties", "PropMaterial definition getters", material.ToString());
            if (material.UnitWeight < 0 || material.MassDensity < 0)
                results.Add(new(CheckStatus.FAIL, "Material weight/mass density is negative.", material.Name, "Material", Evidence: [source]));
            else if (material.UnitWeight == 0 || material.MassDensity == 0)
                results.Add(new(CheckStatus.WARNING, "Material has zero weight or mass density; verify intended weightless modeling.", material.Name, "Material", Evidence: [source]));
            if (material.ElasticModulus is null)
                results.Add(new(CheckStatus.NOT_VERIFIED, "Material elastic modulus is unavailable for consistency validation.", material.Name, "Material"));
            else if (material.ElasticModulus <= 0)
                results.Add(new(CheckStatus.WARNING, "Material elastic modulus is nonpositive; review intended stiffness modeling.", material.Name, "Material", Evidence: [source]));
            if (material.Type == "Concrete" && material.ConcreteStrength is not > 0)
                results.Add(new(material.ConcreteStrength is null ? CheckStatus.NOT_VERIFIED : CheckStatus.FAIL,
                    "Concrete compressive strength is missing or nonpositive.", material.Name, "Material", Actual: material.ConcreteStrength,
                    Limit: 0, Unit: c.Input.Model.Identity.DisplayUnits.Stress, Evidence: [source]));
            if (material.Type == "Rebar" && (material.RebarYieldStrength is not > 0 || material.RebarUltimateStrength is not > 0 || material.RebarUltimateStrength < material.RebarYieldStrength))
                results.Add(new(material.RebarYieldStrength is null || material.RebarUltimateStrength is null ? CheckStatus.NOT_VERIFIED : CheckStatus.FAIL,
                    "Rebar strengths are missing, nonpositive, or ultimate strength is below yield strength.", material.Name, "Material", Evidence: [source]));
        }
        return results.Count > 0 ? results : c.Input.Model.Materials.Items.Count == 0 ? [Rule.NotApplicable("No material definitions are available.")] :
            [Rule.Pass("Extracted material densities and applicable strengths satisfy definition consistency criteria; code suitability is not evaluated.")];
    }
    private static IReadOnlyList<Observation> ExpectedPatterns(InspectionContext c)
    {
        var expected = c.Configuration.Settings.Loading?.ExpectedPatterns;
        if (!c.Configuration.Settings.Confirmed || expected is null || expected.Count == 0)
            return [Rule.NotVerified("Confirmed expected gravity/load pattern identifiers are required; pattern names are not assumed.")];
        var actual = c.Input.Validation.LoadPatterns!.Items.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(p => !actual.Contains(p.Name)).Select(p => new Observation(CheckStatus.FAIL,
            "Confirmed expected load pattern is missing.", p.Name, "LoadPattern",
            Evidence: [new("Required pattern", c.Configuration.Path ?? "project.json", p.Name)])).ToArray();
        return missing.Length == 0 ? [Rule.Pass("All confirmed expected load pattern names exist.")] : missing;
    }
    private static IReadOnlyList<Observation> SelfWeight(InspectionContext c)
    {
        var patterns = c.Input.Validation.LoadPatterns!.Items;
        var expected = c.Configuration.Settings.Confirmed ? (c.Configuration.Settings.Loading?.ExpectedPatterns ?? []).Where(p => p.SelfWeightMultiplier is not null).ToArray() : [];
        List<Observation> results = [];
        if (expected.Length == 0)
        {
            results.Add(Rule.NotVerified("No confirmed self-weight multiplier expectations were supplied; no compliance conclusion is available."));
            foreach (var p in patterns.Where(p => p.SelfWeightMultiplier < 0)) results.Add(new(CheckStatus.WARNING,
                "Negative self-weight multiplier requires review of load direction and modeling intent.", p.Name, "LoadPattern", Actual: p.SelfWeightMultiplier, Unit: "dimensionless"));
            var nonzero = patterns.Where(p => p.SelfWeightMultiplier != 0).ToArray();
            if (nonzero.Length > 1) results.Add(new(CheckStatus.WARNING, "Multiple patterns include self weight; review actual case/combination use before concluding duplication.",
                Evidence: [new("Patterns with self weight", "LoadPatterns.GetSelfWTMultiplier", string.Join(", ", nonzero.Select(p => p.Name + "=" + p.SelfWeightMultiplier.ToString("G17", CultureInfo.InvariantCulture))))]));
            return results;
        }
        foreach (var item in expected)
        {
            var p = patterns.FirstOrDefault(p => p.Name == item.Name);
            if (p is null) { results.Add(new(CheckStatus.NOT_VERIFIED, "Expected pattern is absent; its self-weight factor cannot be compared.", item.Name, "LoadPattern")); continue; }
            var limit = item.SelfWeightMultiplier!.Value;
            if (Math.Abs(p.SelfWeightMultiplier - limit) > 1e-9 * Math.Max(1, Math.Abs(limit))) results.Add(new(CheckStatus.FAIL,
                "Self-weight multiplier differs from the confirmed project expectation.", p.Name, "LoadPattern", Actual: p.SelfWeightMultiplier, Limit: limit,
                Unit: "dimensionless", Evidence: [new("Expected multiplier", c.Configuration.Path ?? "project.json", limit.ToString("G17", CultureInfo.InvariantCulture))]));
        }
        return results.Count == 0 ? [Rule.Pass("All explicitly configured pattern self-weight factors match confirmed expectations within numerical tolerance.")] : results;
    }
    private static IReadOnlyList<Observation> PatternTypes(InspectionContext c)
    {
        var expected = c.Configuration.Settings.Confirmed ? (c.Configuration.Settings.Loading?.ExpectedPatterns ?? []).Where(p => p.Type is not null).ToArray() : [];
        if (expected.Length == 0) return [Rule.NotVerified("Confirmed pattern classifications are required; names alone do not establish intended load type.")];
        List<Observation> results = [];
        foreach (var item in expected)
        {
            var p = c.Input.Validation.LoadPatterns!.Items.FirstOrDefault(p => p.Name == item.Name);
            if (p is null) results.Add(new(CheckStatus.NOT_VERIFIED, "Expected load pattern is absent; classification cannot be compared.", item.Name, "LoadPattern"));
            else if (p.Type != item.Type) results.Add(new(CheckStatus.FAIL, "Load pattern classification differs from the confirmed expectation.", p.Name, "LoadPattern",
                Evidence: [new("Actual classification", "LoadPatterns.GetLoadType", p.Type), new("Expected classification", c.Configuration.Path ?? "project.json", item.Type)]));
        }
        return results.Count == 0 ? [Rule.Pass("All explicitly configured load classifications match the confirmed project expectations.")] : results;
    }
    private static IReadOnlyList<Observation> RequiredCombinations(InspectionContext c)
    {
        var required = c.Configuration.Settings.Loading?.RequiredCombinations;
        if (!c.Configuration.Settings.Confirmed || required is null || required.Count == 0)
            return [Rule.NotVerified("Confirmed required combination identifiers are missing; code combination adequacy is not inferred.")];
        var names = c.Input.Validation.Combinations!.Items.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(n => !names.Contains(n)).Select(n => new Observation(CheckStatus.FAIL, "Confirmed required load combination is absent.", n, "Combination",
            Combination: n, Evidence: [new("Required combination", c.Configuration.Path ?? "project.json", n)])).ToArray();
        return missing.Length == 0 ? [Rule.Pass("All confirmed required load combination names exist; their design/code adequacy is not checked.")] : missing;
    }
    private static IReadOnlyList<Observation> Combinations(InspectionContext c)
    {
        var combinations = c.Input.Validation.Combinations!.Items.ToDictionary(x => x.Name); var cases = c.Input.Validation.LoadCases!.Items.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        List<Observation> results = [];
        foreach (var combo in combinations.Values.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            if (combo.Type is < 0 or > 4) results.Add(new(CheckStatus.NOT_VERIFIED, "Unknown combination type; structure interpretation is unverified.", combo.Name, "Combination", Combination: combo.Name));
            if (combo.Terms.Count == 0 || combo.Terms.All(t => t.Factor == 0)) results.Add(new(CheckStatus.WARNING, "Combination is empty or all factors are zero; review its intended use.", combo.Name, "Combination", Combination: combo.Name));
            foreach (var term in combo.Terms)
                if (term.Type is not ("LoadCase" or "LoadCombo") || (term.Type == "LoadCase" ? !cases.Contains(term.Name) : !combinations.ContainsKey(term.Name)))
                    results.Add(new(CheckStatus.FAIL, "Combination contains an undefined case/combo reference or unknown member type.", combo.Name + "/" + term.Type + ":" + term.Name, "Combination", Combination: combo.Name,
                        Evidence: [new("Member", "RespCombo.GetCaseList_1", term.ToString())]));
        }
        // Iterative topological removal avoids recursion overflow on deeply nested models.
        var incoming = combinations.ToDictionary(p => p.Key, _ => 0, StringComparer.Ordinal);
        foreach (var combo in combinations.Values)
            foreach (var term in combo.Terms.Where(t => t.Type == "LoadCombo" && combinations.ContainsKey(t.Name))) incoming[term.Name]++;
        var queue = new Queue<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key).Order(StringComparer.Ordinal));
        while (queue.TryDequeue(out var name))
            foreach (var term in combinations[name].Terms.Where(t => t.Type == "LoadCombo" && combinations.ContainsKey(t.Name)))
                if (--incoming[term.Name] == 0) queue.Enqueue(term.Name);
        var cyclicOrDependent = incoming.Where(p => p.Value > 0).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        if (cyclicOrDependent.Length > 0) results.Add(new(CheckStatus.FAIL, "Combination dependency graph contains a recursive cycle; listed members are cyclic or reachable from a cycle.",
            "CombinationGraph", "Combination", Evidence: [new("Unresolved graph nodes", "RespCombo.GetCaseList_1", string.Join(", ", cyclicOrDependent))]));
        return results.Count > 0 ? results : combinations.Count == 0 ? [Rule.NotApplicable("No combinations are defined.")] : [Rule.Pass("Combination references exist and the dependency graph is acyclic; negative factors are retained and code adequacy is not evaluated.")];
    }
    private static IReadOnlyList<Observation> MassSources(InspectionContext c)
    {
        if (c.Input.Validation.MassSources!.Limitations.Count > 0) return [Rule.NotVerified(string.Join(" ", c.Input.Validation.MassSources.Limitations))];
        var sources = c.Input.Validation.MassSources!.Items; var patterns = c.Input.Validation.LoadPatterns!.Items.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (sources.Count == 0)
        {
            var cases = c.Input.Validation.LoadCases;
            if (cases is null || !cases.ReadSucceeded || cases.Limitations.Count > 0)
                return [Rule.NotVerified("No mass sources were returned. Case data could not be read, so the need for mass could not be checked.")];
            var usedLoads = cases.Items.SelectMany(c => c.Loads ?? []).Where(l => l.Factor != 0).ToArray();
            var activeQuakePatterns = c.Input.Validation.LoadPatterns!.Items.Where(p => p.Type == "Quake" && usedLoads.Any(l => l.Type == "Load" && l.Name == p.Name)).ToArray();
            var needsMass = cases.Items.Any(x => x.Type is "Modal" or "ResponseSpectrum" or "LinearHistory" or "NonlinearHistory" or "TimeHistory") ||
                usedLoads.Any(l => l.Type == "Accel") || activeQuakePatterns.Any(p => !string.IsNullOrWhiteSpace(p.AutoSeismicCode));
            if (!needsMass && activeQuakePatterns.Any(p => p.AutoSeismicCode is null))
                return [Rule.NotVerified("No mass sources were returned. Auto-seismic settings could not be read, so the need for mass could not be confirmed.")];
            return needsMass ? [new Observation(CheckStatus.FAIL, "The defined dynamic, acceleration, or auto-seismic loads need mass, but no mass source was returned.") { Kind = FindingKind.MissingInput }]
                : [Rule.NotApplicable("No mass source is defined. The defined static cases do not require a dynamic mass source.")];
        }
        List<Observation> results = [];
        if (sources.Count(s => s.IsDefault) != 1) results.Add(new(CheckStatus.WARNING, "Mass source default selection is not unique; review case-specific source use.", Actual: sources.Count(s => s.IsDefault), Limit: 1, Unit: "sources"));
        foreach (var source in sources)
        {
            if (!source.IncludeElementMass && !source.IncludeAddedMass && !source.IncludeLoads) results.Add(new(CheckStatus.WARNING, "Mass source has all contribution flags disabled.", source.Name, "MassSource"));
            if (!source.IncludeLoads) continue;
            if (source.Loads.Count == 0) results.Add(new(CheckStatus.WARNING, "Load-derived mass is enabled without pattern contributions.", source.Name, "MassSource"));
            foreach (var load in source.Loads)
            {
                if (!patterns.Contains(load.Pattern)) results.Add(new(CheckStatus.FAIL, "Mass source references an undefined active load pattern.", source.Name + "/" + load.Pattern, "MassSource",
                    Evidence: [new("Active pattern", "Mass Source Definition", load.Pattern)]));
                else if (load.Factor < 0) results.Add(new(CheckStatus.WARNING, "Mass source load multiplier is negative; review intentional mass deductions and solver treatment.", source.Name + "/" + load.Pattern, "MassSource", Actual: load.Factor, Unit: "dimensionless"));
            }
            if (source.Loads.Select(l => l.Pattern).Distinct().Count() != source.Loads.Count)
                results.Add(new(CheckStatus.WARNING, "Mass source repeats a load pattern; review all contribution rows before concluding duplication.", source.Name, "MassSource"));
        }
        return results.Count == 0 ? [Rule.Pass("Mass source flags/default selection and active pattern references satisfy definition checks; seismic weight and added mass are not verified.")] : results;
    }
    private static IReadOnlyList<Observation> DuplicateMass(InspectionContext c)
    {
        if (c.Input.Validation.MassSources!.Limitations.Count > 0) return [Rule.NotVerified(string.Join(" ", c.Input.Validation.MassSources.Limitations))];
        var patterns = c.Input.Validation.LoadPatterns!.Items.ToDictionary(p => p.Name); List<Observation> results = [];
        if (c.Input.Validation.MassSources!.Items.Count == 0) return [Rule.NotApplicable("No mass sources are defined, so there are no mass contributions to compare.")];
        foreach (var source in c.Input.Validation.MassSources.Items)
        {
            if (!source.IncludeElementMass || !source.IncludeLoads) continue;
            if (source.Loads.Any(l => !patterns.ContainsKey(l.Pattern))) { results.Add(new(CheckStatus.NOT_VERIFIED, "Unknown active mass pattern prevents overlap screening.", source.Name, "MassSource")); continue; }
            var overlaps = source.Loads.Where(l => l.Factor * patterns[l.Pattern].SelfWeightMultiplier > 0).ToArray();
            if (overlaps.Length > 0) results.Add(new(CheckStatus.WARNING, "Element self mass and load-derived self weight overlap; review possible double counting.", source.Name, "MassSource",
                Actual: overlaps.Sum(l => l.Factor * patterns[l.Pattern].SelfWeightMultiplier), Unit: "dimensionless self-weight contribution",
                Evidence: [new("Include element mass", "Mass Source Definition", "true"),
                    new("Positive self-weight contributions", "Mass Source Definition + LoadPatterns.GetSelfWTMultiplier",
                        string.Join(", ", overlaps.Select(l => l.Pattern + ": " + l.Factor.ToString("G17", CultureInfo.InvariantCulture) + " * " + patterns[l.Pattern].SelfWeightMultiplier.ToString("G17", CultureInfo.InvariantCulture))))]));
        }
        return results.Count == 0 ? [Rule.Pass("No simultaneous element-self-mass and positive load-self-weight overlap was found; applied/added mass duplication remains outside this screen.")] : results;
    }
    private static IReadOnlyList<Observation> RequiredCases(InspectionContext c)
    {
        var settings = c.Configuration.Settings;
        if (!settings.Confirmed)
            return [new Observation(CheckStatus.NOT_VERIFIED, "Required case names have not been confirmed. Case completeness was not checked.") { Kind = FindingKind.IncompleteConfiguration }];
        var requests = new List<(string Name, bool CaseOnly)>();
        requests.AddRange((settings.Analysis?.DriftCases ?? []).Concat(settings.Analysis?.DisplacementCases ?? []).Select(n => (n, false)));
        if (!string.IsNullOrWhiteSpace(settings.Analysis?.ModalCase)) requests.Add((settings.Analysis.ModalCase, true));
        foreach (var axis in settings.SeismicVerification?.Axes ?? [])
        {
            requests.Add((axis.ElasticCase, true));
            if (!string.IsNullOrWhiteSpace(axis.SpectrumCase)) requests.Add((axis.SpectrumCase, true));
        }
        requests.AddRange((settings.SeismicVerification?.GravityLoads ?? []).Select(g => (g.Case, true)));
        if (requests.Count == 0) return [Rule.NotApplicable("No analysis case names are required by the inspection settings.")];
        var cases = c.Input.Validation.LoadCases!.Items.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var combos = c.Input.Validation.Combinations;
        List<Observation> findings = [];
        foreach (var request in requests.Distinct().OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            if (cases.Contains(request.Name) || !request.CaseOnly && combos?.Items.Any(x => x.Name == request.Name) == true) continue;
            if (!request.CaseOnly && combos?.ReadSucceeded != true)
                findings.Add(new Observation(CheckStatus.NOT_VERIFIED, "A required result name is not a case, and combination data could not be read.", request.Name, "LoadCase") { Kind = FindingKind.ApiUnavailable });
            else
                findings.Add(new Observation(CheckStatus.FAIL, "A case required by the confirmed inspection settings is missing.", request.Name, "LoadCase",
                    Evidence: [new("Required case", c.Configuration.Path ?? "inspection settings", request.Name)]) { Kind = FindingKind.MissingInput });
        }
        return findings.Count > 0 ? findings : [Rule.Pass("All case names required by the confirmed inspection settings exist.")];
    }

    private static IReadOnlyList<Observation> Cases(InspectionContext c)
    {
        var patterns = c.Input.Validation.LoadPatterns!.Items.Select(p => p.Name).ToHashSet(StringComparer.Ordinal); List<Observation> results = [];
        foreach (var group in c.Input.Validation.LoadCases!.Items.Where(x => x.Type != "LinearStatic" || x.Loads is null).GroupBy(x => x.Type))
            results.Add(new Observation(CheckStatus.NOT_VERIFIED, $"Detailed load definitions for {group.Count()} {group.Key} cases could not be checked through this API.",
                Evidence: [new("Affected cases", "Load case definitions", string.Join(", ", group.Select(x => x.Name)))]) { Kind = FindingKind.ApiUnavailable });
        foreach (var loadCase in c.Input.Validation.LoadCases!.Items.Where(x => x.Type == "LinearStatic" && x.Loads is not null))
        {
            foreach (var load in loadCase.Loads!)
            {
                if (load.Type == "Load" && !patterns.Contains(load.Name)) results.Add(new(CheckStatus.FAIL, "Linear static case references an undefined load pattern.", loadCase.Name + "/" + load.Name, "LoadCase", Combination: loadCase.Name));
                else if (load.Type == "Accel" && load.Name is not ("UX" or "UY" or "UZ" or "RX" or "RY" or "RZ")) results.Add(new(CheckStatus.FAIL, "Linear static acceleration direction is unknown.", loadCase.Name + "/" + load.Name, "LoadCase"));
                else if (load.Type is not ("Load" or "Accel")) results.Add(new(CheckStatus.NOT_VERIFIED, "Unknown linear static load component type.", loadCase.Name + "/" + load.Name, "LoadCase"));
            }
            if (loadCase.Loads.Count == 0) results.Add(new(CheckStatus.WARNING, "Linear static case contains no loads.", loadCase.Name, "LoadCase"));
        }
        return results.Count > 0 ? results : c.Input.Validation.LoadCases.Items.Count == 0 ? [Rule.NotApplicable("No load cases are defined.")] : [Rule.Pass("Linear static load component references are valid; no case analysis results were required.")];
    }
}


