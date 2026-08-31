namespace PanoramicData.NugetManagement.Models;

/// <summary>
/// One named dependency, in one ecosystem.
/// </summary>
/// <param name="Ecosystem">Which kind of dependency this is.</param>
/// <param name="Name">Its name: a NuGet package id, or an <c>owner/name</c> action.</param>
/// <remarks>
/// The single currency between a Dependabot pull request, the rule that governs the dependency, and
/// the issue raised when no rule does. Equality is case-insensitive on the name because neither
/// NuGet package ids nor action names are case-sensitive in practice, and a mismatch of case would
/// quietly read as "no rule governs this" — the failure mode this type exists to prevent.
/// </remarks>
public sealed record DependencyRef(DependencyEcosystem Ecosystem, string Name)
{
	/// <inheritdoc />
	public bool Equals(DependencyRef? other)
		=> other is not null
			&& Ecosystem == other.Ecosystem
			&& string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

	/// <inheritdoc />
	public override int GetHashCode()
		=> HashCode.Combine(Ecosystem, Name.ToLowerInvariant());
}
