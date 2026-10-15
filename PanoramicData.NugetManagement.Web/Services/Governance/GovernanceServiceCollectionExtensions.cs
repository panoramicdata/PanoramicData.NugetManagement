using System.Runtime.CompilerServices;

namespace PanoramicData.NugetManagement.Web.Services.Governance;

/// <summary>
/// Registers the governance record: the ledger, the policy store, the packet store and the recorder.
/// </summary>
public static class GovernanceServiceCollectionExtensions
{
	/// <summary>
	/// Adds the governance services.
	/// </summary>
	/// <param name="services">The service collection.</param>
	/// <param name="ledgerFolder">Where the ledger is kept.</param>
	/// <param name="policyPath">The policy file.</param>
	/// <param name="packetFolder">Where review packets are kept.</param>
	/// <remarks>
	/// The ledger stamps every entry with the hash of the policy in force, and the policy store writes its
	/// changes to the ledger, so each needs the other. The holder breaks the cycle: the ledger asks it lazily,
	/// and the store fills it in as soon as it exists. Without it the container would be asked to build the
	/// store in order to build the ledger it is itself being built from.
	/// </remarks>
	public static IServiceCollection AddGovernance(
		this IServiceCollection services,
		string ledgerFolder,
		string policyPath,
		string packetFolder)
	{
		var policyInForce = new StrongBox<AutopilotPolicyStore?>();

		services.AddSingleton(_ => new GovernanceLedger(ledgerFolder, () => policyInForce.Value?.PolicyHash));

		services.AddSingleton(provider =>
		{
			var store = new AutopilotPolicyStore(policyPath, provider.GetRequiredService<GovernanceLedger>());
			policyInForce.Value = store;

			return store;
		});

		services.AddSingleton(_ => new ReviewPacketStore(packetFolder));
		services.AddSingleton<GovernanceRecorder>();

		return services;
	}
}
