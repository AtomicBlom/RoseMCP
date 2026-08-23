using System.Diagnostics;

namespace RoslynMcp.Worker.Tests;

/// <summary>
/// A throwaway copy of a fixture solution in a temp directory.
/// <para>
/// Tests copy rather than use the fixtures in place because most of what is worth testing here is
/// destructive: deleting build output, editing files behind the workspace's back, switching git
/// branches. The copy also starts with no bin or obj, which is the fresh-clone state where source
/// generators quietly produce nothing.
/// </para>
/// </summary>
public sealed class FixtureSolution : IDisposable
{
	private FixtureSolution(string root, string solutionPath)
	{
		Root = root;
		SolutionPath = solutionPath;
	}

	public string Root { get; }

	public string SolutionPath { get; }

	public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

	/// <summary>
	/// Copies <paramref name="fixtureName"/> into a temp directory, sources only. The fixture
	/// directory's Directory.Build.props stoppers come too, so the copy keeps building like a
	/// stranger's solution rather than inheriting this repo's settings.
	/// </summary>
	public static FixtureSolution Copy(string fixtureName, string solutionFileName)
	{
		var source = FixtureRoot();
		var root = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(), "roslynmcp-tests", $"{fixtureName}-{Guid.NewGuid():N}");

		Directory.CreateDirectory(root);

		foreach (var stopper in Directory.GetFiles(source, "Directory.*"))
		{
			File.Copy(stopper, System.IO.Path.Combine(root, System.IO.Path.GetFileName(stopper)));
		}

		CopyTree(System.IO.Path.Combine(source, fixtureName), System.IO.Path.Combine(root, fixtureName));

		return new FixtureSolution(root, System.IO.Path.Combine(root, fixtureName, solutionFileName));
	}

	/// <summary>Builds one project inside the copy, for tests that need real build output on disk.</summary>
	public void Build(params string[] projectParts)
	{
		var project = Path(projectParts);
		var startInfo = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = System.IO.Path.GetDirectoryName(project),
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		startInfo.ArgumentList.Add("build");
		startInfo.ArgumentList.Add(project);

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet build did not start.");
		var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0) throw new InvalidOperationException($"Building {project} failed:{Environment.NewLine}{output}");
	}

	/// <summary>Walks up from the test binary to the repository root, identified by its solution file.</summary>
	private static string FixtureRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "RoslynHost.slnx")))
		{
			directory = directory.Parent;
		}

		if (directory is null) throw new InvalidOperationException("Could not locate the repository root from the test binary.");

		return System.IO.Path.Combine(directory.FullName, "tests", "fixtures");
	}

	private static void CopyTree(string source, string destination)
	{
		Directory.CreateDirectory(destination);

		foreach (var file in Directory.GetFiles(source))
		{
			File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
		}

		foreach (var directory in Directory.GetDirectories(source))
		{
			var name = System.IO.Path.GetFileName(directory);
			if (name is "bin" or "obj" or ".git") continue;

			CopyTree(directory, System.IO.Path.Combine(destination, name));
		}
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(Root, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// A loaded analyzer assembly can keep a file handle open on Windows. Leaving a temp
			// directory behind is not worth failing a test over.
		}
	}
}
