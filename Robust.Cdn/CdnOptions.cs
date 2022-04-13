namespace Robust.Cdn;

public sealed class CdnOptions
{
    public const string Position = "Cdn";

    public string VersionDiskPath { get; set; } = "";
    public string DatabaseFileName { get; set; } = "content.db";
    public string ClientZipName { get; set; } = "SS14.Client.zip";

    /// <summary>
    /// Whether to do stream compression over downloads.
    /// </summary>
    public bool StreamCompress { get; set; } = false;

    /// <summary>
    /// Compression level for stream compression.
    /// </summary>
    public int StreamCompressLevel { get; set; } = 3;

    /// <summary>
    /// Whether to compress blobs on-disk.
    /// </summary>
    public bool BlobCompress { get; set; } = true;

    /// <summary>
    /// Compression level for individual compression.
    /// </summary>
    public int BlobCompressLevel { get; set; } = 14;

    /// <summary>
    /// Whether to decompress individual compression before sending over the network.
    /// Recommended to enable this if stream compression is enabled.
    /// </summary>
    public bool SendPreCompressed { get; set; } = true;

    public int BlobCompressSavingsThreshold { get; set; } = 10;

    public int ManifestCompressLevel { get; set; } = 14;

    /// <summary>
    /// Ratio of total files that need to be sent after which we switch to stream compression automatically.
    /// If this is enabled (disable by setting to a negative number),
    /// SendPreCompressed and StreamCompress are ignored and decided automatically.
    /// </summary>
    public float AutoStreamCompressRatio { get; set; } = 0.5f;

    public bool LogRequests { get; set; } = false;

    public string UpdateToken { get; set; } = "CHANGE ME";
}
