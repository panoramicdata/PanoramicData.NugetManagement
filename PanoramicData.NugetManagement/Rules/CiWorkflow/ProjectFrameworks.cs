using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// The frameworks a repository's projects target, for rules about files that must agree with them.
/// </summary>
internal static class ProjectFrameworks
{
	/// <summary>
	/// Every framework named by a project's TargetFramework or TargetFrameworks.
	/// </summary>
	/// <param name="context">The repository.</param>
	public static HashSet<string> Targeted(RepositoryContext context)
	{
		var targeted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var csproj in context.FindFiles(".csproj"))
		{
			var content = context.GetFileContent(csproj);
			foreach (var property in new[] { "TargetFramework", "TargetFrameworks" })
			{
				foreach (var value in MsBuildProperties.TryGetValues(content, property) ?? [])
				{
					targeted.UnionWith(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
				}
			}
		}

		return targeted;
	}

	/// <summary>
	/// Whether a project targets the latest framework and none targets an older one, so a workflow that
	/// sets up an older SDK has no project left that needs it.
	/// </summary>
	/// <param name="context">The repository.</param>
	public static bool OnlyLatest(RepositoryContext context)
	{
		var targeted = Targeted(context);
		var latest = Standards.LatestTargetFramework;
		if (!targeted.Contains(latest) || Major(latest) is not { } latestMajor)
		{
			return false;
		}

		return targeted.All(framework => Major(framework) is not { } major || major >= latestMajor);
	}

	private static int? Major(string framework)
	{
		if (!framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) || framework.Length <= 3 || !char.IsDigit(framework[3]))
		{
			return null;
		}

		var dot = framework.IndexOf('.', StringComparison.Ordinal);
		return dot > 3 && int.TryParse(framework.AsSpan(3, dot - 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
			? major
			: null;
	}
}
