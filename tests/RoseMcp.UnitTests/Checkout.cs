namespace RoseMcp.UnitTests;

/// <summary>
/// The repository a test was built from, for the tests that hold a committed file to a rule: the
/// published layout, the stdout rule, the tap's tiers. Read from the checkout rather than copied into
/// the build output, so a test reads what a person edits.
/// </summary>
internal static class Checkout
{
	/// <summary>A file in the checkout, found by walking up to the solution from the test's own output.</summary>
	public static string RepositoryFile(params string[] segments)
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx")))
			{
				return Path.Combine([directory.FullName, .. segments]);
			}
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
