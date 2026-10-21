using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeskMon;

/// <summary>
/// Whether a newer DeskMon has been released - the newest release on GitHub of
/// the UpdateRepository setting, compared with the running version.
///
/// Only says so: nothing is downloaded or installed. DeskMon runs elevated and
/// reads hardware, so replacing its own exe from the internet is not something
/// it should do behind anyone's back.
/// </summary>
public static class AppUpdate
{
    /// <summary>A release newer than the running version.</summary>
    public sealed record Release(Version Version, string Name, string Url);

    /// <summary>The running version, as the csproj's Version gives it - e.g. 1.55.0.</summary>
    public static Version Running { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The last answer, for the versions window. Null until a check has found something newer.</summary>
    public static Release? Newer { get; private set; }

    /// <summary>When the last check ran, and what went wrong with it if it failed - for the versions window.</summary>
    public static DateTime? CheckedAt { get; private set; }
    public static string? Problem { get; private set; }

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub's API refuses requests without a user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskMon");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// Asks GitHub for <paramref name="repository"/>'s newest release ("owner/name").
    /// Returns it when it is newer than this one, otherwise null - also when
    /// offline, which <see cref="Problem"/> then says.
    /// </summary>
    public static async Task<Release?> CheckAsync(string repository)
    {
        repository = repository.Trim().Trim('/');
        if (repository.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            repository = repository["https://github.com/".Length..];

        // Owner and name only. The name goes into the API address and, below,
        // into a page address that gets opened - "../" or "?" in it would
        // point both somewhere else.
        if (!RepositoryName.IsMatch(repository))
        {
            Problem = $"\"{repository}\" isn't a GitHub repository - it should look like owner/name.";
            CheckedAt = DateTime.Now;
            Newer = null;
            return null;
        }

        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{repository}/releases/latest");
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            string url = root.TryGetProperty("html_url", out var link) ? link.GetString() ?? string.Empty : string.Empty;
            string name = root.TryGetProperty("name", out var title) && title.GetString() is { Length: > 0 } n ? n : tag;

            Problem = null;
            CheckedAt = DateTime.Now;

            // "v1.56.0" or "1.56.0"; anything else isn't a version to compare.
            Newer = Version.TryParse(tag.TrimStart('v', 'V'), out var version) && version > Running
                ? new Release(version, name, IsGitHubPage(url) ? url : $"https://github.com/{repository}/releases")
                : null;
            return Newer;
        }
        catch (Exception ex)
        {
            Problem = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? $"No releases found for {repository}."
                : ex.Message;
            CheckedAt = DateTime.Now;

            // Not kept from an earlier answer: after the release page was
            // changed, that one would be about a different repository.
            Newer = null;
            return null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RepositoryName =
        new(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$");

    /// <summary>
    /// Whether a release's page is a GitHub web page. The address comes from
    /// the API's answer and is opened with the shell - by a process running as
    /// administrator - so anything but https on github.com itself (another
    /// scheme, another host, a file path) is replaced with the repository's
    /// releases page rather than opened.
    /// </summary>
    private static bool IsGitHubPage(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
           && uri.IsDefaultPort
           && string.IsNullOrEmpty(uri.UserInfo);
}
