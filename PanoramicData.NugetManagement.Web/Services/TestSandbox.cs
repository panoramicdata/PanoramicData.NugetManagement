using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The environment a model-driven test run happens in: CI's, not the developer's.
/// </summary>
/// <remarks>
/// Added after a coverage session ran a client library's tests with the real credentials configured on
/// this machine. Those tests made live calls to the vendor's API, took five minutes, and printed the
/// API key into a transcript. Three things were wrong at once, and one cause covered them: the run saw
/// secrets that CI never has.
/// <para>
/// So a sandboxed run sees none. User secrets live under <c>%APPDATA%</c>, so that is pointed at an
/// empty folder, and every environment variable whose name says it holds a credential is removed. What
/// remains is what the repository's own CI gives its tests, which is the only environment they are
/// required to pass in. Tests that need a real service then skip or fail as they do in CI, fast and
/// without touching anything.
/// </para>
/// </remarks>
public static partial class TestSandbox
{
	/// <summary>
	/// Environment overrides for a test process: a value to set, or null to remove the variable.
	/// </summary>
	/// <param name="environment">The environment to start from; the process's own when null.</param>
	public static IReadOnlyDictionary<string, string?> Overrides(IDictionary<string, string?>? environment = null)
	{
		var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

		var names = environment is null
			? Environment.GetEnvironmentVariables().Keys.Cast<string>()
			: environment.Keys;

		foreach (var name in names.Where(HoldsACredential))
		{
			overrides[name] = null;
		}

		var empty = Path.Combine(Path.GetTempPath(), "nugetmanagement-sandbox", "appdata");
		Directory.CreateDirectory(empty);
		overrides["APPDATA"] = empty;

		return overrides;
	}

	/// <summary>
	/// Whether a variable's name says it holds a secret.
	/// </summary>
	/// <param name="name">The variable name.</param>
	public static bool HoldsACredential(string name) => CredentialName().IsMatch(name);

	// Whole words only, so PATH and PATHEXT are not mistaken for a personal access token. Separators are
	// anything that is not a letter: GITHUB_TOKEN, Joker__ApiKey and codacy.api.token all match.
	[GeneratedRegex(
		@"(^|[^A-Za-z])(KEY|APIKEY|TOKEN|SECRET|PASSWORD|PASSWD|PWD|CREDENTIAL|CREDENTIALS|PAT|CONNECTIONSTRING|ACCESSKEY|PRIVATEKEY)($|[^A-Za-z])|ApiKey|ConnectionString",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex CredentialName();
}
