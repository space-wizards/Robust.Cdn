namespace Robust.Cdn;

public sealed class CdnOptions
{
    public const string Position = "Cdn";

    /// <summary>
    /// Directory path where new version zips are read from stored. See docs site for details.
    /// </summary>
    public string VersionDiskPath { get; set; } = "";

    /// <summary>
    /// File path for the database to store files, versions and logs into.
    /// </summary>
    public string DatabaseFileName { get; set; } = "content.db";

    /// <summary>
    /// The name of client zip files in the directory structure.
    /// </summary>
    public string ClientZipName { get; set; } = "SS14.Client.zip";

    /// <summary>
    /// Whether to do stream compression over whole download requests.
    /// Ignored if AutoStreamCompressRatio is used.
    /// </summary>
    public bool StreamCompress { get; set; } = false;

    /// <summary>
    /// Compression level for stream compression.
    /// </summary>
    public int StreamCompressLevel { get; set; } = 5;

    /// <summary>
    /// Whether to compress blobs on-disk. You probably want to leave this on.
    /// </summary>
    public bool BlobCompress { get; set; } = true;

    /// <summary>
    /// Compression level for on-disk compression.
    /// </summary>
    public int BlobCompressLevel { get; set; } = 18;

    /// <summary>
    /// The amount of bytes blob compression needs to save for it to be considered "worth it".
    /// Otherwise no compression is used.
    /// </summary>
    public int BlobCompressSavingsThreshold { get; set; } = 10;

    /// <summary>
    /// Whether to send on-disk compressed blobs over the network as pre-compression.
    /// Ignored if AutoStreamCompressRatio is used.
    /// </summary>
    public bool SendPreCompressed { get; set; } = true;

    /// <summary>
    /// Compression level to use for content manifests.
    /// </summary>
    public int ManifestCompressLevel { get; set; } = 18;

    /// <summary>
    /// Ratio of total files that need to be sent after which we switch to stream compression automatically.
    /// If this is enabled (disable by setting to a negative number),
    /// SendPreCompressed and StreamCompress are ignored and decided automatically.
    /// </summary>
    /// <remarks>
    /// Stream compression is generally better for large downloads, whereas individual is better for small downloads.
    /// This default value is arbitrarily decided without scientific testing or measurement.
    /// </remarks>
    public float AutoStreamCompressRatio { get; set; } = 0.5f;

    /// <summary>
    /// Log all download requests to the database.
    /// </summary>
    public bool LogRequests { get; set; } = false;

    /// <summary>
    /// Authentication token to initiate version updates via the POST /control/update endpoint.
    /// </summary>
    public string UpdateToken { get; set; } = "CHANGE ME";
}
