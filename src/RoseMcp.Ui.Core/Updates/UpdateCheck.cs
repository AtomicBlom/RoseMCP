using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoseMcp.Ui.Core.Updates;

/// <summary>
/// What the latest release is, as far as this process last managed to find out.
/// </summary>
/// <param name="IsUpdateAvailable">Whether <paramref name="LatestVersion"/> is newer than the running build.</param>
/// <param name="LatestVersion">The newest released version, or null when it is not known.</param>
/// <param name="ReleaseUrl">Where that release can be read about and downloaded.</param>
/// <param name="Problem">
/// Why the answer is not known, or null when it is. Carried rather than thrown: nothing about a
/// tray failing to reach GitHub is worth interrupting somebody over, but a person who goes looking
/// for why it never offers an update deserves to find the reason rather than silence.
/// </param>
public sealed record UpdateStatus(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? Problem)
{
	/// <summary>Nothing known yet, and nothing wrong. What a check that has not run reports.</summary>
	public static readonly UpdateStatus Unknown = new(false, null, null, null);
}

/// <summary>
/// Asks GitHub for the latest release and says whether the running build is behind it.
/// <para>
/// Read-only and anonymous. The releases endpoint of a public repository needs no token, and asking
/// for one would mean holding a credential to learn something already public.
/// </para>
/// <para>
/// It only reports. Downloading and installing is deliberately left to the person: the package is a
/// hundred megabytes and the install stops a tray that is holding somebody's loaded solution, which
/// is not a thing to do to them while they are working because a background poll noticed a tag.
/// </para>
/// </summary>
public sealed class UpdateCheck : IDisposable
{
	/// <summary>The releases endpoint. <c>/latest</c> excludes drafts and prereleases, which is why a
	/// tagged <c>v0.4.0-rc.1</c> is not offered to somebody running a released build.</summary>
	public static readonly Uri GitHubLatestRelease =
		new("https://api.github.com/repos/AtomicBlom/RoseMCP/releases/latest");

	/// <summary>
	/// Long enough that a tray started at sign-in is not doing anything the moment somebody logs in,
	/// and short enough that a release is noticed the day it happens. Nothing turns on the exact
	/// number; it is here rather than at the call site so the two windows cannot choose differently.
	/// </summary>
	public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

	private readonly HttpClient _http;
	private readonly bool _ownsHandler;
	private readonly ReleaseVersion _current;
	private readonly string _currentText;

	/// <summary>
	/// The ETag of the last answer, so a later check can ask GitHub whether anything changed rather
	/// than for the whole thing. A 304 costs no quota against the 60-per-hour anonymous limit, which
	/// matters because that limit is per address and shared with everything else on the machine.
	/// </summary>
	private string? _etag;

	private UpdateStatus _last = UpdateStatus.Unknown;

	public UpdateCheck(string currentVersion, Uri? endpoint = null, HttpMessageHandler? handler = null)
	{
		Endpoint = endpoint ?? GitHubLatestRelease;
		_currentText = currentVersion;
		ReleaseVersion.TryParse(currentVersion, out _current);

		_ownsHandler = handler is null;
		_http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
		_http.Timeout = TimeSpan.FromSeconds(15);

		// GitHub answers 403 to a request with no User-Agent, and the failure reads like a permission
		// problem rather than a missing header.
		_http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RoseMCP", VersionHeader(currentVersion)));
		_http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
	}

	public Uri Endpoint { get; }

	/// <summary>What the last completed check found.</summary>
	public UpdateStatus Last => _last;

	/// <summary>
	/// Asks once. Never throws for anything the network or GitHub does: a tray that crashed because a
	/// coffee shop hotspot intercepted a request would be a far worse bug than the one this feature
	/// fixes.
	/// </summary>
	public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
			if (_etag is { Length: > 0 }) request.Headers.IfNoneMatch.ParseAdd(_etag);

			using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

			// Nothing has changed since the last answer, so the last answer still stands.
			if (response.StatusCode == HttpStatusCode.NotModified) return _last;

			if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
			{
				// The anonymous limit is 60 an hour per address, and it is shared: a checkout script
				// or another tool on the same machine can spend it. Saying so beats "forbidden".
				return Remember(_last with { Problem = "GitHub is rate limiting anonymous requests from this machine; the check will try again later." });
			}

			if (!response.IsSuccessStatusCode)
			{
				return Remember(_last with { Problem = $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}." });
			}

			var release = await response.Content
				.ReadFromJsonAsync(UpdateJson.Default.GitHubRelease, cancellationToken)
				.ConfigureAwait(false);

			if (release?.TagName is not { Length: > 0 } tag)
			{
				return Remember(_last with { Problem = "GitHub's answer carried no tag name." });
			}

			_etag = response.Headers.ETag?.ToString();

			return Remember(Compare(tag, release.HtmlUrl));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The window is closing, or the poll loop is being stopped. Not a problem to report.
			return _last;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
			or InvalidOperationException or JsonException or NotSupportedException)
		{
			// JsonException and NotSupportedException belong here as much as the network ones do: what
			// answers a request is not always GitHub. A captive portal returns its own login page with
			// a 200 and an HTML body, and parsing that is where the tray would otherwise go down --
			// on a hotel network, in the background, for a feature nobody was waiting on.
			return Remember(_last with { Problem = $"Could not reach GitHub: {exception.Message}" });
		}
	}

	/// <summary>
	/// The released version against the running one. A version this end cannot parse is reported as
	/// such rather than assumed newer, because the visible consequence of guessing is a permanent
	/// notice offering an upgrade that never goes away.
	/// </summary>
	private UpdateStatus Compare(string tag, string? url)
	{
		if (!ReleaseVersion.TryParse(tag, out var latest))
		{
			return new UpdateStatus(false, tag, url, $"The latest release is tagged '{tag}', which is not a version this can compare.");
		}

		if (_current == default && !ReleaseVersion.TryParse(_currentText, out _))
		{
			return new UpdateStatus(false, latest.ToString(), url, $"This build reports its version as '{_currentText}', which is not a version this can compare.");
		}

		return new UpdateStatus(_current.IsOlderThan(latest), latest.ToString(), url, null);
	}

	private UpdateStatus Remember(UpdateStatus status)
	{
		_last = status;

		return status;
	}

	/// <summary>
	/// A User-Agent product version has to be a token: no '+' and no spaces. An informational version
	/// carrying build metadata would otherwise throw on the way in, at construction, long before
	/// anybody could see it was the header that was wrong.
	/// </summary>
	private static string VersionHeader(string version)
	{
		var trimmed = (version ?? string.Empty).Split('+')[0].Trim();

		return trimmed.Length == 0 ? "0.0.0" : trimmed;
	}

	public void Dispose()
	{
		// Only a handler this made. One passed in belongs to the caller, which is what lets a test
		// serve several checks from one fake.
		if (_ownsHandler) _http.Dispose();
	}
}

/// <summary>The fields of a release this reads, and nothing else GitHub sends.</summary>
internal sealed record GitHubRelease
{
	[JsonPropertyName("tag_name")]
	public string? TagName { get; init; }

	[JsonPropertyName("html_url")]
	public string? HtmlUrl { get; init; }
}

[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class UpdateJson : JsonSerializerContext;
