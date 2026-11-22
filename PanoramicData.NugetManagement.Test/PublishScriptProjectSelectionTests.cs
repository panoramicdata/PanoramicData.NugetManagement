using System.Diagnostics;
using PanoramicData.NugetManagement.Models;

namespace PanoramicData.NugetManagement.Test;

/// <summary>Runs the standard Publish.ps1 project selection in pwsh against a scratch git repository.</summary>
public class PublishScriptProjectSelectionTests : TestWithOutput, IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"publish-{Guid.NewGuid():n}");

	public PublishScriptProjectSelectionTests(ITestOutputHelper output) : base(output)
	{
		Directory.CreateDirectory(_root);
		Run("git", "init", "-q");
	}

	public void Dispose()
	{
		foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(_root, recursive: true);
		GC.SuppressFinalize(this);
	}

	[Fact]
	public void Selection_FallsBackToAPackableProjectCoveredByADirectoryBuildPropsReferencingNerdbank()
	{
		Write("Directory.Build.props", "<Project><ItemGroup><PackageReference Include=\"Nerdbank.GitVersioning\" /></ItemGroup></Project>");
		Write("src/Lib.Test/Lib.Test.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>");
		Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		Write(".gitignore", ".claude/\n");
		Write(".claude/worktrees/other/Other.csproj", "<Project><ItemGroup><PackageReference Include=\"Nerdbank.GitVersioning\" /></ItemGroup></Project>");
		Run("git", "add", "-A");

		SelectProject().Should().Be("src/Lib/Lib.csproj");
	}

	[Fact]
	public void Selection_PrefersAProjectReferencingNerdbankDirectly()
	{
		Write("Directory.Build.props", "<Project><ItemGroup><PackageReference Include=\"Nerdbank.GitVersioning\" /></ItemGroup></Project>");
		Write("a/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		Write("b/B.csproj", "<Project><ItemGroup><PackageReference Include=\"Nerdbank.GitVersioning\" /></ItemGroup></Project>");
		Run("git", "add", "-A");

		SelectProject().Should().Be("b/B.csproj");
	}

	[Fact]
	public void Selection_FindsNothing_WhenNoTrackedProjectOrPropsReferencesNerdbank()
	{
		Write("Directory.Build.props", "<Project />");
		Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		Run("git", "add", "-A");

		SelectProject().Should().BeEmpty();
	}

	[Fact]
	public void RepositoryPublishScript_IsTheStandardTemplate()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Publish.ps1")))
		{
			directory = directory.Parent;
		}

		directory.Should().NotBeNull();
		var content = File.ReadAllText(Path.Combine(directory!.FullName, "Publish.ps1"));
		Standards.IsGeneratedContent(content, Standards.PublishPs1Content).Should().BeTrue();
	}

	private string SelectProject()
	{
		var template = Standards.PublishPs1Content;
		var start = template.LastIndexOf('\n', template.IndexOf("git ls-files '*.csproj'", StringComparison.Ordinal)) + 1;
		var error = template.IndexOf("Write-Error \"Could not find a tracked project", StringComparison.Ordinal);
		var end = template.LastIndexOf("if (-not $packableProject) {", error, StringComparison.Ordinal);
		var script = template[start..end] + "\n\"$packableProject\"\n";
		File.WriteAllText(Path.Combine(_root, "select.ps1"), script);

		var output = Run("pwsh", "-NoProfile", "-NonInteractive", "-File", "select.ps1");
		Output.WriteLine($"Selected: '{output}'");
		return output;
	}

	private void Write(string relativePath, string content)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	private string Run(string fileName, params string[] args)
	{
		var info = new ProcessStartInfo(fileName) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!; // nosemgrep
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();
		process.ExitCode.Should().Be(0, standardError);
		return standardOutput.Trim();
	}
}
