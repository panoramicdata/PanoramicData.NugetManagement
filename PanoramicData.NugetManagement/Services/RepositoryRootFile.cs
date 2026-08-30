namespace PanoramicData.NugetManagement.Services;

/// <summary>
/// Resolves a file that lives at the scanner repository root, beside the solution.
/// </summary>
/// <remarks>
/// Both committed stores need the same answer, and both need it before the file exists so it can be
/// created on first write. Walking up from the running assembly is how
/// <see cref="ActionVersionCatalog"/> already finds <c>action-versions.json</c>.
/// </remarks>
internal static class RepositoryRootFile
{
	/// <summary>
	/// The path a root-level file should have, whether or not it exists yet, or null when the
	/// repository root cannot be found.
	/// </summary>
	/// <param name="fileName">The file name to resolve.</param>
	public static string? Resolve(string fileName)
	{
		var directory = AppContext.BaseDirectory;
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory, "PanoramicData.NugetManagement.slnx")))
			{
				return Path.Combine(directory, fileName);
			}

			directory = Directory.GetParent(directory)?.FullName;
		}

		return null;
	}
}
