using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Reporting.Export;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Risk.Register;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>07 Review: the reports carry the risk register (heat maps now and after the responses, the register table
/// and the actions) and the CSV export gains register.csv.</summary>
public class RegisterReportTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture fx;

    public RegisterReportTests(ExportFixture fx) => this.fx = fx;

    [Fact]
    public void The_report_has_the_register_after_the_cost_benefit()
    {
        var titles = fx.Doc.Sections.Select(x => x.Title).ToList();
        int cb = titles.IndexOf("Cost-benefit of responses"), reg = titles.IndexOf("Risk register"), act = titles.IndexOf("Risk actions");
        Assert.True(cb >= 0 && reg == cb + 1 && act == reg + 1, string.Join(" | ", titles));
        var section = fx.Doc.Sections[reg];
        Assert.Contains("4 approved risks", section.Lead);
        var charts = section.Blocks.OfType<ChartBlock>().Select(c => c.Chart.Key).ToList();
        Assert.Equal(new[] { "heatmap-current", "heatmap-target" }, charts);
        var table = section.Blocks.OfType<TableBlock>().Single().Table;
        Assert.Equal(4, table.Rows.Count);
        Assert.Equal(new[] { "ID", "Risk", "Category", "Owner", "Now", "After response", "Response" }, table.Header.Select(c => c.Text));
    }

    [Fact]
    public void Actions_list_overdue_ones_first_as_of_the_report_date()
    {
        var actions = fx.Doc.Sections.Single(x => x.Title == "Risk actions").Blocks.OfType<TableBlock>().Single().Table;
        Assert.Equal(3, actions.Rows.Count);
        Assert.Equal("Overdue", actions.Rows[0].Cells[4].Text);
        Assert.Equal("Chase the second supplier", actions.Rows[0].Cells[1].Text);
        Assert.Equal("Open", actions.Rows[1].Cells[4].Text);
        Assert.Equal("Done", actions.Rows[2].Cells[4].Text);
    }

    [Fact]
    public void Heat_map_charts_show_every_cell_in_its_rating_colour_with_its_risks()
    {
        var chart = fx.Doc.Charts.Single(c => c.Key == "heatmap-current");
        foreach (var cell in new[] { "I.A", "V.E", "III.C" }) Assert.Contains($">{cell}<", chart.Svg);
        Assert.Equal(25, System.Text.RegularExpressions.Regex.Matches(chart.Svg, "class=\"hm (rag-r|rag-a|rag-g)").Count);
        Assert.Contains("R01", chart.Svg);
        Assert.Contains(".rag-r{", chart.Svg);
        Assert.Equal("Heat map now (current assessment)", chart.Title);
    }

    [Fact]
    public void Without_a_register_the_report_has_no_register_sections()
    {
        var s = TestData.Load("synth_500.xer");
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), new CpmEngine(s).CriticalToProjectFinish());
        var mc = new MonteCarloEngine(s, m);
        var pre = SimulationSummary.Build(mc, mc.Run(100, 3));
        foreach (var reg in new[] { null, new RiskRegister() })
        {
            var doc = ReportContent.Build(new ReportInput(s, pre, Register: reg, Generated: ExportFixture.When), fx.Fonts);
            Assert.DoesNotContain(doc.Sections, x => x.Title.StartsWith("Risk register") || x.Title == "Risk actions");
        }
    }

    [Fact]
    public void The_html_report_has_the_heat_maps_and_tables()
    {
        string html = HtmlReport.Build(fx.Schedule, fx.Pre, fx.Post, register: fx.Register, generated: ExportFixture.When);
        Assert.Contains("<h2>Risk register</h2>", html);
        Assert.Contains("<h2>Risk actions</h2>", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "aria-label=\"Heat map").Count);
        Assert.Contains(">V.B<", html);
        Assert.Contains("Chase the second supplier", html);
    }

    [Fact]
    public void Register_csv_has_a_row_per_risk_with_cells_ratings_and_actions()
    {
        string csv = CsvExport.Register(fx.Register, DateOnly.FromDateTime(ExportFixture.When));
        var lines = csv.TrimEnd().Split("\r\n");
        Assert.Equal("risk_id,title,cause,event,effect,category,kind,status,raised_by,raised,owner,inherent_cell,inherent_rating,"
            + "current_cell,current_rating,target_cell,target_rating,response,response_description,response_cost,open_actions,overdue_actions", lines[0]);
        Assert.Equal(fx.Register.Risks.Count + 1, lines.Length);
        var r01 = lines.Single(l => l.StartsWith("R01,"));
        Assert.Contains(",III.C,Amber,III.C,Amber,II.B,Green,", r01);            // inherent = current (moved from the model), target after the response
        Assert.Contains("approved", r01);
        Assert.Contains(",mitigate,", r01);
        Assert.EndsWith(",2,1", r01);                                        // two open actions, one overdue
        Assert.Contains("\"Late vendor data, again\"", csv);                 // commas are quoted
    }
}
