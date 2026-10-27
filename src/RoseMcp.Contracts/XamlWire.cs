using System.Globalization;

using RoseMcp.Tap.Protocol;

namespace RoseMcp.Contracts;

/// <summary>
/// The host's reading of what a tap says: whether its greeting is accepted, what the in-box taps
/// declare, and the rows an apply and an Ask press come back as.
/// <para>
/// How a field is escaped, how fields make a row and how a reply is matched to its request belong to
/// the tap contract both sides compile, <see cref="TapWire"/>, because a tap built apart from RoseMCP
/// has to write them exactly as this host reads them. What is here is the host's side of it: the
/// sentences a refusal is said in, and the readers for replies only the host takes apart.
/// </para>
/// <para>
/// Here rather than beside the host because there it would be internal to <c>RoseMcp.LiveApp</c>,
/// where no test reaches it: the unit suite does not reference the host at all, and the one test
/// project that does, <c>RoseMcp.IntegrationTests.Windows</c>, sees only its public surface and runs
/// only on Windows. The in-box taps implement the same contract in <c>tap_channel.h</c>, and
/// <see cref="ProtocolVersion"/> and <see cref="InBoxCapabilityLine"/> are what say the two are the
/// same one.
/// </para>
/// </summary>
public static class XamlWire
{
	/// <summary>
	/// The version of the protocol this host speaks, and the only one an in-box tap may greet with: the
	/// tap contract's own. Raised in the contract whenever a row an existing command answers with changes
	/// shape.
	/// </summary>
	public const int ProtocolVersion = TapGreeting.ProtocolVersion;

	/// <summary>How every greeting starts, so one that does not can be told from one with the wrong version.</summary>
	public const string GreetingPrefix = TapGreeting.Prefix;

	/// <summary>
	/// What a provider from before greetings named a version greets with. Named, because it is the
	/// mismatch this refusal exists for, and a refusal that says the provider is older than the host
	/// tells the reader which copy to replace.
	/// </summary>
	public const string UnversionedGreeting = "hello from the provider";

	/// <summary>
	/// What the in-box XAML taps declare they serve: every command, every overlay mode and every edit
	/// kind. Written in <c>tap_channel.h</c> too, and held to this by a test, so a tap that gains or loses
	/// a capability says so in both places.
	/// </summary>
	public const string InBoxCapabilityLine =
		"apply:AddChild,ClearProperty,RemoveChild,SetProperty,SetResource ask changes deselect detach idle properties "
		+ "select:pick,rulers selecthandle selection tree";

	/// <summary>The greeting an in-box tap sends: the protocol it speaks, the key its session issued, and what it serves.</summary>
	/// <param name="nonce">The key the host handed the provider in its initialisation data.</param>
	public static string Greeting(string nonce) => TapGreeting.Write(nonce, TapCapabilities.Parse(InBoxCapabilityLine));

