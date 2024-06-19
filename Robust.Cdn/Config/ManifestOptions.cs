namespace Robust.Cdn.Config;

public sealed class ManifestOptions
{
    public const string Position = "Manifest";

    public string FileDiskPath { get; set; } = "";

    public Dictionary<string, ManifestForkOptions> Forks { get; set; } = new();
}

public sealed class ManifestForkOptions
{
    public string? UpdateToken { get; set; }
}
