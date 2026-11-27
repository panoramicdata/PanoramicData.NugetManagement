using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Web.Remediations.CommunityHealth;

/// <summary>
/// Switches on a repository's Sponsorships setting, so the organisation FUNDING.yml shows a Sponsor button.
/// </summary>
/// <remarks>
/// A repository setting rather than a file, so nothing is committed. Success is recorded only when GitHub
/// answers that the setting is on: a token without admin rights is refused, and reporting that as applied
/// would leave COM-06 failing under a green Fix.
/// </remarks>
public sealed partial class SponsorshipsRemediation : IRemediation
{
	private const string Mutation =
		"mutation($id:ID!){updateRepository(input:{repositoryId:$id,hasSponsorshipsEnabled:true}){repository{hasSponsorshipsEnabled}}}";

	private readonly Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> _runGh;

	/// <summary>
	/// Initializes a new instance of the <see cref="SponsorshipsRemediation"/> class.
	/// </summary>
	public SponsorshipsRemediation()
		: this((workingDirectory, arguments) => LocalRepoService
			.RunCommandAsync(workingDirectory, "gh", arguments, CancellationToken.None)
			.GetAwaiter()
			.GetResult())
	{
	}

	/// <summary>
	/// Initializes a new instance with an explicit <c>gh</c> runner (for testing).
	/// </summary>
	/// <param name="runGh">Runs <c>gh</c> in a working directory with the given arguments.</param>
	internal SponsorshipsRemediation(Func<string, IReadOnlyList<string>, (int ExitCode, string Output)> runGh)
		=> _runGh = runGh;

	/// <inheritdoc />
	public string RuleId => "COM-06";

	/// <inheritdoc />
	public bool CanRemediate(RuleResult result)
		=> !result.Passed && result.Advisory is not null && TryGetRepository(result) is not null;

	/// <inheritdoc />
	public void Apply(string localPath, RuleResult result, List<string> applied, Action<string>? onOutput)
	{
		var repository = TryGetRepository(result);
		if (repository is null)
		{
			onOutput?.Invoke("❌ [COM-06] The advisory names no valid owner/name repository, so there is nothing to enable.");
			return;
		}

		var nodeId = TryGetNodeId(localPath, repository, onOutput);
		if (nodeId is not null && TryEnable(localPath, repository, nodeId, onOutput))
		{
			applied.Add($"(sponsorships:{repository})");
			onOutput?.Invoke($"✅ [COM-06] Switched on Sponsorships for {repository}");
		}
	}

	private string? TryGetNodeId(string localPath, string repository, Action<string>? onOutput)
	{
		var (exitCode, output) = _runGh(localPath, ["api", $"repos/{repository}", "--jq", ".node_id"]);
		var nodeId = output.Trim();
		if (exitCode == 0 && NodeIdPattern().IsMatch(nodeId))
		{
			return nodeId;
		}

		onOutput?.Invoke($"❌ [COM-06] Could not read the node id of {repository}: {Describe(output)}");
		return null;
	}

	private bool TryEnable(string localPath, string repository, string nodeId, Action<string>? onOutput)
	{
		var (exitCode, output) = _runGh(localPath, ["api", "graphql", "-f", $"query={Mutation}", "-f", $"id={nodeId}"]);
		if (exitCode == 0
			&& RepositoryContextBuilder.ReadJsonBoolean(output, "data", "updateRepository", "repository", "hasSponsorshipsEnabled") == true)
		{
			return true;
		}

		onOutput?.Invoke(
			$"❌ [COM-06] GitHub did not switch on Sponsorships for {repository} (admin rights are needed): {Describe(output)}");
		return false;
	}

	private static string Describe(string output)
		=> string.IsNullOrWhiteSpace(output) ? "no error output was captured" : output.Trim();

	/// <summary>
	/// The repository the advisory names, or null unless it is a plain owner/name. The value ends up in a
	/// command line, so anything else is refused rather than escaped.
	/// </summary>
	private static string? TryGetRepository(RuleResult result)
		=> result.Advisory?.Data.TryGetValue("repository", out var value) == true
			&& value is string repository
			&& OwnerNamePattern().IsMatch(repository)
				? repository
				: null;

	[GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
	private static partial Regex OwnerNamePattern();

	[GeneratedRegex(@"^[A-Za-z0-9_=-]+$")]
	private static partial Regex NodeIdPattern();
}
