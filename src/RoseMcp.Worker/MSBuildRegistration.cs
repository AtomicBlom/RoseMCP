using Microsoft.Build.Locator;

namespace RoseMcp.Worker;

/// <summary>
/// Puts the SDK's own MSBuild behind this process's MSBuild references, so a project can be
/// evaluated here and not only built in Roslyn's build host.
/// <para>
/// The worker evaluates projects itself to learn what each one imports, which the build host does
/// not report. MSBuild is referenced for compilation only and never shipped beside the worker:
/// evaluation has to use the SDK the design-time build uses, and a copy of
/// <c>Microsoft.Build.Framework</c> loaded out of the worker's own directory beside the SDK's
/// <c>Microsoft.Build</c> is two versions of one type system in one process.
/// </para>
/// <para>
/// It has to run before anything loads an MSBuild type, and Roslyn's MSBuild workspace loads
/// <c>Microsoft.Build.Framework</c> as soon as it is used. So <see cref="SolutionLoader.LoadAsync"/>
/// calls this before any of the loading code is compiled, which covers the worker and every test that
/// loads a solution, and a second call does nothing.
/// </para>
/// </summary>
public static class MSBuildRegistration
{
	private static readonly Lock Gate = new();

	/// <summary>
	/// Registers the MSBuild of the SDK that <c>dotnet</c> would choose in the solution's directory,
	/// so a <c>global.json</c> pin is honoured. Only the first call in a process registers anything,
	/// which matches a worker owning exactly one solution.
	/// <para>
	/// The SDK's MSBuild loads into this process, so only an SDK whose major version is no newer than
	/// the runtime the worker runs on is offered. A machine whose only SDKs are newer finds none at all,
	/// which is why the error names the runtime rather than only a pin.
	/// </para>
	/// </summary>
	/// <exception cref="InvalidOperationException">No usable .NET SDK could be found for that directory.</exception>
	public static void Ensure(string solutionPath)
	{
		lock (Gate)
		{
			if (MSBuildLocator.IsRegistered) return;

			var directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Environment.CurrentDirectory;

			var instance = MSBuildLocator
				.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
				{
					DiscoveryTypes = DiscoveryType.DotNetSdk,
					WorkingDirectory = directory,
				})
				.FirstOrDefault()
				?? throw new InvalidOperationException(
					$"No .NET SDK this worker can use was found for {directory}, and one is needed to evaluate its projects. "
						+ $"The worker runs on .NET {Environment.Version.Major} and can only load an SDK of that major version or older; "
						+ "an SDK newer than that needs a RoseMCP built for it. If a global.json in or above the directory pins "
						+ "an SDK, install that one or remove the pin. 'dotnet --list-sdks' shows what is installed.");

			MSBuildLocator.RegisterInstance(instance);
		}
	}
}
