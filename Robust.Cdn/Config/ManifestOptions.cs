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

    public ManifestForkNotifyWatchdog[] NotifyWatchdogs { get; set; } = [];

    public bool Private { get; set; } = false;

    public Dictionary<string, string> PrivateUsers { get; set; } = new();
}

public sealed class ManifestForkNotifyWatchdog
{
    public required string WatchdogUrl { get; set; }
    public required string Instance { get; set; }
    public required string ApiToken { get; set; }
}
