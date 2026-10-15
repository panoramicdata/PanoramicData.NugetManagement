# PDDiffPanel and the AI Fix Changes tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reusable Monaco-based `PDDiffPanel` in PanoramicData.Blazor, and a Changes tab in this app's AI Fix session view that shows git HEAD vs. the clone for each touched file, live.

**Architecture:** Part A adds `PDDiffPanel` (wrapping BlazorMonaco's `StandaloneDiffEditor`, swapping model text in place on parameter change) to `../PanoramicData.Blazor` and releases it. Part B adds `CloneChangeReader` (git + disk) and a Log/Changes switch in `WorkItemDetailView`, then consumes the released package.

**Tech Stack:** .NET 10, Blazor Server, BlazorMonaco 3.5.0, bUnit 2.x, xUnit v3 (FluentAssertions in this repo), git CLI.

**Spec:** `docs/superpowers/specs/2026-10-15-pddiffpanel-changes-tab-design.md`

## Global Constraints

- Tabs for C#/Razor/XML/JSON; spaces only for YAML. Read `.editorconfig`; fix IDE0055 in files touched; do not reformat unrelated code.
- `TreatWarningsAsErrors` stays on. Build only the affected project, not the whole solution.
- Do not edit the PanoramicData.Blazor `main` checkout directly: use a worktree/branch there. Concurrent Claude sessions edit both repos; check whose file broke the build before blaming your own change.
- The dev server of this app locks the build: stop it, or build with `-p:OutDir=bin\verify\` into an in-repo subdir.
- Starting the app claims real queued work (clones, AI fixes). Run it only with the queue idle and the user's go-ahead.
- Razor: no `@* *@` comments inside tags (builds clean, crashes on render).
- Commits end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; PR bodies end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Out of scope: diffs for non-AI remediations, Dirty-repo working-tree diff, inline diffs in the log.

## Review Focus

- File deleted in the clone (`Modified` is empty, `Original` is HEAD): shows as a deletion, not an error.
- New untracked file: `Original` empty, shown as added.
- Binary file or file over 1 MB: "not diffable (N bytes)", never loaded into Monaco.
- Path with spaces or non-ASCII characters, and a renamed file: must read via `-z` output, not parsed from quoted text.
- Repo with no commits or a clone path that vanished mid-session: reader returns an empty list, no exception into the render.

## File Structure

PanoramicData.Blazor (`../PanoramicData.Blazor`):
- Create `PanoramicData.Blazor/Models/Monaco/DiffLanguage.cs`: file name to Monaco language id.
- Create `PanoramicData.Blazor/PDDiffPanel.razor`, `PDDiffPanel.razor.cs`, `PDDiffPanel.razor.css`.
- Create `PanoramicData.Blazor.Demo/Pages/PDDiffPanel.razor`; modify `Shared/NavMenu.razor`.
- Create `PanoramicData.Blazor.Test/Components/DiffLanguageTests.cs`, `PDDiffPanelTests.cs`.
- Modify `ComponentDocumentation.md`.

This repo:
- Create `PanoramicData.NugetManagement.Web/Services/CloneChangeReader.cs`: git + disk, no UI.
- Modify `PanoramicData.NugetManagement.Web/Models/WorkItem.cs`: `PreExistingChanges`.
- Modify `PanoramicData.NugetManagement.Web/Components/WorkItemDetailView.razor`: Log/Changes switch.
- Modify `PanoramicData.NugetManagement.Web/wwwroot/app.css`.
- Create `PanoramicData.NugetManagement.Test/CloneChangeReaderTests.cs`.
- Modify `Directory.Packages.props`: PanoramicData.Blazor version.

---

# Part A: PanoramicData.Blazor

Work in a worktree: `git -C ../PanoramicData.Blazor worktree add ../PanoramicData.Blazor-diffpanel -b feature/pddiffpanel`. All Part A paths below are relative to that worktree.

### Task A1: DiffLanguage

**Files:**
- Create: `PanoramicData.Blazor/Models/Monaco/DiffLanguage.cs`
- Test: `PanoramicData.Blazor.Test/Components/DiffLanguageTests.cs`

**Interfaces:**
- Produces: `public static class DiffLanguage { public static string FromFileName(string? fileName); }` returning a Monaco language id, `"plaintext"` when unknown or null.

- [ ] **Step 1: Write the failing test**

```csharp
namespace PanoramicData.Blazor.Test.Components;

public class DiffLanguageTests
{
	[Theory]
	[InlineData("Foo.cs", "csharp")]
	[InlineData("src/Page.razor", "razor")]
	[InlineData("a/b/app.json", "json")]
	[InlineData("ci.yml", "yaml")]
	[InlineData("ci.yaml", "yaml")]
	[InlineData("x.csproj", "xml")]
	[InlineData("x.props", "xml")]
	[InlineData("README.md", "markdown")]
	[InlineData("run.ps1", "powershell")]
	[InlineData("site.css", "css")]
	[InlineData("app.TS", "typescript")]
	[InlineData("noextension", "plaintext")]
	[InlineData("weird.zzz", "plaintext")]
	[InlineData(null, "plaintext")]
	public void FromFileName_MapsExtension(string? fileName, string expected)
		=> DiffLanguage.FromFileName(fileName).Should().Be(expected);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test PanoramicData.Blazor.Test --filter "FullyQualifiedName~DiffLanguageTests"`
Expected: build FAIL, `DiffLanguage` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace PanoramicData.Blazor.Models.Monaco;

/// <summary>
/// Maps a file name to the Monaco language id used to highlight it.
/// </summary>
public static class DiffLanguage
{
	private static readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase)
	{
		[".cs"] = "csharp",
		[".razor"] = "razor",
		[".cshtml"] = "razor",
		[".json"] = "json",
		[".yml"] = "yaml",
		[".yaml"] = "yaml",
		[".csproj"] = "xml",
		[".props"] = "xml",
		[".targets"] = "xml",
		[".slnx"] = "xml",
		[".xml"] = "xml",
		[".md"] = "markdown",
		[".ps1"] = "powershell",
		[".css"] = "css",
		[".html"] = "html",
		[".js"] = "javascript",
		[".ts"] = "typescript",
		[".sql"] = "sql",
		[".sh"] = "shell"
	};

	/// <summary>
	/// Returns the Monaco language id for <paramref name="fileName"/>, or <c>plaintext</c> when unknown.
	/// </summary>
	/// <param name="fileName">A file name or path, or null.</param>
	public static string FromFileName(string? fileName)
		=> _byExtension.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out var language)
			? language
			: "plaintext";
}
```

- [ ] **Step 4: Run to verify it passes** (same command). Expected: PASS.
- [ ] **Step 5: Commit** `feat: DiffLanguage maps file names to Monaco languages`

### Task A2: PDDiffPanel

**Files:**
- Create: `PanoramicData.Blazor/PDDiffPanel.razor`, `PDDiffPanel.razor.cs`, `PDDiffPanel.razor.css`
- Test: `PanoramicData.Blazor.Test/Components/PDDiffPanelTests.cs`

**Interfaces:**
- Consumes: `DiffLanguage.FromFileName(string?)` from A1.
- Produces: component `PDDiffPanel` with parameters `string Original`, `string Modified`, `string? FileName`, `string? Language`, `bool SideBySide = true`, `bool ReadOnly = true`, `string Height = "100%"`, `string Theme = ""`; property `string EffectiveLanguage`; property `bool HasChanges`.

Before coding, open `PDMonacoEditor.razor.cs` `OnAfterRenderAsync`/theme code and copy how it picks the theme and initializes the JS module; reuse that logic rather than inventing a second theme rule. BlazorMonaco 3.5.0 API: `StandaloneDiffEditor` with `ConstructionOptions` (`StandaloneDiffEditorConstructionOptions`, `Func<StandaloneDiffEditor, ...>`), `OnDidInit`, `SetModel(DiffEditorModel)`, and `Global.CreateModel(IJSRuntime, string value, string? language, string? uri)` returning a `TextModel` that has `SetValue(string)` and `Dispose()`. Check these names against the package's XML docs/IDE tooltips; if one differs, adjust the code below, not the design.

- [ ] **Step 1: Write the failing tests** (parameter logic only; Monaco itself needs a browser, so keep it out of bUnit)

```csharp
namespace PanoramicData.Blazor.Test.Components;

public class PDDiffPanelTests : BunitContext
{
	public PDDiffPanelTests() => JSInterop.Mode = JSRuntimeMode.Loose;

	[Fact]
	public void EffectiveLanguage_UsesExplicitLanguageFirst()
	{
		var cut = Render<PDDiffPanel>(p => p
			.Add(c => c.FileName, "a.cs")
			.Add(c => c.Language, "json"));
		cut.Instance.EffectiveLanguage.Should().Be("json");
	}

	[Fact]
	public void EffectiveLanguage_FallsBackToFileName()
	{
		var cut = Render<PDDiffPanel>(p => p.Add(c => c.FileName, "a.cs"));
		cut.Instance.EffectiveLanguage.Should().Be("csharp");
	}

	[Fact]
	public void HasChanges_FalseWhenIdentical_ShowsNoChanges()
	{
		var cut = Render<PDDiffPanel>(p => p
			.Add(c => c.Original, "same")
			.Add(c => c.Modified, "same"));
		cut.Instance.HasChanges.Should().BeFalse();
		cut.Markup.Should().Contain("No changes");
	}

	[Fact]
	public void HasChanges_TrueWhenDifferent_HidesNoChanges()
	{
		var cut = Render<PDDiffPanel>(p => p
			.Add(c => c.Original, "a")
			.Add(c => c.Modified, "b"));
		cut.Instance.HasChanges.Should().BeTrue();
		cut.Markup.Should().NotContain("No changes");
	}

	[Fact]
	public void SideBySideToggle_FlipsOnButtonClick()
	{
		var cut = Render<PDDiffPanel>(p => p.Add(c => c.Original, "a").Add(c => c.Modified, "b"));
		cut.Instance.SideBySide.Should().BeTrue();
		cut.Find(".pd-diffpanel-toggle").Click();
		cut.Instance.SideBySide.Should().BeFalse();
	}
}
```

- [ ] **Step 2: Run to verify it fails.** `dotnet test PanoramicData.Blazor.Test --filter "FullyQualifiedName~PDDiffPanelTests"` Expected: build FAIL.

- [ ] **Step 3: Implement.** `PDDiffPanel.razor`:

```razor
<div class="pd-diffpanel" style="height:@Height">
	<div class="pd-diffpanel-toolbar">
		@if (!string.IsNullOrEmpty(FileName))
		{
			<span class="pd-diffpanel-filename">@FileName</span>
		}
		<button type="button" class="btn btn-sm btn-outline-secondary pd-diffpanel-toggle" @onclick="ToggleLayoutAsync">
			@(SideBySide ? "Inline" : "Side by side")
		</button>
	</div>
	@if (!HasChanges)
	{
		<div class="pd-diffpanel-empty">No changes</div>
	}
	<div class="pd-diffpanel-editor" hidden="@(!HasChanges)">
		<StandaloneDiffEditor @ref="_editor"
							  Id="@Id"
							  ConstructionOptions="GetOptions"
							  OnDidInit="OnEditorInitAsync" />
	</div>
</div>
```

`PDDiffPanel.razor.cs` (follow the license/xml-doc style of `PDMonacoEditor.razor.cs`):

```csharp
using BlazorMonaco;
using BlazorMonaco.Editor;
using PanoramicData.Blazor.Models.Monaco;

namespace PanoramicData.Blazor;

/// <summary>
/// A read-only (by default) Monaco diff view of two texts. Changing <see cref="Original"/> or
/// <see cref="Modified"/> swaps text into the existing models, so scroll position survives live refresh.
/// </summary>
public partial class PDDiffPanel : IAsyncDisposable
{
	private static int _seq;

	private StandaloneDiffEditor? _editor;
	private TextModel? _originalModel;
	private TextModel? _modifiedModel;
	private bool _initialized;
	private string _shownOriginal = string.Empty;
	private string _shownModified = string.Empty;
	private string _shownLanguage = string.Empty;

	/// <summary>Gets or sets the JavaScript runtime.</summary>
	[Inject]
	public IJSRuntime? JSRuntime { get; set; }

	/// <summary>Gets or sets the editor id.</summary>
	[Parameter]
	public string Id { get; set; } = $"PDDiffPanel-{++_seq}";

	/// <summary>Gets or sets the left-hand (before) text.</summary>
	[Parameter]
	public string Original { get; set; } = string.Empty;

	/// <summary>Gets or sets the right-hand (after) text.</summary>
	[Parameter]
	public string Modified { get; set; } = string.Empty;

	/// <summary>Gets or sets the file name shown in the toolbar and used to infer the language.</summary>
	[Parameter]
	public string? FileName { get; set; }

	/// <summary>Gets or sets an explicit Monaco language id, overriding the one inferred from <see cref="FileName"/>.</summary>
	[Parameter]
	public string? Language { get; set; }

	/// <summary>Gets or sets whether the two sides are shown side by side (true) or inline (false).</summary>
	[Parameter]
	public bool SideBySide { get; set; } = true;

	/// <summary>Gets or sets whether the modified side is read-only.</summary>
	[Parameter]
	public bool ReadOnly { get; set; } = true;

	/// <summary>Gets or sets the CSS height of the panel.</summary>
	[Parameter]
	public string Height { get; set; } = "100%";

	/// <summary>Gets or sets the Monaco theme; empty uses Monaco's default.</summary>
	[Parameter]
	public string Theme { get; set; } = string.Empty;

	/// <summary>Gets the language id in effect.</summary>
	public string EffectiveLanguage => Language ?? DiffLanguage.FromFileName(FileName);

	/// <summary>Gets whether the two texts differ.</summary>
	public bool HasChanges => !string.Equals(Original, Modified, StringComparison.Ordinal);

	private StandaloneDiffEditorConstructionOptions GetOptions(StandaloneDiffEditor _)
		=> new()
		{
			AutomaticLayout = true,
			ReadOnly = ReadOnly,
			OriginalEditable = false,
			RenderSideBySide = SideBySide,
			Theme = string.IsNullOrEmpty(Theme) ? null : Theme
		};

	private async Task OnEditorInitAsync()
	{
		if (_editor is null || JSRuntime is null)
		{
			return;
		}

		_originalModel = await Global.CreateModel(JSRuntime, Original, EffectiveLanguage, null).ConfigureAwait(true);
		_modifiedModel = await Global.CreateModel(JSRuntime, Modified, EffectiveLanguage, null).ConfigureAwait(true);
		await _editor.SetModel(new DiffEditorModel { Original = _originalModel, Modified = _modifiedModel }).ConfigureAwait(true);
		(_shownOriginal, _shownModified, _shownLanguage) = (Original, Modified, EffectiveLanguage);
		_initialized = true;
	}

	/// <inheritdoc />
	protected override async Task OnParametersSetAsync()
	{
		if (!_initialized || _originalModel is null || _modifiedModel is null || JSRuntime is null)
		{
			return;
		}

		if (_shownOriginal != Original)
		{
			await _originalModel.SetValue(Original).ConfigureAwait(true);
			_shownOriginal = Original;
		}

		if (_shownModified != Modified)
		{
			await _modifiedModel.SetValue(Modified).ConfigureAwait(true);
			_shownModified = Modified;
		}

		if (_shownLanguage != EffectiveLanguage)
		{
			await Global.SetModelLanguage(JSRuntime, _originalModel, EffectiveLanguage).ConfigureAwait(true);
			await Global.SetModelLanguage(JSRuntime, _modifiedModel, EffectiveLanguage).ConfigureAwait(true);
			_shownLanguage = EffectiveLanguage;
		}
	}

	private async Task ToggleLayoutAsync()
	{
		SideBySide = !SideBySide;
		if (_editor is not null)
		{
			await _editor.UpdateOptions(new DiffEditorBaseOptions { RenderSideBySide = SideBySide }).ConfigureAwait(true);
		}
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_originalModel is not null)
		{
			await _originalModel.DisposeModel().ConfigureAwait(true);
		}

		if (_modifiedModel is not null)
		{
			await _modifiedModel.DisposeModel().ConfigureAwait(true);
		}

		GC.SuppressFinalize(this);
	}
}
```

If the BlazorMonaco member names differ (`UpdateOptions`/`DiffEditorBaseOptions`, `Global.SetModelLanguage`, `TextModel.DisposeModel`), use the closest equivalents the compiler shows; the behaviours above are what matters.

`PDDiffPanel.razor.css`:

```css
.pd-diffpanel { display: flex; flex-direction: column; min-height: 200px; }
.pd-diffpanel-toolbar { display: flex; align-items: center; gap: .5rem; padding: .25rem 0; }
.pd-diffpanel-filename { font-family: monospace; opacity: .8; flex: 1; }
.pd-diffpanel-empty { padding: 1rem; opacity: .7; }
.pd-diffpanel-editor { flex: 1; min-height: 0; }
.pd-diffpanel-editor ::deep > div { height: 100%; }
```

- [ ] **Step 4: Run tests.** Same filter. Expected: PASS (all 5).
- [ ] **Step 5: Commit** `feat: PDDiffPanel, a Monaco diff view`

### Task A3: Demo page, docs, Playwright, release

**Files:**
- Create: `PanoramicData.Blazor.Demo/Pages/PDDiffPanel.razor`, `tests/pddiffpanel.spec.ts`
- Modify: `PanoramicData.Blazor.Demo/Shared/NavMenu.razor` (next to the `pdmonaco` link at ~line 206), `ComponentDocumentation.md` (new `## PDDiffPanel` section after PDMonacoEditor at ~line 1411)

- [ ] **Step 1: Demo page** (`@page "/pddiffpanel"`; two textareas bound to `_original`/`_modified`, a file name box, and a `PDDiffPanel` with `Height="400px"`). Seed `_original`/`_modified` with a small C# method before/after.

```razor
@page "/pddiffpanel"

<h1>Diff Panel</h1>

<DemoSourceView SourceFiles="Pages/PDDiffPanel.razor">
	<div class="host">
		<input class="form-control mb-2" @bind="_fileName" @bind:event="oninput" />
		<div class="row mb-2">
			<div class="col"><textarea class="form-control" rows="6" @bind="_original" @bind:event="oninput"></textarea></div>
			<div class="col"><textarea class="form-control" rows="6" @bind="_modified" @bind:event="oninput"></textarea></div>
		</div>
		<PDDiffPanel Original="@_original" Modified="@_modified" FileName="@_fileName" Height="400px" />
	</div>
</DemoSourceView>

@code {
	private string _fileName = "Greeter.cs";
	private string _original = "public string Greet() => \"Hello\";\n";
	private string _modified = "public string Greet(string name) => $\"Hello {name}\";\n";
}
```

- [ ] **Step 2: NavMenu link and ComponentDocumentation section** (parameters table copied from the A2 interface list).
- [ ] **Step 3: Playwright spec** modelled on `tests/pdmonaco-editor.spec.ts`: open `/pddiffpanel`, wait for `.monaco-diff-editor`, assert it is visible and that editing the second textarea changes the modified-side text.
- [ ] **Step 4: Run the demo and spec.** `dotnet build PanoramicData.Blazor.Demo`, start it, `npx playwright test tests/pddiffpanel.spec.ts`. Expected: PASS. Look at the page in a browser once: dark theme, both sides rendered, toggle works, editing a textarea updates the diff without the panel flashing.
- [ ] **Step 5: Commit, push the branch, open a PR** to PanoramicData.Blazor `main`; wait for CI. After merge, release with that repo's `Publish.ps1` (not hand tags; see the repo's CLAUDE.md) and note the published version number for Part B. Publishing is outward-facing: confirm with the user before running it.

---

# Part B: this repo

Work in a worktree of this repo (concurrent sessions commit to `main`): `git worktree add .claude/worktrees/diffpanel-changes-tab -b feature/changes-tab`.

### Task B1: CloneChangeReader

**Files:**
- Create: `PanoramicData.NugetManagement.Web/Services/CloneChangeReader.cs`
- Test: `PanoramicData.NugetManagement.Test/CloneChangeReaderTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum ChangeKind { Modified, Added, Deleted }

public sealed record ChangedFile(string Path, ChangeKind Kind, string? OriginalPath = null);

public sealed record FilePair(string Original, string Modified, string? NotDiffableReason);

public sealed class CloneChangeReader
{
	public IReadOnlyList<ChangedFile> ReadChanges(string clonePath);
	public FilePair ReadPair(string clonePath, ChangedFile file);
}
```

`Path` is repo-relative with forward slashes. `ReadChanges` returns `[]` for a missing directory, a non-repo, or a repo with no commits. `ReadPair` sets `NotDiffableReason` to `"binary file (N bytes)"` or `"file too large (N bytes)"` (limit 1 048 576) and returns empty strings alongside it.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Diagnostics;
using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

public class CloneChangeReaderTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"changes-{Guid.NewGuid():n}");
	private readonly CloneChangeReader _reader = new();

	public CloneChangeReaderTests()
	{
		Directory.CreateDirectory(_root);
		Git("init", "-q");
		Git("config", "user.email", "t@example.com");
		Git("config", "user.name", "t");
		Git("config", "core.autocrlf", "false");
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "one\n");
		File.WriteAllText(Path.Combine(_root, "gone.txt"), "bye\n");
		File.WriteAllText(Path.Combine(_root, "sp ace é.txt"), "x\n");
		Git("add", "-A");
		Git("commit", "-q", "-m", "init");
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

	private void Git(params string[] args)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!;
		process.WaitForExit();
		process.ExitCode.Should().Be(0);
	}

	[Fact]
	public void CleanTree_HasNoChanges()
		=> _reader.ReadChanges(_root).Should().BeEmpty();

	[Fact]
	public void Modified_Added_Deleted_AreReported()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		File.WriteAllText(Path.Combine(_root, "new.txt"), "n\n");
		File.Delete(Path.Combine(_root, "gone.txt"));

		var changes = _reader.ReadChanges(_root).ToDictionary(c => c.Path);

		changes["keep.txt"].Kind.Should().Be(ChangeKind.Modified);
		changes["new.txt"].Kind.Should().Be(ChangeKind.Added);
		changes["gone.txt"].Kind.Should().Be(ChangeKind.Deleted);
	}

	[Fact]
	public void ReadPair_ModifiedFile_ReturnsHeadAndDisk()
	{
		File.WriteAllText(Path.Combine(_root, "keep.txt"), "two\n");
		var pair = _reader.ReadPair(_root, new ChangedFile("keep.txt", ChangeKind.Modified));
		pair.Original.Should().Be("one\n");
		pair.Modified.Should().Be("two\n");
		pair.NotDiffableReason.Should().BeNull();
	}

	[Fact]
	public void ReadPair_Deleted_HasEmptyModified_Added_HasEmptyOriginal()
	{
		File.Delete(Path.Combine(_root, "gone.txt"));
		File.WriteAllText(Path.Combine(_root, "new.txt"), "n\n");

		var deleted = _reader.ReadPair(_root, new ChangedFile("gone.txt", ChangeKind.Deleted));
		deleted.Original.Should().Be("bye\n");
		deleted.Modified.Should().BeEmpty();

		var added = _reader.ReadPair(_root, new ChangedFile("new.txt", ChangeKind.Added));
		added.Original.Should().BeEmpty();
		added.Modified.Should().Be("n\n");
	}

	[Fact]
	public void PathWithSpacesAndNonAscii_IsReadVerbatim()
	{
		File.WriteAllText(Path.Combine(_root, "sp ace é.txt"), "y\n");
		var change = _reader.ReadChanges(_root).Single();
		change.Path.Should().Be("sp ace é.txt");
		_reader.ReadPair(_root, change).Modified.Should().Be("y\n");
	}

	[Fact]
	public void BinaryFile_IsNotDiffable()
	{
		File.WriteAllBytes(Path.Combine(_root, "b.bin"), [1, 0, 2, 0, 3]);
		var pair = _reader.ReadPair(_root, new ChangedFile("b.bin", ChangeKind.Added));
		pair.NotDiffableReason.Should().StartWith("binary file");
		pair.Modified.Should().BeEmpty();
	}

	[Fact]
	public void HugeFile_IsNotDiffable()
	{
		File.WriteAllText(Path.Combine(_root, "big.txt"), new string('a', 1_048_577));
		var pair = _reader.ReadPair(_root, new ChangedFile("big.txt", ChangeKind.Added));
		pair.NotDiffableReason.Should().StartWith("file too large");
	}

	[Fact]
	public void Rename_ReadsOriginalFromOldPath()
	{
		Git("mv", "keep.txt", "renamed.txt");
		File.WriteAllText(Path.Combine(_root, "renamed.txt"), "one\nmore\n");
		var change = _reader.ReadChanges(_root).Single(c => c.Path == "renamed.txt");
		change.OriginalPath.Should().Be("keep.txt");
		_reader.ReadPair(_root, change).Original.Should().Be("one\n");
	}

	[Fact]
	public void MissingDirectory_AndNonRepo_AreEmpty()
	{
		_reader.ReadChanges(Path.Combine(_root, "nope")).Should().BeEmpty();
		var plain = Path.Combine(Path.GetTempPath(), "nugetmanagement-tests", $"plain-{Guid.NewGuid():n}");
		Directory.CreateDirectory(plain);
		try
		{
			_reader.ReadChanges(plain).Should().BeEmpty();
		}
		finally
		{
			Directory.Delete(plain);
		}
	}
}
```

Match this repo's test conventions (look at `AiFixObservabilityTests` header: `TestWithOutput`, global usings for xUnit/FluentAssertions) and drop the explicit usings the global usings already supply.

- [ ] **Step 2: Run to verify it fails.** `dotnet test PanoramicData.NugetManagement.Test --filter "FullyQualifiedName~CloneChangeReaderTests"` Expected: build FAIL.

- [ ] **Step 3: Implement.** Use `git status --porcelain=v1 -z --untracked-files=all`. Each entry is `XY<space>path\0`; for `R`/`C` the next NUL-separated token is the original path. Map `??` to Added, `D` in either column to Deleted, `A` to Added, everything else to Modified. `ReadPair`: original via `git show HEAD:<path>` (stdout read as UTF-8; empty when Added), modified from `File.ReadAllText` (empty when Deleted). Check size and NUL bytes on the raw bytes before decoding. All git calls use `ArgumentList` and `GetBytes`-safe UTF-8 stdout (`StandardOutputEncoding = Encoding.UTF8`), with `-c core.quotepath=off` as a belt-and-braces. Wrap the whole of `ReadChanges` in a try that returns `[]` for `IOException`, `Win32Exception`, `InvalidOperationException` or non-zero exit.

```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>What happened to a file in the clone relative to git HEAD.</summary>
public enum ChangeKind { Modified, Added, Deleted }

