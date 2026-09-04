using System.Diagnostics;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Which .NET a running process is actually hosting, read from the modules it has loaded.
/// <para>
/// This exists because "the process never signalled runtime startup. Is it a .NET (Core) app?" is a
/// question, and the answer was already in the process. A packaged app with a stale registration --
/// a <c>Release\AppX</c> registered while a fresh <c>Debug\AppX</c> sits on disk, which
/// <c>Add-AppxPackage -Register</c> silently no-ops into -- activates the Release build, and a
/// Release UWP build is .NET Native. There is no CoreCLR in it, so there is no startup event and
/// nothing for an ICorDebug debugger to attach to. Diagnosing that by hand meant listing modules and
/// recognising <c>mrt100_app.dll</c>.
/// </para>
/// <para>
/// Reported rather than acted on. Nothing here tries to attach differently or guess what the
/// developer meant to deploy; it turns a question into a fact and points at the deploy rather than
/// at RoseMCP.
/// </para>
/// </summary>
internal static class RuntimeFlavour
{
	/// <summary>
	/// .NET Native's runtime, which is what a Release UWP build carries. The versioned name is the
	/// one shipped inside the package; the unversioned is the framework package's.
	/// </summary>
	private static readonly string[] NetNativeModules =
	[
		"mrt100_app.dll",
		"mrt100.dll",
		"microsoft.net.native.runtime",
	];

	/// <summary>
	/// What is hosting this process, as a sentence, or null when it cannot be told.
	/// <para>
	/// Never throws: it runs on a failure path, and a diagnosis that fails is worth less than the
	/// original error it was trying to improve on.
	/// </para>
	/// </summary>
	public static string? Describe(int processId)
	{
		var modules = LoadedModules(processId);
		if (modules.Count == 0) return null;

		if (modules.Any(IsNetNative))
		{
			return "The process is a .NET Native (AOT) build -- it has loaded " + NamesLike(modules, IsNetNative)
				+ " and there is no CoreCLR in it, so it raises no runtime-startup event and ICorDebug cannot "
				+ "debug it at all. This is what a Release UWP build looks like. A debuggable UWP build is "
				+ "Debug, which is CoreCLR-based; if you did deploy Debug, the registration is pointing "
				+ "somewhere else -- Add-AppxPackage -Register silently does nothing when a package of the "
				+ "same identity and version is already registered from another layout.";
		}

		if (modules.Any(module => Named(module, "coreclr.dll")))
		{
			return "The process has loaded coreclr.dll, so it is a CoreCLR app and should have signalled "
				+ "startup. Something else stopped the notification arriving -- the runtime may have loaded "
				+ "before the notification was armed.";
		}

		if (modules.Any(module => Named(module, "clr.dll")))
		{
			return "The process has loaded clr.dll, so it is hosting the desktop .NET Framework rather than "
				+ ".NET (Core). It raises no CoreCLR startup event, and this debugger attaches to CoreCLR.";
		}

		return "The process has loaded no .NET runtime this can recognise -- no coreclr.dll, no clr.dll and "
			+ "no .NET Native runtime -- so it is most likely unmanaged.";
	}

	/// <summary>
	/// The loaded module names, or empty when they cannot be read.
	/// <para>
	/// An AppContainer process owned by the same user is readable, but a process that has exited
	/// between the timeout and this call is not, and neither is one of another architecture. Both are
	/// ordinary here, so failure is silence rather than an exception.
	/// </para>
	/// </summary>
	private static IReadOnlyList<string> LoadedModules(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);

			return [.. process.Modules.Cast<ProcessModule>().Select(module => module.ModuleName)];
		}
		catch (Exception)
		{
			return [];
		}
	}

	private static bool IsNetNative(string moduleName) =>
		NetNativeModules.Any(candidate => moduleName.Contains(candidate, StringComparison.OrdinalIgnoreCase));

	private static bool Named(string moduleName, string candidate) =>
		moduleName.Equals(candidate, StringComparison.OrdinalIgnoreCase);

	/// <summary>The modules that matched, named, because the evidence is more use than the verdict alone.</summary>
	private static string NamesLike(IReadOnlyList<string> modules, Func<string, bool> matches) =>
		string.Join(", ", modules.Where(matches).Distinct(StringComparer.OrdinalIgnoreCase));
}
