using System.Text.Json.Serialization;

namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Container for build manifest data.
/// </summary>
public sealed class ManifestBuildData
{
    /// <summary>
    /// Version publish time in UTC.
    /// </summary>
    [JsonPropertyName("Time")]
    public DateTime PublishTime { get; set; }

    /// <summary>
    /// Artifact manifest data for client side of build.
    /// </summary>
    public required ManifestArtifact Client { get; set; }

    /// <summary>
    /// Collection of manifest data for server side of build.
    /// Key is the server name, value is the manifest artifact for that server.
    /// </summary>
    public required Dictionary<string, ManifestArtifact> Server { get; set; }
}
