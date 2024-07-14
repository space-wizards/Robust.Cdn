using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn;

/// <summary>
/// Manages storage for manifest server builds.
/// </summary>
/// <remarks>
/// For now this takes care of all the "<c>Path.Combine</c>" calls in the project.
/// In the future this should be expanded to other file access methods like cloud storage, if we want those.
/// </remarks>
public sealed class BuildDirectoryManager(IOptions<ManifestOptions> options)
{
    public string GetForkPath(string fork)
    {
        return Path.Combine(Path.GetFullPath(options.Value.FileDiskPath), fork);
    }

    public string GetBuildVersionPath(string fork, string version)
    {
        return Path.Combine(GetForkPath(fork), version);
    }

    public string GetBuildVersionFilePath(string fork, string version, string file)
    {
        return Path.Combine(GetBuildVersionPath(fork, version), file);
    }
}
