using Microsoft.Extensions.Hosting;

namespace RoseMcp.TestSupport;

/// <summary>
/// The host stops the process when a solution vanishes; a test has nothing to stop.
/// </summary>
public sealed class NeverStops : IHostApplicationLifetime
{
	public CancellationToken ApplicationStarted => CancellationToken.None;

	public CancellationToken ApplicationStopping => CancellationToken.None;

	public CancellationToken ApplicationStopped => CancellationToken.None;

	public void StopApplication()
	{
	}
}
