using ScheduleRisk.Web.Services.Access;

namespace ScheduleRisk.Desktop;

/// <summary>The accounts file, in the user's local app data folder on this PC.</summary>
public sealed class AccessFile : IAccessFile
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectRiskAnalysis", "access.json");

    private readonly string path;

    public AccessFile(string path) => this.path = path;

    public string? Read() => File.Exists(path) ? File.ReadAllText(path) : null;

    public void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write a new file, then swap it in, so a crash part-way cannot leave a half-written file.
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
