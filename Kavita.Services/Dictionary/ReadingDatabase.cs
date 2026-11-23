using System.IO;

namespace Kavita.Services.Dictionary;

/// <summary>
/// Owns config/reading/reading.db, the SQLite file behind <see cref="ReadingStore"/>. It's kept out of kavita.db so
/// tokens and analysis stay out of Kavita's backups, and the tokenizer never waits behind kavita.db's writer
/// </summary>
public class ReadingDatabase(string databasePath)
{
    /// <summary>
    /// config/reading/reading.db, resolved against the working directory the same way DirectoryService resolves config/
    /// </summary>
    public static string DefaultDatabasePath =>
        Path.Join(Directory.GetCurrentDirectory(), "config", "reading", "reading.db");

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);
}
