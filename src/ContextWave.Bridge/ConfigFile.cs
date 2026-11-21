namespace ContextWave.Bridge;

/// <summary>Safe file handling shared by the places setup edits other apps' config files.</summary>
public static class ConfigFile
{
    /// <summary>Copies a file to a timestamped backup beside it and returns the backup's path.</summary>
    public static string Backup(string file)
    {
        var backup = $"{file}.bak-{DateTime.Now:yyyyMMddHHmmss}";
        File.Copy(file, backup);
        return backup;
    }

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
