using System.Globalization;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Numerics;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Core.Model;

/// <summary>Builds a <see cref="Schedule"/> for one project in an XER document. Twin of model.py.</summary>
public static class ScheduleBuilder
{
    private static readonly Dictionary<string, RelType> RelCodes = new(StringComparer.Ordinal)
    {
        ["PR_FS"] = RelType.FS, ["PR_SS"] = RelType.SS, ["PR_FF"] = RelType.FF, ["PR_SF"] = RelType.SF,
    };

    private static readonly Dictionary<string, ActivityStatus> StatusCodes = new(StringComparer.Ordinal)
    {
        ["TK_NotStart"] = ActivityStatus.NotStarted, ["TK_Active"] = ActivityStatus.InProgress, ["TK_Complete"] = ActivityStatus.Complete,
    };

    private static readonly Dictionary<string, ActivityType> TypeCodes = new(StringComparer.Ordinal)
    {
        ["TT_Task"] = ActivityType.Task, ["TT_Rsrc"] = ActivityType.ResourceDependent, ["TT_Mile"] = ActivityType.StartMilestone,
        ["TT_FinMile"] = ActivityType.FinishMilestone, ["TT_LOE"] = ActivityType.LevelOfEffort, ["TT_WBS"] = ActivityType.WbsSummary,
    };

    public static List<(string Id, string ShortName)> ListProjects(XerDocument doc) =>
        doc.Rows("PROJECT").Select(p => (p["proj_id"], p["proj_short_name"])).ToList();

    private static double F(string s, double dflt = 0.0) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : dflt;

