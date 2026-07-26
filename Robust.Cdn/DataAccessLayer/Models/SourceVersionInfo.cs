namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Detailed info on sources used for building version.
/// </summary>
/// <param name="SourceUrl">URL for repository that holds sources.</param>
/// <param name="CommitId">Commit ID used for building sources.</param>
/// <param name="BranchName">Branch name or tag, used for building sources.</param>
public record SourceVersionInfo(string? SourceUrl, string? CommitId, string? BranchName);