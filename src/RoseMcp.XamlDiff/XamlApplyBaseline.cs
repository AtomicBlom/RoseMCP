namespace RoseMcp.XamlDiff;

/// <summary>
/// What a live edit has already sent to a running app, per source file, so a caller does not have to
/// hold the previous version of a file it has just edited (#12).
/// <para>
/// The edit-to-live loop is why this exists. Applying used to need both versions of the markup, which
/// reads reasonably and is close to unusable in the loop it was built for: an agent that has just
/// written a file no longer has what was there before it wrote, and asking it to keep a copy hands the
/// caller the one piece of state the session is in a position to hold itself.
/// </para>
/// <para>
/// A baseline is <em>what this side has sent to the app</em>, and never what is on disk now. That
/// distinction is the design. It advances when the commands reach the provider, whether or not every
/// one of them worked, because a structural edit is not idempotent: re-sending an <c>AddChild</c>
/// that failed would, on the attempt that succeeds, put a second copy of the element in. So a failure
/// is reported and the caller decides, rather than being retried by a later apply that was never
/// asked to.
/// </para>
/// <para>
/// Pure, and therefore unit tested. The session that owns one of these cannot be, since it targets
/// Windows and the test projects cannot see inside it -- the same reason the diff and the
/// materialiser live here.
/// </para>
/// </summary>
public sealed class XamlApplyBaseline
{
	// Case-insensitive because these are Windows paths, where one file arrives spelled two ways as a
	// matter of course -- a drive letter's case differs between what a client sends and what this
	// process resolves -- and two spellings of one path would mean two baselines for one file.
	private readonly Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// What <paramref name="current"/> should be diffed against for <paramref name="path"/>, or
	/// nothing to diff against and the reason why. <paramref name="age"/> is what can be said about
	/// the file relative to the moment the target started running.
	/// </summary>
	public XamlApplyPlan Prepare(string path, string current, XamlBaselineAge age)
	{
		if (_applied.TryGetValue(path, out var applied))
		{
			// Said rather than left to be inferred. Nothing changed and the diff finding nothing both
			// end in "0 edits", and they mean different things: one is a caller who has not saved, the
			// other is a change this engine cannot express.
			var unchanged = string.Equals(applied, current, StringComparison.Ordinal);

			return new XamlApplyPlan
			{
				OldXaml = applied,
				Note = unchanged ? $"{Name(path)} is unchanged since the last apply, so there was nothing to send." : null,
			};
		}

		// No baseline: this is the first apply for the file, and the version the running app was built
		// from is not something this side can produce. Recording it and applying nothing is the honest
		// move -- the alternative is to diff against the current contents, which is a guess dressed as
		// a baseline and would silently skip the caller's first edit.
		_applied[path] = current;

		var reason = age switch
		{
			XamlBaselineAge.UnchangedSinceTargetStarted =>
				$"Nothing has edited {Name(path)} since the app started, so there is nothing for this apply to send. "
					+ "Its contents are the baseline now: edit it and call again.",
			XamlBaselineAge.ChangedSinceTargetStarted =>
				$"{Name(path)} has changed since the app started, so what the app was built from is no longer on "
					+ "disk and this side cannot reconstruct it -- nothing was applied. Its contents are the "
					+ "baseline now, so the next edit applies on its own; to apply this one, pass oldXaml as well.",
			_ =>
				$"This is the first apply for {Name(path)} and the app's start time could not be read, so whether "
					+ "the file has changed since cannot be said -- nothing was applied. Its contents are the "
					+ "baseline now, so the next edit applies on its own; to apply this one, pass oldXaml as well.",
		};

		return new XamlApplyPlan { Note = reason };
	}

	/// <summary>
	/// Records <paramref name="text"/> as what the app has now been sent for <paramref name="path"/>.
	/// Called when the commands reached the provider, however they fared -- see the type's own remarks
	/// for why a partial failure still advances.
	/// </summary>
	public void Advance(string path, string text) => _applied[path] = text;

	/// <summary>Whether a baseline is held for <paramref name="path"/>.</summary>
	public bool Knows(string path) => _applied.ContainsKey(path);

	// The file name alone in prose. A note naming a full path twice reads as a diagnostic dump, and
	// the caller passed the path in, so it already knows which directory this is about.
	private static string Name(string path)
	{
		var slash = path.LastIndexOfAny(['\\', '/']);

		return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
	}
}
