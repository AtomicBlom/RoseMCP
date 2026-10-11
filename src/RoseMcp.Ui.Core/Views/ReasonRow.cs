using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// One reason in a workspace's health, as its opened row shows it: Rose's line, what it does to answers, what it
/// covers, since when, whether the person has accepted it and what agents are told -- and, for accepting it, the
/// person's choice of what agents read.
/// </summary>
public sealed class ReasonRow : Observable
{
	private AgentsRead _chosen;
	private string _note = string.Empty;

	public ReasonRow(HealthReason reason)
	{
		Id = reason.Id;
		Update(reason);
		_chosen = reason.Accepted?.AgentsRead ?? AgentsRead.RoseLine;
		_note = reason.Accepted?.Note ?? string.Empty;
	}

	/// <summary>The reason's id within its answer. Rows are matched on it.</summary>
	public string Id { get; }

	/// <summary>Which cause this is, the same at every load.</summary>
	public string Kind { get; private set; } = string.Empty;

	/// <summary>Rose's line: the opening sentence an agent reading the workspace is told.</summary>
	public string Line { get; private set; } = string.Empty;

	/// <summary>What it does to answers, in words.</summary>
	public string Effect { get; private set; } = string.Empty;

	/// <summary>The projects it covers, or what it is about where it names none, and when it was first seen.</summary>
	public string Facts { get; private set; } = string.Empty;

	/// <summary>Whether the person has accepted it for every project it covers.</summary>
	public bool IsAccepted { get; private set; }

	public bool IsNotAccepted => !IsAccepted;

	/// <summary>When it was accepted and what agents read, or that it is not accepted.</summary>
	public string Acceptance { get; private set; } = string.Empty;

	/// <summary>Exactly what an agent reading the workspace is told about it.</summary>
	public string AgentsTold { get; private set; } = string.Empty;

	/// <summary>Whether the reason blocks answers, which is drawn loudest.</summary>
	public bool BlocksAnswers { get; private set; }

	/// <summary>The projects an acceptance covers.</summary>
	public IReadOnlyList<string> Projects { get; private set; } = [];

	/// <summary>What an acceptance covers where the reason names no project.</summary>
	public IReadOnlyList<string> Subjects { get; private set; } = [];

	/// <summary>What the person chooses agents read once it is accepted. Bound both ways.</summary>
	public AgentsRead Chosen
	{
		get => _chosen;
		set => Set(ref _chosen, value);
	}

	/// <summary>Index of <see cref="Chosen"/> among Rose's line, a note and nothing, for a list of three choices.</summary>
	public int ChosenIndex
	{
		get => (int)Chosen;
		set
		{
			if (value is < 0 or > (int)AgentsRead.Nothing) return;

			Chosen = (AgentsRead)value;
			OnPropertyChanged(nameof(ChosenIndex));
		}
	}

	/// <summary>The person's note, where agents are to read one instead of Rose's line. Bound both ways.</summary>
	public string Note
	{
		get => _note;
		set => Set(ref _note, value);
	}

	/// <summary>A note asking an agent about this reason: what "Ask an agent why" opens.</summary>
	public NoteComposer Composer { get; } = new();

	/// <summary>Says which solution this reason is in and which sessions a note about it can go to, the likeliest first.</summary>
	public void OfferNote(string workspace, IReadOnlyList<NoteRecipient> recipients)
	{
		Composer.Regard(new NoteSubject
		{
			Kind = NoteSubjectKind.WorkspaceReason,
			Readable = Line,
			Workspace = workspace,
			ReasonKind = Kind,
			Reason = Id,
		});

		Composer.Offer(recipients, NoteTargets.NobodyToAsk);
	}

