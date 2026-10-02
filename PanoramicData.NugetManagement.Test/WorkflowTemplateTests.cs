using PanoramicData.NugetManagement.Models;
using YamlDotNet.RepresentationModel;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// The YAML the app writes into other people's repositories has to be YAML that GitHub accepts and
/// a workflow that can run.
/// </summary>
/// <remarks>
/// Written after two defects in one template, both found only by running the output on real
/// repositories.
/// <para>
/// First, <c>CodeQlWorkflowContent</c> is a raw string inside a tab-indented C# file, so its nested
/// YAML inherited the file's tabs. YAML forbids tabs for indentation, so GitHub rejected every
/// repository's <c>codeql.yml</c> before any step ran: a zero-second failure on every push, in about
/// thirty repositories, for weeks. That was first blamed on the AI fixer generalising a tab
/// convention. It was not; the template was deterministic about it.
/// </para>
/// <para>
/// Second, its checkout had no <c>fetch-depth</c>. Nerdbank.GitVersioning derives the version from
/// commit height and cannot on a shallow clone, so once the YAML was repaired the build inside CodeQL
/// still failed (MSB4018 in GetCommitHeight). The CI template already carries
/// <c>fetch-depth: 0</c> for exactly that reason.
/// </para>
/// </remarks>
public class WorkflowTemplateTests
{
	public static TheoryData<string, string> Templates => new()
	{
		{ nameof(Standards.CiWorkflowContent), Standards.CiWorkflowContent },
		{ nameof(Standards.CodeQlWorkflowContent), Standards.CodeQlWorkflowContent },
		{ nameof(Standards.DependabotYmlContent), Standards.DependabotYmlContent }
	};

	[Theory]
	[MemberData(nameof(Templates))]
	public void EveryTemplate_IsValidYaml(string name, string content)
	{
		var act = () => new YamlStream().Load(new StringReader(content));

		act.Should().NotThrow($"{name} is written into other repositories, where GitHub rejects invalid YAML before any step runs");
	}

	[Theory]
	[MemberData(nameof(Templates))]
	public void NoTemplateIsIndentedWithTabs(string name, string content)
	{
		var tabIndented = content
			.Split('\n')
			.Select((line, index) => (line, number: index + 1))
			.Where(x => x.line.StartsWith('\t'))
			.Select(x => x.number)
			.ToList();

		tabIndented.Should().BeEmpty($"YAML forbids tabs for indentation; {name} has tab-indented lines");
	}

	[Theory]
	[MemberData(nameof(WorkflowTemplates))]
	public void EveryWorkflowTemplate_ChecksOutFullHistory(string name, string content)
	{
		// nbgv computes the version from commit height and fails on a shallow clone, which is what
		// actions/checkout makes by default. Every workflow that builds needs the history.
		content.Should().MatchRegex(@"fetch-depth:\s*0", $"{name} builds the repository, and nbgv cannot version a shallow clone");
	}

	public static TheoryData<string, string> WorkflowTemplates => new()
	{
		{ nameof(Standards.CiWorkflowContent), Standards.CiWorkflowContent },
		{ nameof(Standards.CodeQlWorkflowContent), Standards.CodeQlWorkflowContent }
	};

	[Fact]
	public void TheCodeQlTemplate_StillDeclaresTheJobsItNeeds()
	{
		// Guards the repair itself: re-indenting must not have changed the structure into something
		// that parses but does nothing.
		var stream = new YamlStream();
		stream.Load(new StringReader(Standards.CodeQlWorkflowContent));

		var root = (YamlMappingNode)stream.Documents[0].RootNode;
		var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
		var analyze = (YamlMappingNode)jobs.Children[new YamlScalarNode("analyze")];
		var steps = (YamlSequenceNode)analyze.Children[new YamlScalarNode("steps")];

		steps.Children.Should().HaveCount(4, "checkout, initialize, autobuild and analyze");
		((YamlMappingNode)analyze.Children[new YamlScalarNode("strategy")]).Children
			.Should().ContainKey(new YamlScalarNode("matrix"));
	}
}
