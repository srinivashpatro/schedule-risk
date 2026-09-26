using System.Text.RegularExpressions;

namespace ScheduleRisk.Tests;

/// <summary>
/// The browser app's privacy promise: XER files are never uploaded and the page loads nothing from other sites.
/// These checks scan the web project's own files (not the Blazor framework) for anything that would reach another site.
/// </summary>
public class WebAssetsTests
{
    private static readonly string WebDir = Path.Combine(Path.GetDirectoryName(TestData.Dir)!, "src", "ScheduleRisk.Web");

    /// <summary>The web project's sources: pages, components, scripts and styles (not sample data, fonts or build output).</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string[] exts = { ".razor", ".cs", ".html", ".css", ".js" };
        return Directory.EnumerateFiles(WebDir, "*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f)))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    // XML namespaces name a vocabulary; nothing is fetched from them.
    private static readonly string[] Namespaces = { "http://www.w3.org/2000/svg", "http://www.w3.org/1999/xlink", "http://www.w3.org/1999/xhtml" };

    [Fact]
    public void The_scan_finds_the_app_files()
    {
        var names = SourceFiles().Select(Path.GetFileName).ToList();
        Assert.Contains("index.html", names);
        Assert.Contains("app.css", names);
        Assert.Contains("app.js", names);
        Assert.Contains("SchedulePanel.razor", names);
    }

    [Fact]
    public void No_absolute_web_addresses_except_xml_namespaces()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles())
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"https?://[^\s""'<>)]+"))
                if (!Namespaces.Any(ns => m.Value.StartsWith(ns, StringComparison.Ordinal)))
                    bad.Add($"{Path.GetFileName(f)}: {m.Value}");
        Assert.Empty(bad);
    }

    [Fact]
    public void No_protocol_relative_or_imported_resources()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles())
        {
            string text = File.ReadAllText(f), name = Path.GetFileName(f);
            // src="//host/x", href='//host/x', url(//host/x)
            foreach (Match m in Regex.Matches(text, @"(?:\b(?:src|href|srcset|poster|action)\s*=\s*[""']?|url\(\s*[""']?)//", RegexOptions.IgnoreCase))
                bad.Add($"{name}: {m.Value}");
            // an @import rule, not the word in a comment
            if (Path.GetExtension(f) == ".css" && Regex.IsMatch(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline), @"@import\b"))
                bad.Add($"{name}: @import");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Scripts_make_no_network_requests()
    {
        var bad = new List<string>();
        foreach (var f in SourceFiles().Where(f => Path.GetExtension(f) == ".js"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"\b(?:fetch|XMLHttpRequest|sendBeacon|WebSocket|EventSource|importScripts)\b"))
                bad.Add($"{Path.GetFileName(f)}: {m.Value}");
        Assert.Empty(bad);
    }
}
