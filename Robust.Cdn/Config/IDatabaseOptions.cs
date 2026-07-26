namespace Robust.Cdn.Config;

/// <summary>
/// Options that contain database config settings.
/// </summary>
public interface IDatabaseOptions
{
    /// <summary>
    /// File to be used as SQLite database file. If the file does not exist, it will be created.
    /// </summary>
    public string DatabaseFileName { get; }
}
