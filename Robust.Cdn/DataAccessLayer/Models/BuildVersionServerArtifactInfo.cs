namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Server information for a specific build version artifact.
/// </summary>
public sealed class BuildVersionServerArtifactInfo
{
    /// <summary>
    /// Target platform.
    /// </summary>
    public required string Platform { get; init; }

    /// <summary>
    /// File name of artifact.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long? FileSize { get; init; }
}
