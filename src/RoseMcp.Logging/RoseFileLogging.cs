using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace RoseMcp.Logging;

/// <summary>
/// Adds a file sink to a host's existing Microsoft.Extensions.Logging setup.
/// <para>
/// Serilog is the sink and nothing more. Every call site keeps its <c>ILogger&lt;T&gt;</c>, MEL
/// keeps its filters, and the console provider each host already installs stays exactly as it is
/// -- this only gives that same stream somewhere durable to land, because MEL ships no file
/// provider of its own and the tray, which logs to nowhere at all outside a debugger, is the
/// process whose failures are hardest to reconstruct after the fact.
/// </para>
/// </summary>
public static class RoseFileLogging
{
	/// <summary>
	/// The file this process is writing to, for a UI that offers to open it. Set once during
	/// startup and never again, which is what makes a static safe here: a process configures its
	/// logging exactly once, before anything is running that could read this.
	/// </summary>
	public static string? Destination { get; private set; }

	/// <summary>
	/// Writes this process's logs to
	/// %LOCALAPPDATA%/BinaryVibrance/RoseMCP/Logs/{component}/[{solution}-]{timestamp}.log.
	/// </summary>
	/// <param name="logging">The host's logging builder.</param>
	/// <param name="component">Server, Worker, or Tray -- the process, not the assembly.</param>
	/// <param name="solutionPath">
	/// The solution this process owns, naming its file so one worker's log can be told from
	/// another's. Null for the hosts that serve many solutions at once.
	/// </param>
	/// <param name="localAppData">Overrides the profile root; for tests.</param>
	public static ILoggingBuilder AddRoseFileLogging(
		this ILoggingBuilder logging,
		string component,
		string? solutionPath = null,
		string? localAppData = null)
	{
		// A logging failure must never be why a process does not start, and this runs before there
		// is any logger to report it to. Serilog's own complaints go to stderr, never stdout: in
		// stdio mode stdout carries protocol frames and one stray byte corrupts the stream.
		SelfLog.Enable(Console.Error);

		try
		{
			var directory = RoseLogFile.DirectoryFor(component, localAppData);
			RoseLogFile.PruneSessions(directory);

			var path = RoseLogFile.Claim(directory, solutionPath, DateTimeOffset.UtcNow);

			var logger = new LoggerConfiguration()
				// Verbose here, so MEL's configured filters are the only thing deciding. Two
				// independent minimums is how a level someone raised fails to change anything.
				.MinimumLevel.Is(LogEventLevel.Verbose)
				.Enrich.With<UtcTimestamp>()
				.WriteTo.File(
					path,
					outputTemplate:
						"{Utc:yyyy-MM-dd HH:mm:ss.fff}Z [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
					fileSizeLimitBytes: RoseLogFile.DefaultFileSizeLimitBytes,
					rollOnFileSizeLimit: true,
					retainedFileCountLimit: RoseLogFile.DefaultPartsPerSession,
					// Unbuffered: the tray is force-killed on every deploy, and a buffered sink
					// loses precisely the last few lines that say why something went wrong.
					buffered: false)
				.CreateLogger();

			Destination = path;
			logging.AddSerilog(logger, dispose: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"rosemcp: file logging is off, could not open a log file: {exception.Message}");
		}

		return logging;
	}
}

/// <summary>
/// Puts the event's time on it in UTC.
/// <para>
/// Serilog renders {Timestamp} in local time, and the file name is UTC, so without this a log
/// opened in Adelaide is named nine and a half hours away from every line inside it. One zone
/// throughout, marked with a Z, and no reader has to work out which one they are looking at.
/// </para>
/// </summary>
internal sealed class UtcTimestamp : ILogEventEnricher
{
	public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory) =>
		logEvent.AddPropertyIfAbsent(new LogEventProperty("Utc", new ScalarValue(logEvent.Timestamp.UtcDateTime)));
}
