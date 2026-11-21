namespace ContextWave.Bridge;

/// <summary>Safe file handling shared by the places setup edits other apps' config files.</summary>
public static class ConfigFile
{
    /// <summary>
    /// Copies a file to a timestamped backup beside it and returns the backup's path. A second backup in the
    /// same second gets a numeric suffix rather than failing or overwriting the first.
    /// </summary>
    public static string Backup(string file)
    {
        var stem = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
        for (var n = 0; ; n++)
        {
            var backup = n == 0 ? stem : $"{stem}-{n}";
            try
            {
                File.Copy(file, backup);
                return backup;
            }
            catch (IOException) when (File.Exists(backup))
            {
                // Taken (by an earlier change this second): try the next name.
            }
        }
    }

    /// <summary>The failures editing a config file can expect: bad content, or a file that cannot be changed.</summary>
    public static bool IsExpectedFailure(Exception ex) =>
        ex is System.Text.Json.JsonException or InvalidDataException or IOException
            or ArgumentException or UnauthorizedAccessException;

    /// <summary>Writes beside the file and swaps it in, so a failure cannot leave it half-written.</summary>
    public static void WriteAtomically(string file, string text)
    {
        var temporary = file + ".contextwave-tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, file, overwrite: true);
        }
        catch
        {
            // Leave nothing half-written beside the file.
            try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
