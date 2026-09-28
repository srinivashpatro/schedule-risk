using System.Globalization;
using System.Text;
using System.Text.Json;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Analysis;

public enum HealthStatus { Pass, Fail, NotApplicable, FailInformational }

/// <summary>
/// One activity or relationship a check flagged, with what flagged it. For a relationship <see cref="Code"/> and
/// <see cref="Name"/> are the successor's and <see cref="PredCode"/> is set.
/// </summary>
public sealed record HealthFlag(string TaskId, string Code, string Name)
{
    public int Index { get; init; } = -1;
    public string? Reason { get; init; }
    public IReadOnlyList<string>? Reasons { get; init; }
    /// <summary>The JSON name of <see cref="Value"/>, e.g. total_float_hr or remaining_duration_hr.</summary>
    public string? ValueKey { get; init; }
    public double? Value { get; init; }
    public string? Constraint { get; init; }
    public string? BaselineFinish { get; init; }
    public string? CurrentFinish { get; init; }
    public string? PredCode { get; init; }
    public string? PredName { get; init; }
    public string? PredType { get; init; }
    public double? LagHours { get; init; }

    public bool IsRelationship => PredCode != null;
}

/// <summary>One P6 Check Schedule parameter or DCMA check: its target, result, status, note and flagged items.</summary>
public sealed class HealthItem
{
    /// <summary>The P6 dialog's tab ("Relationships and Assignments", ...); empty for DCMA checks.</summary>
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    /// <summary>DCMA check number, "1" to "14"; null for P6 parameters.</summary>
    public string? Number { get; init; }
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public int? Count { get; init; }
    public int? Denominator { get; init; }
    public double? Actual { get; init; }
    /// <summary>For DCMA #12, whose result is yes or no.</summary>
    public bool? ActualBool { get; init; }
    /// <summary>"%", "count", "index" or "bool".</summary>
    public string Unit { get; init; } = "%";
    /// <summary>"&lt;", "&lt;=", "&gt;", "&gt;=" or "n/a".</summary>
    public string Operator { get; init; } = "<";
    public double? Target { get; init; }
    public HealthStatus Status { get; init; }
    /// <summary>Relationship Types only: the status on the conventional reading (FS share at least 90%).</summary>
    public HealthStatus? StatusConventional { get; init; }
    public string? Note { get; init; }
    /// <summary>Checks with the same flag key share one list of flagged items (a P6 parameter and its DCMA twin).</summary>
    public string FlagKey { get; init; } = "";
    public IReadOnlyList<HealthFlag> Flagged { get; init; } = Array.Empty<HealthFlag>();
    public int FlaggedTotal { get; init; }
    /// <summary>Relationship Types only: count by type (PR_FS, PR_SS, PR_FF, PR_SF).</summary>
    public IReadOnlyDictionary<string, int>? Breakdown { get; init; }

    public string StatusText => HealthCheck.StatusText(Status);

    private static string N(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);

    /// <summary>The result as shown: "8.9%", "0.97", "12" or "Yes"; "–" when not measured.</summary>
    public string ActualText => ActualBool is bool b ? (b ? "Yes" : "No")
        : Actual is not double a ? "–" : Unit == "%" ? N(a, "0.0") + "%" : Unit == "index" ? N(a, "0.00") : N(a, "0");

    /// <summary>The target as shown, e.g. "&lt; 5%", "≥ 0.95"; "–" for a pass or fail check.</summary>
    public string TargetText => Target is double t
        ? $"{Operator.Replace("<=", "≤").Replace(">=", "≥")} {N(t, "0.##")}{(Unit == "%" ? "%" : "")}" : "–";

    public string CountText => Status != HealthStatus.NotApplicable && Count is int c && Denominator is int d ? $"{N(c, "N0")} / {N(d, "N0")}" : "";

    /// <summary>The first flagged activity IDs (relationships as predecessor → successor), for a report's notes column.</summary>
    public string Examples(int max)
    {
        if (Flagged.Count == 0) return "";
        var ids = Flagged.Take(max).Select(f => f.IsRelationship ? $"{f.PredCode} → {f.Code}" : f.Code);
        return string.Join(", ", ids) + (Flagged.Count > max ? $" and {Flagged.Count - max:N0} more" : "");
    }
}

/// <summary>
/// Thresholds and targets. The defaults are the owner's P6 Check Schedule template (see
/// docs/SCHEDULE_CHECK.md), including two operators that read against their own description (Positive Lags "&gt; 5%",
/// Relationship Types "&lt; 90%"), which are reported as configured and never silently turned round.
/// </summary>
public sealed class HealthCheckSettings
{
    /// <summary>Hours; 352 h is 44 eight-hour working days, P6's default and DCMA's threshold.</summary>
    public double LongLagHours { get; set; } = 352;
    public double LargeFloatHours { get; set; } = 352;
    public double LargeDurationHours { get; set; } = 352;

