using PanoramicData.NugetManagement.Services;

namespace PanoramicData.NugetManagement.Web.Services.AiChange;

/// <summary>
/// Takes an AI change from a clean clone to a merged pull request, or says which gate stopped it.
/// </summary>
/// <remarks>
/// Every decision is a gate that either lets the change on or names why it cannot go. Nothing reaches
/// <c>main</c> except through the merge at the end, which needs the local gate and, where the repository
/// has CI, the CI gate too. The clone is always returned to <c>main</c>, however the attempt ends,
/// because a clone left on a branch or dirty is what blocks the next fix.
/// </remarks>
public sealed class AiChangePipeline(
	IAiChangeGit git,
	IAiChangeTests tests,
	IAiChangeWork work,
	IGitHubWriteApi github,
	AiChangeOptions options)
{
	private const int _evidenceCharacters = 2_000;

	/// <summary>
	/// Runs one attempt.
	/// </summary>
	/// <param name="request">What to change.</param>
	/// <param name="say">Where progress lines go.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	public async Task<AiChangeOutcome> RunAsync(
		AiChangeRequest request,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		var refusal = await git.RefusalAsync(cancellationToken).ConfigureAwait(false);

		if (refusal is not null)
		{
			return Finish(say, new AiChangeOutcome(AiChangeOutcomeKind.Refused, refusal));
		}

		return await OnBranchAsync(
			request,
			say,
			async () =>
			{
				string? redFilter = null;
				var redEvidence = string.Empty;

				if (request.RequiresRedTest)
				{
					(redFilter, redEvidence) = await FindRedTestAsync(request, say, cancellationToken).ConfigureAwait(false);

					if (redFilter is null)
					{
						await ReportNoRedTestAsync(request, cancellationToken).ConfigureAwait(false);

						return (new AiChangeOutcome(
							AiChangeOutcomeKind.NoValidRedTest,
							"No test could be made to reproduce the report, so no fix was attempted."), null, string.Empty);
					}
				}

				var fix = await work.FixAsync(request, redFilter, cancellationToken).ConfigureAwait(false);

				return fix.Succeeded
					? (null, redFilter, redEvidence)
					: (Failed("fix", fix.Summary), null, string.Empty);
			},
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Delivers edits that are already in the working tree: branch, gate, pull request, CI, merge.
	/// </summary>
	/// <param name="request">What was changed.</param>
	/// <param name="ownedPaths">The repository-relative paths the caller wrote, which must be the only uncommitted changes.</param>
	/// <param name="say">Where progress lines go.</param>
	/// <param name="cancellationToken">Signalled when the user stops the work.</param>
	/// <remarks>
	/// For a task whose model session ran before delivery, such as a rule fix. There is no red step: a rule
	/// is its own specification and was re-checked by the session that made the edit.
	/// </remarks>
	public async Task<AiChangeOutcome> DeliverAsync(
		AiChangeRequest request,
		IReadOnlySet<string> ownedPaths,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		var refusal = await git.DeliverRefusalAsync(ownedPaths, cancellationToken).ConfigureAwait(false);

		if (refusal is not null)
		{
			return Finish(say, new AiChangeOutcome(AiChangeOutcomeKind.Refused, refusal));
		}

		return await OnBranchAsync(
			request,
			say,
			() => Task.FromResult<(AiChangeOutcome?, string?, string)>((null, null, string.Empty)),
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<AiChangeOutcome> OnBranchAsync(
		AiChangeRequest request,
		Action<string> say,
		Func<Task<(AiChangeOutcome? Early, string? RedFilter, string RedEvidence)>> prepare,
		CancellationToken cancellationToken)
	{
		var branch = $"ai/{request.Slug}";
		var deleteRemote = false;

		await git.CreateBranchAsync(branch, cancellationToken).ConfigureAwait(false);

		try
		{
			var (early, redFilter, redEvidence) = await prepare().ConfigureAwait(false);

			if (early is not null)
			{
				return Finish(say, early);
			}

			if (options.ChangesReady is { } changesReady)
			{
				try
				{
					await changesReady(cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					say($"⚠️ Could not keep a copy of the changes for the Changes tab: {ex.Message}");
				}
			}

			var gate = await RunLocalGateAsync(redFilter, cancellationToken).ConfigureAwait(false);

			if (!gate.Passed)
			{
				return Finish(say, Failed(gate.FailedGate!, gate.Message));
			}

			// From here until a pull request owns the branch, it may exist on origin with nothing to clean it
			// up, so a failure below must delete it.
			deleteRemote = true;

			string sha;
			int number;

			try
			{
				sha = await git.CommitAsync(request.Title, cancellationToken).ConfigureAwait(false);
				await git.PushBranchAsync(branch, cancellationToken).ConfigureAwait(false);

				number = await github
					.CreatePullRequestAsync(
						request.Owner,
						request.Name,
						branch,
						"main",
						request.Title,
						BuildBody(request, redEvidence, gate.Message),
						cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return Finish(say, new AiChangeOutcome(
					AiChangeOutcomeKind.DeliveryFailed,
					$"Delivery failed before a pull request existed: {ex.Message}"));
			}

			// The pull request now owns the branch, and a failed check or merge leaves both for a person.
			deleteRemote = false;

			say($"Opened pull request #{number}.");

			var ci = await WaitForCiAsync(request, sha, say, cancellationToken).ConfigureAwait(false);

			if (ci is not null)
			{
				return Finish(say, ci with { PullRequestNumber = number });
			}

			try
			{
				await github
					.MergePullRequestAsync(request.Owner, request.Name, number, sha, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				return Finish(say, new AiChangeOutcome(
					AiChangeOutcomeKind.LeftOpenMergeRefused,
					$"GitHub refused the merge of #{number}: {ex.Message}",
					number));
			}

			deleteRemote = true;

			return Finish(say, new AiChangeOutcome(
				AiChangeOutcomeKind.Merged,
				$"Merged pull request #{number}.",
				number));
		}
		finally
		{
			// CancellationToken.None on purpose: a stop is the very case that needs the clone put back.
			var problem = await git.ReturnToMainAsync(branch, deleteRemote, CancellationToken.None).ConfigureAwait(false);

			// Said, not swallowed: a clone left on an ai/ branch blocks every later fix on this repository,
			// and the outcome line alone (even "Merged") would not tell anyone.
			if (problem is not null)
			{
				say($"⚠️ {problem}");
			}
		}
	}

	private static AiChangeOutcome Finish(Action<string> say, AiChangeOutcome outcome)
	{
		say(outcome.Message);
		return outcome;
	}

	private static AiChangeOutcome Failed(string gate, string detail)
		=> new(AiChangeOutcomeKind.LocalGateFailed, $"The {gate} did not pass: {detail}");

	private async Task<(string? Filter, string Evidence)> FindRedTestAsync(
		AiChangeRequest request,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		string? feedback = null;

		for (var attempt = 1; attempt <= options.MaxRedAttempts; attempt++)
		{
			var filter = await work.WriteRedTestAsync(request, feedback, cancellationToken).ConfigureAwait(false);

			if (filter is null)
			{
				feedback = "No test was written. Write a test that fails while the reported bug is present.";
				continue;
			}

			var run = await tests.RunOnlyAsync(filter, cancellationToken).ConfigureAwait(false);
			var verdict = RedTestGate.Validate(run);

			if (verdict.IsValidRed)
			{
				say($"Red test confirmed on attempt {attempt}.");
				return (filter, Tail(run.Output));
			}

			say($"Red test attempt {attempt} rejected: {verdict.Message.Split('\n')[0]}");
			feedback = verdict.Message;
		}

		return (null, string.Empty);
	}

	private async Task ReportNoRedTestAsync(AiChangeRequest request, CancellationToken cancellationToken)
	{
		if (request.IssueNumber is not { } issue)
		{
			return;
		}

		await github
			.CommentAsync(
				request.Owner,
				request.Name,
				issue,
				"An automated attempt could not write a test that reproduces this report, so no fix was "
					+ "attempted. That points at a gap in the tests or at a report that needs more detail; "
					+ "once there is a failing test, a fix can follow.",
				cancellationToken)
			.ConfigureAwait(false);
	}

	private sealed record GateResult(bool Passed, string? FailedGate, string Message);

	private async Task<GateResult> RunLocalGateAsync(string? redFilter, CancellationToken cancellationToken)
	{
		var changed = await git.ChangedFilesAsync(cancellationToken).ConfigureAwait(false);

		var rejected = changed.FirstOrDefault(file => !options.Limits.IsAllowedPath(file.Path));

		if (rejected is not null)
		{
			return new GateResult(false, "path allowlist", $"{rejected.Path} may not be changed.");
		}

		var productionPaths = changed.Count(file => !options.Limits.IsTestPath(file.Path));

		if (productionPaths > options.Limits.MaxPaths)
		{
			return new GateResult(false, "path cap", $"{productionPaths} files changed, and at most {options.Limits.MaxPaths} may be.");
		}

		var build = await tests.BuildAsync(cancellationToken).ConfigureAwait(false);

		if (!build.Succeeded)
		{
			return new GateResult(false, "build", build.Output);
		}

		if (redFilter is not null)
		{
			var green = await tests.RunOnlyAsync(redFilter, cancellationToken).ConfigureAwait(false);

			if (!green.Passed)
			{
				return new GateResult(false, "reproducing test", "it still fails after the fix.");
			}
		}

		var all = await tests.RunAllAsync(cancellationToken).ConfigureAwait(false);

		if (!all.Passed)
		{
			return new GateResult(false, "tests", Tail(all.Output));
		}

		// Production code only. A test file's own lines run by definition, and pooled with the production
		// lines they would carry untested code over the bar.
		var code = changed.Where(file => IsCode(file.Path) && !options.Limits.IsTestPath(file.Path)).ToList();

		if (code.Count == 0)
		{
			return new GateResult(true, null, "No code changed, so no changed-line coverage was needed.");
		}

		var cobertura = await tests.CoberturaAsync(cancellationToken).ConfigureAwait(false);

		if (cobertura is null)
		{
			return new GateResult(false, "coverage", "no coverage report could be produced.");
		}

		var coverage = ChangedLineCoverage.Evaluate(code, cobertura);

		return new GateResult(coverage.Passed, coverage.Passed ? null : "coverage", coverage.Message);
	}

	private static bool IsCode(string path)
		=> path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
			|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

	private async Task<AiChangeOutcome?> WaitForCiAsync(
		AiChangeRequest request,
		string sha,
		Action<string> say,
		CancellationToken cancellationToken)
	{
		var waited = TimeSpan.Zero;

		while (true)
		{
			var runs = await github
				.GetCheckRunsAsync(request.Owner, request.Name, sha, cancellationToken)
				.ConfigureAwait(false);

			switch (CheckRunGate.Evaluate(runs))
			{
				case CheckGateResult.Passed:
					return null;

				case CheckGateResult.Failed:
					var failed = string.Join(", ", runs.Where(run => run.Conclusion == CheckRunConclusion.Failed).Select(run => run.Name));
					return new AiChangeOutcome(AiChangeOutcomeKind.LeftOpenCiFailed, $"CI failed ({failed}); the pull request is left open.");

				case CheckGateResult.NoChecks when waited >= options.NoChecksGrace:
					say("The repository has no CI checks, so the local gate alone decides the merge.");
					return null;
			}

			if (waited >= options.CiTimeout)
			{
				return new AiChangeOutcome(
					AiChangeOutcomeKind.LeftOpenCiTimedOut,
					$"CI had not finished after {options.CiTimeout.TotalMinutes:0} minutes; the pull request is left open.");
			}

			await options.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
			waited += options.PollInterval;
		}
	}

	private static string BuildBody(AiChangeRequest request, string redEvidence, string coverageMessage)
	{
		var lines = new List<string>
		{
			request.IssueNumber is { } issue ? $"Fixes #{issue}" : $"Automated fix: {request.Title}",
			string.Empty,
			"Made and checked by this application; merged automatically once the gates passed."
		};

		if (redEvidence.Length > 0)
		{
			lines.Add(string.Empty);
			lines.Add("### Reproduction (failing before the fix)");
			lines.Add("```");
			lines.Add(redEvidence);
			lines.Add("```");
		}

		lines.Add(string.Empty);
		lines.Add("### Local gate");
		lines.Add("The build, the full suite and the reproducing test pass. " + coverageMessage);

		return string.Join('\n', lines);
	}

	private static string Tail(string output)
		=> output.Length <= _evidenceCharacters ? output : output[^_evidenceCharacters..];
}
