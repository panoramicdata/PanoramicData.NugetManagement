using System.Globalization;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that copyright notices naming the expected holder carry the current year.
/// </summary>
public class CopyrightYearRule : RuleBase
{
	private static readonly string[] _files = ["README.md", "LICENSE", "Directory.Build.props"];

	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Initializes a new instance of the <see cref="CopyrightYearRule"/> class.
	/// </summary>
	public CopyrightYearRule()
		: this(TimeProvider.System)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="CopyrightYearRule"/> class.
	/// </summary>
	/// <param name="timeProvider">Clock that decides the current year.</param>
	public CopyrightYearRule(TimeProvider timeProvider)
	{
		_timeProvider = timeProvider;
	}

	/// <inheritdoc />
	public override string RuleId => "LIC-04";

	/// <inheritdoc />
	public override string RuleName => "Copyright notices carry the current year";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.Licensing;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Warning;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
	{
		var holder = context.Options.ExpectedCopyrightHolder;
		var year = _timeProvider.GetUtcNow().Year;

		IReadOnlyList<CopyrightNotices.Notice> NoticesIn(string file)
			=> context.GetFileContent(file) is { } content ? CopyrightNotices.Find(content, holder, year) : [];

		var found = _files
			.Select(file => (File: file, Notices: NoticesIn(file)))
			.Where(entry => entry.Notices.Count > 0)
			.ToList();

		if (found.Count == 0)
		{
			return Task.FromResult(NotApplicable($"No copyright notice naming \"{holder}\" carries a year."));
		}

		var stale = found
			.Select(entry => (entry.File, Notices: entry.Notices.Where(notice => notice.IsStale).ToList()))
			.Where(entry => entry.Notices.Count > 0)
			.ToList();

		if (stale.Count == 0)
		{
			return Task.FromResult(Pass($"Copyright notices naming \"{holder}\" carry {year}."));
		}

		var locations = stale
			.SelectMany(entry => entry.Notices.Select(notice => $"{entry.File}:{notice.Line}"))
			.ToArray();

		return Task.FromResult(Fail(
			$"Copyright notice is not {year}: {string.Join(", ", locations)}.",
			new RuleAdvisory
			{
				Summary = $"Change the year in the copyright notice to {year}.",
				Detail = $"These notices name `{holder}` but do not carry {year}: {string.Join(", ", locations.Select(location => $"`{location}`"))}. "
					+ $"Set a single year to {year}, or the end of a range to {year} (`2020-{year}`).",
				Data = new()
				{
					["remediation_type"] = "update_copyright_year",
					["files"] = stale.Select(entry => entry.File).ToArray(),
					["year"] = year.ToString(CultureInfo.InvariantCulture),
					["holder"] = holder
				}
			}));
	}
}
