using System.Diagnostics;

using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Which XAML framework a running process is hosting, read from the modules it has loaded (#74).
/// <para>
/// The live half cannot ask a compilation the way stub generation does: it holds a process id for an
/// app it may never have built, and attaching to an arbitrary running app is an ordinary thing to do
/// here. What it can ask is the process, and the answer has been sitting there the whole time -- a
/// WinUI 3 target used to fail after a twenty-second wait with a diagnosis about packaging, when its
/// module list said what it was in microseconds.
/// </para>
/// <para>
/// Reading the process is this class; deciding what the names mean is
/// <see cref="XamlStackModules.Identify"/>, which is pure and covered by tests. Same rule as
/// <see cref="Debugging.RuntimeFlavour"/>, which reads modules for the neighbouring question: never
/// throw. This runs ahead of an operation that has its own errors to report, and a diagnosis that
/// fails is worth less than the thing it was improving on.
/// </para>
/// </summary>
internal static class XamlStackProbe
{
	/// <summary>What XAML framework is hosting this process, with the modules that said so.</summary>
	public static XamlStackDetection Detect(int processId)
	{
		var modules = LoadedModules(processId);
		if (modules.Count == 0)
		{
			return new XamlStackDetection
			{
				Stack = XamlStack.Unknown,
				Evidence = [],
				Reason = "the process's loaded modules could not be read",
			};
		}

		var (stack, evidence) = XamlStackModules.Identify(modules);
		if (stack == XamlStack.Unknown)
		{
			return new XamlStackDetection
			{
				Stack = stack,
				Evidence = evidence,
				Reason = $"none of the {modules.Count} loaded modules belongs to a XAML framework this recognises",
			};
		}

		return new XamlStackDetection
		{
			Stack = stack,
			Evidence = evidence,
			Reason = $"it has loaded {string.Join(", ", evidence)}",
		};
	}

	/// <summary>
	/// Where the target loaded a module from, or null when it has not loaded one by that name.
	/// <para>
	/// This is how the initialiser is found (#76). UWP's <c>InitializeXamlDiagnosticsEx</c> is
	/// exported from Windows.UI.Xaml.dll, a system DLL that loads by bare name; WinUI 3's is exported
	/// from <c>Microsoft.Internal.FrameworkUdk.dll</c>, which lives in a versioned, per-architecture
	/// WindowsAppRuntime framework package under Program Files\WindowsApps and is on no search path
	/// this process has. Asking the target sidesteps all of that and cannot pick the wrong version or
	/// the wrong architecture, because it returns the file the target itself is running.
	/// </para>
	/// <para>
	/// Not exported from Microsoft.UI.Xaml.dll, which is the obvious guess and was checked: it
	/// exports eight functions and that is not among them.
	/// </para>
	/// </summary>
	public static string? ModulePath(int processId, string moduleName)
	{
		try
		{
			using var process = Process.GetProcessById(processId);

			return process.Modules
				.Cast<ProcessModule>()
				.FirstOrDefault(module => module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
				?.FileName;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// The loaded module names, or empty when they cannot be read -- an exited process, or one of
	/// another architecture. Both are ordinary here, so failure is silence rather than an exception.
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
}
