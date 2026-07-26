namespace Robust.Cdn.DataAccessLayer.Models;

public sealed class ArtifactBriefInfo
{
    /// <summary>
    /// Marker, if artifact is client or server part.
    /// </summary>
    public ArtifactType Type { get; set; }

    /// <summary>
    /// Target platform for the artifact.
    /// </summary>
    public string? Platform { get; set; }
}
