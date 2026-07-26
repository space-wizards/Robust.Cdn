namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Build version info for artifacts.
/// </summary>
public sealed class BuildVersionArtifactInfo
{
    /// <summary>
    /// Human-readable build version number.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Publish time of this build version.
    /// </summary>
    public required DateTime PublishedTime { get; init; }

    /// <summary>
    /// Engine version, used for this build version.
    /// </summary>
    public required string? EngineVersion { get; init; }

    /// <summary>
    /// Array of server artifact info for this build version.
    /// </summary>
    public required BuildVersionServerArtifactInfo[] Servers { get; init; }
}
