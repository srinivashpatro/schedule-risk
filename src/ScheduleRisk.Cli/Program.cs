using System.Diagnostics;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Risk.Register;
using ScheduleRisk.Core.Simulation;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Cli;

public static class Program
{
    private const string Usage = @"sra - schedule risk analysis for Primavera P6 XER files

usage:
  sra info      <file.xer>
  sra cpm       <file.xer> [--project ID] [--csv out.csv]
  sra validate  <file.xer> [--project ID] [--json health.json]
                [--long-lag-hr 352] [--large-float-hr 352] [--large-duration-hr 352]
  sra verify    <file.xer> [--project ID] [--tolerance-min N]
  sra simulate  <file.xer> --risk model.json [--project ID] [--iterations N] [--seed N]
                [--scenario pre|post|both] [--out folder] [--threads N] [--register register.json]
  sra promote   <file.xer> --register register.json [--risk model.json] [--out model.json]
                [--import [--register-out register.json]] [--project ID]

simulate writes to --out (default: ./sra-output):
  report.html  summary.pre.json  summary.post.json  activities.csv  risks.csv  iterations.csv

promote turns the register's approved risks that meet its promote rule into risks in the model
(--risk, or an empty model) and writes it to --out (default: promoted.risk.json). --import first moves
the model's typed-in risks to the register (written to --register-out, default promoted.register.json).
simulate --register promotes the register into the model before simulating, as the app does.";

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
                case "validate": return Validate(s, r, path, opts);
                case "verify": return Verify(s, r, opts);
                case "simulate": return Simulate(s, engine, r, opts);
                case "promote": return Promote(s, engine, r, opts);
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

