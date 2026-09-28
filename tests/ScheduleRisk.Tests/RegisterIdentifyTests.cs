using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Tests;

/// <summary>Step 02 Identify: proposing risks, the approval workflow, and finding risks in the register.</summary>
public class RegisterIdentifyTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    private static RiskRegister Sample()
    {
        var reg = new RiskRegister();
        reg.Propose("Late heavy-lift crane", "Single crane supplier", "the crane arrives late", "erection of the converter slips",
            "Procurement", RiskKind.Threat, "Planner", Today);
        reg.Propose("Early permit", "Regulator backlog cleared", "the permit comes early", "piling can start sooner",
            "Regulatory", RiskKind.Opportunity, "Engineer", Today);
        reg.Propose("Monsoon flooding", "Heavy rain", "the site floods", "earthworks stop", "External", RiskKind.Threat, "Planner", Today);
        return reg;
    }

    [Fact]
    public void Proposing_a_risk_gives_it_the_next_id_and_proposed_status()
    {
        var reg = Sample();
        Assert.Equal(new[] { "R01", "R02", "R03" }, reg.Risks.Select(r => r.Id));
        var r = reg.Risks[0];
        Assert.Equal(RiskStatus.Proposed, r.Status);
        Assert.Equal(Today, r.Raised);
        Assert.Equal("Planner", r.RaisedBy);
        Assert.Equal("Procurement", r.Category);
    }

    [Fact]
    public void The_statement_joins_cause_event_and_effect()
    {
        var r = Sample().Risks[0];
        Assert.Equal("Because of single crane supplier, the crane arrives late, which would lead to erection of the converter slips.", r.Statement);
        var opportunity = Sample().Risks[1];
        Assert.StartsWith("Because of regulator backlog cleared, the permit comes early", opportunity.Statement);
        Assert.Equal("", new RegisterRisk().Statement);
        Assert.Equal("The site floods.", new RegisterRisk { Event = "the site floods" }.Statement);
        Assert.Equal("Because of heavy rain, the site floods.", new RegisterRisk { Cause = "Heavy rain.", Event = "the site floods" }.Statement);
    }

    [Fact]
    public void Approval_needs_a_title_and_an_event_and_prefers_a_cause_and_an_effect()
    {
        var r = new RegisterRisk { Id = "R09" };
        var issues = RiskRegister.ApprovalIssues(r);
        Assert.Contains(issues, i => i.Error && i.Text.Contains("title"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("event"));
        Assert.Contains(issues, i => !i.Error && i.Text.Contains("cause"));
        Assert.Contains(issues, i => !i.Error && i.Text.Contains("effect"));
        Assert.Empty(RiskRegister.ApprovalIssues(Sample().Risks[0]));
    }

    [Theory]
    [InlineData(RiskStatus.Proposed, RiskStatus.Approved, true)]
    [InlineData(RiskStatus.Proposed, RiskStatus.Rejected, true)]
    [InlineData(RiskStatus.Proposed, RiskStatus.Closed, false)]
    [InlineData(RiskStatus.Approved, RiskStatus.Closed, true)]
    [InlineData(RiskStatus.Approved, RiskStatus.Rejected, false)]
    [InlineData(RiskStatus.Approved, RiskStatus.Proposed, false)]
    [InlineData(RiskStatus.Rejected, RiskStatus.Proposed, true)]
    [InlineData(RiskStatus.Rejected, RiskStatus.Approved, false)]
    [InlineData(RiskStatus.Closed, RiskStatus.Approved, true)]
    [InlineData(RiskStatus.Closed, RiskStatus.Proposed, false)]
    public void Status_moves_along_the_approval_workflow(RiskStatus from, RiskStatus to, bool allowed)
    {
        Assert.Equal(allowed, RiskRegister.CanMove(from, to));
    }

    [Fact]
    public void Setting_a_status_checks_the_workflow_and_the_approval_needs()
    {
        var reg = Sample();
        var r = reg.Risks[0];
        reg.SetStatus(r, RiskStatus.Approved);
        Assert.Equal(RiskStatus.Approved, r.Status);
        Assert.Throws<InvalidOperationException>(() => reg.SetStatus(r, RiskStatus.Proposed));
        var bare = new RegisterRisk { Id = "R09", Title = "No event yet" };
        reg.Risks.Add(bare);
        var e = Assert.Throws<InvalidOperationException>(() => reg.SetStatus(bare, RiskStatus.Approved));
        Assert.Contains("event", e.Message);
        reg.SetStatus(bare, RiskStatus.Rejected);            // rejecting needs nothing
        Assert.Equal(RiskStatus.Rejected, bare.Status);
    }

    [Fact]
    public void Only_proposed_or_rejected_risks_can_be_deleted()
    {
        var reg = Sample();
        reg.SetStatus(reg.Risks[0], RiskStatus.Approved);
        Assert.False(reg.CanDelete(reg.Risks[0]));
        Assert.Throws<InvalidOperationException>(() => reg.Delete(reg.Risks[0]));
        reg.SetStatus(reg.Risks[1], RiskStatus.Rejected);
        reg.Delete(reg.Risks[1]);
        reg.Delete(reg.Risks[1]);                            // R03, still proposed
        Assert.Equal(new[] { "R01" }, reg.Risks.Select(r => r.Id));
        Assert.Equal("R02", reg.NextId());
    }

    [Fact]
    public void Search_matches_id_title_description_and_people_ignoring_case()
    {
        var reg = Sample();
        string[] Find(string q) => new RegisterFilter { Search = q }.Apply(reg.Risks).Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "R01" }, Find("CRANE"));
        Assert.Equal(new[] { "R02" }, Find("r02"));
        Assert.Equal(new[] { "R03" }, Find("earthworks"));      // effect
        Assert.Equal(new[] { "R02" }, Find("engineer"));        // raised by
        Assert.Equal(new[] { "R01", "R02", "R03" }, Find(" "));
        Assert.Equal(new[] { "R01", "R03" }, Find("planner the"));  // every word must match
    }

    [Fact]
    public void Filters_combine_category_status_and_kind()
    {
        var reg = Sample();
        reg.SetStatus(reg.Risks[0], RiskStatus.Approved);
        string[] Ids(RegisterFilter f) => f.Apply(reg.Risks).Select(r => r.Id).ToArray();
        Assert.Equal(new[] { "R01" }, Ids(new RegisterFilter { Status = RiskStatus.Approved }));
        Assert.Equal(new[] { "R02", "R03" }, Ids(new RegisterFilter { Status = RiskStatus.Proposed }));
        Assert.Equal(new[] { "R02" }, Ids(new RegisterFilter { Kind = RiskKind.Opportunity }));
        Assert.Equal(new[] { "R03" }, Ids(new RegisterFilter { Category = "External", Status = RiskStatus.Proposed }));
        Assert.Empty(Ids(new RegisterFilter { Category = "External", Kind = RiskKind.Opportunity }));
        Assert.Equal(3, Ids(new RegisterFilter()).Length);
    }

    [Fact]
    public void Status_counts_list_every_status()
    {
        var reg = Sample();
        reg.SetStatus(reg.Risks[0], RiskStatus.Approved);
        reg.SetStatus(reg.Risks[1], RiskStatus.Rejected);
        var c = reg.StatusCounts();
        Assert.Equal(1, c[RiskStatus.Proposed]);
        Assert.Equal(1, c[RiskStatus.Approved]);
        Assert.Equal(1, c[RiskStatus.Rejected]);
        Assert.Equal(0, c[RiskStatus.Closed]);
    }
}
