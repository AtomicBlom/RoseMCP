using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// Which build of Rose the window is talking to, under the view list: its version and commit, when it was built
/// and from which checkout, and how far that checkout has moved on since -- the question a person running a local
/// build asks before anything else, which is whether what they see is the code they just wrote.
/// </summary>
/// <param name="Build">Version, commit, and when and where it was built.</param>
/// <param name="Checkout">How far the checkout has moved past the build, or why that cannot be said; empty for a build from no checkout.</param>
/// <param name="CheckoutIsAhead">Whether the checkout holds commits the running build does not, which is worth a deploy.</param>
/// <param name="Mismatch">That this window is another build from the broker, where it is.</param>
public sealed record BuildLine(string Build, string Checkout, bool CheckoutIsAhead, string Mismatch)
{
	/// <summary>What the line says before the broker has answered.</summary>
	public static BuildLine Unknown { get; } = new("Asking Rose which build it is…", string.Empty, false, string.Empty);

	public bool HasCheckout => Checkout.Length > 0;

	public bool HasMismatch => Mismatch.Length > 0;

	/// <summary>The line for the broker's hello, read at <paramref name="now"/>, from a window that is build <paramref name="own"/>.</summary>
	public static BuildLine Of(OperatorHello hello, BuildIdentity own, DateTimeOffset now)
	{
		var build = hello.Build;
		var facts = new List<string> { build.Version };

		if (build.ShortCommit() is { } commit) facts.Add(build.Dirty == true ? $"{commit} with uncommitted changes" : commit);
		if (build.BuiltUtc is { } built) facts.Add($"built {Ago(now - built)}");
		if (build.Checkout is { Length: > 0 } checkout) facts.Add($"from {checkout}");

		var (distance, ahead) = Distance(hello.Checkout);
		var mismatch = BuildIdentity.Mismatch("The broker", build, own) is { } said
			? $"{said} Restart RoseMCP from the tray, or open this window from the install the broker runs from."
			: string.Empty;

		return new BuildLine(string.Join(" ", facts), distance, ahead, mismatch);
	}

	private static (string Text, bool Ahead) Distance(CheckoutDistance? distance)
	{
		if (distance is null) return (string.Empty, false);

		var parts = new List<string>();
		var ahead = false;

		if (distance.Head.Count is { } head)
		{
			ahead = head > 0;
			parts.Add(head == 0 ? "the checkout is this build" : $"the checkout is {Format.Count(head, "commit")} ahead");
		}
		else if (distance.Head.Unknown is { Length: > 0 } why)
		{
			parts.Add($"the checkout's distance is unknown: {why}");
		}

		if (distance.OriginMain.Count is > 0 and var main) parts.Add($"{distance.OriginMain.Ref} is {Format.Count(main, "commit")} ahead");
		else if (distance.OriginMain.Count is null && distance.OriginMain.Unknown is { Length: > 0 } unknown) parts.Add($"{distance.OriginMain.Ref}: {unknown}");

		var checkedAt = distance.CheckedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
		var text = parts.Count == 0 ? string.Empty : $"{Capitalise(string.Join("; ", parts))} (checked {checkedAt} in {distance.Checkout})";

		return (text, ahead);
	}

	private static string Ago(TimeSpan age) => age.TotalMinutes < 1 ? "just now" : Format.Age(age);

	private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