    public static Schedule Build(XerDocument doc, string? project = null, double horizonYears = 30)
    {
        var s = new Schedule();
        var projects = doc.Rows("PROJECT").ToList();
        if (projects.Count == 0) throw new InvalidDataException("XER contains no PROJECT table");
        XerRow proj;
        if (project == null)
        {
            proj = projects[0];
            if (projects.Count > 1) s.Warnings.Add($"{projects.Count} projects in file; using {proj["proj_short_name"]}");
        }
        else
        {
            int k = projects.FindLastIndex(p => p["proj_id"] == project || p["proj_short_name"] == project);
            if (k < 0) throw new InvalidDataException($"project '{project}' not found");
            proj = projects[k];
        }
        string pid = proj["proj_id"];
        s.ProjectId = pid;
        s.ProjectCode = proj["proj_short_name"];

        var st = s.Settings;
        st.DataDate = Time.ParseP6(proj["last_recalc_date"]);
        if (st.DataDate == Time.None) st.DataDate = Time.ParseP6(proj["plan_start_date"]);
        if (st.DataDate == Time.None) throw new InvalidDataException("project has no data date (last_recalc_date) or planned start");
        // P6 "Must Finish By". scd_end_date is P6's calculated scheduled finish, not a constraint.
        st.MustFinishBy = Time.ParseP6(proj["plan_end_date"]);
        string lagc = proj["sched_calendar_on_relationship_lag"].ToLowerInvariant();
        st.LagCalendar = lagc.Contains("succ") ? LagCalendarMode.Successor
            : lagc.Contains("24") ? LagCalendarMode.TwentyFourHour
            : lagc.Contains("proj") ? LagCalendarMode.ProjectDefault
            : LagCalendarMode.Predecessor;
        string po = proj["sched_progress_override"];
        st.RetainedLogic = (po.Length == 0 ? "N" : po).ToUpperInvariant() != "Y";
        string rl = proj.Has("sched_retained_logic") ? proj["sched_retained_logic"] : "Y";
        if (rl.ToUpperInvariant() == "N" && po.ToUpperInvariant() != "Y")
            s.Warnings.Add("neither retained logic nor progress override set; using retained logic");
        string ft = proj["sched_float_type"].ToLowerInvariant();
        st.FloatType = ft.Contains("sf") || ft.Contains("start") ? FloatType.Start
            : ft.Contains("min") || ft.Contains("small") ? FloatType.Smallest
            : FloatType.Finish;
        st.CriticalFloat = MathX.RoundHalfUp(F(proj["critical_drtn_hr_cnt"], 0.0) * 60);

        // ---- horizon from every date we can see
        var dates = new List<long> { st.DataDate };
        var tasksAll = doc.Rows("TASK").ToList();
        var tasks = tasksAll.Where(t => t["proj_id"] == pid).ToList();
        foreach (var t in tasks)
            foreach (var k in new[] { "act_start_date", "act_end_date", "target_start_date", "early_start_date", "cstr_date", "cstr_date2" })
            {
                long v = Time.TryParseP6(t[k]);
                if (v != Time.None) dates.Add(v);
            }
        if (st.MustFinishBy != Time.None) dates.Add(st.MustFinishBy);
        long h0 = dates.Min() - 400L * 1440;
        long h1 = dates.Max() + (long)(horizonYears * 365.25) * 1440;
        // The backward pass starts from the must-finish-by. A finish after it (every finish lies before h1)
        // pulls late dates back by as much as the overrun, so the calendars reach that far before it.
        if (st.MustFinishBy != Time.None)
            h0 = Math.Min(h0, st.MustFinishBy - (h1 - st.DataDate) - 400L * 1440);
        h0 = Time.ToMinutes(Time.FromMinutes(h0).Date);

        // ---- calendars
        var calRows = doc.Rows("CALENDAR").ToList();
        foreach (var c in calRows)
        {
            var (week, exc, warns) = CalendarDataParser.Parse(c["clndr_data"]);
            foreach (var w in warns) s.Warnings.Add($"calendar {c["clndr_name"]}: {w}");
            try
            {
                s.Calendars[c["clndr_id"]] = new WorkCalendar(c["clndr_id"], c["clndr_name"], week, exc, h0, h1, F(c["day_hr_cnt"], 8.0));
            }
            catch (InvalidOperationException e)
            {
                s.Warnings.Add(e.Message);
            }
        }
        s.Cal24 = WorkCalendar.TwentyFourHour(h0, h1);
        string? defaultCal = proj["clndr_id"];
        if (!s.Calendars.ContainsKey(defaultCal))
        {
            var defaults = calRows.Where(c => c["default_flag"] == "Y" && s.Calendars.ContainsKey(c["clndr_id"])).Select(c => c["clndr_id"]).ToList();
            defaultCal = defaults.Count > 0 ? defaults[0] : (s.Calendars.Count > 0 ? calRows.Select(c => c["clndr_id"]).First(id => s.Calendars.ContainsKey(id)) : null);
        }
        if (defaultCal == null)
        {
            s.Calendars["__default__"] = new WorkCalendar("__default__", "Default 5x8", CalendarDataParser.DefaultWeek(),
                new Dictionary<DateOnly, List<WorkShift>>(), h0, h1, 8.0);
            defaultCal = "__default__";
            s.Warnings.Add("no usable calendars in file; default 5x8 used");
        }
        st.ProjectCalendar = s.Calendars[defaultCal];

        // ---- WBS
        int wbsOrder = 0;
        foreach (var w in doc.Rows("PROJWBS"))
            if (w["proj_id"] == pid)
                s.Wbs[w["wbs_id"]] = new WbsNode(w["parent_wbs_id"], w["wbs_short_name"], w["wbs_name"],
                    int.TryParse(w["seq_num"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq) ? seq : 0, wbsOrder++);

        // ---- activity codes
        var codeTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in doc.Rows("ACTVTYPE")) codeTypes[r["actv_code_type_id"]] = r["actv_code_type"];
        var codeVals = new Dictionary<string, (string TypeId, string Short)>(StringComparer.Ordinal);
        foreach (var r in doc.Rows("ACTVCODE")) codeVals[r["actv_code_id"]] = (r["actv_code_type_id"], r["short_name"]);
        var taskCodes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var r in doc.Rows("TASKACTV"))
        {
            if (!codeVals.TryGetValue(r["actv_code_id"], out var cv)) continue;
            if (!taskCodes.TryGetValue(r["task_id"], out var m)) taskCodes[r["task_id"]] = m = new Dictionary<string, string>(StringComparer.Ordinal);
            m[codeTypes.TryGetValue(cv.TypeId, out var tn) ? tn : cv.TypeId] = cv.Short;
        }

