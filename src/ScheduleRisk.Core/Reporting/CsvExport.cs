using System.Globalization;
using System.Text;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Risk.Register;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>CSV outputs that open directly in Excel.</summary>
public static class CsvExport
{
    private static string Q(string? s)
    {
        s ??= "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string N(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Cpm(Schedule s, CpmResult r)
    {
        var sb = new StringBuilder("activity_id,name,status,early_start,early_finish,late_start,late_finish,total_float_days,critical\r\n");
        foreach (var a in s.Activities)
        {
            int j = a.Index;
            double tfd = r.TF[j] == Time.None ? double.NaN : r.TF[j] / (double)a.Calendar.MinutesPerDay;
            sb.Append(Q(a.Code)).Append(',').Append(Q(a.Name)).Append(',').Append(a.Status).Append(',')
              .Append(Time.Format(r.ES[j])).Append(',').Append(Time.Format(r.EF[j])).Append(',')
              .Append(Time.Format(r.LS[j])).Append(',').Append(Time.Format(r.LF[j])).Append(',')
              .Append(double.IsNaN(tfd) ? "" : N(tfd)).Append(',').Append(r.IsCritical(s, j) ? "Y" : "N").Append("\r\n");
        }
        return sb.ToString();
    }

    public static string Activities(SimulationSummary sum)
    {
        var sb = new StringBuilder("activity_id,name,criticality,sensitivity,cruciality\r\n");
        foreach (var a in sum.Activities)
            sb.Append(Q(a.Code)).Append(',').Append(Q(a.Name)).Append(',').Append(N(a.Criticality)).Append(',')
              .Append(N(a.Sensitivity)).Append(',').Append(N(a.Cruciality)).Append("\r\n");
        return sb.ToString();
    }

    public static string Risks(SimulationSummary sum)
    {
        var sb = new StringBuilder("risk_id,title,occurrence,sensitivity,mean_finish_delta_days\r\n");
        foreach (var r in sum.Risks)
            sb.Append(Q(r.Id)).Append(',').Append(Q(r.Title)).Append(',').Append(N(r.Occurrence)).Append(',')
              .Append(N(r.Sensitivity)).Append(',').Append(r.MeanFinishDeltaDays.HasValue ? N(r.MeanFinishDeltaDays.Value) : "").Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The risk register: one row per risk with its description, status, the cell and rating at each
    /// assessment point, its response and how many of its actions are open and overdue on <paramref name="today"/>.</summary>
    public static string Register(RiskRegister reg, DateOnly today)
    {
        var m = reg.Matrix;
        var sb = new StringBuilder("risk_id,title,cause,event,effect,category,kind,status,raised_by,raised,owner,inherent_cell,inherent_rating,"
            + "current_cell,current_rating,target_cell,target_rating,response,response_description,response_cost,open_actions,overdue_actions\r\n");
        string Cell(Assessment a) => m.Rate(a) is RiskRating r ? $"{m.CellName(a)},{r}" : ",";
        foreach (var r in reg.Risks)
        {
            sb.Append(Q(r.Id)).Append(',').Append(Q(r.Title)).Append(',').Append(Q(r.Cause)).Append(',').Append(Q(r.Event)).Append(',')
              .Append(Q(r.Effect)).Append(',').Append(Q(r.Category)).Append(',').Append(r.Kind.ToString().ToLowerInvariant()).Append(',')
              .Append(r.Status.ToString().ToLowerInvariant()).Append(',').Append(Q(r.RaisedBy)).Append(',')
              .Append(r.Raised?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "").Append(',').Append(Q(r.Owner)).Append(',')
              .Append(Cell(r.Inherent)).Append(',').Append(Cell(r.Current)).Append(',').Append(Cell(r.Target)).Append(',')
              .Append(r.Response.ToString().ToLowerInvariant()).Append(',').Append(Q(r.ResponseDescription)).Append(',')
              .Append(r.ResponseCost is double c ? N(c) : "").Append(',')
              .Append(r.Actions.Count(a => a.Status == ActionStatus.Open)).Append(',').Append(r.Actions.Count(a => a.Overdue(today))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>One row per iteration: finish date (for custom histograms in Excel).</summary>
    public static string Iterations(SimulationResult res)
    {
        var sb = new StringBuilder("iteration,project_finish\r\n");
        for (int i = 0; i < res.Finish.Count; i++) sb.Append(i + 1).Append(',').Append(Time.Format(res.Finish[i])).Append("\r\n");
        return sb.ToString();
    }
}
