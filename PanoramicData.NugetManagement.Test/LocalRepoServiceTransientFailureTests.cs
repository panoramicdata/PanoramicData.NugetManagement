using PanoramicData.NugetManagement.Web.Services;

namespace PanoramicData.NugetManagement.Test;

public class LocalRepoServiceTransientFailureTests
{
	[Theory]
	[InlineData("fatal: unable to access 'https://github.com/panoramicdata/Meraki.Api.git/': getaddrinfo() thread failed to start")]
	[InlineData("fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com")]
	[InlineData("fatal: unable to access 'https://github.com/x/y.git/': Failed to connect to github.com port 443 after 21000 ms")]
	[InlineData("error: RPC failed; curl 56 Recv failure: Connection was reset")]
	[InlineData("fatal: the remote end hung up unexpectedly")]
	[InlineData("fatal: early EOF")]
	public void IsTransientNetworkFailure_ConnectionTroubles_AreRetried(string output)
		=> Assert.True(LocalRepoService.IsTransientNetworkFailure(output));

	[Theory]
	[InlineData("remote: This repository was archived so it is read-only.\nfatal: unable to access 'https://github.com/x/y.git/': The requested URL returned error: 403")]
	[InlineData("! [rejected]        main -> main (non-fast-forward)")]
	[InlineData("fatal: Authentication failed for 'https://github.com/x/y.git/'")]
	[InlineData("CONFLICT (content): Merge conflict in AGENTS.md")]
	[InlineData("")]
	public void IsTransientNetworkFailure_RefusalsAndConflicts_AreNotRetried(string output)
		=> Assert.False(LocalRepoService.IsTransientNetworkFailure(output));
}
