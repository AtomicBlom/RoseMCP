using RoseMcp.Broker;
using RoseMcp.Contracts;
using RoseMcp.Settings;

namespace RoseMcp.UnitTests;

/// <summary>
/// The preferences file, and the rule that decides whether a window opens.
/// <para>
/// Every test here writes into a directory of its own. The real path is shared by every host on
/// the machine, which is the point of it -- and exactly why a test must not touch it: a suite that
/// flipped the developer's own setting would be changing their tools to check its own.
/// </para>
/// </summary>
public sealed class RoseSettingsTests
{
	/// <summary>
	/// Beside the logs and the install, in the vendor/product folder they share. Asserted because a
	/// preference written somewhere else is one no other host reads, and nothing would say so.
	/// </summary>
	[Test]
	public void Settings_live_beside_the_logs_and_the_install()
	{
		var path = RoseSettingsFile.PathFor(@"D:\local");

		Assert.Equal(Path.Combine(@"D:\local", "BinaryVibrance", "RoseMCP", "settings.json"), path);
	}

	/// <summary>
	/// A file that is not there is the defaults, which is the state every machine starts in. A host
	/// that refused to start until somebody created one would be unusable out of the box.
	/// </summary>
	[Test]
	public void Nothing_written_yet_reads_as_the_defaults()
	{
		using var home = new TemporaryHome();

		var settings = RoseSettingsFile.Read(home.Path);

		Assert.False(settings.ShowInspectorOnAttach);
	}

	[Test]
	public void What_is_written_is_what_is_read()
	{
		using var home = new TemporaryHome();

		Assert.True(RoseSettingsFile.Write(new RoseSettings { ShowInspectorOnAttach = true }, home.Path));
		Assert.True(RoseSettingsFile.Read(home.Path).ShowInspectorOnAttach);

		Assert.True(RoseSettingsFile.Write(new RoseSettings { ShowInspectorOnAttach = false }, home.Path));
		Assert.False(RoseSettingsFile.Read(home.Path).ShowInspectorOnAttach);
	}

	/// <summary>
	/// Nonsense in the file is the defaults too. A preference is not load-bearing, and a broker
	/// that would not start because somebody hand-edited JSON badly trades a small inconvenience
	/// for a large one.
	/// </summary>
	[Test]
	public void A_file_that_cannot_be_read_is_the_defaults()
	{
		using var home = new TemporaryHome();
		var path = RoseSettingsFile.PathFor(home.Path);

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ this is not json");

		Assert.False(RoseSettingsFile.Read(home.Path).ShowInspectorOnAttach);
	}

	/// <summary>
	/// The file is written whole and stays readable, so a person can see and edit what they chose.
	/// A preference nobody can inspect is a preference nobody can undo when it goes wrong.
	/// </summary>
	[Test]
	public void The_file_is_readable_json_naming_its_setting()
	{
		using var home = new TemporaryHome();
		RoseSettingsFile.Write(new RoseSettings { ShowInspectorOnAttach = true }, home.Path);

		var written = File.ReadAllText(RoseSettingsFile.PathFor(home.Path));

		Assert.Contains("showInspectorOnAttach", written);
		Assert.Contains("true", written);
	}

	/// <summary>
	/// Which of the three the caller asked for, against what the machine prefers. Only
	/// <see cref="InspectorVisibility.UserPreference"/> consults the file; the others are a caller
	/// overruling it on purpose, which is what makes them worth having.
	/// </summary>
	[Test]
	[Arguments(InspectorVisibility.UserPreference, false, false)]
	[Arguments(InspectorVisibility.UserPreference, true, true)]
	[Arguments(InspectorVisibility.Always, false, true)]
	[Arguments(InspectorVisibility.Always, true, true)]
	[Arguments(InspectorVisibility.Never, false, false)]
	[Arguments(InspectorVisibility.Never, true, false)]
	public void What_was_asked_for_and_what_is_preferred_decide_together(
		InspectorVisibility asked,
		bool preferred,
		bool expected)
	{
		Assert.Equal(expected, InspectorRequest.Wanted(asked, new RoseSettings { ShowInspectorOnAttach = preferred }));
	}

	/// <summary>
	/// A host with no operator endpoint says why rather than doing nothing. An argument that is
	/// silently ignored is how somebody concludes the feature is broken.
	/// </summary>
	[Test]
	public void A_host_without_an_endpoint_explains_itself()
	{
		var presenter = NoInspector.WithoutAnEndpoint;

		Assert.False(presenter.CanShow);
		Assert.NotNull(presenter.Obstacle);
		Assert.Contains("stdio", presenter.Obstacle);
		Assert.Equal(presenter.Obstacle, presenter.Show("s1", 4242));
	}

	/// <summary>A local-app-data folder of this test's own, removed afterwards.</summary>
	private sealed class TemporaryHome : IDisposable
	{
		public string Path { get; } =
			System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rose-settings", Guid.NewGuid().ToString("n"));

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
}
