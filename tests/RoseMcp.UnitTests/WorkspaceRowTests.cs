using System.Collections.ObjectModel;

using RoseMcp.Contracts;
using RoseMcp.Ui.Core.Views;

namespace RoseMcp.UnitTests;

/// <summary>
/// A workspace as the Workspaces view shows it: one line of health, coloured only where somebody can act; who used
/// it, read from the record; how long it has gone unused, counted on from its last use; the detail an opened row
/// reads on request; and a row that survives the list spelling its path another way.
/// </summary>
public sealed class WorkspaceRowTests
{
	private const string Solution = @"D:\Drawboard\PDF\Drawboard.All.slnx";

	private static readonly DateTime Noon = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

	/// <summary>
	/// The tone follows the reasons' shape and whether the person accepted them, never their wording: a reason that
	/// blocks answers is critical, one that may affect some projects is caution, and either accepted is quiet, as are
	/// notes, which change no answer.
	/// </summary>
	[Test]
	public void Health_is_coloured_only_where_somebody_can_act()
	{
		HealthLine.Of(Workspace(Solution, WorkspaceState.Loaded)).ShouldBe(new HealthLine("Ready", HealthTone.Quiet));

		var blocking = WithHealth(
			Reason("restore-failed", HealthEffect.BlocksAnswers, "The restore failed."),
			Reason("project-not-loaded", HealthEffect.MayAffectSomeProjects, "Core did not load."));
		HealthLine.Of(blocking).ShouldBe(new HealthLine("The restore failed. (and 1 more reason)", HealthTone.Critical));

		var partial = WithHealth(Reason("project-not-loaded", HealthEffect.MayAffectSomeProjects, "Core did not load."));
		HealthLine.Of(partial).Tone.ShouldBe(HealthTone.Caution);

		var accepted = WithHealth(
			Reason("project-not-loaded", HealthEffect.MayAffectSomeProjects, "Tests.Legacy did not load.", accepted: true),
			Reason("project-not-loaded", HealthEffect.MayAffectSomeProjects, "Ink.Legacy did not load.", accepted: true),
			Reason("configuration-chosen", HealthEffect.Note, "Debug was chosen."));
		HealthLine.Of(accepted).ShouldBe(new HealthLine("Ready  ·  2 accepted warnings", HealthTone.Quiet));
	}

	/// <summary>A rebuilt analyzer is caution, because Reload is the whole fix; loading is on its way; a stopped row is quiet unless its worker faulted.</summary>
	[Test]
	public void State_and_rebuilt_analyzers_have_tones_of_their_own()
	{
		var rebuilt = Workspace(Solution, WorkspaceState.Loaded) with { RebuiltAnalyzers = [@"D:\tools\Drawboard.Analyzers.dll"] };
		HealthLine.Of(rebuilt).ShouldBe(new HealthLine("Drawboard.Analyzers.dll was rebuilt. Answers use the old build until a reload.", HealthTone.Caution));

		HealthLine.Of(Workspace(Solution, WorkspaceState.Loading)).Tone.ShouldBe(HealthTone.Working);

		var evicted = Workspace(Solution, WorkspaceState.Unloaded, alive: false, exitReason: "Closed after 45 minutes unused. The next call opens it again.");
		HealthLine.Of(evicted).ShouldBe(new HealthLine("Closed after 45 minutes unused.", HealthTone.Quiet));

		var faulted = Workspace(Solution, WorkspaceState.Faulted, alive: false, exitReason: "The worker exited with code 3.");
		HealthLine.Of(faulted).Tone.ShouldBe(HealthTone.Critical);
	}

	/// <summary>
	/// "Used by" is not a field: it is the agents whose calls the record holds for the workspace, each once, matched
	/// on the path however it is spelled where the platform ignores case.
	/// </summary>
	[Test]
	public void Used_by_is_who_the_record_says_called_it()
	{
		var entries = new[]
		{
			Call("1", Solution, "PDF", minutesAgo: 5),
			Call("2", Solution, "PDF", minutesAgo: 4),
			Call("3", Solution, "second session", minutesAgo: 1),
			Call("4", @"D:\elsewhere\Other.slnx", "other", minutesAgo: 1),
		};

		WorkspaceRow.UsersOf(Solution, entries).ShouldBe(["second session", "PDF"]);

		var row = new WorkspaceRow(Workspace(Solution, WorkspaceState.Loaded), entries, Noon);
		row.UsedBy.ShouldBe("2 sessions");
		row.UsedByDetail.ShouldContain("second session, PDF");

		new WorkspaceRow(Workspace(@"D:\quiet\Q.slnx", WorkspaceState.Loaded), entries, Noon).UsedBy.ShouldBe("nobody");
	}

