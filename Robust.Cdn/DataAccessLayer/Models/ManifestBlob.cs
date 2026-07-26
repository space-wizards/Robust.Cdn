namespace Robust.Cdn.DataAccessLayer.Models;

/// <summary>
/// Container for manifest stream info.
/// </summary>
public class ManifestBlob(Stream blob, string manifestHash) : IDisposable
{
    /// <summary>
    /// Blob of manifest data.
    /// </summary>
    public Stream Blob { get; } = blob;

    /// <summary>
    /// Hash of version manifest.
    /// </summary>
    public string ManifestHash { get; } = manifestHash;

    public void Dispose()
    {
        Blob.Dispose();
    }
}
