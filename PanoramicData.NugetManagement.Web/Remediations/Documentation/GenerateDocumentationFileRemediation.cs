namespace PanoramicData.NugetManagement.Web.Remediations.Documentation;

/// <summary>Adds GenerateDocumentationFile to Directory.Build.props.</summary>
public sealed class GenerateDocumentationFileRemediation : DataDrivenRemediation
{
	/// <inheritdoc />
	public override string RuleId => "DOC-01";
}