/// <summary>A file changed in the clone. <paramref name="OriginalPath"/> is set for a rename.</summary>
public sealed record ChangedFile(string Path, ChangeKind Kind, string? OriginalPath = null);

/// <summary>The two sides of one file's diff, or why there is none.</summary>
public sealed record FilePair(string Original, string Modified, string? NotDiffableReason);

/// <summary>Reads what an AI fix session has changed in a clone: git HEAD against the working tree.</summary>
public sealed class CloneChangeReader
{
	private const int MaxBytes = 1_048_576;

	/// <summary>Lists the files that differ from HEAD, or nothing when the clone cannot be read.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	public IReadOnlyList<ChangedFile> ReadChanges(string clonePath)
	{
		if (!Directory.Exists(clonePath))
		{
			return [];
		}

		try
		{
			var output = RunGit(clonePath, "status", "--porcelain=v1", "-z", "--untracked-files=all");
			return output is null ? [] : Parse(output);
		}
		catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
		{
			return [];
		}
	}

	/// <summary>Reads both sides of <paramref name="file"/>.</summary>
	/// <param name="clonePath">The clone's directory.</param>
	/// <param name="file">The file, as returned by <see cref="ReadChanges"/>.</param>
	public FilePair ReadPair(string clonePath, ChangedFile file)
	{
		var diskPath = System.IO.Path.Combine(clonePath, file.Path);
		byte[] modified = [];
		if (file.Kind != ChangeKind.Deleted && File.Exists(diskPath))
		{
			modified = File.ReadAllBytes(diskPath);
		}

		var original = file.Kind == ChangeKind.Added
			? []
			: RunGitBytes(clonePath, "show", $"HEAD:{file.OriginalPath ?? file.Path}") ?? [];

		foreach (var side in new[] { modified, original })
		{
			if (side.Length > MaxBytes)
			{
				return new FilePair(string.Empty, string.Empty, $"file too large ({side.Length} bytes)");
			}

			if (Array.IndexOf(side, (byte)0) >= 0)
			{
				return new FilePair(string.Empty, string.Empty, $"binary file ({side.Length} bytes)");
			}
		}

		return new FilePair(Encoding.UTF8.GetString(original), Encoding.UTF8.GetString(modified), null);
	}