	/// <summary>Unused time is counted on from the last use, with when the workspace closes; a workspace in use says so.</summary>
	[Test]
	public void Unused_time_counts_on_from_the_last_use()
	{
		var idle = Workspace(Solution, WorkspaceState.Loaded) with
		{
			LastUsedUtc = Noon.AddMinutes(-25),
			ClosesAtUtc = Noon.AddMinutes(35),
			InUse = false,
		};

		var row = new WorkspaceRow(idle, [], Noon);
		row.Unused.ShouldBe("25m  ·  closes in 35m 00s");

		row.Tick(Noon.AddMinutes(1));
		row.Unused.ShouldBe("26m  ·  closes in 34m 00s");

		row.Update(idle with { InUse = true }, [], Noon);
		row.Unused.ShouldBe("in use");
	}

	/// <summary>
	/// The list is answered only when a row changes in anything but its memory, so a row takes the memory the
	/// window's timer sampled rather than keep the figure the list carried; a stopped row holds nothing to sample.
	/// </summary>
	[Test]
	public void A_row_shows_the_timers_memory_sample()
	{
		var row = new WorkspaceRow(Workspace(Solution, WorkspaceState.Loaded), [], Noon);
		row.Memory.ShouldBe("2.0 GB");

		var report = new MemoryReport
		{
			SampledUtc = Noon,
			Broker = new ProcessMemory { ProcessId = 1, WorkingSetBytes = 1, PrivateMemoryBytes = 1 },
			Workers =
			[
				new WorkerMemory
				{
					Workspace = Solution,
					WorkspaceKey = "Drawboard.All-1",
					Process = new ProcessMemory { ProcessId = 77, WorkingSetBytes = 700L * 1024 * 1024, PrivateMemoryBytes = 650L * 1024 * 1024 },
				},
			],
		};

		row.ShowMemory(report);
		row.Memory.ShouldBe("700 MB");
		row.MemoryDetail.ShouldBe("working set 700 MB  ·  private 650 MB  ·  pid 77");

		var stopped = new WorkspaceRow(Workspace(Solution, WorkspaceState.Unloaded, alive: false), [], Noon);
		stopped.ShowMemory(report);
		stopped.Memory.ShouldBe("—");
	}

	/// <summary>
	/// A row is keyed by its path as the broker compares paths, so a list that spells the path another way updates
	/// the row a reader opened rather than replacing it with its detail closed.
	/// </summary>
	[Test]
	public void A_row_survives_its_path_spelled_another_way()
	{
		var rows = new ObservableCollection<WorkspaceRow>();
		WorkspaceRow.Merge(rows, [Workspace(Solution, WorkspaceState.Loaded)], [], Noon, StringComparer.OrdinalIgnoreCase);

		var opened = rows.ShouldHaveSingleItem();
		opened.IsExpanded = true;

		WorkspaceRow.Merge(rows, [Workspace(Solution.ToLowerInvariant(), WorkspaceState.Degraded)], [], Noon, StringComparer.OrdinalIgnoreCase);

		rows.ShouldHaveSingleItem().ShouldBeSameAs(opened);
		opened.IsExpanded.ShouldBeTrue();
	}

	/// <summary>
	/// Accepting a reason sends what the person chose agents read, and the note only where agents read a note; what
	/// agents are told is said exactly.
	/// </summary>
	[Test]
	public void Accepting_a_reason_sends_what_the_person_chose()
	{
		var reason = new ReasonRow(Reason("project-not-loaded", HealthEffect.MayAffectSomeProjects, "Legacy did not load.") with { Projects = ["Legacy"] });

		reason.AgentsTold.ShouldBe("Legacy did not load.");
		reason.IsAccepted.ShouldBeFalse();

		reason.ChosenIndex = (int)AgentsRead.Note;
		reason.Note = "Ignore this unless you work in Legacy.";

		var request = reason.Accept(Solution);
		request.Kind.ShouldBe("project-not-loaded");
		request.Projects.ShouldBe(["Legacy"]);
		request.AgentsRead.ShouldBe(AgentsRead.Note);
		request.Note.ShouldBe("Ignore this unless you work in Legacy.");

		reason.ChosenIndex = (int)AgentsRead.Nothing;
		reason.Accept(Solution).Note.ShouldBeNull("a note nobody reads is not sent");

		ReasonRow.Told(AgentsRead.Nothing, "line", null).ShouldStartWith("Nothing");
	}

