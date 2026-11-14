using System.Text;

namespace PanoramicData.NugetManagement.Web.Remediations;

/// <summary>
/// Reads and writes a text file inside a cloned repository without changing its encoding, and
/// refuses any path that leaves the repository.
/// </summary>
internal static class RepositoryFileText
{
	/// <summary>
	/// The text and the encoding it was read with, or null when the file is missing or the path is not
	/// inside <paramref name="root"/>.
	/// </summary>
	public static (string Text, Encoding Encoding)? Read(string root, string relative)
	{
		if (ContainedPath(root, relative) is not { } path || !File.Exists(path))
		{
			return null;
		}

		var bytes = File.ReadAllBytes(path);
		var bom = bytes is [0xEF, 0xBB, 0xBF, ..];
		try
		{
			var strict = new UTF8Encoding(bom, throwOnInvalidBytes: true);
			return (strict.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), strict);
		}
		catch (DecoderFallbackException)
		{
			// Latin-1 maps every byte to one char, so untouched bytes round-trip unchanged.
			return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
		}
	}

	/// <summary>Writes the text back with the encoding <see cref="Read"/> returned.</summary>
	public static void Write(string root, string relative, string text, Encoding encoding)
		=> File.WriteAllText(ContainedPath(root, relative) ?? throw new InvalidOperationException($"{relative} is outside the repository"), text, encoding);

	/// <summary>
	/// The path under <paramref name="root"/>, or null when it is rooted or escapes it.
	/// </summary>
	private static string? ContainedPath(string root, string relative)
	{
		if (Path.IsPathRooted(relative))
		{
			return null;
		}

		var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
		return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
	}
}
