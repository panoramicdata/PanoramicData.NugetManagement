using System.Diagnostics;
using System.Text.RegularExpressions;
using PanoramicData.NugetManagement.Models;
using PanoramicData.NugetManagement.Services;
using PanoramicData.NugetManagement.Web.Models;

namespace PanoramicData.NugetManagement.Web.Services;

public partial class LocalRepoService
{
	/// <summary>
	/// Checks whether git can reach GitHub over https, and — when it cannot because the TLS chain is
	/// not trusted — whether switching git to the Windows certificate store would fix it.
	/// </summary>
	/// <remarks>
	/// Worth distinguishing, because a TLS trust failure is not a fault in this app or in the network:
	/// it is git's bundled OpenSSL not knowing the certificate an intercepting proxy presents, while
	/// every browser on the machine trusts it via the Windows store. The remedy is one setting, and the
	/// only honest way to recommend it is to try it first — hence the second probe, which runs only
	/// after the first has failed for that reason.
	/// </remarks>
	public async Task<GitRemoteAccessResult> CheckRemoteAccessAsync(
		string probeUrl,
		CancellationToken cancellationToken)
	{
		var workingDirectory = Directory.GetCurrentDirectory();

		var (exitCode, output) = await RunCommandAsync(
			workingDirectory, "git", ["ls-remote", "--quiet", probeUrl, "HEAD"], cancellationToken).ConfigureAwait(false);
		if (exitCode == 0)
		{
			return new GitRemoteAccessResult(GitRemoteAccess.Ok, output, SchannelWouldFix: false);
		}

		if (!LooksLikeTlsTrustFailure(output))
		{
			_logger.LogWarning("git cannot reach {Url}: {Output}", probeUrl, output);
			return new GitRemoteAccessResult(GitRemoteAccess.Unreachable, output, SchannelWouldFix: false);
		}

		var (schannelExitCode, _) = await RunCommandAsync(
			workingDirectory,
			"git",
			["-c", "http.sslBackend=schannel", "ls-remote", "--quiet", probeUrl, "HEAD"],
			cancellationToken).ConfigureAwait(false);

		_logger.LogWarning(
			"git cannot verify the TLS chain to {Url}; the Windows certificate store {Verdict}.",
			probeUrl,
			schannelExitCode == 0 ? "would fix it" : "would not fix it either");

		return new GitRemoteAccessResult(GitRemoteAccess.TlsTrustFailure, output, schannelExitCode == 0);
	}

	/// <summary>
	/// Whether git's failure output is about not trusting the certificate chain, as opposed to the host
	/// being unreachable or the repository not existing.
	/// </summary>
	private static bool LooksLikeTlsTrustFailure(string output) =>
		output.Contains("unable to get local issuer certificate", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("SSL certificate problem", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("self-signed certificate", StringComparison.OrdinalIgnoreCase)
		|| output.Contains("self signed certificate", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Clones a repository from GitHub.
	/// </summary>
	public async Task<(bool Success, string Output)> CloneAsync(
		string cloneUrl,
		string repoIdentity,
		Action<string>? onOutput,
		CancellationToken cancellationToken)
	{
		var targetPath = GetLocalPath(repoIdentity);

		// The owner directory (and the clone root itself, on first use) will not exist yet.
		var parent = Path.GetDirectoryName(targetPath) ?? GetReposRoot();
		Directory.CreateDirectory(parent);

		_logger.LogInformation("Cloning {Url} to {Path}", cloneUrl, targetPath);
		return await RunCommandWithStreamingAsync(parent, "git", ["clone", cloneUrl, Path.GetFileName(targetPath)], onOutput, cancellationToken, null).ConfigureAwait(false);
	}
}
