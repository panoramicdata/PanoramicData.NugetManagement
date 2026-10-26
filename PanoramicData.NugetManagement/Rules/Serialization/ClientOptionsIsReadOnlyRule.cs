using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that an API client's options class offers <c>IsReadOnly</c>, so a caller can stop the client
/// mutating the server.
/// </summary>
/// <remarks>
/// <para>
/// Meraki is the model: callers set the option and the HTTP pipeline refuses anything but a read
/// before the request is sent. The estate had drifted into two names (<c>ReadOnly</c> and
/// <c>IsReadOnly</c>); <c>IsReadOnly</c> is the one to keep, and a surviving <c>ReadOnly</c> must be a
/// read-write <c>[Obsolete]</c> alias so existing callers still compile and behave the same.
/// </para>
/// <para>
/// The default value is deliberately not checked: it must stay false so adding the option is not a
/// SemVer break, and a package that defaults to true is its own owner's decision.
/// </para>
/// <para>
/// Applicability is by file name, because the assessor reads only <c>*ClientOptions.cs</c> among the C#
/// files. It cannot see whether the API has write verbs or whether a handler enforces the option, so a
/// read-only API is waived through <c>rule-waivers.json</c> and enforcement is the playbook's job.
/// </para>
/// </remarks>
public partial class ClientOptionsIsReadOnlyRule : RuleBase
{
	/// <summary>The suffix of the file that holds a client's options.</summary>
	public static string OptionsFileSuffix => "ClientOptions.cs";

	[GeneratedRegex(@"\bbool\s+IsReadOnly\b", RegexOptions.None, 2000)]
	private static partial Regex IsReadOnlyProperty();

	[GeneratedRegex(@"\bbool\s+ReadOnly\b", RegexOptions.None, 2000)]
	private static partial Regex LegacyReadOnlyProperty();

	[GeneratedRegex(@"\[\s*(?:System\.)?Obsolete\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public|internal|protected)\s+bool\s+ReadOnly\b", RegexOptions.None, 2000)]
	private static partial Regex ObsoleteLegacyReadOnlyProperty();

	/// <inheritdoc />
	public override string RuleId => "HTTP-02";

	/// <inheritdoc />
	public override string RuleName => "Client options expose IsReadOnly";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.HttpClient;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var optionsFiles = context.FilePaths
			.Where(path => path.EndsWith(OptionsFileSuffix, StringComparison.OrdinalIgnoreCase) && !IsTestPath(path))
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (optionsFiles.Count == 0)
		{
			return Task.FromResult(NotApplicable($"No *{OptionsFileSuffix} file found, so there is no API client to make read-only."));
		}

		var read = optionsFiles
			.Select(path => (Path: path, Content: context.GetFileContent(path)))
			.Where(file => file.Content is not null)
			.ToList();

		if (read.Count == 0)
		{
			return Task.FromResult(NotApplicable("The client options file was not read, so IsReadOnly could not be checked."));
		}

		var problems = new List<string>();

		if (!read.Any(file => IsReadOnlyProperty().IsMatch(file.Content!)))
		{
			problems.Add($"none of {string.Join(", ", read.Select(file => file.Path))} declares a `bool IsReadOnly` property");
		}

		problems.AddRange(read
			.Where(file => LegacyReadOnlyProperty().IsMatch(file.Content!) && !ObsoleteLegacyReadOnlyProperty().IsMatch(file.Content!))
			.Select(file => $"{file.Path} still has a `bool ReadOnly` property that is not marked [Obsolete]"));

		if (problems.Count == 0)
		{
			return Task.FromResult(Pass("Client options expose IsReadOnly."));
		}

		return Task.FromResult(Fail(
			"API client options need IsReadOnly: " + string.Join("; ", problems) + ".",
			BuildAdvisory([.. read.Select(file => file.Path)], problems)));
	}

	private static RuleAdvisory BuildAdvisory(string[] optionsFiles, List<string> problems)
		=> new()
		{
			Summary = "Give the client options an IsReadOnly property, enforced before any request is sent.",
			Detail = """
				A caller must be able to stop the client changing the server. Add `public bool IsReadOnly { get; set; }`
				to the client options, default false so existing callers are unaffected, and have the HTTP pipeline throw
				`InvalidOperationException` for any request that is not a read, before it is sent (see Meraki.Api's
				`AuthenticatedBackingOffHttpClientHandler`). Where the API uses POST for reads, allow those by name.
				If the options already have a `ReadOnly` property, keep it as a read-write alias marked
				`[Obsolete("Use IsReadOnly")]` so no caller breaks. If the API has no write operations at all,
				waive this rule in rule-waivers.json instead.
				""",
			Data = new()
			{
				["options_files"] = optionsFiles,
				["problems"] = problems.ToArray()
			}
		};

	private static bool IsTestPath(string path)
		=> path.Split('/').Any(segment => segment.Contains("Test", StringComparison.OrdinalIgnoreCase));
}
