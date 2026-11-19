using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Checks that RepositoryUrl is set in packable projects. Private repositories are skipped.
/// </summary>
public class RepositoryUrlSetRule : ProjectPropertySetRuleBase
{
	/// <inheritdoc />
	public override string RuleId => "META-02";

	/// <inheritdoc />
	public override string RuleName => "RepositoryUrl set";

	/// <inheritdoc />
	public override AssessmentCategory Category => AssessmentCategory.ProjectMetadata;

	/// <inheritdoc />
	public override AssessmentSeverity Severity => AssessmentSeverity.Error;

	/// <inheritdoc />
	public override Task<RuleResult> EvaluateAsync(RepositoryContext context, CancellationToken cancellationToken)
		=> context.IsPrivateRepository == true
			? Task.FromResult(NotApplicable("The repository is private, so it should not publish a RepositoryUrl."))
			: base.EvaluateAsync(context, cancellationToken);

	/// <inheritdoc />
	protected override string PropertyName => "RepositoryUrl";

	/// <inheritdoc />
	protected override string Summary => "Add <RepositoryUrl>https://github.com/org/repo</RepositoryUrl> to the .csproj.";

	/// <inheritdoc />
	protected override string Detail(List<string> missing)
		=> StandardDetail(missing, "<RepositoryUrl>https://github.com/org/repo</RepositoryUrl>");

	/// <inheritdoc />
	protected override Dictionary<string, object> AdvisoryData(RepositoryContext context, List<string> missing)
		=> SetPropertyData(missing, $"https://github.com/{context.FullName}");
}
