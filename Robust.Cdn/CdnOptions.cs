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
    public bool StreamCompression { get; set; } = true;

    /// <summary>
    /// Compression level for stream compression.
    /// </summary>
    public int StreamCompressionLevel { get; set; } = 3;

    /// <summary>
    /// Whether to compress blobs on-disk.
    /// </summary>
    public bool IndividualCompression { get; set; } = true;

    /// <summary>
    /// Compression level for individual compression.
    /// </summary>
    public int IndividualCompressionLevel { get; set; } = 14;

    /// <summary>
    /// Whether to decompress individual compression before sending over the network.
    /// Recommended to enable this if stream compression is enabled.
    /// </summary>
    public bool IndividualDecompression { get; set; } = true;

    public int IndividualCompressSavingsThreshold { get; set; } = 10;

    public int ManifestCompressLevel { get; set; } = 14;
}
