using System.Diagnostics;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.XamlDiff;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Applying an edit to the live tree, and remembering what the app was last told.
/// <para>
/// Separate from the questions because of what it owns rather than what it does. Reading a tree, a
/// selection or an element's properties keeps nothing: each is one message on the pipe and the
/// answer is the whole of it. An apply carries state between calls -- the markup last sent for each
/// file -- because a live edit is diffed against what the app was last told and never against what
/// is on disk, and a caller in the edit-to-live loop no longer holds what it just overwrote.
/// </para>
/// <para>
/// Every method here assumes the caller already holds the session's request lock, which is what the
/// <c>Core</c> in <see cref="ApplyEditsCore"/> says. One pipe carries one request and one reply at a
/// time, and the rule that keeps the choice of lock free rather than load-bearing is that a public
/// entry point takes it exactly once and everything below assumes it is held.
/// </para>
/// </summary>
internal sealed class XamlApply(XamlProviderSession provider, ILogger logger)
{
	private readonly XamlProviderSession _provider = provider;

	/// <summary>
	/// What the app was last told, per file. The baseline advances whether or not every edit in a
	/// batch took, because a structural edit is not idempotent: re-sending an AddChild because
	/// something else in the batch failed puts a second copy of the element in on the attempt that
	/// works.
	/// </summary>
	private readonly XamlApplyBaseline _baselines = new();

	private TimeSpan Reply => _provider.Bounds.Snapshot;

	private string Unanswered(string request, string what) => XamlChannelBounds.Unanswered(request, what, Reply);

