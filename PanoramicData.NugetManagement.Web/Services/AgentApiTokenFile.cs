namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Where the agent API's token can be kept so that it survives a restart by whoever starts the application.
/// </summary>
/// <remarks>
/// The environment variable is per launch, which suits a person starting the app by hand and fails the moment
/// any other session restarts it: the new process has no token, the API silently does not exist, and the agent
/// is locked out until someone relaunches with the right variable. A file next to the app's other state does
/// not have that problem.
/// <para>
/// Read only if it is already there, never created. The API is off by default on purpose — whoever holds the
/// token can queue work against the local clones — so switching it on is something a person (or an agent they
/// asked) does once, deliberately, by writing the file. It sits in the user's own profile, which is the same
/// boundary the clones themselves are inside. An explicit setting or variable wins over the file.
/// </para>
/// </remarks>
public static class AgentApiTokenFile
{
	/// <summary>The file's name inside the application's folder.</summary>
	public const string FileName = "agent-token.txt";

	/// <summary>The default location: the application's folder under the user's local application data.</summary>
	public static string DefaultPath { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"PanoramicData.NugetManagement",
		FileName);

	/// <summary>
	/// The token in the file, or null when there is no file or nothing usable in it.
	/// </summary>
	/// <param name="path">The file to read; the default location when omitted.</param>
	public static string? Read(string? path = null)
	{
		try
		{
			var file = path ?? DefaultPath;

			return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } token
				? token
				: null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// An unreadable file is the same as none: the API stays off rather than the application failing to start.
			return null;
		}
	}

	/// <summary>
	/// The token the API should use: the one configured explicitly, else the one in the file.
	/// </summary>
	/// <param name="configured">The setting or environment value.</param>
	/// <param name="path">The file to fall back to; the default location when omitted.</param>
	public static string? Resolve(string? configured, string? path = null)
		=> string.IsNullOrWhiteSpace(configured) ? Read(path) : configured;
}