        // ---- activities
        var idxByTid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tasks)
        {
            string code = t.Has("task_code") ? t["task_code"] : t["task_id"];
            var a = new Activity
            {
                Index = s.Activities.Count,
                TaskId = t["task_id"],
                Code = code,
                Name = t["task_name"],
                WbsId = t["wbs_id"],
            };
            if (TypeCodes.TryGetValue(t["task_type"], out var tt)) a.Type = tt;
            else
            {
                a.Type = ActivityType.Task;
                s.Warnings.Add($"{a.Code}: unknown task_type '{t["task_type"]}', treated as task");
            }
            a.Status = StatusCodes.TryGetValue(t["status_code"], out var sc) ? sc : ActivityStatus.NotStarted;
            if (s.Calendars.TryGetValue(t["clndr_id"], out var cal)) a.Calendar = cal;
            else
            {
                s.Warnings.Add($"{a.Code}: calendar {t["clndr_id"]} missing; project calendar used");
                a.Calendar = st.ProjectCalendar;
            }
            a.OriginalDuration = Math.Max(0, MathX.RoundHalfUp(F(t["target_drtn_hr_cnt"]) * 60));
            string rem = t["remain_drtn_hr_cnt"];
            a.RemainingDuration = rem.Length > 0 ? Math.Max(0, MathX.RoundHalfUp(F(rem) * 60)) : a.OriginalDuration;
            if (a.Status == ActivityStatus.Complete) a.RemainingDuration = 0;
            a.ActualStart = Time.ParseP6(t["act_start_date"]);
            a.ActualFinish = Time.ParseP6(t["act_end_date"]);
            if (a.Status != ActivityStatus.NotStarted && a.ActualStart == Time.None)
            {
                s.Warnings.Add($"{a.Code}: status {t["status_code"]} without actual start; treated as not started");
                a.Status = ActivityStatus.NotStarted;
            }
            if (a.Status == ActivityStatus.Complete && a.ActualFinish == Time.None)
            {
                s.Warnings.Add($"{a.Code}: complete without actual finish; treated as in progress");
                a.Status = ActivityStatus.InProgress;
            }
            if (a.IsMilestone) { a.OriginalDuration = 0; a.RemainingDuration = 0; }
            if (t["cstr_type"].Length > 0) a.Constraint1 = new Constraint(t["cstr_type"], Time.ParseP6(t["cstr_date"]));
            if (t["cstr_type2"].Length > 0) a.Constraint2 = new Constraint(t["cstr_type2"], Time.ParseP6(t["cstr_date2"]));
            foreach (var k in new[] { "early_start_date", "early_end_date", "late_start_date", "late_end_date",
                                      "restart_date", "reend_date", "rem_late_start_date", "rem_late_end_date" })
                a.P6Dates[k] = Time.TryParseP6(t[k]);
            string tf = t["total_float_hr_cnt"];
            a.P6TotalFloatHours = tf.Length > 0 ? F(tf) : null;
            if (taskCodes.TryGetValue(a.TaskId, out var codes))
                foreach (var kv in codes) a.Codes[kv.Key] = kv.Value;
            idxByTid[a.TaskId] = a.Index;
            if (s.ByCode.ContainsKey(a.Code)) s.Warnings.Add($"duplicate activity id {a.Code}");
            else s.ByCode[a.Code] = a.Index;
            s.Activities.Add(a);
        }

        // ---- relationships
        var extTasks = new Dictionary<string, XerRow>(StringComparer.Ordinal);
        foreach (var t in tasksAll) if (t["proj_id"] != pid) extTasks[t["task_id"]] = t;
        foreach (var r in doc.Rows("TASKPRED"))
        {
            if (!idxByTid.TryGetValue(r["task_id"], out int succ)) continue;
            if (!RelCodes.TryGetValue(r["pred_type"], out var rtype))
            {
                s.Warnings.Add($"relationship {r["task_pred_id"]}: unknown type '{r["pred_type"]}', FS assumed");
                rtype = RelType.FS;
            }
            long lag = MathX.RoundHalfUp(F(r["lag_hr_cnt"]) * 60);
            if (!idxByTid.TryGetValue(r["pred_task_id"], out int pred))
            {
                if (!extTasks.TryGetValue(r["pred_task_id"], out var et))
                {
                    s.Warnings.Add($"{s.Activities[succ].Code}: predecessor {r["pred_task_id"]} not in file; ignored");
                    continue;
                }
                long es = Time.ParseP6(et["act_start_date"]);
                if (es == Time.None) es = Time.ParseP6(et["early_start_date"]);
                long ef = Time.ParseP6(et["act_end_date"]);
                if (ef == Time.None) ef = Time.ParseP6(et["early_end_date"]);
                s.Relationships.Add(new Relationship(-1, succ, rtype, lag, es, ef));
                s.Warnings.Add($"{s.Activities[succ].Code}: external predecessor {et["task_code"]} held at its P6 dates");
                continue;
            }
            if (pred == succ)
            {
                s.Warnings.Add($"{s.Activities[succ].Code}: self-relationship ignored");
                continue;
            }
            s.Relationships.Add(new Relationship(pred, succ, rtype, lag));
        }

        int n = s.Activities.Count;
        s.Preds = new List<int>[n];
        s.Succs = new List<int>[n];
        for (int i = 0; i < n; i++) { s.Preds[i] = new List<int>(); s.Succs[i] = new List<int>(); }
        for (int k = 0; k < s.Relationships.Count; k++)
        {
            var r = s.Relationships[k];
            s.Preds[r.Succ].Add(k);
            if (r.Pred >= 0) s.Succs[r.Pred].Add(k);
        }
        return s;
    }
}