	/// <summary>
	/// Why a tap's greeting is refused, or null when it speaks a protocol in <paramref name="accepted"/>
	/// and presents the key the session issued -- which is what proves it is the tap this session loaded
	/// rather than another app that reached the pipe first. <paramref name="capabilities"/> is what an
	/// accepted tap declared it serves, and <see cref="TapCapabilities.None"/> for a refused one.
	/// </summary>
	/// <param name="greeting">The first frame the provider sent.</param>
	/// <param name="nonce">The key this session handed the provider it loaded.</param>
	/// <param name="accepted">The protocol versions this tap may speak: exactly the host's own for an in-box tap.</param>
	/// <param name="capabilities">What the tap declared, once accepted.</param>
	public static string? RefuseGreeting(string greeting, string nonce, TapProtocolRange accepted, out TapCapabilities capabilities)
	{
		capabilities = TapCapabilities.None;

		if (greeting == UnversionedGreeting)
		{
			return "It greeted without naming a protocol version, which is how a provider older than this "
				+ $"host greets, and this host speaks protocol {ProtocolVersion}. Its copy is stale.";
		}

		if (!greeting.StartsWith(GreetingPrefix, StringComparison.Ordinal))
		{
			return $"It greeted with '{Shortened(greeting)}', which is not a provider's greeting.";
		}

		if (!TapGreeting.TryRead(greeting, out var version, out var presented, out var declared))
		{
			return $"It greeted with '{Shortened(greeting)}', which names no protocol version.";
		}

		if (!accepted.Contains(version))
		{
			return accepted.Minimum == accepted.Maximum
				? $"It speaks protocol {version} and this host speaks {ProtocolVersion}, so the two are from "
					+ "different builds and one of them is a stale copy."
				: $"It speaks protocol {version}, and this host accepts {accepted} from a tap built apart from it. "
					+ (version < accepted.Minimum
						? "The tap was built against an older contract and has to be rebuilt against a newer one."
						: "The tap was built against a newer contract than this RoseMCP knows, so RoseMCP has to be updated.");
		}

		if (!string.Equals(presented, nonce, StringComparison.Ordinal))
		{
			return "It did not present the key this session gave the provider it injected, so it is not that "
				+ "provider: something else connected to the pipe first.";
		}

		capabilities = declared;
		return null;
	}

	/// <summary>
	/// One row of an apply's reply, or null when the line is not one: the command's op, target, property and arg,
	/// which are what the host finds its result again by, then its status, the handle it landed on and what the
	/// property said before it.
	/// <para>
	/// The handle and the old value were added after the first five fields, and are read only where they are
	/// there, so the five a result is keyed by mean the same thing whichever provider wrote the row. A handle of
	/// zero is the provider saying it reached no element. An old value is kept even when empty, because the empty
	/// string is a value a text property really had; whether an edit has an old value at all is decided by its
	/// kind, not by this.
	/// </para>
	/// </summary>
	public static XamlApplyRow? ReadApplyRow(string line)
	{
		if (line.Length == 0) return null;

		var fields = TapWire.Fields(line);
		if (fields.Length < 5) return null;

		ulong? handle = fields.Length > 5 && ulong.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var landed) && landed != 0
			? landed
			: null;

		return new XamlApplyRow(
			fields[0],
			fields[1],
			fields[2],
			fields[3],
			fields[4],
			handle,
			fields.Length > 6 ? fields[6] : null);
	}

	/// <summary>
	/// The press the toolbar's Ask button recorded, from the provider's reply to <c>ask</c>, or null when there is
	/// none -- the reply is <c>none</c>, or not a row this format writes. The sequence counts presses in the app's
	/// process, so a host tells a new press from the one it already holds by it; the last field is the press's age in
	/// milliseconds, and a row without one is not read, since a press of unknown age could be hours old.
	/// </summary>
	public static XamlAskRow? ReadAsk(string reply)
	{
		var line = reply.Split('\n')[0].TrimEnd('\r');
		var fields = TapWire.Fields(line);
		if (fields.Length < 8 || fields[0] != "ask") return null;
		if (!long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence <= 0) return null;
		if (!long.TryParse(fields[7], NumberStyles.None, CultureInfo.InvariantCulture, out var ageMilliseconds)) return null;

		return new XamlAskRow(
			sequence,
			fields[2],
			fields[3],
			HandleOrNull(fields[4]),
			HandleOrNull(fields[5]),
			fields[6].Length > 0 ? fields[6] : null,
			TimeSpan.FromMilliseconds(ageMilliseconds));
	}

	/// <summary>A handle field, where zero and anything unreadable both mean the press named no element.</summary>
	private static ulong? HandleOrNull(string field) =>
		ulong.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var handle) && handle != 0 ? handle : null;

	/// <summary>A greeting short enough to quote, since whatever reached the pipe first can send anything.</summary>
	private static string Shortened(string greeting) => greeting.Length <= 60 ? greeting : greeting[..60] + "...";
}