	internal LiveXamlApplyResult ApplyEditsCore(int pid, string? oldXaml, string? newXaml, string? filePath)
	{
		var (inputs, failure) = Resolve(pid, oldXaml, newXaml, filePath);
		if (failure is not null) return new LiveXamlApplyResult { Detail = failure };

		// Nothing to diff against, which is not a failure: the file's contents are the baseline from
		// here on, so the caller's next edit applies on its own. The note says which reason it was.
		if (inputs!.OldXaml is null)
		{
			return new LiveXamlApplyResult { Notes = inputs.Note is null ? [] : [inputs.Note] };
		}

		XamlDiffResult diff;
		try
		{
			diff = RoseMcp.XamlDiff.XamlDiff.Compute(inputs.OldXaml, inputs.NewXaml);
		}
		catch (Exception exception)
		{
			return new LiveXamlApplyResult { Detail = $"Could not diff the XAML: {exception.Message}" };
		}

		// The target goes to the provider exactly as the diff wrote it, `#name` or path alike: whether an
		// address resolves is a question about the live tree, so it is asked where the live tree is.
		//
		// An addition is the one edit that is not a single command. There is no way to apply markup --
		// CreateInstance builds one object from a type name -- so the subtree is taken apart into build
		// steps and sent as several commands, and this edit's outcome is the outcome of all of them. The
		// taking apart lives in the diff library, which is pure and unit tested; doing it here would put
		// the fiddliest part of this somewhere no unit test can reach.
		var commands = new List<string>();
		var plans = new List<(XamlEdit Edit, List<string> Keys)>();
		var notes = new List<string>();
		if (inputs.Note is not null) notes.Add(inputs.Note);
		notes.AddRange(diff.Notes);

		foreach (var edit in diff.Edits)
		{
			var keys = new List<string>();

			if (edit.Kind is XamlEditKind.SetProperty or XamlEditKind.ClearProperty or XamlEditKind.RemoveChild)
			{
				var property = edit.Property ?? string.Empty;
				commands.Add(XamlProviderWire.Line(XamlProviderWire.Op(edit.Kind), edit.Target, property, edit.ValueType ?? string.Empty, edit.Value ?? string.Empty, string.Empty, 0));
				keys.Add(XamlProviderWire.Key(XamlProviderWire.Op(edit.Kind), edit.Target, property, string.Empty));
			}
			else if (edit.Kind is XamlEditKind.AddChild && edit.Payload is { } payload)
			{
				try
				{
					foreach (var step in XamlMaterialiser.Steps(payload, edit.Target, edit.Index ?? 0))
					{
						var (line, key) = XamlProviderWire.Command(step);
						commands.Add(line);
						keys.Add(key);
					}
				}
				catch (Exception exception)
				{
					keys.Clear();
					notes.Add($"The element added under {edit.Target} could not be taken apart into build steps: {exception.Message}");
				}
			}
			else if (edit.Kind is XamlEditKind.SetResource && edit.Payload is { } resource)
			{
				// A resource is built the same way an added element is and then put somewhere else:
				// behind a key rather than into a parent's children. So the same steps run, minus the
				// attach, and one ReplaceResource finishes it.
				try
				{
					foreach (var step in XamlMaterialiser.Unattached(resource))
					{
						var (line, key) = XamlProviderWire.Command(step);
						commands.Add(line);
						keys.Add(key);
					}

					var name = edit.Property ?? string.Empty;
					commands.Add(XamlProviderWire.Line("ReplaceResource", edit.Target, name, string.Empty, string.Empty, XamlMaterialiser.RootSlot, 0));
					keys.Add(XamlProviderWire.Key("ReplaceResource", edit.Target, name, XamlMaterialiser.RootSlot));
				}
				catch (Exception exception)
				{
					keys.Clear();
					notes.Add($"The resource '{edit.Property}' on {edit.Target} could not be taken apart into build steps: {exception.Message}");
				}
			}

			plans.Add((edit, keys));
		}

		var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
		if (commands.Count > 0)
		{
			var (pipe, unready) = _provider.Connect(pid);
			if (pipe is null) return new LiveXamlApplyResult { Detail = unready };

			var request = "apply\n" + string.Join("\n", commands);
			var served = pipe.Request(request, Reply);
			if (served is null)
			{
				// Not retried anywhere, and the baseline is deliberately left where it was. A structural
				// edit is not idempotent, and a missing reply cannot tell "never ran" from "ran, and the
				// answer was lost" -- so resending would put a second copy of everything this batch adds
				// into the app. The message says what that costs the caller.
				return new LiveXamlApplyResult
				{
					Detail = Unanswered(request, "a batch of edits to be applied")
						+ " Applying the same change again could therefore add a second copy of anything this one "
						+ "was adding.",
				};
			}

			statuses = XamlProviderWire.ParseApplyResults(served.Split('\n', StringSplitOptions.RemoveEmptyEntries));
			logger.LogInformation("Applied {Count} XAML command(s) to pid {Pid} over the pipe.", commands.Count, pid);
		}

		// Advanced whether or not every edit took, and that is the deliberate half. The app has been
		// sent this version; re-sending a structural edit because something else in the batch failed
		// would duplicate the elements that did go in. The failures are in the results to act on.
		if (inputs.SourcePath is not null) _baselines.Advance(inputs.SourcePath, inputs.NewXaml);

		var results = new List<LiveXamlEditResult>();
		foreach (var (edit, keys) in plans)
		{
			results.Add(new LiveXamlEditResult
			{
				Kind = edit.Kind.ToString(),
				Target = edit.Target,
				Property = edit.Property,
				Value = edit.Value,
				Status = XamlProviderWire.Outcome(keys, statuses),
			});
		}

		// An edit that did not take is said in the notes as well as in its own row. The tool's contract is
		// that the notes list edits worked out and not applied, so an empty notes list reads as a clean
		// apply -- leaving the difference between applied and total as the only signal there was, which
		// is the one a caller following the documentation never looks at.
		foreach (var failed in results.Where(result => result.Status != "applied"))
		{
			var what = failed.Property is { Length: > 0 } property
				? $"{failed.Kind} '{property}' on {failed.Target}"
				: $"{failed.Kind} on {failed.Target}";

			notes.Add($"{what} was not applied: {failed.Status}.");
		}

		return new LiveXamlApplyResult
		{
			Applied = results.Count(result => result.Status == "applied"),
			Results = results,
			Notes = notes,
		};
	}

