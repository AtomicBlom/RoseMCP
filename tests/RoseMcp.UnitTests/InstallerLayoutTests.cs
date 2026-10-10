using System.Text.RegularExpressions;

namespace RoseMcp.UnitTests;

/// <summary>
/// That <c>installer/rosemcp.iss</c> lays down the published layout.
/// <para>
/// Inno Setup cannot read <c>tools/published-layout.json</c>, so the script restates what it needs of
/// it: which payload folder each architecture gets and on which machine, which debug hosts each machine
/// gets, and where the tray and worker sit once installed. This is what holds that restatement to the
/// file deploy.ps1 and install.ps1 read. Without it a layout change passes every other test and the
/// installer lays down the old shape on somebody's machine, or compiles against a folder the package
/// no longer has.
/// </para>
/// <para>
/// Read as text rather than compiled, because ISCC is a Windows tool this suite does not need, and a
/// [Files] line is regular enough that the parts which matter can be read off it.
/// </para>
/// </summary>
public sealed partial class InstallerLayoutTests
{
	private static readonly PublishedLayout Layout = PublishedLayout.Load();

	private static readonly string Script = File.ReadAllText(Checkout.RepositoryFile("installer", "rosemcp.iss"));

	private static readonly IReadOnlyList<FilesEntry> Entries = ReadFilesEntries(Script);

	/// <summary>
	/// Each architecture's own payload, laid down only on a machine of that architecture. A Check:
	/// naming the wrong machine installs a payload that runs emulated, with no native debug host, and
	/// nothing says so.
	/// </summary>
	[Test]
	public void Lays_down_each_architectures_payload_on_its_own_machine()
	{
		foreach (var runtime in Layout.Runtimes)
		{
			var entry = EntryFor(PublishedLayout.Expand(Layout.Package.Runtime, rid: runtime.Rid));

			entry.DestDir.ShouldBe("{app}");
			entry.Check.ShouldBe(runtime.InnoCheck);
		}
	}

	/// <summary>The shared payload, which every architecture needs and so carries no condition.</summary>
	[Test]
	public void Lays_down_the_shared_payload_on_every_machine()
	{
		var entry = EntryFor(Layout.Package.Shared);

		entry.DestDir.ShouldBe("{app}");
		entry.Check.ShouldBeNull();
	}

	/// <summary>
	/// Each debug host, into the folder the resolvers look in, on exactly the machines whose runtime
	/// carries it: unconditionally when every runtime does, and otherwise only where one does. An ARM64
	/// host on an x64 machine is weight nothing can load; an x86 host missing anywhere is every new UWP
	/// app undebuggable there.
	/// </summary>
	[Test]
	public void Lays_down_each_debug_host_on_every_machine_that_can_run_it()
	{
		foreach (var host in Layout.AllLiveAppHosts)
		{
			var entry = EntryFor(PublishedLayout.Expand(Layout.Package.LiveAppHost, host: host));

			var carriers = Layout.Runtimes.Where(runtime => runtime.LiveAppHosts.Contains(host)).ToList();
			var expectedCheck = carriers.Count == Layout.Runtimes.Count
				? null
				: string.Join(" or ", carriers.Select(runtime => runtime.InnoCheck));

			entry.DestDir.ShouldBe(App(PublishedLayout.Expand(Layout.LiveAppHost.Folder, host: host)));
			entry.Check.ShouldBe(expectedCheck, $"the Check: on {host}'s host");
		}
	}

	/// <summary>
	/// Nothing from the package that the layout does not describe. A folder the layout dropped would
	/// fail the compile, or lay down a host no install is meant to have.
	/// </summary>
	[Test]
	public void Takes_nothing_from_the_payload_the_layout_does_not_describe()
	{
		var described = Layout.Runtimes.Select(runtime => PublishedLayout.Expand(Layout.Package.Runtime, rid: runtime.Rid))
			.Append(Layout.Package.Shared)
			.Concat(Layout.AllLiveAppHosts.Select(host => PublishedLayout.Expand(Layout.Package.LiveAppHost, host: host)))
			.Select(folder => Staged(folder) + @"\*")
			.ToHashSet(StringComparer.Ordinal);

		var payloadRoot = Staged(Layout.Package.Shared.Split('/')[0]) + @"\";
		var undescribed = Entries
			.Where(entry => entry.Source.StartsWith(payloadRoot, StringComparison.Ordinal) && !described.Contains(entry.Source))
			.Select(entry => entry.Source)
			.ToList();

		undescribed.ShouldBeEmpty();
	}

	/// <summary>
	/// Every file the installer takes from the root of the package is one packaging puts there. The
	/// installer runs RoseMcp.Deploy.ps1 to stop a running install, and a script it expects that the
	/// package does not carry fails the compile.
	/// </summary>
	[Test]
	public void Takes_only_scripts_the_package_carries_from_its_root()
	{
		var fromRoot = Entries
			.Select(entry => entry.Source)
			.Where(source => source.StartsWith(Staged(string.Empty), StringComparison.Ordinal))
			.Select(source => source[Staged(string.Empty).Length..])
			.Where(relative => !relative.Contains('\\'))
			.ToList();

		fromRoot.ShouldNotBeEmpty();
		fromRoot.ShouldBeSubsetOf(Layout.Package.Scripts.Select(script => script.File));
	}

