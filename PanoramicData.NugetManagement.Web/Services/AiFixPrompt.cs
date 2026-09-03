using System.Collections;
using System.Globalization;
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
	/// <param name="targetPath">
	/// The one file this session is for, when the failure was split across several. Everything the
	/// advisory says about the other files is dropped rather than merely de-emphasised: a small model
	/// told about three files will try to fix three files.
	/// </param>
	public static string BuildTask(
		RuleResult result,
		string repositoryFullName,
		IRuleAiPlaybook? playbook,
		string? targetPath = null)
	{
		var builder = new StringBuilder();

		// The target's own description, where the rule wrote one. Everything below then describes this
		// file and no other — the rule's repository-wide message and advisory are set aside entirely,
		// because a session told to change one file must not be handed a page about three.
		var target = FindTarget(result.Advisory, targetPath);

		builder.AppendLine($"Repository: {repositoryFullName}");
		builder.AppendLine($"Rule: {result.RuleId} — {result.RuleName}");
		builder.AppendLine();

		if (!string.IsNullOrWhiteSpace(targetPath))
		{
			builder.AppendLine($"Change this one file and no other: {targetPath}");
			builder.AppendLine();
		}

		// The most specific statement of what is wrong that exists, and the same text fed back on a
		// retry, so the model sees one consistent description.
		builder.AppendLine("What the rule reports:");
		builder.AppendLine(target?.Summary ?? result.Message);
		builder.AppendLine();

		if (target is not null)
		{
			builder.AppendLine("Goal:");
			builder.AppendLine($"Change {target.Path} so the problems below no longer apply.");
			builder.AppendLine();

			builder.AppendLine("Guidance:");
			builder.AppendLine(target.Detail);
			builder.AppendLine();
		}
		else if (playbook is not null)
		{
			AppendPlaybook(builder, playbook);
		}
		else
		{
			AppendAdvisoryFallback(builder, result.Advisory);
		}

		AppendData(builder, result.Advisory, targetPath);

		return builder.ToString();
	}

	/// <summary>
	/// The advisory's description of one file, or null when this session is not for one file.
	/// </summary>
	private static AdvisoryTarget? FindTarget(RuleAdvisory? advisory, string? targetPath)
		=> string.IsNullOrWhiteSpace(targetPath)
			? null
			: advisory?.Targets?.FirstOrDefault(candidate => string.Equals(
				candidate.Path.Replace('\\', '/'),
				targetPath.Replace('\\', '/'),
				StringComparison.OrdinalIgnoreCase));

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
	private static void AppendData(StringBuilder builder, RuleAdvisory? advisory, string? targetPath)
	{
		if (advisory is null || advisory.Data.Count == 0)
		{
			return;
		}

		builder.AppendLine("Facts:");

		foreach (var (key, value) in advisory.Data)
		{
			AppendFact(builder, string.Empty, key, Narrow(value, targetPath));
		}
	}

	/// <summary>
	/// One advisory fact, and anything nested inside it, rendered as lines of "key: value".
	/// </summary>
	/// <param name="key">The fact's name.</param>
	/// <param name="value">Its value, of any shape.</param>
	/// <param name="indent">Leading whitespace for the fact's own line; nesting adds two spaces a level.</param>
	/// <param name="markdown">
	/// True to render as a markdown nested list, for the frontier-model prompt the dashboard builds;
	/// false for the plain indented form the small-model prompt uses.
	/// </param>
	/// <remarks>
	/// Shared with the dashboard's remediation prompt, which used to join a fact's items with
	/// <c>string.Join</c> and so rendered CQ-06's list of file dictionaries as a row of
	/// <c>System.Collections.Generic.Dictionary`2[...]</c> — every per-file grade the rule had gathered,
	/// thrown away at the last step.
	/// </remarks>
	public static string RenderFact(string key, object? value, string indent = "", bool markdown = false)
	{
		var builder = new StringBuilder();

		AppendFact(builder, indent, key, value, markdown);

		return builder.ToString();
	}

	/// <summary>
	/// Writes one fact, and anything nested inside it, as indented lines of "key: value".
	/// </summary>
	/// <remarks>
	/// Nesting arrives whether or not it is wanted: CQ-06's <c>files</c> is a list of dictionaries, each
	/// holding a list of dictionaries of its own. Flattening the lot with <c>ToString</c> produced
	/// <c>[path, Publish.ps1]</c> and worse, which is how the model came to be guessing at issues it had
	/// in fact been sent. Indentation is enough structure for a small model and costs a line each.
	/// </remarks>
	private static void AppendFact(
		StringBuilder builder,
		string indent,
		string key,
		object? value,
		bool markdown = false)
	{
		var lead = markdown ? $"{indent}- `{key}`" : $"{indent}{key}";
		var childIndent = indent + "  ";

		switch (value)
		{
			case IDictionary dictionary:
				builder.AppendLine($"{lead}:");

				foreach (DictionaryEntry entry in dictionary)
				{
					AppendFact(
						builder,
						childIndent,
						entry.Key?.ToString() ?? "(none)",
						entry.Value,
						markdown);
				}

				return;

			case string or null:
				builder.AppendLine($"{lead}: {Render(value)}");
				return;

			case IEnumerable items:
			{
				var list = items.Cast<object?>().ToList();

				if (list.Count == 0)
				{
					builder.AppendLine($"{lead}: (none)");
					return;
				}

				// A list of scalars reads better on one line than as a stack of numbered bullets, and
				// most advisory data — missing files, package names — is exactly that.
				if (list.All(item => item is null or string or ValueType))
				{
					builder.AppendLine($"{lead}: {string.Join(", ", list.Select(Render))}");
					return;
				}

				builder.AppendLine($"{lead}:");

				var index = 1;

				foreach (var item in list)
				{
					AppendFact(
						builder,
						childIndent,
						index++.ToString(CultureInfo.InvariantCulture),
						item,
						markdown);
				}

				return;
			}

			default:
				builder.AppendLine($"{lead}: {Render(value)}");
				return;
		}
	}

	/// <summary>
	/// Drops everything a fact says about files other than this session's.
	/// </summary>
	/// <remarks>
	/// Recognised by shape rather than by key name: a list whose entries are dictionaries carrying a
	/// <c>path</c> is the form every rule that splits its fix across files already emits, and matching on
	/// it means a new such rule needs nothing here. A value of any other shape is left alone, and a
	/// target that matches nothing leaves the list whole rather than emptying it — an empty Facts block
	/// would tell the model there is nothing to do.
	/// </remarks>
	private static object? Narrow(object? value, string? targetPath)
	{
		if (string.IsNullOrWhiteSpace(targetPath)
			|| value is string
			|| value is not IEnumerable items)
		{
			return value;
		}

		var list = items.Cast<object?>().ToList();

		var matching = list
			.OfType<IDictionary>()
			.Where(entry => entry.Contains("path")
				&& string.Equals(
					entry["path"]?.ToString()?.Replace('\\', '/'),
					targetPath.Replace('\\', '/'),
					StringComparison.OrdinalIgnoreCase))
			.Cast<object?>()
			.ToList();

		return matching.Count == 0 ? value : matching;
	}

	private static string Render(object? value) => value switch
	{
		null => "(none)",
		string text => text,
		IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Render)),
		_ => value.ToString() ?? "(none)"
	};
}
