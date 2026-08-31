using System.Text.Json;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Rules;

/// <summary>
/// Reads a list of dependency names out of a failing result's advisory data.
/// </summary>
/// <remarks>
/// <see cref="RuleAdvisory.Data"/> is <c>Dictionary&lt;string, object&gt;</c>, so what comes back
/// depends on where the result has been. A result still in the process that produced it holds the
/// <c>string[]</c> the rule put there; one that has been round-tripped through the row cache holds a
/// <see cref="JsonElement"/> instead. A plain <c>is IEnumerable&lt;string&gt;</c> test therefore
/// answers "no" for a perfectly good list, which is how a narrowed claim silently widens back out
/// again after a restart.
/// </remarks>
public static class AdvisoryNames
{
	/// <summary>
	/// Whether the advisory names this dependency under the given key.
	/// </summary>
	/// <param name="failure">The failing result to read.</param>
	/// <param name="key">The advisory data key holding the names.</param>
	/// <param name="name">The dependency name to look for.</param>
	/// <remarks>
	/// A key that is absent or unreadable answers <c>false</c>, not <c>true</c>. The caller is asking
	/// "will this failure move that dependency", and a claim that cannot be read is not a claim.
	/// </remarks>
	public static bool Contains(RuleResult failure, string key, string name)
		=> Read(failure, key).Contains(name, StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The names the advisory lists under the given key, or empty when there are none to read.
	/// </summary>
	/// <param name="failure">The failing result to read.</param>
	/// <param name="key">The advisory data key holding the names.</param>
	public static IReadOnlyList<string> Read(RuleResult failure, string key)
	{
		if (failure.Advisory?.Data.TryGetValue(key, out var value) is not true || value is null)
		{
			return [];
		}

		return value switch
		{
			IEnumerable<string> names => [.. names],
			JsonElement { ValueKind: JsonValueKind.Array } element =>
			[
				.. element
					.EnumerateArray()
					.Where(item => item.ValueKind == JsonValueKind.String)
					.Select(item => item.GetString()!)
			],
			IEnumerable<object> items => [.. items.OfType<string>()],
			_ => []
		};
	}
}