	/// <summary>
	/// A degraded workspace says why on request: its failed projects by name, the analyzers that would not load in
	/// the loader's own words, its restore, and every project's own figures.
	/// </summary>
	[Test]
	public void The_load_detail_says_why_in_the_tools_own_words()
	{
		var status = new WorkspaceStatusReport
		{
			SolutionPath = Solution,
			State = WorkspaceState.Degraded,
			Revision = 7,
			LoadSeconds = 41,
			ProjectCount = 2,
			Projects =
			[
				Project("Core", loaded: true),
				Project("Legacy", loaded: false),
			],
			LoadDiagnostics = [],
			DegradedReasons = ["Legacy did not load. Fix: install the netcoreapp3.1 targeting pack."],
			AnalyzerLoadFailures = [new AnalyzerLoadFailure { Assembly = "Drawboard.Analyzers.dll", Message = "Could not load file or assembly 'Roslyn 5.0'." }],
			Restore = new RestoreReport { Ran = true, Succeeded = false, Reason = "assets were missing", Unrestored = ["Legacy"] },
		};

		var sections = LoadDetail.Of(status);
		var text = string.Join("\n", sections.SelectMany(section => section.Lines.Prepend(section.Title)));

		text.ShouldContain("41 s");
		text.ShouldContain("1 project that failed to load");
		text.ShouldContain(@"Legacy (netcoreapp3.1)  ·  D:\repo\Legacy\Legacy.csproj");
		text.ShouldContain("Drawboard.Analyzers.dll: Could not load file or assembly 'Roslyn 5.0'.");
		text.ShouldContain("Ran and failed: assets were missing");
		text.ShouldContain("Not restored: Legacy");
		LoadDetail.Describe(status.Projects[0]).ShouldContain("1 generator making 4 files");
	}

	internal static OperatorWorkspace Workspace(string path, WorkspaceState state, bool alive = true, string exitReason = "") => new()
	{
		Summary = new WorkspaceSummary
		{
			Workspace = path,
			DisplayName = Path.GetFileName(path.Replace('\\', '/')),
			Alive = alive,
			ExitReason = exitReason,
			State = state,
			StartedUtc = Noon.AddHours(-1),
			Uptime = TimeSpan.FromHours(1),
			WorkingSetBytes = alive ? 2L * 1024 * 1024 * 1024 : null,
		},
		Folder = @"D:\Drawboard\PDF",
		Configuration = "Debug",
		Platform = "x64",
		LastUsedUtc = Noon.AddMinutes(-1),
		IdleFor = TimeSpan.FromMinutes(1),
		InUse = false,
	};

	private static OperatorWorkspace WithHealth(params HealthReason[] reasons)
	{
		var workspace = Workspace(Solution, WorkspaceState.Degraded);

		return workspace with { Summary = workspace.Summary with { Health = reasons } };
	}

	private static HealthReason Reason(string kind, HealthEffect effect, string line, bool accepted = false) => new()
	{
		Id = $"{kind}:{line.GetHashCode()}",
		Kind = kind,
		Effect = effect,
		Line = line,
		Accepted = accepted ? new ReasonAcceptance { AcceptedUtc = Noon.AddDays(-9), AgentsRead = AgentsRead.Nothing } : null,
	};

	private static ActivityEntry Call(string id, string workspace, string label, int minutesAgo) =>
		OperationRowTests.Call(id, new DateTimeOffset(Noon).AddMinutes(-minutesAgo), new DateTimeOffset(Noon).AddMinutes(-minutesAgo + 1)) with
		{
			Workspace = workspace,
			ClientLabel = label,
		};

	private static ProjectStatus Project(string name, bool loaded) => new()
	{
		Name = name,
		FilePath = $@"D:\repo\{name}\{name}.csproj",
		TargetFramework = loaded ? "net10.0" : "netcoreapp3.1",
		LoadedSuccessfully = loaded,
		DocumentCount = 12,
		AdditionalDocumentCount = 0,
		AnalyzerReferenceCount = 3,
		GeneratorCount = 1,
		GeneratedDocumentCount = 4,
		MissingAnalyzerOutputs = [],
	};
}