	/// <summary>
	/// Works out what to diff, from what the caller gave, or names what is missing or contradictory.
	/// <para>
	/// There are three ways to ask, and the one this was built for is to name the file and nothing
	/// else. Two versions of the markup is the original shape, still honoured because a caller
	/// composing markup may have no file at all. A file plus an explicit old version is the escape
	/// hatch for the first apply after an edit this side never saw.
	/// </para>
	/// <para>
	/// A file plus a new version is refused rather than reconciled. Two answers to "what does it say
	/// now" is a question, and picking one silently is how a tool applies something convincingly and
	/// not what was asked.
	/// </para>
	/// </summary>
	private (ApplyInputs? Inputs, string? Error) Resolve(int pid, string? oldXaml, string? newXaml, string? filePath)
	{
		var hasFile = !string.IsNullOrWhiteSpace(filePath);
		var hasNew = !string.IsNullOrEmpty(newXaml);
		var hasOld = !string.IsNullOrEmpty(oldXaml);

		if (!hasFile)
		{
			if (!hasNew)
			{
				return (null, "Nothing to apply: pass filePath to apply what a XAML file now holds, or newXaml with "
					+ "oldXaml to apply markup that is not on disk.");
			}

			if (!hasOld)
			{
				return (null, "Nothing to diff against: pass filePath rather than newXaml and this side keeps track "
					+ "of what it has already applied to the file, or pass oldXaml alongside newXaml.");
			}

			return (new ApplyInputs { OldXaml = oldXaml, NewXaml = newXaml! }, null);
		}

		if (hasNew)
		{
			return (null, "filePath and newXaml both say what the markup is now, so pass one: filePath to apply what "
				+ "the file holds, newXaml to apply markup that is not on disk.");
		}

		string full;
		try
		{
			full = Path.GetFullPath(filePath!);
		}
		catch (Exception exception)
		{
			return (null, $"'{filePath}' is not a usable path: {exception.Message}");
		}

		if (!File.Exists(full)) return (null, $"There is no file at {full}.");

		string current;
		try
		{
			current = File.ReadAllText(full);
		}
		catch (Exception exception)
		{
			return (null, $"Could not read {full}: {exception.Message}");
		}

		// Refused rather than recorded, and this is the reason the check is worth its lines. A first
		// apply records what it read as the baseline for the next one, so recording markup that does
		// not parse would leave every apply after it diffing against something unparseable -- a call
		// reporting a parse error about a file the caller has since fixed, with nothing it can do to
		// say so.
		if (!RoseMcp.XamlDiff.XamlDiff.Parses(current, out var reason))
		{
			return (null, $"{full} is not markup this can diff, so nothing was recorded or applied: {reason}");
		}

		// An explicit old version wins over the baseline and still refreshes it. The caller is telling
		// this side something it had no way to know, and the applies after it should carry on from
		// there rather than needing to be told again.
		if (hasOld) return (new ApplyInputs { OldXaml = oldXaml, NewXaml = current, SourcePath = full }, null);

		var plan = _baselines.Prepare(full, current, AgeOf(pid, full));

		return (new ApplyInputs { OldXaml = plan.OldXaml, NewXaml = current, SourcePath = full, Note = plan.Note }, null);
	}

	/// <summary>
	/// What can be said about a file's last write against the moment the target started running. It
	/// decides only the note on a first apply, and says "cannot tell" rather than assuming: "changed
	/// since the app started" is a claim about the file, and a process that will not give its start
	/// time is no evidence either way.
	/// </summary>
	private static XamlBaselineAge AgeOf(int pid, string path)
	{
		try
		{
			using var process = Process.GetProcessById(pid);

			return File.GetLastWriteTimeUtc(path) > process.StartTime.ToUniversalTime()
				? XamlBaselineAge.ChangedSinceTargetStarted
				: XamlBaselineAge.UnchangedSinceTargetStarted;
		}
		catch (Exception)
		{
			return XamlBaselineAge.Unknown;
		}
	}

	/// <summary>What an apply will diff, once the ways of asking for it have been reconciled.</summary>
	private sealed record ApplyInputs
	{
		/// <summary>Null when there is nothing to diff against; <see cref="Note"/> then says why.</summary>
		public string? OldXaml { get; init; }

		public required string NewXaml { get; init; }

		/// <summary>The file the markup came from, when it came from one. Keys the baseline.</summary>
		public string? SourcePath { get; init; }

		public string? Note { get; init; }
	}
}
