using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Extensions.DependencyInjection;
using ScheduleRisk.Web.Services;
using ScheduleRisk.Web.Services.Access;

namespace ScheduleRisk.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // WebView2 keeps its browser data next to the program unless told otherwise, which fails when the program is
        // in a read-only folder such as Program Files; keep it in the user's local app data instead.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectRiskAnalysis", "WebView2"));

        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
        services.AddSingleton(new AccessStore(new AccessFile(AccessFile.DefaultPath)));
        services.AddSingleton<AppState>();
        services.AddSingleton(new HttpClient(new LocalFiles(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
            { BaseAddress = LocalFiles.BaseAddress });
        services.AddSingleton<ReportExporter>();

        var view = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = Path.Combine("wwwroot", "index.html"),
            Services = services.BuildServiceProvider(),
        };
        view.RootComponents.Add<DesktopRoot>("#app");

        var form = new Form
        {
            Text = "Project Risk Analysis",
            Width = 1400,
            Height = 900,
            StartPosition = FormStartPosition.CenterScreen,
            WindowState = FormWindowState.Maximized,
        };
        form.Controls.Add(view);
        Application.Run(form);
    }
}
