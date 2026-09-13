using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="IssueFixSnapshot"/>: putting the clone back when an issue fix gives up.
/// </summary>
/// <remarks>
/// A rule-driven session that fails leaves a partial change behind, and that is tolerable because the
/// rule still fails and the next assessment says so — the mess announces itself. An issue-driven one
/// has nothing pointing at it: a re-assessment would come back clean over a half-finished change
/// derived from a stranger's prose. So it cleans up after itself.
/// </remarks>
public class IssueFixSnapshotTests(ITestOutputHelper output) : TestWithOutput(output), IDisposable
{
	private readonly string _root = Directory.CreateTempSubdirectory("issue-fix-snapshot-").FullName;

	private string Path(string relativePath)
		=> System.IO.Path.Combine(_root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

	private void GivenFile(string relativePath, string content)
	{
		var full = Path(relativePath);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Restore_PutsBackAFileTheSessionChanged()
	{
		GivenFile("src/Documents.cs", "// original");
		var snapshot = IssueFixSnapshot.Capture(_root, ["src/Documents.cs"]);

		File.WriteAllText(Path("src/Documents.cs"), "// the model's attempt");
		snapshot.Restore();

		File.ReadAllText(Path("src/Documents.cs")).Should().Be("// original");
	}

	[Fact]
	public void Restore_DeletesAFileTheSessionCreated()
	{
		var snapshot = IssueFixSnapshot.Capture(_root, ["src/Invented.cs"]);

		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path("src/Invented.cs"))!);
		File.WriteAllText(Path("src/Invented.cs"), "// invented");
		snapshot.Restore();

		File.Exists(Path("src/Invented.cs")).Should().BeFalse(
			"a file that did not exist before a failed attempt has no business surviving it");
	}

	[Fact]
	public void Restore_LeavesFilesOutsideTheSnapshotAlone()
	{
		GivenFile("src/Documents.cs", "// original");
		GivenFile("src/Unrelated.cs", "// somebody else's work in progress");
		var snapshot = IssueFixSnapshot.Capture(_root, ["src/Documents.cs"]);

		File.WriteAllText(Path("src/Unrelated.cs"), "// edited meanwhile");
		snapshot.Restore();

		File.ReadAllText(Path("src/Unrelated.cs")).Should().Be("// edited meanwhile",
			"the clone is shared with whoever else is working in it, so a revert reaches only the "
				+ "files this session was allowed to write");
	}
}
