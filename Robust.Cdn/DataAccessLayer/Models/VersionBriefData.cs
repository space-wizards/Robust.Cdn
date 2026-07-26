namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Short data for build version.
/// </summary>
public sealed class VersionBriefData
{
    /// <summary>
    /// Build version id.
    /// </summary>
    public required int Id { get; set; }

    /// <summary>
    /// Human-readable build version number.
    /// </summary>
    public required string Name { get; set; }
}
