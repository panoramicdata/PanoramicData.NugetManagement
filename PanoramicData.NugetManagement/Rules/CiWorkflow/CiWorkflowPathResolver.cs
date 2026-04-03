using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

internal static class CiWorkflowPathResolver
{
    public static string Resolve(RepositoryContext context)
    {
        var fileName = string.IsNullOrWhiteSpace(context.Options.CiFileName)
            ? "ci.yml"
            : context.Options.CiFileName.Trim();

        return $".github/workflows/{fileName}";
    }
}
