using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// The playbooks available, by rule.
/// </summary>
/// <remarks>
/// Discovered from the assembly, as <c>RemediationRegistry</c> discovers remediations, so adding a
/// playbook is adding one file and nothing else.
/// </remarks>
public sealed class AiPlaybookRegistry
{
	private readonly Dictionary<string, IRuleAiPlaybook> _playbooks;

	/// <summary>
	/// Discovers every playbook in the assembly.
	/// </summary>
	public AiPlaybookRegistry()
		: this(Discover())
	{
	}

	/// <summary>
	/// Initialises the registry over an explicit set, for tests.
	/// </summary>
	/// <param name="playbooks">The playbooks to serve.</param>
	public AiPlaybookRegistry(IReadOnlyList<IRuleAiPlaybook> playbooks)
		=> _playbooks = playbooks.ToDictionary(p => p.RuleId, StringComparer.OrdinalIgnoreCase);

	/// <summary>The rules that have a playbook.</summary>
	public IReadOnlyCollection<string> RuleIds => _playbooks.Keys;

	/// <summary>
	/// The playbook for a rule, or null when it has none — in which case the prompt falls back to the
	/// rule's advisory.
	/// </summary>
	/// <param name="ruleId">The rule.</param>
	public IRuleAiPlaybook? For(string ruleId)
		=> _playbooks.TryGetValue(ruleId, out var playbook) ? playbook : null;

	private static List<IRuleAiPlaybook> Discover()
		=> [.. typeof(AiPlaybookRegistry).Assembly
			.GetTypes()
			.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IRuleAiPlaybook).IsAssignableFrom(t))
			.Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
			.Select(t => (IRuleAiPlaybook)Activator.CreateInstance(t)!)];
}
