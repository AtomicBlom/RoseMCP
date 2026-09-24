using System.Text.RegularExpressions;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// The wire contract between a live-app host and the XAML provider, which has to be the same contract
/// at both ends. Escaped on one side only, a tab in a value shifts every field after it and a newline
/// splits a command in two, and since a result is found again by the fields it was sent with, an edit
/// that landed reports that it never did.
/// <para>
/// The provider's half is C++ and has no tests of its own, and the live round trip that proves the two
/// agree needs a probe app, which a hosted runner does not have. So the last tests here read
/// <c>tap_channel.h</c> and hold its escape table and protocol version against these: on a runner, they
/// are the only thing that notices one side changing without the other.
/// </para>
/// </summary>
public sealed class XamlWireTests
{
	private const string ProviderChannel = "src/RoseMcp.Xaml.Tap/tap_channel.h";

	/// <summary>
	/// Every character the wire is delimited by, and the escape character itself, alone and together.
	/// An escaped field holds none of the delimiters, which is what lets a row be split on tabs and a
	/// message on newlines without a field being cut, and it comes back as it was.
	/// </summary>
	[Test]
	[Arguments("")]
	[Arguments("plain")]
	[Arguments("one\ttwo")]
	[Arguments("one\ntwo")]
	[Arguments("one\r\ntwo")]
	[Arguments(@"C:\path\to\file.xaml")]
	[Arguments(@"\t is not a tab")]
	[Arguments(@"ends in a backslash\")]
	[Arguments("\t\t\n\\\\")]
	[Arguments("all of them: \t \n \r \\ and after")]
	public void A_field_holds_no_delimiter_once_escaped_and_comes_back_as_it_was(string value)
	{
		var escaped = XamlWire.Escape(value);

		Assert.DoesNotContain('\t', escaped);
		Assert.DoesNotContain('\n', escaped);
		Assert.DoesNotContain('\r', escaped);
		Assert.Equal(value, XamlWire.Unescape(escaped));
	}

	/// <summary>
	/// The failure this contract exists to prevent, as a batch travels: commands joined into one message,
	/// the message split back into commands, and every field of every command intact -- a value holding a
	/// newline included, which is what split one command into two.
	/// </summary>
	[Test]
	public void A_batch_of_commands_holding_their_own_delimiters_comes_back_as_it_was()
	{
		string[][] commands =
		[
			["SetProperty", "#Slot7/TextBlock[0]", "Text", "String", "one\ttwo\nthree\\four", string.Empty, "0"],
			["AddChild", "#Slot7", string.Empty, string.Empty, string.Empty, "slot:1", "2"],
		];

		var message = string.Join('\n', commands.Select(fields => XamlWire.Row(fields)));
		var read = message.Split('\n').Select(XamlWire.Fields).ToArray();

		Assert.Equal(commands.Length, read.Length);
		for (var i = 0; i < commands.Length; i++)
		{
			Assert.Equal(commands[i], read[i]);
		}
	}

	/// <summary>
	/// A result is found again by a key made of the fields it was sent with, so two commands whose
	/// fields differ only in where a tab falls must not get one key -- or one's outcome replaces the
	/// other's.
	/// </summary>
	[Test]
	public void Two_rows_whose_fields_differ_only_in_where_a_tab_falls_are_different_rows()
	{
		Assert.NotEqual(XamlWire.Row("a\tb", "c"), XamlWire.Row("a", "b\tc"));
	}

	[Test]
	public void A_frame_carries_its_id_and_its_body_whole()
	{
		var frame = XamlWire.Frame(42, "apply\nSetProperty\tone\nAddChild\ttwo");

		Assert.True(XamlWire.TryReadFrame(frame, out var id, out var body));
		Assert.Equal(42u, id);
		Assert.Equal("apply\nSetProperty\tone\nAddChild\ttwo", body);
	}

	/// <summary>A reply with an empty body is still a reply to its request, which is how "nothing" is said.</summary>
	[Test]
	public void An_empty_body_keeps_its_id()
	{
		Assert.True(XamlWire.TryReadFrame(XamlWire.Frame(7, string.Empty), out var id, out var body));
		Assert.Equal(7u, id);
		Assert.Equal(string.Empty, body);
	}

	[Test]
	[Arguments("tree")]
	[Arguments("\ntree")]
	[Arguments("x\ntree")]
	[Arguments("-1\ntree")]
	[Arguments(" 1\ntree")]
	public void Something_that_was_not_made_as_a_frame_is_not_read_as_one(string frame)
	{
		Assert.False(XamlWire.TryReadFrame(frame, out _, out _));
	}

	[Test]
	public void The_greeting_a_provider_sends_is_accepted_with_the_key_its_session_issued()
	{
		Assert.Null(XamlWire.RefuseGreeting(XamlWire.Greeting("abc123"), "abc123"));
	}

	/// <summary>
	/// The mismatch this exists for. The host and the provider ship together, so the way to hold two
	/// versions apart is a stale copy, and the refusal has to say which copy is the old one.
	/// </summary>
	[Test]
	public void A_provider_that_greets_without_a_version_is_refused_as_older_than_the_host()
	{
		var refusal = XamlWire.RefuseGreeting(XamlWire.UnversionedGreeting, "abc123");

		Assert.NotNull(refusal);
		Assert.Contains("older than this host", refusal);
		Assert.Contains("stale", refusal);
	}

	[Test]
	public void A_provider_that_speaks_another_version_is_refused_naming_both()
	{
		var refusal = XamlWire.RefuseGreeting($"{XamlWire.GreetingPrefix}{XamlWire.ProtocolVersion + 1}/abc123", "abc123");

		Assert.NotNull(refusal);
		Assert.Contains($"protocol {XamlWire.ProtocolVersion + 1}", refusal);
		Assert.Contains($"speaks {XamlWire.ProtocolVersion}", refusal);
	}

	/// <summary>
	/// The pipe grants every packaged app on the machine, so whatever reaches it first greets first. A
	/// greeting in the right shape without this session's key is not the provider this session injected.
	/// </summary>
	[Test]
	public void A_provider_that_presents_another_key_is_refused()
	{
		var refusal = XamlWire.RefuseGreeting(XamlWire.Greeting("someone-else"), "abc123");

		Assert.NotNull(refusal);
		Assert.Contains("key", refusal);
	}

	[Test]
	public void A_provider_given_no_key_is_refused_rather_than_accepted()
	{
		Assert.NotNull(XamlWire.RefuseGreeting(XamlWire.Greeting(string.Empty), "abc123"));
	}

	/// <summary>Whatever reached the pipe first can send anything, so a refusal quotes only the start of it.</summary>
	[Test]
	public void Something_that_is_not_a_greeting_is_refused_and_quoted_short()
	{
		var refusal = XamlWire.RefuseGreeting(new string('x', 5000), "abc123");

		Assert.NotNull(refusal);
		Assert.True(refusal.Length < 200, $"expected a short refusal, got {refusal.Length} characters");
	}

	/// <summary>
	/// The provider greets with the protocol version it declares in <c>tap_channel.h</c>, and the host
	/// refuses any version but its own -- so the two constants have to move together, and this is what
	/// notices one of them moving alone.
	/// </summary>
	[Test]
	public void The_provider_declares_the_protocol_version_the_host_speaks()
	{
		var channel = ProviderSource();

		var declared = Regex.Match(channel, @"static constexpr int RoseTapProtocolVersion = (\d+);");
		Assert.True(declared.Success, $"{ProviderChannel} no longer declares RoseTapProtocolVersion where this looks for it.");
		Assert.Equal(XamlWire.ProtocolVersion, int.Parse(declared.Groups[1].Value));

		Assert.Contains($"\"{XamlWire.GreetingPrefix}\"", channel);
	}

	/// <summary>
	/// The two escape tables, compared. Each side escapes exactly these four characters as these four
	/// sequences, and the provider's reader undoes the three that are letters -- a backslash before
	/// anything else, the backslash included, standing for itself on both sides.
	/// </summary>
	[Test]
	public void The_provider_escapes_and_unescapes_the_same_characters_as_the_host()
	{
		var channel = ProviderSource();
		var escape = Body(channel, "static std::wstring Escape(");
		var unescape = Body(channel, "static std::wstring Unescape(");

		(char Raw, string Source, string Escaped)[] table =
		[
			('\t', @"case L'\t': result += L""\\t""; break;", @"\t"),
			('\r', @"case L'\r': result += L""\\r""; break;", @"\r"),
			('\n', @"case L'\n': result += L""\\n""; break;", @"\n"),
			('\\', @"case L'\\': result += L""\\\\""; break;", @"\\"),
		];

		foreach (var (raw, source, escaped) in table)
		{
			Assert.Equal(escaped, XamlWire.Escape(raw.ToString()));
			Assert.Contains(source, escape);
		}

		Assert.Equal(table.Length, Regex.Matches(escape, @"case L'").Count);

		foreach (var letter in "trn")
		{
			Assert.Contains($"case L'{letter}': result += L'\\{letter}'; break;", unescape);
		}

		Assert.Equal(3, Regex.Matches(unescape, @"case L'").Count);
	}

	/// <summary>A function's text, from its signature to the next top-level declaration.</summary>
	private static string Body(string source, string signature)
	{
		var start = source.IndexOf(signature, StringComparison.Ordinal);
		Assert.True(start >= 0, $"{ProviderChannel} no longer has '{signature}'.");

		var end = source.IndexOf("\nstatic ", start + signature.Length, StringComparison.Ordinal);
		return end < 0 ? source[start..] : source[start..end];
	}

	private static string ProviderSource() =>
		File.ReadAllText(Path.Combine(RepositoryRoot(), ProviderChannel.Replace('/', Path.DirectorySeparatorChar)));

	private static string RepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx"))) return directory.FullName;
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
