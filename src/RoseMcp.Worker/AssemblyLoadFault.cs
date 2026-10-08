using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace RoseMcp.Worker;

/// <summary>
/// An assembly this worker's own code needed and the runtime would not load, read off the exception a
/// tool call ended with.
/// <para>
/// Worth its own shape because it is the one tool failure that is a fact about the process rather than
/// about the call. A framework or Roslyn assembly that cannot be loaded once cannot be loaded on any later
/// call either, so every tool reaching the same code fails the same way for as long as the worker lives,
/// while the tools that do not -- diagnostics, status -- answer normally and the workspace goes on calling
/// itself healthy. The raw loader message names a file and says nothing of that.
/// </para>
/// <para>
/// Read at the MCP boundary rather than caught where it is thrown, for the reason every error is: inside
/// the worker the exception type is what callers branch on, and only the wire needs the explanation.
/// </para>
/// </summary>
public sealed record AssemblyLoadFault
{
	/// <summary>The simple name of the assembly that would not load, as the runtime gave it.</summary>
	public required string Assembly { get; init; }

	/// <summary>The tool whose call failed on it.</summary>
	public required string Tool { get; init; }

	/// <summary>What the runtime said.</summary>
	public required string Message { get; init; }

	/// <summary>
	/// Whether the directory of the .NET runtime this process started from is gone from disk, which is
	/// .NET being updated or removed underneath a running worker and explains a framework assembly that
	/// will not load.
	/// </summary>
	public required bool RuntimeDirectoryMissing { get; init; }

	/// <summary>
	/// What the caller is told instead of the loader's message alone: which assembly, that it is the
	/// worker and not the solution, and how to get a worker that can load it.
	/// </summary>
	public string Refusal =>
		$"{Tool} could not run, because this worker could not load {Assembly}: {Message} "
			+ (RuntimeDirectoryMissing ? RuntimeGone : string.Empty)
			+ "That is the worker's own code failing to load, not anything in the solution, and a running "
			+ "process does not recover from it: every call reaching the same code fails the same way while "
			+ "the rest answer. rose_workspace_reload starts a fresh worker.";

	/// <summary>Said when <see cref="RuntimeDirectoryMissing"/> holds, by both the refusal and the status.</summary>
	public const string RuntimeGone =
		"The .NET runtime directory this worker started from is no longer on disk, so .NET was updated or "
			+ "removed underneath it. ";

	/// <summary>
	/// The fault <paramref name="exception"/> describes, or null when it is not an assembly failing to load
	/// for this worker's own code.
	/// <para>
	/// A <see cref="FileNotFoundException"/> counts only when it names an assembly by its display name, since
	/// a tool reading a file that is not there throws the same type naming a path. One thrown from code an
	/// analyzer load context holds is left alone: that is a generator's dependency missing, which a rebuild of
	/// the generator fixes and <c>analyzerLoadFailures</c> already reports, not this process going wrong.
	/// </para>
	/// </summary>
	/// <param name="exception">What the tool call ended with. Its inner exceptions are searched too.</param>
	/// <param name="tool">The tool that was called.</param>
	/// <param name="runtimeDirectory">The runtime directory to check, which is this process's own when null.</param>
	public static AssemblyLoadFault? From(Exception exception, string tool, string? runtimeDirectory = null)
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (AssemblyNamed(current) is not { } assembly) continue;
			if (ThrownInAnalyzerCode(current)) return null;

			var directory = runtimeDirectory ?? RuntimeEnvironment.GetRuntimeDirectory();

			return new AssemblyLoadFault
			{
				Assembly = assembly,
				Tool = tool,
				Message = current.Message,
				RuntimeDirectoryMissing = !Directory.Exists(directory),
			};
		}

		return null;
	}

	private static string? AssemblyNamed(Exception exception) => exception switch
	{
		FileLoadException load => SimpleName(load.FileName) ?? "an assembly the runtime did not name",
		FileNotFoundException missing when IsDisplayName(missing.FileName) => SimpleName(missing.FileName),
		_ => null,
	};

	/// <summary>
	/// An assembly's display name carries its version and never a directory; a path to a missing file carries
	/// the directory and never a version.
	/// </summary>
	private static bool IsDisplayName(string? fileName) =>
		fileName is not null
		&& fileName.Contains("Version=", StringComparison.OrdinalIgnoreCase)
		&& fileName.IndexOfAny(['\\', '/']) < 0;

	private static string? SimpleName(string? fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName)) return null;

		try
		{
			return new AssemblyName(fileName).Name ?? fileName;
		}
		catch (Exception exception) when (exception is ArgumentException or FileLoadException)
		{
			return Path.GetFileNameWithoutExtension(fileName);
		}
	}

	/// <summary>
	/// Whether the method the exception came out of belongs to an assembly some load context other than the
	/// default one holds, which in this worker means an analyzer or generator.
	/// </summary>
	private static bool ThrownInAnalyzerCode(Exception exception)
	{
		var origin = exception.TargetSite?.DeclaringType?.Assembly;
		if (origin is null) return false;

		var context = AssemblyLoadContext.GetLoadContext(origin);

		return context is not null && context != AssemblyLoadContext.Default;
	}
}
