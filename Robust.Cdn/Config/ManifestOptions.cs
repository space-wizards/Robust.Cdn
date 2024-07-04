namespace Robust.Cdn.Config;

public sealed class ManifestOptions
{
    public const string Position = "Manifest";

    /// <summary>
    /// File path for the database to store data in for the manifest system.
    /// </summary>
    public string DatabaseFileName { get; set; } = "manifest.db";

    public string FileDiskPath { get; set; } = "";

    public Dictionary<string, ManifestForkOptions> Forks { get; set; } = new();
}

public sealed class ManifestForkOptions
{
    public string? UpdateToken { get; set; }

    /// <summary>
    /// The name of client zip files in the directory structure, excluding the ".zip" extension.
    /// </summary>
    public string ClientZipName { get; set; } = "SS14.Client";

    public string ServerZipName { get; set; } = "SS14.Server_";
}
