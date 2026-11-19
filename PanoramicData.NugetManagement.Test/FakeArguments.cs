namespace PanoramicData.NugetManagement.Test;

/// <summary>Lets a fake that does not need its arguments still accept and check them.</summary>
internal static class FakeArguments
{
	/// <summary>Fails fast when a caller hands the fake a missing argument array.</summary>
	public static void Accept(params object?[] arguments) => ArgumentNullException.ThrowIfNull(arguments);
}
