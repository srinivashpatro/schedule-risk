namespace ScheduleRisk.Web.Services;

/// <summary>
/// Files the app fetches from its own site after it has loaded: the sample project and the report fonts. Behind Cloudflare
/// Access (docs/HOSTING.md) an ended session sends those requests to the sign-in page on another site, so they fail, as
/// they do when the connection drops. Reloading would sign in again but lose the schedule and results in the tab, so the
/// message says to sign in from a new tab instead: the sign-in is shared with this tab, which can then try again.
/// </summary>
public static class SiteFiles
{
    public static string LoadFailed(string what, Exception e) => e is HttpRequestException
        ? $"Could not load {what} from this site: the connection may have dropped, or your sign-in may have ended. "
          + "Open the app in a new tab (sign in if it asks), then try again here; this tab keeps your work."
        : $"Could not load {what}: {e.Message}";
}
