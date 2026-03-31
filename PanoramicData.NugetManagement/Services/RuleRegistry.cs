using System.Reflection;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Discovers and holds all <see cref="IRule"/> implementations from the current assembly.
/// </summary>
public static class RuleRegistry
{
	private static readonly Lazy<IReadOnlyList<IRule>> _rules = new(DiscoverRules);

	/// <summary>
	/// All discovered rule instances.
	/// </summary>
	public static IReadOnlyList<IRule> Rules => _rules.Value;

	private static IReadOnlyList<IRule> DiscoverRules()
		=> Assembly
			.GetExecutingAssembly()
			.GetTypes()
			.Where(t => t is { IsAbstract: false, IsInterface: false } && t.IsAssignableTo(typeof(IRule)))
			.Select(t => (IRule)Activator.CreateInstance(t)!)
			.OrderBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
			.ToList();
}