    /// <summary>Operator and target for each P6 parameter, by key.</summary>
    public Dictionary<string, (string Op, double Target)> P6Targets { get; } = Defaults();

    public static Dictionary<string, (string Op, double Target)> Defaults() => new()
    {
        ["logic"] = ("<", 5), ["negative_lags"] = ("<", 1), ["positive_lags"] = (">", 5), ["long_lags"] = ("<", 5),
        ["relationship_types"] = ("<", 90), ["out_of_sequence"] = ("<", 2), ["resources_cost"] = ("<", 1),
        ["dangling_start"] = ("<=", 0), ["dangling_finish"] = ("<=", 0), ["large_float"] = ("<", 1), ["negative_float"] = ("<", 1),
        ["large_durations"] = ("<", 5), ["invalid_progress_dates"] = ("<", 1), ["late_activities"] = ("<", 5), ["bei"] = (">", 0.95),
        ["hard_constraints"] = ("<", 1), ["soft_constraints"] = ("<", 5),
    };
}

/// <summary>The result of a health check: the schedule's facts, the P6 parameters and the DCMA 14 checks.</summary>
public sealed class HealthReport
{
    public string ProjectId { get; init; } = "";
    public string ProjectCode { get; init; } = "";
    public long DataDate { get; init; } = Time.None;
    public long PlanStart { get; init; } = Time.None;
    public long ProjectFinish { get; init; } = Time.None;
    public int TotalActivities { get; init; }
    public int Schedulable { get; init; }
    public int TotalRelationships { get; init; }
    public double ProgressPct { get; init; }
    public bool Unstarted { get; init; }
    public bool HasResourceData { get; init; }
    public IReadOnlyDictionary<string, int> TaskTypes { get; init; } = new Dictionary<string, int>();
    public List<HealthItem> P6 { get; } = new();
    public List<HealthItem> Dcma { get; } = new();
    /// <summary>Notes about the app itself, e.g. constraints the engine does not model.</summary>
    public List<string> AppNotes { get; } = new();

    public HealthItem Item(string key) => P6.First(i => i.Key == key);

    public (int Pass, int Fail, int NotApplicable) Score(IEnumerable<HealthItem> items)
    {
        var list = items.ToList();
        return (list.Count(i => i.Status == HealthStatus.Pass),
                list.Count(i => i.Status is HealthStatus.Fail or HealthStatus.FailInformational),
                list.Count(i => i.Status == HealthStatus.NotApplicable));
    }

    /// <summary>The report as JSON in the owner's health-check schema (meta, p6_check_schedule, dcma_14_point), which the
    /// owner's Word report builder reads.</summary>
    public string ToJson(string sourceFile)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("meta");
            w.WriteString("source_file", sourceFile);
            w.WriteString("project_id", ProjectId);
            w.WriteString("project_short_name", ProjectCode);
            w.WriteString("data_date", Time.Format(DataDate));
            w.WriteString("plan_start_date", Time.Format(PlanStart));
            w.WriteString("project_finish_date", Time.Format(ProjectFinish));
            w.WriteNumber("total_activities", TotalActivities);
            w.WriteNumber("schedulable_activities", Schedulable);
            w.WriteNumber("wbs_summary_bars_excluded", TotalActivities - Schedulable);
            w.WriteNumber("total_relationships", TotalRelationships);
            w.WriteNumber("progress_pct_activities_with_actuals", Math.Round(ProgressPct, 2));
            w.WriteBoolean("is_unstarted_or_baseline_only", Unstarted);
            w.WriteBoolean("has_resource_cost_data", HasResourceData);
            w.WriteStartObject("task_type_breakdown");
            foreach (var (k, v) in TaskTypes) w.WriteNumber(k, v);
            w.WriteEndObject();
            w.WriteStartArray("app_notes");
            foreach (var n in AppNotes) w.WriteStringValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("p6_check_schedule");
            foreach (var i in P6) WriteItem(w, i);
            w.WriteEndArray();
            w.WriteStartArray("dcma_14_point");
            foreach (var i in Dcma) WriteItem(w, i);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void NumberOrNull(Utf8JsonWriter w, string name, double? v)
    {
        if (v is double d) w.WriteNumber(name, d); else w.WriteNull(name);
    }

