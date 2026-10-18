using PanoramicData.NugetManagement.Web.Services.AiChange;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Tests for <see cref="DeclarationOnlySource"/>: a file is spared the changed-line coverage gate only when it
/// provably holds no logic, and anything doubtful stays subject to it.
/// </summary>
public class DeclarationOnlySourceTests
{
	[Fact]
	public void ARefitInterface_IsDeclarationsOnly()
		=> DeclarationOnlySource.Is("""
			using Aruba.Api.Data;

			namespace Aruba.Api.Interfaces;

			/// <summary>Live commands. Do not return anything if (x) { here }.</summary>
			public interface IThing
			{
				[Post("/network/v1/cx/{serialNumber}/reboot")]
				Task<AsyncOperationResponse> RebootAsync(string serialNumber, CancellationToken cancellationToken = default);

				[Get("/a/{id}")]
				Task<Page<string>> ListAsync(string id, CancellationToken cancellationToken = default);
			}
			""").Should().BeTrue();

	[Fact]
	public void ADataClassWithAutoPropertiesAndInitialisers_IsDeclarationsOnly()
		=> DeclarationOnlySource.Is("""
			namespace Sample;

			public sealed class AccessPoint
			{
				[JsonPropertyName("serial")]
				public string Serial { get; set; } = string.Empty;

				public List<string> Names { get; init; } = [];

				public int? Count { get; set; }
			}
			""").Should().BeTrue();

	[Theory]
	[InlineData("public enum Kind { A, B, C }")]
	[InlineData("public record Point(int X, int Y);")]
	[InlineData("public static class Names { public const string A = \"a\"; public const int B = 2; }")]
	public void EnumsRecordsAndConstants_AreDeclarationsOnly(string source)
		=> DeclarationOnlySource.Is(source).Should().BeTrue();

	[Theory]
	[InlineData("public class A { public int F(int x) { return x; } }")]
	[InlineData("public class A { public A(int x) : base() { } }")]
	[InlineData("public class A { public int P => 1; }")]
	[InlineData("public class A { private int _x; public int P { get { return _x; } } }")]
	[InlineData("public class A { private int _x; public int P { get; set { _x = value; } } }")]
	[InlineData("public class A { public void F() { if (true) { } } }")]
	[InlineData("public interface I { void F() { Console.WriteLine(); } }")]
	public void AnythingThatCouldBeCode_IsNot(string source)
		=> DeclarationOnlySource.Is(source).Should().BeFalse();

	[Fact]
	public void CodeLookingTextInCommentsAndStrings_DoesNotCount()
		=> DeclarationOnlySource.Is("""
			public static class Texts
			{
				// return x; if (a) { b }
				public const string Help = "call Foo(a) { return 1; } => ok";
			}
			""").Should().BeTrue();
}
