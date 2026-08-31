using System.Collections;
using System.Text;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Builds what the model is told.
/// </summary>
/// <remarks>
/// Three layers, kept apart on purpose: fixed instructions that never vary, a per-rule playbook where
/// one exists, and this repository's own data. Only the middle layer needs writing per rule, and only
/// the last changes per repository.
/// </remarks>
public static class AiFixPrompt
{
	/// <summary>
	/// The fixed instructions, sent on every turn.
	/// </summary>
	/// <remarks>
	/// Written for a small model and not for a reader: short imperative sentences, no hedging, no
	/// courtesy, no explanation of why. Every clause here was earned by a failure mode — rewriting whole
	/// files, reformatting unrelated code, wandering into files it was not pointed at, narrating instead
	/// of acting, and declaring success without changing anything.
	/// <para>
	/// It is deliberately short. It is spent on every turn of every attempt, and a small model's
	/// attention is the scarce resource — a long preamble crowds out the task.
	/// </para>
	/// </remarks>
	public const string SystemPrompt = """
		You are fixing one specific problem in one code repository. You work only through the tools.

		Rules:
		1. Make the smallest change that achieves the goal. Change nothing else.
		2. Read a file before you rewrite it. Never write a file you have not read, unless it does not exist yet.
		3. write_file replaces the whole file. Include every line you want to keep.
		4. Touch only the files you were told about.
		5. Do not reformat, re-indent, reorder or tidy anything.
		6. Do not explain your reasoning. Call a tool instead.
		7. When the goal is achieved, call finish. Do not call finish before you have changed something.

		If a tool returns an error, read it and correct your next call.
		""";

	/// <summary>
	/// The task message for one rule in one repository.
	/// </summary>
	/// <param name="result">The failing rule result.</param>
	/// <param name="repositoryFullName">The repository, as "owner/name".</param>
	/// <param name="playbook">Its playbook, or null to fall back to the advisory.</param>
	public static string BuildTask(
		RuleResult result,
		string repositoryFullName,
		IRuleAiPlaybook? playbook)
	{
		var builder = new StringBuilder();

		builder.AppendLine($"Repository: {repositoryFullName}");
		builder.AppendLine($"Rule: {result.RuleId} — {result.RuleName}");
		builder.AppendLine();

		// The rule's own message, always. It is the most specific statement of what is wrong that exists,
		// and it is the same text fed back on a retry, so the model sees one consistent description.
		builder.AppendLine("What the rule reports:");
		builder.AppendLine(result.Message);
		builder.AppendLine();

		if (playbook is not null)
		{
			AppendPlaybook(builder, playbook);
		}
		else
		{
			AppendAdvisoryFallback(builder, result.Advisory);
		}

		AppendData(builder, result.Advisory);

		return builder.ToString();
	}

	private static void AppendPlaybook(StringBuilder builder, IRuleAiPlaybook playbook)
	{
		builder.AppendLine("Goal:");
		builder.AppendLine(playbook.Goal);
		builder.AppendLine();

		if (playbook.Files.Count > 0)
		{
			builder.AppendLine("Files to change:");

			foreach (var file in playbook.Files)
			{
				builder.AppendLine($"- {file}");
			}

			builder.AppendLine();
		}

		builder.AppendLine("Done means:");
		builder.AppendLine(playbook.ExpectedEndState);
		builder.AppendLine();

		builder.AppendLine("Example:");
		builder.AppendLine(playbook.WorkedExample);
		builder.AppendLine();
	}

	/// <summary>
	/// The advisory, used only when no playbook exists.
	/// </summary>
	/// <remarks>
	/// <c>Detail</c> appears here and nowhere else. It is markdown prose written for a frontier model
	/// reading a whole repository's worth of context; for a small model it is a source of tangents. With
	/// no playbook, though, it is the only description of the fix there is, so it is better than nothing.
	/// </remarks>
	private static void AppendAdvisoryFallback(StringBuilder builder, RuleAdvisory? advisory)
	{
		if (advisory is null)
		{
			builder.AppendLine("Goal:");
			builder.AppendLine("Change the repository so that the rule above no longer reports a problem.");
			builder.AppendLine();

			return;
		}

		builder.AppendLine("Goal:");
		builder.AppendLine(advisory.Summary);
		builder.AppendLine();

		if (!string.IsNullOrWhiteSpace(advisory.Detail))
		{
			builder.AppendLine("Guidance:");
			builder.AppendLine(advisory.Detail);
			builder.AppendLine();
		}
	}

	/// <summary>
	/// The advisory's structured data, flattened to lines of "key: value".
	/// </summary>
	/// <remarks>
	/// Rendered rather than serialised as JSON: a small model reads a flat list more reliably than nested
	/// braces, and these values are already the concrete facts — paths, versions, expected content — that
	/// the fix turns on. Collections are expanded, because <c>System.String[]</c> tells the model nothing.
	/// </remarks>
	private static void AppendData(StringBuilder builder, RuleAdvisory? advisory)
	{
		if (advisory is null || advisory.Data.Count == 0)
		{
			return;
		}

		builder.AppendLine("Facts:");

		foreach (var (key, value) in advisory.Data)
		{
			builder.AppendLine($"- {key}: {Render(value)}");
		}
	}

	private static string Render(object? value) => value switch
	{
		null => "(none)",
		string text => text,
		IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Render)),
		_ => value.ToString() ?? "(none)"
	};
}
