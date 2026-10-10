namespace RoseMcp.UnitTests;

/// <summary>
/// A local application data folder of a test's own, gone when the test is. What lives in the real one is
/// shared by every host on the machine and relied on by the person using it, so no test writes there.
/// </summary>
internal sealed class TemporaryLocalAppData : IDisposable
{
	/// <summary>The folder, which does not exist until something writes into it.</summary>
	public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rose-local-app-data", Guid.NewGuid().ToString("n"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
		}
		catch (IOException)
		{
			// A temp directory left behind is litter, not a failure.
		}
	}
}
