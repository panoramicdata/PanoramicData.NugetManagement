using System.Text.Json;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Reads the version Nerdbank.GitVersioning reports for a working tree out of its JSON output.
/// </summary>
/// <remarks>
/// Separate from the process launch so the part with a decision in it can be tested without one.
/// Every answer it cannot be sure of is null: an informational finding built on a wrong number is
/// worse than one that stays quiet.
/// </remarks>
public static class NbgvVersionReader
{
	/// <summary>
	/// Names the nbgv executable to launch: where the global dotnet tool installs it when it is
	/// there, and the bare command otherwise.
	/// </summary>
	/// <param name="userProfilePath">The user's home directory, or null when there is none.</param>
	/// <param name="fileExists">Tests whether a path exists.</param>
	/// <remarks>
	/// nbgv is a global dotnet tool, and its install directory is not reliably on the PATH of the
	/// process that runs it — on the machine this was written for it resolved from one shell and not
	/// from another. Naming the install location first makes it work regardless; falling back to the
	/// bare command keeps a machine that installed it some other way working. Neither found, the
	/// launch fails and the caller reads that as no version known.
	/// </remarks>
	public static string ResolveExecutable(string? userProfilePath, Func<string, bool> fileExists)
	{
		if (string.IsNullOrWhiteSpace(userProfilePath))
		{
			return "nbgv";
		}

		var installed = Path.Combine(userProfilePath, ".dotnet", "tools", "nbgv.exe");
		return fileExists(installed) ? installed : "nbgv";
	}

	/// <summary>
	/// Reads the version the next release from this working tree would carry, or null when the output
	/// does not establish one.
	/// </summary>
	/// <param name="json">The stdout of `nbgv get-version -f json`.</param>
	public static string? ReadVersion(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		JsonElement root;
		try
		{
			using var document = JsonDocument.Parse(json);
			root = document.RootElement.Clone();
		}
		catch (JsonException)
		{
			// What an nbgv that failed leaves on stdout — a sentence, not a document.
			return null;
		}

		if (root.ValueKind is not JsonValueKind.Object)
		{
			return null;
		}

		// nbgv answers for a repository with no version.json too, with a version nothing asked for.
		// Compared against a tag, that number is an invention.
		if (!root.TryGetProperty("VersionFileFound", out var found)
			|| found.ValueKind is not JsonValueKind.True)
		{
			return null;
		}

		// NuGetPackageVersion is the one a release would actually publish. The three version fields
		// agree on a plain release build and diverge on a prerelease, where this is the one that
		// carries the prerelease tag — and so the one worth comparing with a tag.
		return root.TryGetProperty("NuGetPackageVersion", out var version)
			&& version.ValueKind is JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(version.GetString())
				? version.GetString()
				: null;
	}
}
