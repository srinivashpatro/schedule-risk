using System.Text.Json;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Tests;

internal static class TestData
{
    private static string? _dir;

    /// <summary>Repository testdata folder, found by walking up from the test binaries.</summary>
    public static string Dir
    {
        get
        {
            if (_dir != null) return _dir;
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "testdata"))) d = d.Parent;
            if (d == null) throw new DirectoryNotFoundException("testdata folder not found above " + AppContext.BaseDirectory);
            _dir = Path.Combine(d.FullName, "testdata");
            return _dir;
        }
    }

    public static string PathOf(string name) => Path.Combine(Dir, name);

    public static Schedule Load(string xerName) => ScheduleBuilder.Build(XerDocument.Load(PathOf(xerName)));

    public static (Schedule S, CpmResult R) LoadAndRun(string xerName)
    {
        var s = Load(xerName);
        return (s, new CpmEngine(s).Run());
    }

    public static JsonElement Golden(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "golden", name)));
        return doc.RootElement.Clone();
    }
}