    private static void WriteItem(Utf8JsonWriter w, HealthItem i)
    {
        w.WriteStartObject();
        if (i.Number != null)
        {
            w.WriteString("number", i.Number);
            w.WriteString("name", i.Label);
        }
        else
        {
            w.WriteString("section", i.Section);
            w.WriteString("key", i.Key);
            w.WriteString("label", i.Label);
        }
        w.WriteString("description", i.Description);
        w.WriteString("operator", i.Operator);
        NumberOrNull(w, "target", i.Target);
        w.WriteString("unit", i.Unit);
        if (i.ActualBool is bool b) w.WriteBoolean("actual", b); else NumberOrNull(w, "actual", i.Actual);
        NumberOrNull(w, "count", i.Count);
        NumberOrNull(w, "denominator", i.Denominator);
        w.WriteString("status", i.StatusText);
        if (i.StatusConventional is HealthStatus sc) w.WriteString("status_conventional", HealthCheck.StatusText(sc));
        if (i.Note != null) w.WriteString("note", i.Note); else w.WriteNull("note");
        w.WriteString("flag_key", i.FlagKey);
        w.WriteStartArray("flagged");
        foreach (var f in i.Flagged)
        {
            w.WriteStartObject();
            if (f.IsRelationship)
            {
                w.WriteString("pred_task_code", f.PredCode);
                w.WriteString("pred_task_name", f.PredName);
                w.WriteString("succ_task_code", f.Code);
                w.WriteString("succ_task_name", f.Name);
                w.WriteString("pred_type", f.PredType);
                NumberOrNull(w, "lag_hr", f.LagHours);
            }
            else
            {
                w.WriteString("task_id", f.TaskId);
                w.WriteString("task_code", f.Code);
                w.WriteString("task_name", f.Name);
                if (f.Reason != null) w.WriteString("reason", f.Reason);
                if (f.Reasons != null)
                {
                    w.WriteStartArray("reasons");
                    foreach (var r in f.Reasons) w.WriteStringValue(r);
                    w.WriteEndArray();
                }
                if (f.ValueKey != null) NumberOrNull(w, f.ValueKey, f.Value);
                if (f.Constraint != null) w.WriteString("constraint", f.Constraint);
                if (f.BaselineFinish != null) w.WriteString("baseline_finish", f.BaselineFinish);
                if (f.CurrentFinish != null) w.WriteString("current_finish", f.CurrentFinish);
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("flagged_total", i.FlaggedTotal);
        if (i.Breakdown != null)
        {
            w.WriteStartObject("breakdown");
            foreach (var (k, v) in i.Breakdown) w.WriteNumber(k, v);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }
}

/// <summary>
/// Schedule health check (docs/SCHEDULE_CHECK.md): the parameters of P6 Professional's Tools &gt; Check Schedule dialog
/// and the DCMA 14-Point Assessment, side by side, on the schedule as the engine recalculated it (identical to P6's
/// stored values when <c>sra verify</c> matches, and available for files P6 never scheduled). The definitions follow
/// the owner's health-check report; DCMA #12 and #13 use the DCMA method on the engine rather than P6's
/// driving_path_flag.
/// </summary>
public static class HealthCheck
{
    public const string RelationshipsTab = "Relationships and Assignments", DatesTab = "Dates and Durations", ConstraintsTab = "Constraints";

    private static readonly Dictionary<string, string> Hard = new()
    {
        ["CS_MSO"] = "Start On", ["CS_MEO"] = "Finish On", ["CS_MANDSTART"] = "Mandatory Start", ["CS_MANDFIN"] = "Mandatory Finish",
    };
    private static readonly Dictionary<string, string> Soft = new()
    {
        ["CS_MSOB"] = "Start On or Before", ["CS_MSOA"] = "Start On or After", ["CS_MEOB"] = "Finish On or Before",
        ["CS_MEOA"] = "Finish On or After", ["CS_ALAP"] = "As Late As Possible",
    };
    private static readonly Dictionary<ActivityType, string> TypeNames = new()
    {
        [ActivityType.Task] = "Task Dependent", [ActivityType.ResourceDependent] = "Resource Dependent",
        [ActivityType.LevelOfEffort] = "Level of Effort", [ActivityType.WbsSummary] = "WBS Summary",
        [ActivityType.StartMilestone] = "Start Milestone", [ActivityType.FinishMilestone] = "Finish Milestone",
    };

    /// <summary>Delay given to a critical activity in the Critical Path Test (DCMA #12).</summary>
    public const int CriticalPathTestDays = 600;

    public static string StatusText(HealthStatus s) => s switch
    {
        HealthStatus.Pass => "PASS",
        HealthStatus.Fail => "FAIL",
        HealthStatus.FailInformational => "FAIL (informational)",
        _ => "N/A",
    };

    private static double Pct(int n, int d) => d > 0 ? n * 100.0 / d : 0.0;

    public static bool Meets(double actual, string op, double target) => op switch
    {
        "<" => actual < target,
        "<=" => actual <= target,
        ">" => actual > target,
        ">=" => actual >= target,
        _ => throw new ArgumentException($"unknown operator {op}"),
    };

    private static HealthStatus Of(bool pass) => pass ? HealthStatus.Pass : HealthStatus.Fail;

    private static string Rel(RelType t) => "PR_" + t;

    private static string Hours(double h) => h.ToString("0.##", CultureInfo.InvariantCulture);

    public static HealthReport Run(Schedule s, CpmResult r, HealthCheckSettings? settings = null)
    {
        settings ??= new HealthCheckSettings();
        var acts = s.Activities;
        var sched = acts.Where(a => a.Type != ActivityType.WbsSummary).ToList();
        int n = sched.Count;
        int nRel = s.Relationships.Count + s.ExternalSuccessors.Count;
        long dd = s.Settings.DataDate;

        int withActuals = acts.Count(a => a.ActualStart != Time.None || a.ActualFinish != Time.None);
        bool unstarted = withActuals == 0;

        HealthFlag Flag(Activity a) => new(a.TaskId, a.Code, a.Name) { Index = a.Index };

        // ---- relationships, incoming and outgoing, including links to and from other projects
        var hasPred = new HashSet<int>();
        var hasSucc = new HashSet<int>();
        var startDriven = new HashSet<int>();    // an FS or SS predecessor drives the start
        var finishDrives = new HashSet<int>();   // the finish drives an FS or FF successor
        var rels = new List<(string PredCode, string PredName, Activity Succ, RelType Type, long Lag)>();
        foreach (var x in s.Relationships)
        {
            hasPred.Add(x.Succ);
            if (x.Type is RelType.FS or RelType.SS) startDriven.Add(x.Succ);
            if (x.Pred >= 0)
            {
                hasSucc.Add(x.Pred);
                if (x.Type is RelType.FS or RelType.FF) finishDrives.Add(x.Pred);
            }
            var p = x.Pred >= 0 ? acts[x.Pred] : null;
            rels.Add((p?.Code ?? x.ExternalCode, p?.Name ?? x.ExternalName, acts[x.Succ], x.Type, x.Lag));
        }
        var extRels = new List<HealthFlag>();
        foreach (var x in s.ExternalSuccessors)
        {
            hasSucc.Add(x.Pred);
            if (x.Type is RelType.FS or RelType.FF) finishDrives.Add(x.Pred);
        }
        HealthFlag RelFlag((string PredCode, string PredName, Activity Succ, RelType Type, long Lag) x) =>
            new(x.Succ.TaskId, x.Succ.Code, x.Succ.Name) { Index = x.Succ.Index, PredCode = x.PredCode, PredName = x.PredName, PredType = Rel(x.Type), LagHours = x.Lag / 60.0 };
        IEnumerable<HealthFlag> AllRelationships(Func<RelType, long, bool> match) =>
            rels.Where(x => match(x.Type, x.Lag)).Select(RelFlag)
                .Concat(s.ExternalSuccessors.Where(x => match(x.Type, x.Lag)).Select(x =>
                    new HealthFlag("", x.Code, x.Name) { PredCode = acts[x.Pred].Code, PredName = acts[x.Pred].Name, PredType = Rel(x.Type), LagHours = x.Lag / 60.0 }));

        // Terminal activities (the project's start and finish) are exempt from Logic when they are a small minority.
        var noPred = sched.Where(a => !hasPred.Contains(a.Index)).Select(a => a.Index).ToHashSet();
        var noSucc = sched.Where(a => !hasSucc.Contains(a.Index)).Select(a => a.Index).ToHashSet();
        double allowance = Math.Max(1, n * 0.01);
        var exemptPred = noPred.Count <= allowance ? noPred : new HashSet<int>();
        var exemptSucc = noSucc.Count <= allowance ? noSucc : new HashSet<int>();

        var logic = new List<HealthFlag>();
        foreach (var a in sched)
        {
            bool mp = noPred.Contains(a.Index) && !exemptPred.Contains(a.Index);
            bool ms = noSucc.Contains(a.Index) && !exemptSucc.Contains(a.Index);
            if (mp || ms)
                logic.Add(Flag(a) with { Reason = mp && ms ? "missing predecessor & successor" : mp ? "missing predecessor" : "missing successor" });
        }
        var negLags = AllRelationships((_, lag) => lag < 0).ToList();
        var posLags = AllRelationships((_, lag) => lag > 0).ToList();
        var longLags = AllRelationships((_, lag) => lag / 60.0 > settings.LongLagHours).ToList();
        var byType = new Dictionary<string, int>();
        foreach (var t in rels.Select(x => x.Type).Concat(s.ExternalSuccessors.Select(x => x.Type)))
            byType[Rel(t)] = byType.GetValueOrDefault(Rel(t)) + 1;
        int fs = byType.GetValueOrDefault("PR_FS");

        // ---- progress
        var oos = new List<HealthFlag>();
        var oosSeen = new HashSet<int>();
        foreach (var x in s.Relationships.Where(x => x.Pred >= 0))
        {
            Activity p = acts[x.Pred], q = acts[x.Succ];
            bool bad = x.Type switch
            {
                RelType.FS => q.ActualStart != Time.None && p.ActualFinish != Time.None && q.ActualStart < p.ActualFinish,
                RelType.SS => q.ActualStart != Time.None && p.ActualStart != Time.None && q.ActualStart < p.ActualStart,
                RelType.FF => q.ActualFinish != Time.None && p.ActualFinish != Time.None && q.ActualFinish < p.ActualFinish,
                _ => q.ActualFinish != Time.None && p.ActualStart != Time.None && q.ActualFinish < p.ActualStart,
            };
            if (bad && oosSeen.Add(q.Index))
                oos.Add(Flag(q) with { Reason = $"progressed before {x.Type} predecessor {p.Code} - {p.Name} allowed it" });
        }

        var invalid = new List<HealthFlag>();
        foreach (var a in sched)
        {
            var why = new List<string>();
            if (a.ActualStart != Time.None && a.ActualStart > dd) why.Add("actual start is after the data date");
            if (a.ActualFinish != Time.None && a.ActualFinish > dd) why.Add("actual finish is after the data date");
            if (a.P6StatusCode == "TK_Complete" && a.ActualFinish == Time.None) why.Add("marked complete with no actual finish date");
            if (a.P6StatusCode == "TK_NotStart" && a.ActualStart != Time.None) why.Add("marked not-started but has an actual start date");
            if (a.ActualStart != Time.None && a.ActualFinish != Time.None && a.ActualFinish < a.ActualStart) why.Add("actual finish before actual start");
            if (why.Count > 0) invalid.Add(Flag(a) with { Reasons = why });
        }

        // ---- dangling logic: started activities are left out of Dangling Start (their start is actual, not driven)
        var dStart = sched.Where(a => !exemptPred.Contains(a.Index) && a.ActualStart == Time.None && a.P6StatusCode != "TK_Complete"
                                      && !startDriven.Contains(a.Index)).Select(Flag).ToList();
        var dFinish = sched.Where(a => !exemptSucc.Contains(a.Index) && !finishDrives.Contains(a.Index)).Select(Flag).ToList();

        // ---- float and durations, from the recalculated schedule
        double? TfHours(Activity a) => r.TF[a.Index] == Time.None ? null : r.TF[a.Index] / 60.0;
        var largeFloat = sched.Where(a => TfHours(a) > settings.LargeFloatHours)
            .Select(a => Flag(a) with { ValueKey = "total_float_hr", Value = Math.Round(TfHours(a)!.Value, 4) }).ToList();
        var negFloat = sched.Where(a => TfHours(a) < 0)
            .Select(a => Flag(a) with { ValueKey = "total_float_hr", Value = Math.Round(TfHours(a)!.Value, 4) }).ToList();
        var work = sched.Where(a => a.Type != ActivityType.LevelOfEffort && !a.IsMilestone).ToList();
        var largeDur = work.Where(a => a.RemainingDuration / 60.0 > settings.LargeDurationHours)
            .Select(a => Flag(a) with { ValueKey = "remaining_duration_hr", Value = Math.Round(a.RemainingDuration / 60.0, 4) }).ToList();

        // ---- baseline (target) dates
        long Current(Activity a) => a.ActualFinish != Time.None ? a.ActualFinish : r.EF[a.Index];
        var late = sched.Where(a => a.P6("target_end_date") is long tf && tf != Time.None && Current(a) != Time.None && Current(a) > tf)
            .Select(a => Flag(a) with { BaselineFinish = Time.Format(a.P6("target_end_date")), CurrentFinish = Time.Format(Current(a)) }).ToList();
        var due = sched.Where(a => a.Type != ActivityType.LevelOfEffort && a.P6("target_end_date") != Time.None && a.P6("target_end_date") <= dd).ToList();
        int completed = due.Count(a => a.P6StatusCode == "TK_Complete");
        double? bei = due.Count > 0 ? (double)completed / due.Count : null;

        // ---- resources
        List<HealthFlag>? unresourced = s.HasResourceTable ? work.Where(a => !a.HasAssignment).Select(Flag).ToList() : null;

        // ---- constraints
        List<HealthFlag> Constrained(Dictionary<string, string> kinds) => sched.Select(a =>
        {
            var found = new[] { a.Constraint1, a.Constraint2 }.Where(c => c is { } k && kinds.ContainsKey(k.Type))
                .Select(c => kinds[c!.Value.Type] + (c.Value.Date != Time.None ? $" ({Time.FromMinutes(c.Value.Date):yyyy-MM-dd})" : "")).ToList();
            return found.Count == 0 ? null : Flag(a) with { Constraint = string.Join(", ", found) };
        }).Where(f => f != null).Select(f => f!).ToList();
        var hard = Constrained(Hard);
        var soft = Constrained(Soft);

        var rep = new HealthReport
        {
            ProjectId = s.ProjectId, ProjectCode = s.ProjectCode, DataDate = dd, PlanStart = s.PlanStart, ProjectFinish = r.ProjectFinish,
            TotalActivities = acts.Count, Schedulable = n, TotalRelationships = nRel,
            ProgressPct = Pct(withActuals, n), Unstarted = unstarted, HasResourceData = s.HasResourceTable,
            TaskTypes = acts.GroupBy(a => a.Type).ToDictionary(g => TypeNames[g.Key], g => g.Count()),
        };
        foreach (var a in acts)
            foreach (var c in new[] { a.Constraint1, a.Constraint2 })
                if (c is { } k && !CpmEngine.KnownConstraints.Contains(k.Type))
                    rep.AppNotes.Add($"{a.Code}: constraint {(Soft.TryGetValue(k.Type, out var nm) ? nm : k.Type)} is not modelled by the engine yet; its dates may differ from P6.");

        // ---------------------------------------------------------------- P6 Check Schedule
        void P6(string section, string key, string label, string description, int count, int denom, List<HealthFlag>? flagged,
                string? note = null, bool na = false, string unit = "%")
        {
            var (op, target) = settings.P6Targets[key];
            double? actual = na ? null : unit == "%" ? Pct(count, denom) : count;
            rep.P6.Add(new HealthItem
            {
                Section = section, Key = key, Label = label, Description = description, Count = count, Denominator = denom,
                Actual = actual, Unit = unit, Operator = op, Target = target, Note = note, FlagKey = key,
                Status = na ? HealthStatus.NotApplicable : Of(Meets(actual!.Value, op, target)),
                Flagged = flagged ?? new List<HealthFlag>(), FlaggedTotal = flagged?.Count ?? count,
            });
        }

        P6(RelationshipsTab, "logic", "Logic", "Activities missing predecessors or successors.", logic.Count, n, logic);
        P6(RelationshipsTab, "negative_lags", "Negative lags", "Relationships with a lag duration of less than 0.", negLags.Count, nRel, negLags);
        P6(RelationshipsTab, "positive_lags", "Positive Lags", "Relationships with a positive lag duration.", posLags.Count, nRel, posLags,
            note: settings.P6Targets["positive_lags"].Op == ">"
                ? "Shown with the '>' operator exactly as configured in the template, so it passes when MORE relationships carry positive lag. "
                  + "Most guidance, DCMA included, treats less lag as better: DCMA #3 below reports the same number against '< 5%'. "
                  + "Confirm which reading your organisation means."
                : null);
        P6(RelationshipsTab, "long_lags", "Long Lags", $"Relationships with a lag duration greater than {Hours(settings.LongLagHours)} h.", longLags.Count, nRel, longLags);
        {
            var (op, target) = settings.P6Targets["relationship_types"];
            double share = Pct(fs, nRel);
            rep.P6.Add(new HealthItem
            {
                Section = RelationshipsTab, Key = "relationship_types", Label = "Relationship Types",
                Description = "The majority of the relationships should be Finish to Start.", Count = fs, Denominator = nRel,
                Actual = share, Unit = "%", Operator = op, Target = target, FlagKey = "relationship_types",
                Status = Of(Meets(share, op, target)), StatusConventional = Of(share >= 90),
                Note = op.StartsWith('<')
                    ? "The template's operator ('" + op + "') is shown for the status, but reads against the description: a high Finish to Start share is healthy. "
                      + "The conventional reading (FS share of 90% or more) is given as status_conventional and used in DCMA #4."
                    : null,
                FlaggedTotal = fs, Breakdown = byType,
            });
        }
        P6(RelationshipsTab, "out_of_sequence", "Out Of Sequence", "Activities which are completed or in progress before their predecessors allow.", oos.Count, n, oos,
            na: unstarted, note: unstarted ? "The schedule has no actual progress (a baseline or not-started export), so there is nothing out of sequence to check." : null);
        P6(RelationshipsTab, "resources_cost", "Resources / Cost", "Activities that do not have an expense or resource assigned.", unresourced?.Count ?? 0, n, unresourced,
            na: unresourced == null, note: unresourced == null ? "The file has no TASKRSRC table (no resource or cost assignments were exported), so this cannot be checked." : null);
        P6(RelationshipsTab, "dangling_start", "Dangling Start", "Activities whose start is not driven by a predecessor (started activities left out).", dStart.Count, n, dStart);
        P6(RelationshipsTab, "dangling_finish", "Dangling Finish", "Activities whose finish does not drive a successor.", dFinish.Count, n, dFinish);
        P6(DatesTab, "large_float", "Large Float", $"Activities with total float greater than {Hours(settings.LargeFloatHours)} h.", largeFloat.Count, n, largeFloat);
        P6(DatesTab, "negative_float", "Negative Float", "Activities with total float less than 0.", negFloat.Count, n, negFloat);
        P6(DatesTab, "large_durations", "Large Durations", $"Activities with a remaining duration greater than {Hours(settings.LargeDurationHours)} h.", largeDur.Count, n, largeDur);
        P6(DatesTab, "invalid_progress_dates", "Activities with Invalid progress dates",
            "Actual dates after the data date, status and actual dates that disagree, or a finish before the start.", invalid.Count, n, invalid);
        P6(DatesTab, "late_activities", "Late Activities", "Activities scheduled to finish later than their baseline finish.", late.Count, n, late,
            na: unstarted, note: unstarted ? "The schedule has no actual progress: this export is itself the baseline, so there is nothing to compare." : null);
        {
            var (op, target) = settings.P6Targets["bei"];
            rep.P6.Add(new HealthItem
            {
                Section = DatesTab, Key = "bei", Label = "BEI - Baseline Execution Index",
                Description = "Activities completed against activities that should be complete by the data date per the baseline.",
                Count = bei == null ? null : completed, Denominator = bei == null ? null : due.Count, Actual = bei, Unit = "index",
                Operator = op, Target = target, FlagKey = "bei",
                Status = bei is double v ? Of(Meets(v, op, target)) : HealthStatus.NotApplicable,
                Note = bei == null ? "No activity has a baseline finish on or before the data date, so BEI cannot be measured yet." : null,
            });
        }
        P6(ConstraintsTab, "hard_constraints", "Hard Constraints",
            "Constraints that prevent activities from being moved (Start On, Finish On, Mandatory Start, Mandatory Finish).", hard.Count, n, hard);
        P6(ConstraintsTab, "soft_constraints", "Soft Constraints",
            "Constraints that do not prevent activities from being moved (Start or Finish On or Before / After, As Late As Possible).", soft.Count, n, soft);

        // ---------------------------------------------------------------- DCMA 14-Point
        var shared = rep.P6.ToDictionary(i => i.Key, i => i.Flagged);
        void D(string num, string name, string description, string op, double? target, string unit, double? actual, int? count, int? denom,
               HealthStatus status, string? flagKey = null, string? note = null, bool? actualBool = null)
        {
            var flagged = flagKey != null && shared.TryGetValue(flagKey, out var f) ? f : Array.Empty<HealthFlag>();
            rep.Dcma.Add(new HealthItem
            {
                Key = "dcma_" + num, Number = num, Label = name, Description = description, Operator = op, Target = target, Unit = unit,
                Actual = actual, ActualBool = actualBool, Count = count, Denominator = denom, Status = status, Note = note,
                FlagKey = flagKey ?? "dcma_" + num, Flagged = flagged, FlaggedTotal = flagKey != null ? flagged.Count : count ?? 0,
            });
        }
        double lp = Pct(logic.Count, n), lg = Pct(posLags.Count, nRel), fsp = Pct(fs, nRel), hc = Pct(hard.Count, n),
               hf = Pct(largeFloat.Count, n), hd = Pct(largeDur.Count, n);
        D("1", "Logic", "Activities (not the project's start and finish) missing a predecessor or successor.", "<", 5, "%", lp, logic.Count, n, Of(lp < 5), "logic");
        D("2", "Leads (Negative Lag)", "Relationships with negative lag.", "<=", 0, "count", negLags.Count, negLags.Count, nRel, Of(negLags.Count == 0), "negative_lags");
        D("3", "Lags", "Relationships with positive lag.", "<", 5, "%", lg, posLags.Count, nRel, Of(lg < 5), "positive_lags");
        D("4", "Relationship Types", "Share of relationships that are Finish to Start.", ">=", 90, "%", fsp, fs, nRel, Of(fsp >= 90));
        D("5", "Hard Constraints", "Activities with a hard constraint (Mandatory, Start On, Finish On).", "<", 5, "%", hc, hard.Count, n, Of(hc < 5), "hard_constraints");
        D("6", "High Float", $"Activities with total float greater than 44 working days ({Hours(settings.LargeFloatHours)} h).", "<", 5, "%", hf, largeFloat.Count, n, Of(hf < 5), "large_float");
        D("7", "Negative Float", "Activities with negative total float.", "<=", 0, "count", negFloat.Count, negFloat.Count, n, Of(negFloat.Count == 0), "negative_float");
        D("8", "High Duration", $"Activities (not LOE or milestones) with remaining duration greater than 44 working days ({Hours(settings.LargeDurationHours)} h).",
            "<", 5, "%", hd, largeDur.Count, n, Of(hd < 5), "large_durations");
        D("9", "Invalid Dates", "Activities with actual dates after the data date or other inconsistent progress dates.", "<=", 0, "count",
            invalid.Count, invalid.Count, n, Of(invalid.Count == 0), "invalid_progress_dates");
        if (unresourced == null)
            D("10", "Resources", "Activities without a resource or cost assignment (informational: DCMA sets no universal target).", "<", 5, "%",
                null, null, n, HealthStatus.NotApplicable, "resources_cost", "The file has no TASKRSRC table: resource and cost assignments were not exported.");
        else
        {
            double rp = Pct(unresourced.Count, n);
            D("10", "Resources", "Activities without a resource or cost assignment (informational: DCMA sets no universal target).", "<", 5, "%",
                rp, unresourced.Count, n, rp < 5 ? HealthStatus.Pass : HealthStatus.FailInformational, "resources_cost");
        }
        if (unstarted)
            D("11", "Missed Activities", "Activities finishing later than their baseline finish.", "<", 5, "%", null, null, n,
                HealthStatus.NotApplicable, "late_activities", "The schedule has no actual progress, so there is no baseline to compare against yet.");
        else
        {
            double mp = Pct(late.Count, n);
            D("11", "Missed Activities", "Activities finishing later than their baseline finish.", "<", 5, "%", mp, late.Count, n, Of(mp < 5), "late_activities");
        }

        // #12 Critical Path Test: delay the open critical activity with the least float (earliest first) by 600 working days;
        // on a continuous critical path the project finish moves as far as that activity's finish.
        var pcal = s.Settings.ProjectCalendar;
        double mpd = pcal.MinutesPerDay;
        int critical = acts.Count(a => !a.IsSummary && r.TF[a.Index] != Time.None && r.TF[a.Index] <= s.Settings.CriticalFloat);
        var pick = acts.Where(a => !a.IsSummary && a.RemainingDuration > 0 && r.TF[a.Index] != Time.None)
            .OrderBy(a => r.TF[a.Index]).ThenBy(a => r.ES[a.Index]).ThenBy(a => a.Index).FirstOrDefault();
        const string cptDesc = "Delaying a critical activity moves the project finish by the same amount: the critical path runs unbroken to the finish.";
        if (pick == null)
            D("12", "Critical Path Test", cptDesc, "n/a", null, "bool", null, critical, n, HealthStatus.NotApplicable, note: "No open activity to delay.");
        else
        {
            try
            {
                var dur = acts.Select(a => a.RemainingDuration).ToArray();
                dur[pick.Index] += (long)CriticalPathTestDays * pick.Calendar.MinutesPerDay;
                var r2 = new CpmEngine(s).Run(dur, backward: false);
                double actMoved = pcal.WorkBetween(r.EF[pick.Index], r2.EF[pick.Index]) / mpd;
                double finMoved = pcal.WorkBetween(r.ProjectFinish, r2.ProjectFinish) / mpd;
                bool ok = finMoved > 0 && finMoved >= actMoved - 1;
                D("12", "Critical Path Test", cptDesc, "n/a", null, "bool", null, critical, n, Of(ok), actualBool: ok,
                    note: $"Delaying {pick.Code} by {CriticalPathTestDays} working days moved its finish by {actMoved:0} and the project finish by {finMoved:0} working days. "
                          + $"{critical} activities are critical.");
            }
            catch (CalendarHorizonException)
            {
                D("12", "Critical Path Test", cptDesc, "n/a", null, "bool", null, critical, n, HealthStatus.NotApplicable,
                    note: "The delayed schedule runs past the calendar range, so the test could not be run.");
            }
        }

        // #13 CPLI = (critical path length + project total float) / critical path length, in working days of the project
        // calendar from the data date; the project total float is the Must Finish By minus the finish (0 without one).
        double cpl = pcal.WorkBetween(dd, r.ProjectFinish) / mpd;
        long mfb = s.Settings.MustFinishBy;
        double ptf = mfb == Time.None ? 0 : pcal.WorkBetween(r.ProjectFinish, mfb) / mpd;
        const string cpliDesc = "(Critical path length + project total float) / critical path length.";
        if (cpl <= 0)
            D("13", "Critical Path Length Index (CPLI)", cpliDesc, ">=", 0.95, "index", null, null, null, HealthStatus.NotApplicable,
                note: "The project finishes at the data date, so there is no remaining critical path.");
        else
        {
            double cpli = (cpl + ptf) / cpl;
            D("13", "Critical Path Length Index (CPLI)", cpliDesc, ">=", 0.95, "index", cpli, null, null, Of(cpli >= 0.95),
                note: $"Critical path {cpl:0.#} working days from the data date; project total float {ptf:0.#} working days "
                      + (mfb == Time.None ? "(no Must Finish By, so 0)." : $"to the Must Finish By {Time.Format(mfb)}."));
        }
        if (bei is double b)
            D("14", "Baseline Execution Index (BEI)", "Completed activities / activities that should be complete by the data date per the baseline.",
                ">=", 0.95, "index", b, completed, due.Count, Of(b >= 0.95));
        else
            D("14", "Baseline Execution Index (BEI)", "Completed activities / activities that should be complete by the data date per the baseline.",
                ">=", 0.95, "index", null, null, null, HealthStatus.NotApplicable,
                note: "No activity has a baseline finish on or before the data date, so BEI cannot be measured yet.");
        return rep;
    }
}
