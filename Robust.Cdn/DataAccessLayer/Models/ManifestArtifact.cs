using System.Text.Json.Serialization;

namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Container for build artifact manifest data.
/// </summary>
public sealed class ManifestArtifact
{
    /// <summary>
    /// Url for artifact download.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// Hash of artifact file.
    /// </summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// Artifact file size in bytes.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; set; }
}