	/// <summary>Brings the row in line with the reason as the broker now reports it, keeping the person's half-made choice.</summary>
	public void Update(HealthReason reason)
	{
		Kind = reason.Kind;
		Line = reason.Line;
		BlocksAnswers = reason.Effect == HealthEffect.BlocksAnswers && reason.Accepted is null;
		Projects = reason.Projects;
		Subjects = reason.Subjects;
		Effect = reason.Effect switch
		{
			HealthEffect.BlocksAnswers => "No answer can be trusted until this is fixed",
			HealthEffect.MayAffectSomeProjects => "Answers about these projects may be incomplete",
			_ => "A note; it changes no answer",
		};

		var facts = new List<string>();
		var covers = reason.Projects.Count > 0 ? reason.Projects : reason.Subjects;
		if (covers.Count > 0) facts.Add(string.Join(", ", covers));
		if (reason.FirstSeenUtc is { } first) facts.Add($"first seen {Day(first)}");
		if (reason.AcknowledgedUtc is { } acknowledged) facts.Add($"acknowledged by an agent's session at {Clock(acknowledged)}");
		Facts = string.Join(Format.Separator, facts);

		IsAccepted = reason.Accepted is not null;
		Acceptance = reason.Accepted is { } accepted
			? $"Accepted on {Day(accepted.AcceptedUtc)}, so it draws no attention."
			: "Not accepted.";

		AgentsTold = Told(reason.Accepted?.AgentsRead ?? AgentsRead.RoseLine, reason.Line, reason.Accepted?.Note);

		OnPropertyChanged(string.Empty);
	}

	/// <summary>The request that accepts this reason for the person, with what they chose agents read.</summary>
	public AcceptReasonRequest Accept(string workspace) => new()
	{
		Workspace = workspace,
		Kind = Kind,
		Projects = Projects,
		Subjects = Subjects,
		AgentsRead = Chosen,
		Note = Chosen == AgentsRead.Note ? Note : null,
	};

	/// <summary>The request that stops accepting it.</summary>
	public StopAcceptingRequest StopAccepting(string workspace) => new()
	{
		Workspace = workspace,
		Kind = Kind,
		Projects = Projects,
		Subjects = Subjects,
	};

	/// <summary>Exactly what agents are told on each read, for a choice of what they read.</summary>
	public static string Told(AgentsRead read, string line, string? note) => read switch
	{
		AgentsRead.Note => note is { Length: > 0 } ? note : "(your note, once written)",
		AgentsRead.Nothing => "Nothing: reads do not mention it. Status still lists it, marked accepted.",
		_ => line,
	};

	internal static string Day(DateTime utc) =>
		DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);

	internal static string Clock(DateTime utc) =>
		DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>One acceptance a person has made for a solution, which may outlive the reason it was made for.</summary>
public sealed class AcceptedRow(AcceptedReason accepted)
{
	/// <summary>Kind and what it covers, which is what the acceptance is keyed on.</summary>
	public string Key { get; } = $"{accepted.Kind}:{accepted.Covering}";

	public string Kind { get; } = accepted.Kind;

	/// <summary>The project, or the subject of a reason naming none.</summary>
	public string Covering { get; } = accepted.Covering;

	/// <summary>When, and what agents read about it.</summary>
	public string Facts { get; } = $"accepted {ReasonRow.Day(accepted.AcceptedUtc)}{Format.Separator}agents read "
		+ accepted.AgentsRead switch
		{
			AgentsRead.Note => $"your note: {accepted.Note}",
			AgentsRead.Nothing => "nothing",
			_ => "Rose's line",
		};

	/// <summary>The request that stops accepting it.</summary>
	public StopAcceptingRequest StopAccepting(string workspace) => new()
	{
		Workspace = workspace,
		Kind = Kind,
		Projects = [Covering],
	};
}

/// <summary>An agent's proposal that the person accept a reason, waiting on the person.</summary>
public sealed class ProposalRow(ReasonProposal proposal)
{
	public string Id { get; } = proposal.Id;

	/// <summary>Rose's line for the reason proposed.</summary>
	public string Line { get; } = proposal.Line;

	/// <summary>What it would cover, what agents would read, and when it was proposed.</summary>
	public string Facts { get; } = string.Join(
		Format.Separator,
		new[]
		{
			string.Join(", ", proposal.Projects.Count > 0 ? proposal.Projects : proposal.Subjects),
			$"agents would read {ReasonRow.Told(proposal.AgentsRead, "Rose's line", proposal.Note)}",
			$"proposed {ReasonRow.Day(proposal.ProposedUtc)} at {ReasonRow.Clock(proposal.ProposedUtc)}",
		}.Where(fact => fact.Length > 0));
}
