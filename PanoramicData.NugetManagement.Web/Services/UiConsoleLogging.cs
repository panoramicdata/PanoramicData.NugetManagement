namespace PanoramicData.NugetManagement.Web.Services;

/// <summary>
/// Names the console a log line belongs to, for the duration of one queued run.
/// </summary>
/// <remarks>
/// The console is per navigation-tree node, but work outlives the selection that started it: a
/// refresh takes minutes and the user clicks elsewhere while it runs. The node key is stamped on the
/// asynchronous flow of the run rather than read from the current selection, so every line the run
/// produces — including the ones logged several layers down inside the services — lands in the
/// console it was started from.
/// <para>
/// <see cref="AsyncLocal{T}"/> flows into the parallel assessment tasks and back out of every await,
/// and because it is set inside an async method it cannot leak into the caller's flow.
/// </para>
/// </remarks>
public static class UiConsoleScope
{
	private static readonly AsyncLocal<string?> _nodeKey = new();

	/// <summary>
	/// The console node the current asynchronous flow belongs to, or null when it is not running
	/// queued work — in which case the writer falls back to whatever node is selected.
	/// </summary>
	public static string? NodeKey
	{
		get => _nodeKey.Value;
		set => _nodeKey.Value = value;
	}
}

/// <summary>One log line on its way to the in-app console.</summary>
/// <param name="NodeKey">The console it belongs to, or null to use the reader's current selection.</param>
/// <param name="Level">The level it was logged at.</param>
/// <param name="Message">The formatted message, including the exception where there was one.</param>
public sealed record UiConsoleLogEntry(string? NodeKey, LogLevel Level, string Message);

/// <summary>
/// Carries log output from <see cref="ILogger"/> to whichever circuits are showing a console.
/// </summary>
/// <remarks>
/// Registered as a singleton and raised from whatever thread logged, so subscribers must not assume
/// the renderer's context. This is the whole of the "one logger, two destinations" arrangement: the
/// standard console provider still writes to stdout, and this one fans the same lines out to the UI.
/// </remarks>
public sealed class UiConsoleLogSink
{
	/// <summary>Raised for every log line that passes the provider's filter.</summary>
	public event Action<UiConsoleLogEntry>? LineWritten;

	/// <summary>True when at least one console is listening, so the provider can skip formatting.</summary>
	public bool HasSubscribers => LineWritten is not null;

	internal void Write(UiConsoleLogEntry entry) => LineWritten?.Invoke(entry);
}

/// <summary>
/// The logger provider that feeds <see cref="UiConsoleLogSink"/>.
/// </summary>
/// <remarks>
/// Scoped to this application's own categories. Framework categories are excluded deliberately: the
/// console is a record of what the user asked the app to do, and Kestrel's request logging would
/// bury it.
/// </remarks>
[ProviderAlias("UiConsole")]
public sealed class UiConsoleLoggerProvider(UiConsoleLogSink sink) : ILoggerProvider
{
	/// <summary>The category prefix whose log lines are mirrored into the in-app console.</summary>
	public const string CategoryPrefix = "PanoramicData.NugetManagement";

	/// <summary>Creates the logger for one category.</summary>
	/// <param name="categoryName">The category being logged to.</param>
	/// <returns>A logger that forwards this category's lines to the sink, or drops them all when the
	/// category is not this application's own.</returns>
	public ILogger CreateLogger(string categoryName) => new UiConsoleLogger(categoryName, sink);

	/// <summary>Does nothing: the sink this provider writes to is owned by the container.</summary>
	public void Dispose()
	{
	}

	private sealed class UiConsoleLogger(string categoryName, UiConsoleLogSink sink) : ILogger
	{
		private readonly bool _included = categoryName.StartsWith(CategoryPrefix, StringComparison.Ordinal);

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel)
			=> _included && logLevel >= LogLevel.Information && sink.HasSubscribers;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel))
			{
				return;
			}

			var message = formatter(state, exception);
			if (exception is not null)
			{
				message = string.IsNullOrEmpty(message)
					? exception.Message
					: $"{message}: {exception.Message}";
			}

			if (string.IsNullOrEmpty(message))
			{
				return;
			}

			sink.Write(new UiConsoleLogEntry(UiConsoleScope.NodeKey, logLevel, message));
		}
	}
}