	private static List<ChangedFile> Parse(string porcelainZ)
	{
		var tokens = porcelainZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		var changes = new List<ChangedFile>();
		for (var i = 0; i < tokens.Length; i++)
		{
			var token = tokens[i];
			if (token.Length < 4)
			{
				continue;
			}

			var status = token[..2];
			var path = token[3..];
			if (status.Contains('R', StringComparison.Ordinal) || status.Contains('C', StringComparison.Ordinal))
			{
				var oldPath = i + 1 < tokens.Length ? tokens[++i] : null;
				changes.Add(new ChangedFile(path, ChangeKind.Modified, oldPath));
				continue;
			}

			var kind = status == "??" || status.Contains('A', StringComparison.Ordinal) ? ChangeKind.Added
				: status.Contains('D', StringComparison.Ordinal) ? ChangeKind.Deleted
				: ChangeKind.Modified;
			changes.Add(new ChangedFile(path, kind));
		}

		return changes;
	}

	private static string? RunGit(string directory, params string[] args)
	{
		var bytes = RunGitBytes(directory, args);
		return bytes is null ? null : Encoding.UTF8.GetString(bytes);
	}

	private static byte[]? RunGitBytes(string directory, params string[] args)
	{
		var info = new ProcessStartInfo("git")
		{
			WorkingDirectory = directory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		info.ArgumentList.Add("-c");
		info.ArgumentList.Add("core.quotepath=off");
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start.");
		using var stdout = new MemoryStream();
		var errors = process.StandardError.ReadToEndAsync();
		process.StandardOutput.BaseStream.CopyTo(stdout);
		process.WaitForExit();
		_ = errors.GetAwaiter().GetResult();
		return process.ExitCode == 0 ? stdout.ToArray() : null;
	}
}
```

- [ ] **Step 4: Run tests.** Same filter. Expected: PASS (all 9). A `git status` on a repo with no commits still succeeds and lists everything as untracked, which `ReadChanges` reports as Added; that is acceptable (not an exception).
- [ ] **Step 5: Commit** `feat: CloneChangeReader reads a clone's changes against HEAD`

### Task B2: Pre-existing changes baseline

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Models/WorkItem.cs`
- Modify: the place an AI fix item is started (find with `grep -rn "new AiFixSession" PanoramicData.NugetManagement.Web`)
- Test: add a case to `CloneChangeReaderTests.cs`? No: test through a small pure helper below.

**Interfaces:**
- Produces: `WorkItem.PreExistingChanges` (`IReadOnlySet<string>`, default empty) and `CloneChangeReader.PathsOf(IEnumerable<ChangedFile>)` returning a `HashSet<string>` (ordinal).

- [ ] **Step 1: Failing test** in `CloneChangeReaderTests`:

```csharp
[Fact]
public void PathsOf_ReturnsOrdinalSet()
{
	var set = CloneChangeReader.PathsOf([new ChangedFile("A.cs", ChangeKind.Modified), new ChangedFile("a.cs", ChangeKind.Added)]);
	set.Should().BeEquivalentTo(["A.cs", "a.cs"]);
	set.Should().HaveCount(2);
}
```

- [ ] **Step 2: Run, expect FAIL** (no `PathsOf`).
- [ ] **Step 3: Implement.** Add to `CloneChangeReader`:

```csharp
/// <summary>The repository-relative paths of <paramref name="changes"/> as an ordinal set.</summary>
/// <param name="changes">The changes.</param>
public static HashSet<string> PathsOf(IEnumerable<ChangedFile> changes)
	=> new(changes.Select(c => c.Path), StringComparer.Ordinal);
```

Add to `WorkItem`:

```csharp
/// <summary>
/// The paths that were already changed in the clone when an AI fix session started. The Changes tab
/// labels them "pre-existing" so the session is not blamed for them.
/// </summary>
public IReadOnlySet<string> PreExistingChanges { get; set; } = new HashSet<string>(StringComparer.Ordinal);
```

At the point the AI fix item begins running (before the first model turn), set `item.PreExistingChanges = CloneChangeReader.PathsOf(new CloneChangeReader().ReadChanges(localPath));`. Locate it with the grep above; the item and the clone path are both in scope where the session is constructed.

- [ ] **Step 4: Run tests, expect PASS.** Build `PanoramicData.NugetManagement.Web`.
- [ ] **Step 5: Commit** `feat: record paths already dirty when an AI fix starts`

### Task B3: Changes tab

**Files:**
- Modify: `PanoramicData.NugetManagement.Web/Components/WorkItemDetailView.razor`
- Modify: `PanoramicData.NugetManagement.Web/wwwroot/app.css`
- Modify: `Directory.Packages.props` (PanoramicData.Blazor to the version released in A3)

**Interfaces:**
- Consumes: `CloneChangeReader`, `ChangedFile`, `FilePair`, `WorkItem.PreExistingChanges`, `PDDiffPanel`, and a local path for the item. Get the path with `LocalRepoService.GetLocalPath(Item.RepositoryFullName)` (inject `LocalRepoService`; it is how `DashboardService` resolves clones).

- [ ] **Step 1: Bump the package.** Set `PanoramicData.Blazor` in `Directory.Packages.props` to the released version; `dotnet build PanoramicData.NugetManagement.Web` (or with `-p:OutDir=bin\verify\` if the dev server runs). Expected: build succeeds.
- [ ] **Step 2: Markup.** Above the transcript in the `IsAi` branch add a tab switch; render the transcript when `_tab == Tab.Log`, otherwise the Changes view:

```razor
@if (IsAi)
{
	<ul class="nav nav-tabs mb-2">
		<li class="nav-item"><button class="nav-link @(_tab == Tab.Log ? "active" : "")" @onclick="() => _tab = Tab.Log">Log</button></li>
		<li class="nav-item"><button class="nav-link @(_tab == Tab.Changes ? "active" : "")" @onclick="ShowChanges">Changes (@_changes.Count)</button></li>
	</ul>
}

@if (_tab == Tab.Changes)
{
	<div class="work-changes">
		<div class="work-changes-files">
			@foreach (var change in _changes)
			{
				<button class="work-changes-file @(change.Path == _selectedPath ? "selected" : "")" @onclick="() => SelectFile(change.Path)">
					<span class="badge bg-secondary">@change.Kind</span>
					@change.Path
					@if (Item.PreExistingChanges.Contains(change.Path))
					{
						<span class="text-muted small">(pre-existing)</span>
					}
				</button>
			}
			@if (_changes.Count == 0)
			{
				<div class="text-muted small p-2">No changes in the clone yet.</div>
			}
		</div>
		<div class="work-changes-diff">
			@if (_pair is { NotDiffableReason: { } reason })
			{
				<div class="alert alert-secondary">Not diffable: @reason</div>
			}
			else if (_pair is not null)
			{
				<PDDiffPanel Original="@_pair.Original" Modified="@_pair.Modified" FileName="@_selectedPath" Theme="vs-dark" Height="100%" />
			}
		</div>
	</div>
}
else
{
	@* existing transcript block moves here unchanged *@
}
```

(The final `@* *@` is outside any tag, which is safe; delete it when the transcript block is moved in.)

- [ ] **Step 3: Code.** In `@code`:

```csharp
private enum Tab { Log, Changes }

private static readonly CloneChangeReader _reader = new();
private Tab _tab = Tab.Log;
private IReadOnlyList<ChangedFile> _changes = [];
private string? _selectedPath;
private FilePair? _pair;
private DateTime _lastChangeRead = DateTime.MinValue;

[Inject]
private LocalRepoService LocalRepos { get; set; } = default!;

private string? ClonePath
	=> Item?.RepositoryFullName is { } repo ? LocalRepos.GetLocalPath(repo) : null;

private void ShowChanges()
{
	_tab = Tab.Changes;
	RefreshChanges(force: true);
}

private void SelectFile(string path)
{
	_selectedPath = path;
	LoadSelectedPair();
}

/// <summary>Re-reads the clone at most once a second, and only while the Changes tab is showing.</summary>
private void RefreshChanges(bool force = false)
{
	if (_tab != Tab.Changes || ClonePath is not { } clone || (!force && DateTime.UtcNow - _lastChangeRead < TimeSpan.FromSeconds(1)))
	{
		return;
	}

	_lastChangeRead = DateTime.UtcNow;
	_changes = _reader.ReadChanges(clone);
	_selectedPath ??= _changes.FirstOrDefault()?.Path;
	LoadSelectedPair();
}

private void LoadSelectedPair()
{
	var change = _changes.FirstOrDefault(c => c.Path == _selectedPath);
	_pair = change is not null && ClonePath is { } clone ? _reader.ReadPair(clone, change) : null;
}
```

Call `RefreshChanges()` from the existing 200 ms refresh handler (the timer callback that already calls `InvokeAsync(StateHasChanged)`), before `StateHasChanged`. If `_selectedPath` is no longer in `_changes` (file reverted), `LoadSelectedPair` yields `null`, which renders nothing; reset `_selectedPath` to the first change in that case.

- [ ] **Step 4: CSS** in `app.css`:

```css
.work-changes { display: flex; gap: .5rem; height: 60vh; }
.work-changes-files { width: 28%; overflow: auto; display: flex; flex-direction: column; }
.work-changes-file { text-align: left; background: none; border: 0; padding: .25rem .5rem; color: inherit; font-family: monospace; font-size: .8rem; }
.work-changes-file.selected { background: rgba(255, 255, 255, .12); }
.work-changes-diff { flex: 1; min-width: 0; }
```

- [ ] **Step 5: Verify, build, commit.** `dotnet build PanoramicData.NugetManagement.Web` must pass with zero warnings; run `dotnet test PanoramicData.NugetManagement.Test --filter "FullyQualifiedName~CloneChangeReaderTests"`. Then, with the user's go-ahead and an idle queue, run the app on a spare port in this worktree, queue a small AI fix on a throwaway clone (or edit a file in a clone by hand), open the session, switch to Changes, and confirm: file list updates, selected diff swaps without flicker, Log tab still streams. Note that a render crash is possible even when the build is clean, so this live check is required. Commit `feat: Changes tab shows an AI fix's diff in Monaco`, push, open a PR, auto-merge on green CI per repo policy.

---

## Self-review

- Spec coverage: PDDiffPanel and its parameters (A1, A2); demo, docs, tests, release (A3); Changes tab with HEAD vs. clone (B1, B3); refresh on edits, throttled and only while visible (B3 `RefreshChanges`); binary/large handling (B1); pre-existing label (B2, B3); package bump (B3). The spec's trigger "when the transcript records a successful edit" is simplified to a one-second throttled re-read while the tab is visible and the item runs; this meets the same user-visible behaviour with less coupling and is called out here.
- Types: `ChangedFile`, `FilePair`, `ChangeKind`, `PathsOf`, `PreExistingChanges`, `EffectiveLanguage`, `HasChanges` are used consistently across tasks.
- Risk: BlazorMonaco member names in A2 were written from memory of the 3.x API and the README; the first compile is the check, and the plan says to adapt names, not behaviour.