    /// <summary>Health check: P6 Check Schedule parameters and the DCMA 14-Point Assessment (docs/SCHEDULE_CHECK.md).</summary>
    private static int Validate(Schedule s, CpmResult r, string path, Dictionary<string, string> o)
    {
        var settings = new HealthCheckSettings();
        double Hours(string key, double dflt) => o.TryGetValue(key, out var v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : dflt;
        settings.LongLagHours = Hours("long-lag-hr", settings.LongLagHours);
        settings.LargeFloatHours = Hours("large-float-hr", settings.LargeFloatHours);
        settings.LargeDurationHours = Hours("large-duration-hr", settings.LargeDurationHours);
        var h = HealthCheck.Run(s, r, settings);
        static string Val(HealthItem i) => i.ActualBool is bool b ? (b ? "yes" : "no")
            : i.Actual is not double a ? "-" : i.Unit == "%" ? $"{a:F1}%" : i.Unit == "index" ? $"{a:F2}" : $"{a:0}";
        static string Target(HealthItem i) => i.Target is double t ? $"{i.Operator} {t:0.##}{(i.Unit == "%" ? "%" : "")}" : "";
        void Section(string title, IEnumerable<HealthItem> items)
        {
            var list = items.ToList();
            var (pass, fail, na) = h.Score(list);
            Console.WriteLine($"{title}: {pass} pass, {fail} fail, {na} n/a");
            foreach (var i in list)
            {
                string count = i.Count is int c && i.Denominator is int d ? $"{c}/{d}" : "";
                string name = i.Number != null ? $"{i.Number,2} {i.Label}" : i.Label;
                string conv = i.StatusConventional is HealthStatus sc ? $" (conventional: {HealthCheck.StatusText(sc)})" : "";
                Console.WriteLine($"  {i.StatusText,-20} {name,-42} {Val(i),8} {Target(i),-9} {count,-11}{conv}");
            }
        }
        Console.WriteLine($"{h.ProjectCode}: {h.Schedulable} activities, {h.TotalRelationships} relationships, data date {Time.Format(h.DataDate)}"
                          + (h.Unstarted ? " (no progress: baseline)" : ""));
        Section("P6 Check Schedule", h.P6);
        Section("DCMA 14-Point", h.Dcma);
        foreach (var n in h.AppNotes) Console.WriteLine("note: " + n);
        if (o.TryGetValue("json", out var json))
        {
            File.WriteAllText(json, h.ToJson(Path.GetFileName(path)));
            Console.WriteLine($"json: {Path.GetFullPath(json)}");
        }
        return h.P6.Concat(h.Dcma).Any(i => i.Status == HealthStatus.Fail) ? 1 : 0;
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
            Console.WriteLine($"  {d.Code,-14} {d.Field,-16} P6 {Show(d, d.P6),-16} ours {Show(d, d.Ours),-16} delta {d.DeltaMinutes / 60.0:+0.00;-0.00}h" +
                              (d.OutsideCalendar ? " (elapsed; P6 date outside the calendar range)" : ""));
        Console.WriteLine(rep.Outcome switch
        {
            VerifyOutcome.Matches => "RESULT: engine matches P6",
            VerifyOutcome.Differences => "RESULT: differences found (see above)",
            _ => "RESULT: nothing to compare - no P6-calculated dates in this file (it may not have been scheduled in P6 before export)",
        });
        return rep.Outcome == VerifyOutcome.Differences ? 1 : 0;
    }

    /// <summary>Promote the register's risks into a risk model (docs/RISK_REGISTER.md), as step "Promote" in the app.</summary>
    private static int Promote(Schedule s, CpmEngine engine, CpmResult det, Dictionary<string, string> o)
    {
        if (!o.TryGetValue("register", out var regPath)) throw new ArgumentException("promote needs --register register.json");
        var reg = RiskRegister.FromJson(File.ReadAllText(regPath));
        var issues = reg.Validate();
        foreach (var i in issues) Console.Error.WriteLine((i.Error ? "error: " : "warning: ") + i.Text);
        if (issues.Any(i => i.Error)) return 4;
        var doc = o.TryGetValue("risk", out var riskPath) ? RiskModelDocument.FromJson(File.ReadAllText(riskPath)) : new RiskModelDocument();
        var planned = RegisterPromoter.PlannedDuration(s, det);
        Console.WriteLine($"planned duration: {planned.WorkingDays:F1} working days ({Time.Format(planned.Start)} to {Time.Format(planned.Finish)})");
        if (o.ContainsKey("import"))
        {
            // Move the model's typed-in risks to the register first; they promote back unchanged.
            var imp = RegisterImporter.MoveToRegister(doc, reg, s, engine.CriticalToProjectFinish(), planned.WorkingDays);
            Console.WriteLine($"import: {imp}");
            foreach (var (from, to) in imp.Renamed) Console.WriteLine($"  {from} renamed {to}: the register already had {from}");
            foreach (var n in imp.NotMoved) Console.WriteLine($"  {n.Id} not moved: {n.Reason}");
            foreach (var n in imp.Notes) Console.WriteLine($"  {n}");
            string regOut = o.GetValueOrDefault("register-out", "promoted.register.json");
            File.WriteAllText(regOut, reg.ToJson());
            Console.WriteLine($"register -> {regOut}");
        }
        var report = RegisterPromoter.Apply(doc, reg, planned.WorkingDays);
        foreach (var row in doc.Risks.Where(x => x.Source == RiskRow.RegisterSource))
        {
            string how = report.Added.Contains(row.Id) ? "added" : "updated";
            string post = row.Mitigated ? $", after mitigation {row.MitigatedProbability:P1} {Days(row.MitigatedImpact)}" : "";
            var ids = row.Filter.Value.Split(", ");
            string on = ids.Length <= 5 ? row.Filter.Value : string.Join(", ", ids.Take(5)) + $" and {ids.Length - 5} more";
            Console.WriteLine($"  {how} {row.Id} {row.Title}: {row.Probability:P1} {Days(row.Impact)}{(row.ImpactUnits == "percent" ? " (%)" : "")}{post} on {on}");
        }
        foreach (var id in report.Removed) Console.WriteLine($"  removed {id}: no longer promoted");
        foreach (var sk in report.Skipped) Console.WriteLine($"  skipped {sk.Id}: {sk.Reason}");
        // Check the result the way simulate will read it (activity ids, distributions).
        var model = RiskModelLoader.LoadJson(s, doc.ToJson(), engine.CriticalToProjectFinish());
        foreach (var w in model.Warnings) Console.Error.WriteLine("warning: " + w);
        string outPath = o.GetValueOrDefault("out", "promoted.risk.json");
        File.WriteAllText(outPath, doc.ToJson());
        Console.WriteLine($"{report} -> {outPath}");
        return 0;

        static string Days(DistSpec d) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d.Min:0.#}/{d.MostLikely:0.#}/{d.Max:0.#}d");
    }

    private static int Simulate(Schedule s, CpmEngine engine, CpmResult det, Dictionary<string, string> o)
    {
        if (!o.TryGetValue("risk", out var riskPath)) throw new ArgumentException("simulate needs --risk model.json");
        RiskModel model;
        if (o.TryGetValue("register", out var regPath))
        {
            // The model's discrete risks come from the register: promote it first, as the app does before every run.
            var reg = RiskRegister.FromJson(File.ReadAllText(regPath));
            var doc = RiskModelDocument.FromJson(File.ReadAllText(riskPath));
            var rep = RegisterPromoter.Apply(doc, reg, RegisterPromoter.PlannedDuration(s, det).WorkingDays);
            Console.WriteLine($"register: {rep}");
            foreach (var sk in rep.Skipped) Console.Error.WriteLine($"warning: register risk {sk.Id} is not in the run: {sk.Reason}");
            model = RiskModelLoader.LoadJson(s, doc.ToJson(), engine.CriticalToProjectFinish());
        }
        else model = RiskModelLoader.Load(s, riskPath, engine.CriticalToProjectFinish());
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
                              (sum.ProbMeetMustFinishBy is double pm ? $"must finish by {Time.Format(sum.MustFinishBy)} ({pm:P0})  " : "") +
                              $"P50 {Time.Format(sum.FinishPercentiles[50])}  P80 {Time.Format(sum.FinishPercentiles[80])}  P90 {Time.Format(sum.FinishPercentiles[90])}");
            var du = sum.Duration;
            Console.WriteLine($"  duration in working days from {Time.Format(sum.ProjectStart)}: deterministic {du.Deterministic:F1}  " +
                              $"P50 {du.Percentiles[50]:F1}, {ResultsSummary.Contingency(sum, 50)}  P80 {du.Percentiles[80]:F1}, {ResultsSummary.Contingency(sum, 80)}  " +
                              $"mean {du.Mean:F1}  sd {du.Stdev:F1}  skewness {du.Skewness:F2}  kurtosis {du.Kurtosis:F2}");
            if (sc == Scenario.PreMitigation) { pre = sum; preRes = res; } else post = sum;
        }
        var main = pre ?? post!;
        File.WriteAllText(Path.Combine(outDir, "activities.csv"), CsvExport.Activities(main));
        File.WriteAllText(Path.Combine(outDir, "risks.csv"), CsvExport.Risks(main));
        if (preRes != null) File.WriteAllText(Path.Combine(outDir, "iterations.csv"), CsvExport.Iterations(preRes));
        var checks = HealthCheck.Run(s, det);
        var verify = P6Verifier.Verify(s, det);
        File.WriteAllText(Path.Combine(outDir, "report.html"),
            HtmlReport.Build(s, main, pre != null ? post : null, checks, verify.Compared > 0 ? verify : null, model.Name));
        Console.WriteLine($"report: {Path.GetFullPath(Path.Combine(outDir, "report.html"))}");
        return 0;
    }
}
