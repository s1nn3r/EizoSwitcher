namespace EizoSwitcher;

/// <summary>Small rolling log file in %APPDATA%\EizoSwitcher.</summary>
public static class Log
{
    private static readonly object lock_ = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EizoSwitcher");
    public static string FilePath => Path.Combine(Folder, "EizoSwitcher.log");

    public static void Write(string message)
    {
        lock (lock_)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
