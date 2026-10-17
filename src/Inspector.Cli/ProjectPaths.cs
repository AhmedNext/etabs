using System.IO;

internal static class ProjectPaths
{
    private const string ConfigurationRelativePath = "config/project.asce7-16.json";

    public static string Root { get; } = FindRoot();

    public static string DefaultConfigurationPath => Path.Combine(Root, "src", ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Inspector.sln")))
                    return directory.FullName;
                if (File.Exists(Path.Combine(directory.FullName, "src", ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                    return directory.FullName;
                if (File.Exists(Path.Combine(directory.FullName, ConfigurationRelativePath.Replace('/', Path.DirectorySeparatorChar))) &&
                    string.Equals(directory.Name, "src", StringComparison.OrdinalIgnoreCase))
                    return directory.Parent?.FullName ?? directory.FullName;
                directory = directory.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
