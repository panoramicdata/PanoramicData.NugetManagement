using Microsoft.Extensions.Logging;

namespace PanoramicData.NugetManagement.Test;

/// <summary>
/// Base class for tests providing an ITestOutputHelper-backed logger.
/// </summary>
public abstract class TestWithOutput
{
	/// <summary>
	/// The test output helper for writing diagnostic output.
	/// </summary>
	protected ITestOutputHelper Output { get; }

	/// <summary>
	/// A logger factory that writes to test output.
	/// </summary>
	protected ILoggerFactory LoggerFactory { get; }

	/// <summary>
	/// Initializes a new instance of the <see cref="TestWithOutput"/> class.
	/// </summary>
	/// <param name="output">The xUnit test output helper.</param>
	protected TestWithOutput(ITestOutputHelper output)
	{
		Output = output;
		LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
		{
			builder.SetMinimumLevel(LogLevel.Debug);
			builder.AddProvider(new TestOutputLoggerProvider(output));
		});
	}

	/// <summary>
	/// Creates a typed logger.
	/// </summary>
	/// <typeparam name="T">The type to create a logger for.</typeparam>
	/// <returns>A logger instance.</returns>
	protected ILogger<T> CreateLogger<T>() => LoggerFactory.CreateLogger<T>();
}

/// <summary>
/// Logger provider that writes to xUnit test output.
/// </summary>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
	/// <inheritdoc />
	public ILogger CreateLogger(string categoryName)
		=> new TestOutputLogger(output, categoryName);

	/// <inheritdoc />
	public void Dispose()
	{
		// No resources to dispose
	}
}

/// <summary>
/// Logger that writes to xUnit test output.
/// </summary>
internal sealed class TestOutputLogger(ITestOutputHelper output, string categoryName) : ILogger
{
	/// <inheritdoc />
	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	/// <inheritdoc />
	public bool IsEnabled(LogLevel logLevel) => true;

	/// <inheritdoc />
	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		output.WriteLine($"[{logLevel}] {categoryName}: {formatter(state, exception)}");
		if (exception is not null)
		{
			output.WriteLine(exception.ToString());
		}
	}
}
