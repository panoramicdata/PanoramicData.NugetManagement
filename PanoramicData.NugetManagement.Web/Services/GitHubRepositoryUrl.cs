using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads the owner and name of a GitHub repository out of whatever a nuspec declared as its URL.
/// </summary>
/// <remarks>
/// A nuspec's <c>repository</c> URL is publisher-supplied text, not a guaranteed URI. Three of our
/// own packages — PanoramicData.ConsoleExtensions, Serilog.Sinks.PostgreSql.PanoramicData and
/// LightwaveRfLinkPlus.Api — declare the SCP-style <c>git@github.com:owner/repo.git</c>, which
/// <see cref="Uri"/> rejects with "Invalid URI: The URI scheme is not valid.". Discovery parsed it
/// as a URI regardless, so one badly-formed nuspec threw out of the loop and failed rediscovery for
/// the entire organisation.
///
/// Matching on the host and the two segments after it covers every form the same repository is
/// written in — https, http, git, ssh, SCP-style, with or without <c>www.</c>, a trailing
/// <c>.git</c>, or a deep link — and anything that is not a GitHub repository is null rather than
/// an exception.
/// </remarks>
public static partial class GitHubRepositoryUrl
{
	[GeneratedRegex(
		@"github\.com[:/]+(?<owner>[^/:?#\s]+)/(?<name>[^/?#\s]+)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex RepositoryPattern { get; }

	/// <summary>
	/// The canonical <c>https://github.com/owner/name</c> form of a declared repository URL, or null
	/// when it does not name a GitHub repository.
	/// </summary>
	public static string? Normalize(string? url)
	{
		var match = Match(url);
		return match is null
			? null
			: $"https://github.com/{match.Value.Owner}/{match.Value.Name}";
	}

	/// <summary>
	/// The owner of a declared repository URL, or null when it does not name a GitHub repository.
	/// </summary>
	public static string? Owner(string? url) => Match(url)?.Owner;

	/// <summary>
	/// The name of a declared repository URL, without any trailing <c>.git</c>, or null when it does
	/// not name a GitHub repository.
	/// </summary>
	public static string? Name(string? url) => Match(url)?.Name;

	private static (string Owner, string Name)? Match(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		var match = RepositoryPattern.Match(url);
		if (!match.Success)
		{
			return null;
		}

		var name = match.Groups["name"].Value;
		if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			name = name[..^4];
		}

		return name.Length == 0 ? null : (match.Groups["owner"].Value, name);
	}
}
