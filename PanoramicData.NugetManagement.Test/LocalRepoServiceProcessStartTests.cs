using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Covers how <see cref="LocalRepoService"/> builds the <see cref="System.Diagnostics.ProcessStartInfo"/>
/// used to run git/dotnet commands. Arguments must travel as discrete <c>ArgumentList</c> entries, not a
/// single pre-formatted <c>Arguments</c> string: a value built by string concatenation (e.g. a branch name
/// or commit message containing a space, quote, or a leading "-") can otherwise be split into extra
/// arguments or mistaken for a flag by the child process (Codacy SEC-01: CommandInjection).
/// </summary>
public class LocalRepoServiceProcessStartTests
{
	[Fact]
	public void BuildProcessStartInfo_DoesNotUseTheArgumentsStringSink()
	{
		var startInfo = LocalRepoService.BuildProcessStartInfo(
			@"C:\repos\example",
			"git",
			["commit", "-m", "message with spaces"]);

		startInfo.Arguments.Should().BeEmpty();
	}

	[Fact]
	public void BuildProcessStartInfo_KeepsAnArgumentWithEmbeddedSpacesAndQuotesAsOneElement()
	{
		var startInfo = LocalRepoService.BuildProcessStartInfo(
			@"C:\repos\example",
			"git",
			["commit", "-m", "has \"quotes\" and spaces"]);

		startInfo.ArgumentList.Should().Equal("commit", "-m", "has \"quotes\" and spaces");
	}

	[Fact]
	public void BuildProcessStartInfo_KeepsAnArgumentStartingWithADashAsOneElement()
	{
		// A branch name an attacker could shape to look like a git flag (e.g. "--upload-pack=...")
		// must still occupy exactly one argv slot, never be re-parsed as a separate option.
		var startInfo = LocalRepoService.BuildProcessStartInfo(
			@"C:\repos\example",
			"git",
			["checkout", "-b", "--upload-pack=evil"]);

		startInfo.ArgumentList.Should().Equal("checkout", "-b", "--upload-pack=evil");
	}

	[Fact]
	public void BuildProcessStartInfo_SetsWorkingDirectoryAndFileName()
	{
		var startInfo = LocalRepoService.BuildProcessStartInfo(
			@"C:\repos\example",
			"git",
			["status"]);

		startInfo.FileName.Should().Be("git");
		startInfo.WorkingDirectory.Should().Be(@"C:\repos\example");
		startInfo.UseShellExecute.Should().BeFalse();
	}
}
