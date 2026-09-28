using ScheduleRisk.Web.Services;

namespace ScheduleRisk.Tests;

/// <summary>
/// Files the browser app fetches from its own site after it has loaded. Behind Cloudflare Access an ended session turns
/// those requests into a failed fetch, so the message has to say how to get back in without losing the work in the tab.
/// </summary>
public class SiteFilesTests
{
    [Fact]
    public void A_failed_request_says_to_sign_in_again_in_a_new_tab()
    {
        string m = SiteFiles.LoadFailed("the report fonts", new HttpRequestException("TypeError: Failed to fetch"));
        Assert.Equal("Could not load the report fonts from this site: the connection may have dropped, or your sign-in "
            + "may have ended. Open the app in a new tab (sign in if it asks), then try again here; this tab keeps your work.", m);
        Assert.DoesNotContain("TypeError", m);
    }

    [Fact]
    public void A_refused_request_reads_the_same()
    {
        var refused = new HttpRequestException("Response status code does not indicate success: 403 (Forbidden).", null,
            System.Net.HttpStatusCode.Forbidden);
        Assert.Equal(SiteFiles.LoadFailed("the sample project", new HttpRequestException("x")),
            SiteFiles.LoadFailed("the sample project", refused));
    }

    [Fact]
    public void Other_errors_keep_their_own_message()
    {
        Assert.Equal("Could not load the sample project: Out of memory.",
            SiteFiles.LoadFailed("the sample project", new InvalidOperationException("Out of memory.")));
    }
}
