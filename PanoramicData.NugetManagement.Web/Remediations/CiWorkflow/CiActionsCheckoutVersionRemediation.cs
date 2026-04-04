namespace PanoramicData.NugetManagement.Web.Remediations.CiWorkflow;

/// <summary>Fixes actions/checkout version in CI workflow.</summary>
public sealed class CiActionsCheckoutVersionRemediation : DataDrivenRemediation
{
    /// <inheritdoc />
    public override string RuleId => "CI-05";
}
