using System.Globalization;
using System.Text;

namespace RoseMcp.Contracts;

/// <summary>
/// The format of every message between a live-app host and the XAML provider injected into its
/// target, in both directions: how a field is escaped, how fields make a row, how a reply is matched
/// to the request it answers, and what a provider says when it connects.
/// <para>
/// One contract rather than one per direction, because the protocol is delimited by tabs and
/// newlines and a property value can hold either. Escaped on the way out and unescaped on the way in
/// by one side only, a tab in a value shifts every field after it and a newline splits one command
/// into two -- and since a result is found again by the fields it was sent with, an edit that landed
/// then reports that it was never applied.
/// </para>
/// <para>
/// A reply carries the id of the request it answers, because a pipe matches replies to requests by
/// position only while nothing times out. The provider serves on the app's UI thread, which nothing on
/// this side can cancel, so a request the host has given up on is still answered -- later, and ahead of
/// the reply to whatever the host asked next.
/// </para>
/// <para>
/// Here rather than beside the host because there it would be internal to <c>RoseMcp.LiveApp</c>,
/// where no test reaches it: the unit suite does not reference the host at all, and the one test
/// project that does, <c>RoseMcp.IntegrationTests.Windows</c>, sees only its public surface and runs
/// only on Windows. The provider implements the same contract in <c>tap_channel.h</c>, and
/// <see cref="ProtocolVersion"/> is what says the two are the same one.
/// </para>
/// </summary>
public static class XamlWire
{
	/// <summary>
	/// The version of this format the host speaks, which a provider has to greet with. Raised whenever
	/// either end changes what it writes or how it reads. The host and the provider ship together, so
	/// the only way to hold two versions apart is a stale copy -- one left in a sandbox folder, or copied
	/// over by hand in a rebuild loop -- and that is exactly when a mismatch should be refused by name
	/// rather than read as data.
	/// </summary>
	public const int ProtocolVersion = 2;

	/// <summary>How every greeting starts, so one that does not can be told from one with the wrong version.</summary>
	public const string GreetingPrefix = "RoseTap/";

	/// <summary>
	/// What a provider older than <see cref="ProtocolVersion"/> greets with. Named, because it is the
	/// mismatch this refusal exists for, and a refusal that says the provider is older than the host
	/// tells the reader which copy to replace.
	/// </summary>
	public const string UnversionedGreeting = "hello from the provider";

	/// <summary>The greeting a provider sends: the protocol it speaks, and the key its session issued.</summary>
	/// <param name="nonce">The key the host handed the provider in its initialisation data.</param>
	public static string Greeting(string nonce) =>
		GreetingPrefix + ProtocolVersion.ToString(CultureInfo.InvariantCulture) + "/" + nonce;

	/// <summary>
	/// Why a provider's greeting is refused, or null when it speaks this protocol and presents the key
	/// the session issued -- which is what proves it is the provider this session injected rather than
	/// another packaged app that reached the pipe first.
	/// </summary>
	/// <param name="greeting">The first frame the provider sent.</param>
	/// <param name="nonce">The key this session handed the provider it injected.</param>
	public static string? RefuseGreeting(string greeting, string nonce)
	{
		if (greeting == UnversionedGreeting)
		{
			return "It greeted without naming a protocol version, which is how a provider older than this "
				+ $"host greets, and this host speaks protocol {ProtocolVersion}. Its copy is stale.";
		}

		if (!greeting.StartsWith(GreetingPrefix, StringComparison.Ordinal))
		{
			return $"It greeted with '{Shortened(greeting)}', which is not a provider's greeting.";
		}

		var rest = greeting[GreetingPrefix.Length..];
		var slash = rest.IndexOf('/', StringComparison.Ordinal);
		var versionText = slash < 0 ? rest : rest[..slash];

		if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
		{
			return $"It greeted with '{Shortened(greeting)}', which names no protocol version.";
		}

		if (version != ProtocolVersion)
		{
			return $"It speaks protocol {version} and this host speaks {ProtocolVersion}, so the two are from "
				+ "different builds and one of them is a stale copy.";
		}

		var presented = slash < 0 ? string.Empty : rest[(slash + 1)..];
		if (!string.Equals(presented, nonce, StringComparison.Ordinal))
		{
			return "It did not present the key this session gave the provider it injected, so it is not that "
				+ "provider: something else connected to the pipe first.";
		}

		return null;
	}

	/// <summary>
	/// A field made safe to sit in a row: every tab, carriage return, newline and backslash written as a
	/// backslash and a letter, so the only raw tabs in a row separate its fields and the only raw
	/// newlines in a message separate its rows.
	/// </summary>
	public static string Escape(string field)
	{
		if (field.AsSpan().IndexOfAny("\t\r\n\\") < 0) return field;

		var builder = new StringBuilder(field.Length + 8);
		foreach (var character in field)
		{
			builder.Append(character switch
			{
				'\t' => @"\t",
				'\r' => @"\r",
				'\n' => @"\n",
				'\\' => @"\\",
				_ => character.ToString(),
			});
		}

		return builder.ToString();
	}

	/// <summary>
	/// A field as it was before <see cref="Escape"/>. A backslash before any other character stands for
	/// that character, and one at the very end is kept, so a field that was never escaped comes through
	/// unless it holds a backslash.
	/// </summary>
	public static string Unescape(string field)
	{
		if (field.IndexOf('\\', StringComparison.Ordinal) < 0) return field;

		var builder = new StringBuilder(field.Length);
		for (var i = 0; i < field.Length; i++)
		{
			if (field[i] == '\\' && i + 1 < field.Length)
			{
				var next = field[++i];
				builder.Append(next switch
				{
					't' => '\t',
					'r' => '\r',
					'n' => '\n',
					_ => next,
				});
			}
			else
			{
				builder.Append(field[i]);
			}
		}

		return builder.ToString();
	}

	/// <summary>Fields escaped and joined into one row, which is how every command and every result travels.</summary>
	public static string Row(params string[] fields) => string.Join('\t', fields.Select(Escape));

	/// <summary>A row taken apart into the fields it was made from.</summary>
	public static string[] Fields(string row) => [.. row.Split('\t').Select(Unescape)];

	/// <summary>
	/// A message body carrying the id of the request it is, or the one it answers: the id, a newline,
	/// then the body. In the body rather than beside the length, so that a provider older than this
	/// format still delivers a greeting the host can read and refuse by name, where a longer header
	/// would misread it by four bytes and wait for the rest of a frame that never comes.
	/// </summary>
	public static string Frame(uint id, string body) => id.ToString(CultureInfo.InvariantCulture) + "\n" + body;

	/// <summary>The id and body of a frame made by <see cref="Frame"/>, or false when it was not made that way.</summary>
	public static bool TryReadFrame(string frame, out uint id, out string body)
	{
		var newline = frame.IndexOf('\n', StringComparison.Ordinal);
		if (newline > 0 && uint.TryParse(frame.AsSpan(0, newline), NumberStyles.None, CultureInfo.InvariantCulture, out id))
		{
			body = frame[(newline + 1)..];
			return true;
		}

		id = 0;
		body = string.Empty;
		return false;
	}

	/// <summary>A greeting short enough to quote, since whatever reached the pipe first can send anything.</summary>
	private static string Shortened(string greeting) => greeting.Length <= 60 ? greeting : greeting[..60] + "...";
}
