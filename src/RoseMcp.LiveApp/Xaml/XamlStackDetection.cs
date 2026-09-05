using RoseMcp.Contracts;

namespace RoseMcp.LiveApp.Xaml;

/// <summary>
/// Which XAML framework a process turned out to be hosting, and on what evidence.
/// <para>
/// Shaped like <c>XamlDialectChoice</c> on the workspace side deliberately: both answer the same
/// question from different sources, and both carry the evidence rather than only the verdict, so a
/// wrong answer can be seen to be wrong instead of merely being disbelieved.
/// </para>
/// </summary>
internal sealed record XamlStackDetection
{
	public required XamlStack Stack { get; init; }

	/// <summary>
	/// The loaded modules that decided it, named. Empty when the modules could not be read at all,
	/// which is not the same as reading them and recognising nothing -- the first is ignorance and
	/// the second is a finding.
	/// </summary>
	public required IReadOnlyList<string> Evidence { get; init; }

	/// <summary>How the answer was reached, as a sentence fit to appear in an error.</summary>
	public required string Reason { get; init; }
}
