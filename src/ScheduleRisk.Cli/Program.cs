using System.Diagnostics;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Cli;

public static class Program
{
    private const string Usage = @"sra - schedule risk analysis for Primavera P6 XER files

usage:
  sra info      <file.xer>
  sra cpm       <file.xer> [--project ID] [--csv out.csv]
  sra validate  <file.xer> [--project ID]
  sra verify    <file.xer> [--project ID] [--tolerance-min N]
  sra simulate  <file.xer> --risk model.json [--project ID] [--iterations N] [--seed N]
                [--scenario pre|post|both] [--out folder] [--threads N]

simulate writes to --out (default: ./sra-output):
  report.html  summary.pre.json  summary.post.json  activities.csv  risks.csv  iterations.csv";

    public static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }
        try
        {
            string cmd = args[0].ToLowerInvariant();
            string path = args[1];
            var opts = ParseOptions(args.Skip(2).ToArray());
            var doc = XerDocument.Load(path);
            foreach (var w in doc.Warnings.Take(20)) Console.Error.WriteLine("warning: " + w);
            if (cmd == "info") return Info(doc);
            var s = ScheduleBuilder.Build(doc, opts.GetValueOrDefault("project"));
            foreach (var w in s.Warnings.Take(50)) Console.Error.WriteLine("warning: " + w);
            if (s.Warnings.Count > 50) Console.Error.WriteLine($"warning: ... {s.Warnings.Count - 50} more");
            var sw = Stopwatch.StartNew();
            var engine = new CpmEngine(s);
            var r = engine.Run();
            sw.Stop();
            switch (cmd)
            {
                case "cpm": return Cpm(s, r, opts, sw.Elapsed);
                case "validate": return Validate(s, r);
                case "verify": return Verify(s, r, opts);
                case "simulate": return Simulate(s, engine, r, opts);
                default:
                    Console.Error.WriteLine($"unknown command '{cmd}'\n\n{Usage}");
                    return 2;
            }
        }
        catch (ScheduleLoopException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 3;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or ArgumentException
                                      or InvalidOperationException or CalendarHorizonException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 4;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unexpected argument '{a[i]}'");
            string key = a[i].Substring(2);
            if (i + 1 >= a.Length || a[i + 1].StartsWith("--", StringComparison.Ordinal)) d[key] = "true";
            else d[key] = a[++i];
        }
        return d;
    }

    private static int Info(XerDocument doc)
    {
        Console.WriteLine($"encoding {doc.Encoding}, header: {string.Join(" ", doc.Header.Take(3))}");
        foreach (var (id, name) in ScheduleBuilder.ListProjects(doc)) Console.WriteLine($"project {id} {name}");
        foreach (var name in doc.TableOrder)
        {
            var t = doc.Tables[name];
            Console.WriteLine($"{name,-14} {t.Rows.Count,8} rows {t.Fields.Count,4} fields");
        }
        return 0;
    }

    private static int Cpm(Schedule s, CpmResult r, Dictionary<string, string> o, TimeSpan elapsed)
    {
        if (o.TryGetValue("csv", out var csv))
        {
            File.WriteAllText(csv, CsvExport.Cpm(s, r));
            Console.WriteLine($"wrote {csv}");
        }
        else
        {
            Console.WriteLine($"{"activity",-14} {"early start",-16} {"early finish",-16} {"late start",-16} {"late finish",-16} {"float d",8}");
            foreach (var a in s.Activities)
            {
                int j = a.Index;
                string tf = r.TF[j] == Time.None ? "" : (r.TF[j] / (double)a.Calendar.MinutesPerDay).ToString("F1");
                Console.WriteLine($"{a.Code,-14} {Time.Format(r.ES[j]),-16} {Time.Format(r.EF[j]),-16} {Time.Format(r.LS[j]),-16} {Time.Format(r.LF[j]),-16} {tf,8}");
            }
        }
        int crit = s.Activities.Count(a => r.IsCritical(s, a.Index));
        Console.WriteLine($"project finish {Time.Format(r.ProjectFinish)}; {crit} critical activities; calculated in {elapsed.TotalMilliseconds:F0} ms");
        return 0;
    }

    private static int Validate(Schedule s, CpmResult r)
    {
        var checks = ScheduleValidator.Validate(s, r);
        foreach (var c in checks)
            Console.WriteLine($"{(c.Passed ? "PASS  " : "REVIEW")} {c.Title,-50} {c.Count,6}/{c.Total,-6} {c.Pct,6:F1}%  {c.Note}");
        return checks.All(c => c.Passed) ? 0 : 1;
    }

    private static int Verify(Schedule s, CpmResult r, Dictionary<string, string> o)
    {
        long tol = o.TryGetValue("tolerance-min", out var t) ? long.Parse(t) : 0;
        var rep = P6Verifier.Verify(s, r, tol);
        Console.WriteLine($"compared {rep.Compared} activities: {rep.FieldsMatched}/{rep.FieldsCompared} fields match P6, " +
                          $"{rep.ActivitiesMatched} activities fully match ({rep.SkippedNoP6} without P6 dates skipped)");
        // Total float is a duration in minutes; the other fields are dates.
        static string Show(DateDiff d, long m) => d.Field == "total_float" ? $"{m / 60.0:F2} h" : Time.Format(m);
        foreach (var d in rep.Worst(40))
            Console.WriteLine($"  {d.Code,-14} {d.Field,-16} P6 {Show(d, d.P6),-16} ours {Show(d, d.Ours),-16} delta {d.DeltaMinutes / 60.0:+0.00;-0.00}h");
        Console.WriteLine(rep.Outcome switch
        {
            VerifyOutcome.Matches => "RESULT: engine matches P6",
            VerifyOutcome.Differences => "RESULT: differences found (see above)",
            _ => "RESULT: nothing to compare - no P6-calculated dates in this file (it may not have been scheduled in P6 before export)",
        });
        return rep.Outcome == VerifyOutcome.Differences ? 1 : 0;
    }

    private static int Simulate(Schedule s, CpmEngine engine, CpmResult det, Dictionary<string, string> o)
    {
        if (!o.TryGetValue("risk", out var riskPath)) throw new ArgumentException("simulate needs --risk model.json");
        var crit = Enumerable.Range(0, s.Activities.Count).Select(j => det.IsCritical(s, j)).ToArray();
        var model = RiskModelLoader.Load(s, riskPath, crit);
        foreach (var w in model.Warnings) Console.Error.WriteLine("warning: " + w);
        int? iters = o.TryGetValue("iterations", out var it) ? int.Parse(it) : null;
        long? seed = o.TryGetValue("seed", out var sd) ? long.Parse(sd) : null;
        string scen = o.GetValueOrDefault("scenario", "both").ToLowerInvariant();
        string outDir = o.GetValueOrDefault("out", "sra-output");
        Directory.CreateDirectory(outDir);

        SimulationSummary? pre = null, post = null;
        SimulationResult? preRes = null;
        foreach (var sc in scen == "both" ? new[] { Scenario.PreMitigation, Scenario.PostMitigation }
                                          : new[] { scen == "post" ? Scenario.PostMitigation : Scenario.PreMitigation })
        {
            var mc = new MonteCarloEngine(s, model, sc, engine);
            if (o.TryGetValue("threads", out var th)) mc.MaxDegreeOfParallelism = int.Parse(th);
            var progress = new Progress<(int Done, int Total)>(p => Console.Error.Write($"\r{sc}: {p.Done}/{p.Total} iterations   "));
            var res = mc.Run(iters, seed, progress: progress);
            Console.Error.WriteLine();
            var sum = SimulationSummary.Build(mc, res);
            string tag = sc == Scenario.PostMitigation ? "post" : "pre";
            File.WriteAllText(Path.Combine(outDir, $"summary.{tag}.json"), sum.ToJson());
            Console.WriteLine($"{tag}-mitigation: {res.Iterations} iterations in {res.Elapsed.TotalSeconds:F1}s  " +
                              $"deterministic {Time.Format(sum.DeterministicFinish)} ({sum.ProbMeetDeterministic:P0})  " +
                              $"P50 {Time.Format(sum.FinishPercentiles[50])}  P80 {Time.Format(sum.FinishPercentiles[80])}  P90 {Time.Format(sum.FinishPercentiles[90])}");
            if (sc == Scenario.PreMitigation) { pre = sum; preRes = res; } else post = sum;
        }
        var main = pre ?? post!;
        File.WriteAllText(Path.Combine(outDir, "activities.csv"), CsvExport.Activities(main));
        File.WriteAllText(Path.Combine(outDir, "risks.csv"), CsvExport.Risks(main));
        if (preRes != null) File.WriteAllText(Path.Combine(outDir, "iterations.csv"), CsvExport.Iterations(preRes));
        var checks = ScheduleValidator.Validate(s, det);
        var verify = P6Verifier.Verify(s, det);
        File.WriteAllText(Path.Combine(outDir, "report.html"),
            HtmlReport.Build(s, main, pre != null ? post : null, checks, verify.Compared > 0 ? verify : null, model.Name));
        Console.WriteLine($"report: {Path.GetFullPath(Path.Combine(outDir, "report.html"))}");
        return 0;
    }
}
