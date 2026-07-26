namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Build version metadata.
/// </summary>
public sealed class VersionMetadata
{
    public VersionMetadata(string version, string engineVersion, SourceVersionInfo buildVersionInfo, SourceVersionInfo engineSourceVersionInfo)
    {
        Version = version;
        EngineVersion = engineVersion;
        BuildVersionInfo = buildVersionInfo;
        EngineSourceVersionInfo = engineSourceVersionInfo;
    }

    public VersionMetadata(
        string version,
        string engineVersion,
        string? sourceUrl,
        string? sourceCommitId,
        string? sourceBranchName,
        string? engineSourceUrl,
        string? engineSourceCommitId,
        string? engineSourceBranchName
    )
    {
        Version = version;
        EngineVersion = engineVersion;
        BuildVersionInfo = new SourceVersionInfo(sourceUrl, sourceCommitId, sourceBranchName);
        EngineSourceVersionInfo = new SourceVersionInfo(engineSourceUrl, engineSourceCommitId, engineSourceBranchName);
    }

    /// <summary>
    /// Human-readable version of the build. This is used to identify the build in the CDN and in the game client.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Human-readable version of the engine used to build this version.
    /// </summary>
    public string EngineVersion { get; }

    /// <summary>
    /// Version info for sources, used for build.
    /// </summary>
    public SourceVersionInfo BuildVersionInfo { get; }

    /// <summary>
    /// Version info for sources of engine, used for build.
    /// </summary>
    public SourceVersionInfo EngineSourceVersionInfo { get; }
}