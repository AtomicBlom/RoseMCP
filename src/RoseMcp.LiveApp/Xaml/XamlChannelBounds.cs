namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Every bound on a wait for the XAML provider, in one place, and the sentence each one produces
/// when it expires.
/// <para>
/// One place because the failure they exist to prevent is not a slow answer, it is no answer: a
/// full-suite run hung for fifty minutes on a first tree read, with the pipe logged as listening and
/// nothing after it. The target's UI thread is what serves the tree walk, so an app wedged below
/// managed code never finishes one -- and the injection call is a blocking cross-process call into
/// that thread, so it is the wait with no bound of its own.
/// </para>
/// <para>
/// Every sentence names the channel and the number of seconds, because the four channels here fail
/// identically from outside -- an empty tree and a detail -- and "did not answer in time" leaves the
/// reader unable to tell an app with no XAML from a wedged one from a provider that never loaded.
/// </para>
/// </summary>
internal sealed record XamlChannelBounds
{
	/// <summary>
	/// The variable that shortens every bound below, for a test that needs one to expire without
	/// waiting the real time for it, and for a session willing to give up sooner than the defaults.
	/// </summary>
	public const string CeilingVariable = "ROSEMCP_XAML_TIMEOUT_SECONDS";

	/// <summary>
	/// How long the diagnostics endpoint may take to appear. The endpoint does not exist until the
	/// framework has built a tree, so a session that has only just attached -- which is exactly when
	/// an agent asks -- gets ERROR_NOT_FOUND for a second or two.
	/// </summary>
	public TimeSpan Endpoint { get; init; } = TimeSpan.FromSeconds(20);

	/// <summary>
	/// How long one <c>InitializeXamlDiagnosticsEx</c> call may take. It loads the provider into the
	/// target and does not return until the app's side has sited it, which on WinUI 3 means the app's
	/// UI thread has run the tap's body -- so a target whose UI thread is not running never returns
	/// at all.
	/// </summary>
	public TimeSpan Injection { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>How long the provider may take to write a snapshot into the work folder.</summary>
	public TimeSpan Snapshot { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>How long the provider may take to connect back on the pipe and greet.</summary>
	public TimeSpan Greeting { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>
	/// How long <c>icacls</c> may take to put the AppContainer grants on the work folder. Bounded for
	/// the same reason as the rest: it is a child process, and a child process that never exits holds
	/// the call that started it for as long as the host lives.
	/// </summary>
	public TimeSpan Grant { get; init; } = TimeSpan.FromSeconds(15);

	/// <summary>
	/// The defaults, with every bound capped by <see cref="CeilingVariable"/> where that names a
	/// number of seconds. A ceiling rather than a set of variables, and a cap rather than an
	/// assignment: one number is all a caller in a hurry wants, and capping means the variable can
	/// only ever make a wait shorter, so a typo cannot lengthen the wait it was meant to shorten.
	/// <para>
	/// Zero is accepted and means every bound is already spent, which is the only value a test can
	/// use and be sure of the answer. A small non-zero one cannot: Windows' scheduler granularity is
	/// about fifteen milliseconds, so a bound of one millisecond is really a wait of fifteen, and an
	/// injection into a warm app finishes inside that often enough to make the test pass at random.
	/// </para>
	/// </summary>
	public static XamlChannelBounds FromEnvironment()
	{
		var configured = Environment.GetEnvironmentVariable(CeilingVariable);
		if (!double.TryParse(configured, out var seconds) || seconds < 0) return new XamlChannelBounds();

		var ceiling = TimeSpan.FromSeconds(seconds);
		var defaults = new XamlChannelBounds();

		return new XamlChannelBounds
		{
			Endpoint = Shorter(defaults.Endpoint, ceiling),
			Injection = Shorter(defaults.Injection, ceiling),
			Snapshot = Shorter(defaults.Snapshot, ceiling),
			Greeting = Shorter(defaults.Greeting, ceiling),
			Grant = Shorter(defaults.Grant, ceiling),
		};
	}

	/// <summary>
	/// What to tell a caller when a wait on <paramref name="channel"/> expired. The channel is named
	/// in the words the logs use for it, so a detail in a tool result and the line in the host's log
	/// describe the same thing.
	/// </summary>
	public static string TimedOut(string channel, TimeSpan bound) =>
		$"Waiting on {channel} timed out after {bound.TotalSeconds:0.##}s.";

	private static TimeSpan Shorter(TimeSpan bound, TimeSpan ceiling) => bound < ceiling ? bound : ceiling;
}
