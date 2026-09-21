using ScheduleRisk.Core.Calendars;

namespace ScheduleRisk.Core.Model;

public enum RelType { FS = 0, SS = 1, FF = 2, SF = 3 }

public enum ActivityStatus { NotStarted = 0, InProgress = 1, Complete = 2 }

public enum ActivityType { Task, ResourceDependent, StartMilestone, FinishMilestone, LevelOfEffort, WbsSummary }

public enum LagCalendarMode { Predecessor, Successor, TwentyFourHour, ProjectDefault }

public enum FloatType { Finish, Start, Smallest }

/// <summary>A constraint as stored in P6: type code (e.g. CS_MSOA) and date.</summary>
public readonly record struct Constraint(string Type, long Date);

public sealed class Activity
{
    public int Index { get; init; }
    public string TaskId { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public ActivityType Type { get; set; }
    public ActivityStatus Status { get; set; }
    public WorkCalendar Calendar { get; set; } = null!;
    public string WbsId { get; init; } = "";
    /// <summary>Original duration, working minutes.</summary>
    public long OriginalDuration { get; set; }
    /// <summary>Remaining duration, working minutes.</summary>
    public long RemainingDuration { get; set; }
    public long ActualStart { get; set; } = Time.None;
    public long ActualFinish { get; set; } = Time.None;
    public Constraint? Constraint1 { get; set; }
    public Constraint? Constraint2 { get; set; }
    /// <summary>Dates P6 stored in the file (early_start_date etc.), minutes or Time.None.</summary>
    public Dictionary<string, long> P6Dates { get; } = new(StringComparer.Ordinal);
    public double? P6TotalFloatHours { get; set; }
    /// <summary>Activity code type name -> value short name.</summary>
    public Dictionary<string, string> Codes { get; } = new(StringComparer.Ordinal);

    public bool IsMilestone => Type is ActivityType.StartMilestone or ActivityType.FinishMilestone;
    public bool IsSummary => Type is ActivityType.LevelOfEffort or ActivityType.WbsSummary;

    public long P6(string field) => P6Dates.TryGetValue(field, out var v) ? v : Time.None;

    public override string ToString() => $"{Code} {Name}";
}

public sealed class Relationship
{
    public Relationship(int pred, int succ, RelType type, long lag, long extStart = Time.None, long extFinish = Time.None)
    {
        Pred = pred; Succ = succ; Type = type; Lag = lag; ExternalStart = extStart; ExternalFinish = extFinish;
    }

    /// <summary>Predecessor index, or -1 for a predecessor in another project (held at its P6 dates).</summary>
    public int Pred { get; }
    public int Succ { get; }
    public RelType Type { get; }
    /// <summary>Lag in working minutes on the lag calendar.</summary>
    public long Lag { get; }
    public long ExternalStart { get; }
    public long ExternalFinish { get; }
}

public sealed class ScheduleSettings
{
    public long DataDate { get; set; } = Time.None;
    public long MustFinishBy { get; set; } = Time.None;
    public LagCalendarMode LagCalendar { get; set; } = LagCalendarMode.Predecessor;
    public bool RetainedLogic { get; set; } = true;
    public FloatType FloatType { get; set; } = FloatType.Finish;
    /// <summary>Activities with total float at or below this (minutes) are critical.</summary>
    public long CriticalFloat { get; set; }
    public WorkCalendar ProjectCalendar { get; set; } = null!;
}

public sealed record WbsNode(string ParentId, string ShortName, string Name);

public sealed class Schedule
{
    public string ProjectId { get; set; } = "";
    public string ProjectCode { get; set; } = "";
    public List<Activity> Activities { get; } = new();
    public List<Relationship> Relationships { get; } = new();
    public Dictionary<string, WorkCalendar> Calendars { get; } = new(StringComparer.Ordinal);
    public WorkCalendar Cal24 { get; set; } = null!;
    public ScheduleSettings Settings { get; } = new();
    public Dictionary<string, WbsNode> Wbs { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = new();
    public Dictionary<string, int> ByCode { get; } = new(StringComparer.Ordinal);
    public List<int>[] Preds { get; set; } = Array.Empty<List<int>>();
    public List<int>[] Succs { get; set; } = Array.Empty<List<int>>();

    public Activity Find(string code) => Activities[ByCode[code]];

    public string WbsPath(string wbsId)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? id = wbsId;
        while (id != null && Wbs.TryGetValue(id, out var node) && seen.Add(id))
        {
            parts.Add(node.ShortName);
            id = node.ParentId;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    public WorkCalendar LagCalendarFor(Relationship r)
    {
        switch (Settings.LagCalendar)
        {
            case LagCalendarMode.TwentyFourHour: return Cal24;
            case LagCalendarMode.ProjectDefault: return Settings.ProjectCalendar;
            case LagCalendarMode.Successor: return Activities[r.Succ].Calendar;
            default: return r.Pred < 0 ? Activities[r.Succ].Calendar : Activities[r.Pred].Calendar;
        }
    }
}