	/// <summary>
	/// Every executable the script names in the install -- the shortcuts, the Run key, the post-install
	/// launch, the uninstall icon, the worker the tray is pointed at -- is where the layout puts it. A
	/// tray that moved folders and a shortcut that did not is an install that starts nothing.
	/// </summary>
	[Test]
	public void Names_every_installed_executable_where_the_layout_puts_it()
	{
		var named = InstalledExecutable().Matches(Script).Select(match => match.Groups["path"].Value).Distinct().ToList();
		var laidDown = Layout.Components
			.Select(component => Inno(string.Join('/', new[] { component.Folder, component.Executable + ".exe" }.Where(part => part.Length > 0))))
			.ToList();

		named.ShouldNotBeEmpty();
		named.ShouldBeSubsetOf(laidDown);
	}

	/// <summary>
	/// The setup icon comes out of the shared payload, from inside a folder the layout has. Naming an
	/// architecture's folder fails the compile once deduplication hoists the icon, and naming a folder
	/// the layout dropped fails it at once.
	/// </summary>
	[Test]
	public void Takes_its_icon_from_a_component_folder_in_the_shared_payload()
	{
		var icon = SetupIconFile().Match(Script);
		icon.Success.ShouldBeTrue("rosemcp.iss sets no SetupIconFile.");

		var sharedRoot = Staged(Layout.Package.Shared) + @"\";
		var path = icon.Groups["path"].Value;

		path.ShouldStartWith(sharedRoot, Case.Sensitive);
		path[sharedRoot.Length..].Split('\\')[0].ShouldBeOneOf([.. Layout.Components.Select(component => component.Folder).Where(folder => folder.Length > 0)]);
	}

	/// <summary>One [Files] line, continuation lines joined, with the parts this suite reads.</summary>
	private sealed record FilesEntry(string Source, string? DestDir, string? Check);

	private static FilesEntry EntryFor(string packageFolder)
	{
		var source = Staged(packageFolder) + @"\*";
		var matches = Entries.Where(entry => entry.Source == source).ToList();

		matches.Count.ShouldBe(1, $"rosemcp.iss should take {source} exactly once.");

		return matches[0];
	}

	/// <summary>A package path as rosemcp.iss writes it: under the stage, with backslashes.</summary>
	private static string Staged(string packagePath) => @"{#StageDir}\" + Inno(packagePath);

	/// <summary>An install path as rosemcp.iss writes it.</summary>
	private static string App(string installPath) => @"{app}\" + Inno(installPath);

	private static string Inno(string layoutPath) => layoutPath.Replace('/', '\\');

	/// <summary>
	/// The [Files] section's entries. A line ending in a backslash continues on the next, which is how
	/// the script keeps its flags off the line that names the files.
	/// </summary>
	private static IReadOnlyList<FilesEntry> ReadFilesEntries(string script)
	{
		var entries = new List<FilesEntry>();
		var inFiles = false;
		var pending = string.Empty;

		foreach (var raw in script.Split('\n'))
		{
			var line = raw.TrimEnd('\r').Trim();

			if (line.StartsWith('['))
			{
				inFiles = line.Equals("[Files]", StringComparison.OrdinalIgnoreCase);
				continue;
			}

			if (!inFiles || line.StartsWith(';')) continue;

			if (line.EndsWith('\\'))
			{
				pending += line[..^1] + " ";
				continue;
			}

			var entry = pending + line;
			pending = string.Empty;

			var source = SourceParameter().Match(entry);
			if (!source.Success) continue;

			var destDir = DestDirParameter().Match(entry);
			var check = CheckParameter().Match(entry);

			entries.Add(new FilesEntry(
				source.Groups["value"].Value,
				destDir.Success ? destDir.Groups["value"].Value : null,
				check.Success ? check.Groups["value"].Value.Trim() : null));
		}

		return entries;
	}

	[GeneratedRegex(@"\bSource:\s*""(?<value>[^""]*)""")]
	private static partial Regex SourceParameter();

	[GeneratedRegex(@"\bDestDir:\s*""(?<value>[^""]*)""")]
	private static partial Regex DestDirParameter();

	[GeneratedRegex(@"\bCheck:\s*(?<value>[^;]+)")]
	private static partial Regex CheckParameter();

	[GeneratedRegex(@"\{app\}\\(?<path>[^""\s]+?\.exe)")]
	private static partial Regex InstalledExecutable();

	[GeneratedRegex(@"^SetupIconFile=(?<path>.+?)\s*$", RegexOptions.Multiline)]
	private static partial Regex SetupIconFile();
}
