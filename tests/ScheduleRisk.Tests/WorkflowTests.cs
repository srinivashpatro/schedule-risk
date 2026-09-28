using ScheduleRisk.Core.Reporting;

namespace ScheduleRisk.Tests;

/// <summary>The seven-step risk workflow from the design (design/workflow/steps.png) and the app's use of it.</summary>
public class WorkflowTests
{
    private static readonly string WebDir = Path.Combine(Path.GetDirectoryName(TestData.Dir)!, "src", "ScheduleRisk.Web");

    [Fact]
    public void The_steps_follow_the_design_in_order()
    {
        Assert.Equal(new[] { "Setup", "Identify", "Assess", "Promote", "Schedule check", "Model", "Results", "Review" },
            Workflow.Steps.Select(s => s.Title));
        Assert.Equal(new[] { "01", "02", "03", "", "04", "05", "06", "07" }, Workflow.Steps.Select(s => s.Number));
        Assert.Equal("Score bands become probabilities and days", Workflow.Get(WorkflowStep.Promote).Description);
        Assert.Equal("P6 date verification and health checks", Workflow.Get(WorkflowStep.ScheduleCheck).Description);
        Assert.All(Workflow.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
    }

    [Fact]
    public void Only_promote_is_a_bridge_without_a_number()
    {
        Assert.Equal(new[] { WorkflowStep.Promote }, Workflow.Steps.Where(s => s.Bridge).Select(s => s.Step));
        Assert.Equal("Step 04 · Schedule check", Workflow.Get(WorkflowStep.ScheduleCheck).Kicker);
        Assert.Equal("Promote", Workflow.Get(WorkflowStep.Promote).Kicker);
    }

    [Fact]
    public void Placeholder_steps_say_what_they_will_hold()
    {
        foreach (var s in new[] { WorkflowStep.Identify, WorkflowStep.Assess, WorkflowStep.Promote })
            Assert.NotEmpty(Workflow.Get(s).Planned);
        foreach (var s in new[] { WorkflowStep.Setup, WorkflowStep.ScheduleCheck, WorkflowStep.Model, WorkflowStep.Results, WorkflowStep.Review })
            Assert.Empty(Workflow.Get(s).Planned);
    }

    [Fact]
    public void The_app_draws_its_steps_from_the_workflow()
    {
        string app = File.ReadAllText(Path.Combine(WebDir, "App.razor"));
        Assert.Contains("Workflow.Steps", app);
        Assert.Contains("<ReviewPanel", app);
        Assert.Contains("<StepPlaceholder", app);
        Assert.Contains("<SetupPanel", app);
        // The old three-step labels are gone from the panels' kickers.
        foreach (var f in new[] { "SchedulePanel.razor", "RiskModelPanel.razor", "ResultsPanel.razor" })
            Assert.DoesNotMatch(@"kicker"">Step 0[123]\b(?! ·)|Step 01 · Schedule<", File.ReadAllText(Path.Combine(WebDir, "Components", f)));
    }
}
