using System.Text.Json;

namespace RoseMcp.UnitTests;

/// <summary>
/// <c>tools/published-layout.json</c>, the one description of where each part of RoseMCP sits in an
/// install and in the Windows package. deploy.ps1, install.ps1 and build-installer.ps1 read it; the
/// tests that use this read the same file, so the C# resolvers and the Inno script are held to what
/// the scripts actually write rather than to a copy of it kept here.
/// <para>
/// Read from the checkout rather than copied into the build output, so a test reads what a person
/// edits. Every member the file has is required: a property missing from it is a layout nobody can
/// lay down, and is refused when it is read rather than surfacing as a null in the middle of a test.
/// </para>
/// </summary>
internal sealed record PublishedLayout(
	IReadOnlyList<PublishedLayout.Runtime> Runtimes,
	IReadOnlyList<PublishedLayout.Component> Components,
	PublishedLayout.LiveAppHostLayout LiveAppHost,
	PublishedLayout.PackageLayout Package)
{
	private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		RespectNullableAnnotations = true,
		RespectRequiredConstructorParameters = true,
	};

	/// <summary>Every architecture any runtime carries a debug host for, once each.</summary>
	public IReadOnlyList<string> AllLiveAppHosts => [.. Runtimes.SelectMany(runtime => runtime.LiveAppHosts).Distinct()];

	/// <summary>The folders a broker runs from: the server's and the tray's.</summary>
	public IReadOnlyList<string> BrokerFolders => [.. Components.Where(component => component.HostsBroker).Select(component => component.Folder)];

	public static PublishedLayout Load() =>
		JsonSerializer.Deserialize<PublishedLayout>(File.ReadAllText(RepositoryFile("tools", "published-layout.json")), Options)
			?? throw new InvalidOperationException("tools/published-layout.json is empty.");

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

	/// <summary>The component a project publishes, refused rather than defaulted when the layout has none.</summary>
	public Component ComponentFor(string project) =>
		Components.SingleOrDefault(component => component.Project == project)
			?? throw new InvalidOperationException($"tools/published-layout.json has no component {project}.");

	/// <summary>
	/// A layout path -- relative, forward slashes, with <c>{rid}</c> and <c>{host}</c> placeholders --
	/// as a path on this machine under <paramref name="root"/>.
	/// </summary>
	public static string Under(string root, params string[] layoutPaths) =>
		Path.Combine([root, .. layoutPaths.SelectMany(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries))]);

	/// <summary>A layout path with its placeholders filled in.</summary>
	public static string Expand(string layoutPath, string? rid = null, string? host = null)
	{
		var expanded = layoutPath;
		if (rid is not null) expanded = expanded.Replace("{rid}", rid, StringComparison.Ordinal);
		if (host is not null) expanded = expanded.Replace("{host}", host, StringComparison.Ordinal);

		return expanded;
	}

	/// <summary>
	/// The layout names executables bare. The resolvers append <c>.exe</c> on Windows and nothing
	/// elsewhere, so a staged file has to carry the name they look for on the machine running the test.
	/// </summary>
	public static string OnThisMachine(string executable) => OperatingSystem.IsWindows() ? executable + ".exe" : executable;

	/// <summary>One architecture an install is for, and what it needs.</summary>
	/// <param name="Rid">The runtime identifier, which names its payload folder in the package.</param>
	/// <param name="OsArchitecture">The machine architecture install.ps1 maps to this runtime.</param>
	/// <param name="InnoCheck">The Inno Setup check function that is true on such a machine.</param>
	/// <param name="LiveAppHosts">The debug hosts such a machine can execute, and so carries.</param>
	public sealed record Runtime(string Rid, string OsArchitecture, string InnoCheck, IReadOnlyList<string> LiveAppHosts);

	/// <summary>One published project, and where its executable sits relative to the install root.</summary>
	/// <param name="Project">The project under <c>src</c>.</param>
	/// <param name="Executable">The apphost's name, without an extension.</param>
	/// <param name="Folder">Its folder, or empty for the root.</param>
	/// <param name="WindowsOnly">Whether only a Windows install carries it.</param>
	/// <param name="HostsBroker">Whether a broker runs from its folder, and so resolves the others from there.</param>
	public sealed record Component(string Project, string Executable, string Folder, bool WindowsOnly, bool HostsBroker);

	/// <summary>The live-app debug host, one per architecture under a folder named for it.</summary>
	/// <param name="Project">The project under <c>src</c>.</param>
	/// <param name="Executable">The apphost's name, without an extension.</param>
	/// <param name="Folder">Its folder relative to the install root, with a <c>{host}</c> placeholder.</param>
	/// <param name="XamlProviders">The native taps beside each host.</param>
	public sealed record LiveAppHostLayout(string Project, string Executable, string Folder, XamlProviderLayout XamlProviders);

	/// <summary>The native XAML providers a debug host injects.</summary>
	/// <param name="Folder">Relative to the host's own folder, with a <c>{host}</c> placeholder.</param>
	/// <param name="Files">Every provider, each of which every host carries.</param>
	public sealed record XamlProviderLayout(string Folder, IReadOnlyList<XamlProviderFile> Files);

	/// <summary>One native provider.</summary>
	/// <param name="Project">The project under <c>src</c> that builds it.</param>
	/// <param name="File">The DLL's file name.</param>
	public sealed record XamlProviderFile(string Project, string File);

	/// <summary>Where the Windows package keeps each part of an install.</summary>
	/// <param name="Runtime">One architecture's own payload, with a <c>{rid}</c> placeholder.</param>
	/// <param name="Shared">The payload every architecture shares, laid down first.</param>
	/// <param name="LiveAppHost">One debug host, with a <c>{host}</c> placeholder.</param>
	/// <param name="Scripts">The files at the root of the package that lay the rest of it down.</param>
	public sealed record PackageLayout(string Runtime, string Shared, string LiveAppHost, IReadOnlyList<PackageScript> Scripts);

	/// <summary>A script at the root of the package, and where in the checkout it is copied from.</summary>
	/// <param name="File">Its file name.</param>
	/// <param name="Source">The checkout folder it comes from.</param>
	public sealed record PackageScript(string File, string Source);
}
