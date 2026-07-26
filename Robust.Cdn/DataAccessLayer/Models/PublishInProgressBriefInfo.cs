namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Container for publish-in-progress info.
/// </summary>
/// <param name="Id">Publish process id.</param>
/// <param name="Version">Human-readable fork build version number.</param>
/// <param name="ForkName">Fork name.</param>
/// <param name="StartTime">Publish start time (utc-based).</param>
public sealed record PublishInProgressBriefInfo(int Id, string Version, string ForkName, DateTime StartTime);
