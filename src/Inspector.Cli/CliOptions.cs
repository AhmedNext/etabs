
public sealed record CliOptions(string Command, bool Details, string? EvidencePath)
{
    public string Module { get; init; } = "all";
    public string ConfigurationPath { get; init; } = ProjectPaths.DefaultConfigurationPath;
    public bool ExplicitConfiguration { get; init; }
    public string? OutputPath { get; init; }
    public string? LocateTarget { get; init; }
    public bool LocateStory { get; init; }
    public bool Submission { get; init; }
    public string? ResultTablesPath { get; init; }
    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"] or ["help"])
            return new("help", false, null);
        if (args[0] == "run-analysis")
            throw new ConfigurationException("This inspector is read-only. Run analysis in ETABS when needed.");
        if (args[0] is not ("init-config" or "connect" or "extract" or "inspect" or "report" or "locate" or "analysis-status" or "analysis-results" or "run-analysis"))
            throw new ConfigurationException($"Unknown command '{args[0]}'. Run --help.");
        var module = "all"; int start = 1;
        string? locateTarget = null; bool locateStory = false;
        if (args[0] == "locate")
        {
            locateStory = args.Length > 1 && args[1] == "story";
            int targetIndex = locateStory ? 2 : 1;
            if (args.Length <= targetIndex || string.IsNullOrWhiteSpace(args[targetIndex]) || args[targetIndex].StartsWith("--", StringComparison.Ordinal))
                throw new ConfigurationException("Supported navigation: locate failed, locate ELEMENT_ID, locate story STORY_ID.");
            locateTarget = args[targetIndex]; start = targetIndex + 1;
        }
        if (args[0] == "inspect")
        {
            if (args.Length < 2 || args[1] is not ("all" or "geometry" or "loads" or "analysis" or "seismic" or "design" or "sanity" or "audit"))
                throw new ConfigurationException("Supported modules: inspect all, inspect geometry, inspect loads, inspect analysis, inspect seismic, inspect design, inspect sanity, inspect audit.");
            module = args[1]; start = 2;
        }
        bool details = false; string? evidencePath = null; bool submission = false;
        string? outputPath = null; bool formatSpecified = false;string? resultTablesPath=null;
        string configuration = ProjectPaths.DefaultConfigurationPath; bool explicitConfiguration = false;
        for (int i = start; i < args.Length; i++)
        {
            if(args[i]=="--result-tables"&&args[0] is "inspect" or "report"&&resultTablesPath is null&&i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal))
            {resultTablesPath=args[++i];continue;}
            if (args[i] == "--details" && !details) details = true;
            else if (args[i] == "--evidence" && evidencePath is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                evidencePath = args[++i];
            else if (args[i] == "--config" && args[0] is "init-config" or "inspect" or "locate" or "report" && !explicitConfiguration && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { configuration = args[++i]; explicitConfiguration = true; }
            else if (args[i] == "--format" && args[0] == "report" && !formatSpecified && i + 1 < args.Length)
            {
                if (args[++i] != "html") throw new ConfigurationException("Supported report format: html.");
                formatSpecified = true;
            }
            else if (args[i] == "--output" && args[0] == "report" && outputPath is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                outputPath = args[++i];
            else if (args[i] == "--submission" && args[0] == "report" && !submission) submission = true;
            else throw new ConfigurationException($"Unknown, duplicate, or incomplete option '{args[i]}'.");
        }
        if ((args[0] is "connect" or "analysis-status" or "run-analysis") && (details || evidencePath is not null || explicitConfiguration))
            throw new ConfigurationException("Options are not supported for this command.");
        if (args[0] == "locate" && (details || evidencePath is not null))
            throw new ConfigurationException("Navigation does not support --details or --evidence.");
        if (args[0] == "locate" && explicitConfiguration && (locateStory || locateTarget != "failed"))
            throw new ConfigurationException("--config is supported only for locate failed; element/story selection does not run inspection checks.");
        if (args[0] == "report" && (details || evidencePath is not null))
            throw new ConfigurationException("report supports --format html, --output, --config and --submission only.");
        if (args[0] == "init-config" && (details || evidencePath is not null || !Path.GetExtension(configuration).Equals(".json", StringComparison.OrdinalIgnoreCase)))
            throw new ConfigurationException("init-config supports only --config FILE.json for the generated configuration location.");
        if (outputPath is not null && (string.IsNullOrWhiteSpace(outputPath) || !Path.GetExtension(outputPath).Equals(".html", StringComparison.OrdinalIgnoreCase)))
            throw new ConfigurationException("HTML report output must use a .html file path.");
        if (evidencePath is not null && !string.Equals(Path.GetExtension(evidencePath), ".json", StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException("Extraction evidence must use a .json output file.");
        return new(args[0], details, evidencePath) { Module = module, ConfigurationPath = configuration, ExplicitConfiguration = explicitConfiguration, OutputPath = outputPath, LocateTarget = locateTarget, LocateStory = locateStory, Submission = submission,ResultTablesPath=resultTablesPath };
    }
}


