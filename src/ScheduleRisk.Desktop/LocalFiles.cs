using System.Net;

namespace ScheduleRisk.Desktop;

/// <summary>
/// Answers the app's requests for its own files (the sample project and the report fonts) from the wwwroot folder next
/// to the program. The browser app fetches these from its site; here nothing goes over the network.
/// </summary>
public sealed class LocalFiles : HttpMessageHandler
{
    /// <summary>A made-up address the requests are relative to. It is never looked up: this handler answers them.</summary>
    public static readonly Uri BaseAddress = new UriBuilder(Uri.UriSchemeHttps, "app.local").Uri;

    private readonly string root;

    public LocalFiles(string root) => this.root = Path.GetFullPath(root);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string rel = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimStart('/');
        string path = Path.GetFullPath(Path.Combine(root, rel));
        if (request.RequestUri.Host != BaseAddress.Host || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(File.ReadAllBytes(path)),
            RequestMessage = request,
        });
    }
}
