namespace PanoramicData.NugetManagement.Web.Models;

/// <summary>
/// Whether git can reach GitHub over https, and if not, what kind of failure it is.
/// </summary>
public enum GitRemoteAccess
{
	/// <summary>git reached the remote. Cloning, fetching and pushing can work.</summary>
	Ok,

	/// <summary>
	/// git reached the host but would not trust its certificate chain. Typically TLS interception on
	/// the network, where the intercepting certificate is trusted by the operating system but not by
	/// git's own certificate bundle.
	/// </summary>
	TlsTrustFailure,

	/// <summary>git could not reach the remote for some other reason — offline, blocked, or refused.</summary>
	Unreachable
}

/// <summary>
/// The outcome of probing git's access to GitHub.
/// </summary>
/// <param name="Access">What kind of access git has.</param>
/// <param name="Detail">git's own output, for diagnostics.</param>
/// <param name="SchannelWouldFix">
/// Whether the same probe succeeded when git was pointed at the Windows certificate store. Only
/// established for <see cref="GitRemoteAccess.TlsTrustFailure"/>, and the reason advice can be offered
/// as a tested fix rather than a suggestion.
/// </param>
public sealed record GitRemoteAccessResult(GitRemoteAccess Access, string Detail, bool SchannelWouldFix);
