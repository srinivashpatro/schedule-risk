namespace ScheduleRisk.Core.Reporting;

/// <summary>The product's name and version as users see them in reports and exported documents.</summary>
public static class Brand
{
    public const string Name = "Project Risk Analysis";

    public static string Version => typeof(Brand).Assembly.GetName().Version?.ToString(3) ?? "";
}
