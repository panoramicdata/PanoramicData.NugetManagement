namespace PanoramicData.NugetManagement.Web.Remediations.Licensing;

/// <summary>Adds PackageLicenseExpression to Directory.Build.props.</summary>
public sealed class PackageLicenseExpressionRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "LIC-02";
}
