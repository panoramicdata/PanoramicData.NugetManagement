using System.Text.RegularExpressions;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Checks the Razor markup for mistakes the compiler accepts and the renderer then throws on.
/// </summary>
/// <remarks>
/// The Web project has no bUnit reference, so no test renders a component. That leaves a class of
/// mistake that builds cleanly, passes every test, and fails only when a human loads the page — which
/// is how it should not be found.
/// </remarks>
public partial class RazorMarkupTests(ITestOutputHelper output) : TestWithOutput(output)
{
	/// <summary>
	/// A Razor comment inside a component's attribute list is read as a parameter name. It compiles,
	/// then throws <c>InvalidOperationException: does not have a property matching the name '@* ... *@'</c>
	/// the moment the component is rendered, taking the whole page with it.
	/// </summary>
	[Fact]
	public void NoRazorCommentSitsInsideATag()
	{
		var offenders = new List<string>();

		foreach (var path in RazorFiles())
		{
			var markup = File.ReadAllText(path);

			foreach (var tag in Tag().Matches(markup).Cast<Match>())
			{
				if (!tag.Value.Contains("@*", StringComparison.Ordinal))
				{
					continue;
				}

				var line = markup[..tag.Index].Count(c => c == '\n') + 1;
				offenders.Add($"{Path.GetFileName(path)}:{line}");
			}
		}

		offenders.Should().BeEmpty(
			"a Razor comment between a component's attributes is parsed as a parameter name: it builds, "
			+ "and then throws on render. Put the comment above the element instead");
	}

	private static IEnumerable<string> RazorFiles()
		=> Directory.EnumerateFiles(ResolveWebProjectDirectory(), "*.razor", SearchOption.AllDirectories);

	private static string ResolveWebProjectDirectory()
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			var candidate = Path.Combine(directory, "PanoramicData.NugetManagement.Web", "Components");

			if (Directory.Exists(candidate))
			{
				return candidate;
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new DirectoryNotFoundException("Could not find the Web project's Components directory.");
	}

	/// <summary>
	/// One opening tag, from its name to the closing angle bracket. Non-greedy so that adjacent tags
	/// are not swallowed into one match, and single-line disabled so multi-line attribute lists — the
	/// only place this mistake happens — are seen whole.
	/// </summary>
	[GeneratedRegex(@"<[A-Za-z][^>]*?>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
	private static partial Regex Tag();
}
