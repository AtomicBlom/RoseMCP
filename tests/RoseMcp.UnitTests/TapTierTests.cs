using System.Text;
using System.Text.RegularExpressions;

namespace RoseMcp.UnitTests;

/// <summary>
/// The tap's tiers, checked over the headers rather than described: which header may name what, which
/// may include which, and the order each provider includes them in. See docs/invariants/tap-tiers.md
/// for why the tiers exist.
/// <para>
/// The provider builds enforce part of this already, because a tier-2 header naming a projection fails
/// to compile above the alias block. They do not enforce all of it: the WinUI provider includes xamlOM
/// before the channel, so a tier-1 header naming xamlOM compiles there, and an include moved below the
/// aliases compiles everywhere while quietly returning a header to per-framework code. Neither build
/// runs where the C++ toolset is missing, and this does, on Linux too.
/// </para>
/// <para>
/// Read as text, with comments and string literals blanked first, since the headers explain themselves
/// at length in exactly the vocabulary this looks for.
/// </para>
/// </summary>
public sealed partial class TapTierTests
{
	/// <summary>
	/// Every header under src/RoseMcp.Xaml.Tap and its tier, which is the one place a file's tier is
	/// written down. A header is in the lowest tier whose rule allows everything it names.
	/// </summary>
	private static readonly Dictionary<string, int> Tiers = new(StringComparer.Ordinal)
	{
		// Names nothing external.
		["tap_channel.h"] = 1,
		["tap_measure.h"] = 1,

		// Names only xamlOM, which both frameworks declare identically, so it compiles once.
		["tap_diagnostics.h"] = 2,
		["tap_surface.h"] = 2,
		["tap_tree.h"] = 2,
		["tap_properties.h"] = 2,
		["tap_edits.h"] = 2,
		["tap_object.h"] = 2,

		// Names the projection aliases, so it is compiled once per framework.
		["tap_render.h"] = 3,
		["tap_widgets.h"] = 3,
		["tap_tool_zoom.h"] = 3,
		["tap_pick.h"] = 3,
		["tap_tool_rulers.h"] = 3,
		["tap_overlay.h"] = 3,
	};

	/// <summary>The highest tier that may not name a projection.</summary>
	private const int Unprojected = 2;

	/// <summary>
	/// What xamlOM.h declares. A tier-1 header naming any of them depends on the diagnostics ABI, which is
	/// tier 2's to name.
	/// </summary>
	private static readonly HashSet<string> XamlOm = new(StringComparer.Ordinal)
	{
		"IXamlDiagnostics", "IVisualTreeService", "IVisualTreeService2", "IVisualTreeService3",
		"IVisualTreeServiceCallback", "IVisualTreeServiceCallback2", "IBitmapData", "InstanceHandle",
		"VisualElement", "VisualElementState", "VisualMutationType", "ParentChildRelation", "PropertyChainSource",
		"PropertyChainValue", "CollectionElementValue", "BaseValueSource", "MetadataBit", "SourceInfo", "EnumType",
		"ResourceType", "BitmapDescription", "RenderTargetBitmapOptions",
	};

	[Test]
	public void Every_header_has_a_tier()
	{
		var headers = Directory.EnumerateFiles(TapFolder, "*.h").Select(path => Path.GetFileName(path)!).ToList();

		headers.Except(Tiers.Keys).ShouldBeEmpty(
			"A header under src/RoseMcp.Xaml.Tap has no tier. Add it to TapTierTests.Tiers, in the lowest tier whose rule allows what it names (docs/invariants/tap-tiers.md).");
		Tiers.Keys.Except(headers).ShouldBeEmpty("TapTierTests.Tiers names a header that does not exist.");
	}

	[Test]
	public void A_header_includes_only_its_own_tier_or_below()
	{
		var violations = Headers()
			.SelectMany(header => header.Source.Includes
				.Where(include => include.Quoted)
				.Select(include => (Header: header, Include: include, Tier: Tiers.GetValueOrDefault(Path.GetFileName(include.Target), 0)))
				.Where(edge => edge.Tier == 0 || edge.Tier > edge.Header.Tier)
				.Select(edge => edge.Tier == 0
					? $"{edge.Header.Name}:{edge.Include.Line} (tier {edge.Header.Tier}) includes \"{edge.Include.Target}\", which is not a tap header"
					: $"{edge.Header.Name}:{edge.Include.Line} (tier {edge.Header.Tier}) includes {Path.GetFileName(edge.Include.Target)} (tier {edge.Tier})"))
			.ToList();

		violations.ShouldBeEmpty(
			"A tap header may include only headers of its own tier or below. The repair is a seam in tap_surface.h with its body in tap_render.h, never a header moved up a tier (docs/invariants/tap-tiers.md):"
			+ Environment.NewLine + string.Join(Environment.NewLine, violations));
	}

	[Test]
	public void Nothing_below_tier_three_names_a_projection()
	{
		var aliases = Providers().SelectMany(provider => provider.Aliases).ToHashSet(StringComparer.Ordinal);
		var violations = Headers()
			.Where(header => header.Tier <= Unprojected)
			.SelectMany(header => ProjectionNames(header.Source, aliases).Select(found => $"{header.Name}:{found.Line} (tier {header.Tier}) names {found.What}"))
			.ToList();

		violations.ShouldBeEmpty(
			"Tiers 1 and 2 compile once for both frameworks, so they name no projection: no winrt::, no Windows::UI::Xaml or Microsoft::UI::Xaml, and none of the aliases "
			+ string.Join(", ", aliases.Order()) + ". A handle, a string, a bool or an ::IInspectable* crosses the boundary instead (docs/invariants/tap-tiers.md):"
			+ Environment.NewLine + string.Join(Environment.NewLine, violations));
	}

	[Test]
	public void Tier_one_names_nothing_from_xamlom()
	{
		var violations = Headers()
			.Where(header => header.Tier == 1)
			.SelectMany(header => XamlOmNames(header.Source).Select(found => $"{header.Name}:{found.Line} names {found.What}"))
			.ToList();

		violations.ShouldBeEmpty(
			"Tier 1 names nothing external, xamlOM included; a header that needs the diagnostics ABI is tier 2 (docs/invariants/tap-tiers.md):"
			+ Environment.NewLine + string.Join(Environment.NewLine, violations));
	}

	/// <summary>
	/// The order is the compile-time half of the check: a header above the alias block that names a
	/// projection fails to build. Moving one below the block silences that and gives the boundary up,
	/// so a tier-2 header below the aliases fails here instead.
	/// </summary>
	[Test]
	public void Each_provider_includes_the_tiers_in_order_and_the_unprojected_ones_above_the_aliases()
	{
		var providers = Providers();
		providers.Count.ShouldBe(2, "One provider per XAML framework, each in src/RoseMcp.Xaml.<framework>.Tap.");

		foreach (var provider in providers)
		{
			provider.Aliases.ShouldNotBeEmpty($"{provider.Name} defines no projection aliases.");

			var projected = Math.Min(provider.FirstAliasLine, provider.FirstProjectionIncludeLine);
			var taps = provider.Source.Includes
				.Where(include => include.Quoted && Tiers.ContainsKey(Path.GetFileName(include.Target)))
				.Select(include => (include.Line, Name: Path.GetFileName(include.Target), Tier: Tiers[Path.GetFileName(include.Target)]))
				.ToList();
			var violations = taps
				.Where(tap => tap.Tier <= Unprojected && tap.Line > projected)
				.Select(tap => $"{provider.Name}:{tap.Line} includes {tap.Name} (tier {tap.Tier}) below the projections, which begin at line {projected}")
				.Concat(taps
					.Where(tap => tap.Tier > Unprojected && tap.Line < provider.LastAliasLine)
					.Select(tap => $"{provider.Name}:{tap.Line} includes {tap.Name} (tier {tap.Tier}) above the aliases it names, defined by line {provider.LastAliasLine}"))
				.Concat(taps
					.Zip(taps.Skip(1))
					.Where(pair => pair.Second.Tier < pair.First.Tier)
					.Select(pair => $"{provider.Name}:{pair.Second.Line} includes {pair.Second.Name} (tier {pair.Second.Tier}) after {pair.First.Name} (tier {pair.First.Tier})"))
				.ToList();

			violations.ShouldBeEmpty(
				"A provider includes the tap's tiers in ascending order, tiers 1 and 2 above its projections and aliases (docs/invariants/tap-tiers.md):"
				+ Environment.NewLine + string.Join(Environment.NewLine, violations));
		}

		providers[0].Aliases.Order().ShouldBe(providers[1].Aliases.Order(), "Both providers bind the same aliases, which is what lets one tier-3 source serve both.");
	}

	[Test]
	[Arguments("// winrt::hstring in a comment", false)]
	[Arguments("/* xaml::UIElement\nacross lines */", false)]
	[Arguments("const wchar_t* text = L\"winrt::hstring\";", false)]
	[Arguments("const std::wstring path = LR\"(\\\\.\\pipe\\)\" + name;", false)]
	[Arguments("const char quote = '\"'; int x = 0;", false)]
	[Arguments("int million = 1'000'000; winrt::hstring name;", true)]
	[Arguments("xaml :: UIElement element{ nullptr };", true)]
	[Arguments("ABI::Windows::UI::Xaml::IUIElement* element;", true)]
	[Arguments("#include <winrt/Windows.UI.Xaml.h>", true)]
	public void Reads_code_not_comments_or_strings(string code, bool projected)
	{
		ProjectionNames(new CppSource(code), new HashSet<string>(StringComparer.Ordinal) { "xaml" }).Any().ShouldBe(projected);
	}

	private static string TapFolder => Checkout.RepositoryFile("src", "RoseMcp.Xaml.Tap");

	private static IEnumerable<(string Name, int Tier, CppSource Source)> Headers() =>
		Tiers.Select(tier => (tier.Key, tier.Value, new CppSource(File.ReadAllText(Path.Combine(TapFolder, tier.Key)))));

	private static List<Provider> Providers() =>
		[.. Directory.EnumerateDirectories(Checkout.RepositoryFile("src"), "RoseMcp.Xaml.*.Tap")
			.SelectMany(folder => Directory.EnumerateFiles(folder, "*.cpp"))
			.Order(StringComparer.Ordinal)
			.Select(path => new Provider(Path.GetFileName(path), new CppSource(File.ReadAllText(path))))];

	private static IEnumerable<(int Line, string What)> ProjectionNames(CppSource source, IReadOnlySet<string> aliases)
	{
		foreach (var include in source.Includes.Where(include => !include.Quoted && include.Target.StartsWith("winrt/", StringComparison.Ordinal)))
		{
			yield return (include.Line, $"<{include.Target}>");
		}

		foreach (Match match in ProjectionPattern().Matches(source.Code))
		{
			var name = match.Groups["name"].Value;
			if (name == "winrt" || name == "Xaml" || aliases.Contains(name)) yield return (source.LineOf(match.Index), Regex.Replace(match.Value, @"\s+", string.Empty));
		}
	}

	private static IEnumerable<(int Line, string What)> XamlOmNames(CppSource source)
	{
		foreach (var include in source.Includes.Where(include => include.Target.Equals("xamlOM.h", StringComparison.OrdinalIgnoreCase)))
		{
			yield return (include.Line, include.Target);
		}

		foreach (Match match in IdentifierPattern().Matches(source.Code))
		{
			if (XamlOm.Contains(match.Value)) yield return (source.LineOf(match.Index), match.Value);
		}
	}

	/// <summary>
	/// <c>winrt</c> anywhere, a name followed by <c>::</c> (checked against the aliases by the caller), and
	/// <c>UI::Xaml</c>, which is both frameworks' projected namespace and the ABI one.
	/// </summary>
	[GeneratedRegex(@"\b(?<name>winrt)\b|\bUI\s*::\s*(?<name>Xaml)\b|\b(?<name>[A-Za-z_]\w*)\s*::")]
	private static partial Regex ProjectionPattern();

	[GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
	private static partial Regex IdentifierPattern();

	[GeneratedRegex(@"^[ \t]*#[ \t]*include[ \t]*(?:""(?<quoted>[^""]+)""|<(?<angled>[^>]+)>)", RegexOptions.Multiline)]
	private static partial Regex IncludePattern();

	[GeneratedRegex(@"^[ \t]*namespace[ \t]+(?<alias>\w+)[ \t]*=[ \t]*winrt[ \t]*::", RegexOptions.Multiline)]
	private static partial Regex AliasPattern();

	private sealed record Include(int Line, string Target, bool Quoted);

	/// <summary>A provider's own source: where its aliases are defined and where its projections start.</summary>
	private sealed record Provider(string Name, CppSource Source)
	{
		private IReadOnlyList<Match> AliasDefinitions => AliasPattern().Matches(Source.Code).ToList();

		public IReadOnlyList<string> Aliases => [.. AliasDefinitions.Select(match => match.Groups["alias"].Value)];

		public int FirstAliasLine => AliasDefinitions.Select(match => Source.LineOf(match.Index)).DefaultIfEmpty(int.MaxValue).Min();

		public int LastAliasLine => AliasDefinitions.Select(match => Source.LineOf(match.Index)).DefaultIfEmpty(0).Max();

		public int FirstProjectionIncludeLine =>
			Source.Includes.Where(include => !include.Quoted && include.Target.StartsWith("winrt/", StringComparison.Ordinal)).Select(include => include.Line).DefaultIfEmpty(int.MaxValue).Min();
	}

	/// <summary>
	/// A C++ file with its comments and literals blanked to spaces, newlines kept so an offset still has
	/// its line. Includes are read before the blanking, since a quoted include is a string literal.
	/// </summary>
	private sealed class CppSource
	{
		private readonly int[] _lineStarts;

		public CppSource(string text)
		{
			var withoutComments = Blank(text, literals: false);
			Code = Blank(text, literals: true);
			Includes = [.. IncludePattern().Matches(withoutComments).Select(match => new Include(
				LineOf(text, match.Index),
				match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["angled"].Value,
				match.Groups["quoted"].Success))];
			_lineStarts = [0, .. text.Select((character, index) => (character, index)).Where(pair => pair.character == '\n').Select(pair => pair.index + 1)];
		}

		/// <summary>The code with every comment and every string and character literal blanked.</summary>
		public string Code { get; }

		public IReadOnlyList<Include> Includes { get; }

		public int LineOf(int offset)
		{
			var index = Array.BinarySearch(_lineStarts, offset);
			return (index >= 0 ? index : ~index - 1) + 1;
		}

		private static int LineOf(string text, int offset) => text.AsSpan(0, offset).Count('\n') + 1;

		/// <summary>
		/// Comments, and literals too when asked, replaced by spaces. Handles raw strings, whose bodies
		/// may hold an unescaped quote, and digit separators, which are not the start of a character.
		/// </summary>
		private static string Blank(string text, bool literals)
		{
			var result = new StringBuilder(text);
			var i = 0;

			while (i < text.Length)
			{
				var start = i;
				var isLineComment = text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/';
				var isBlockComment = text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*';
				var isDigitSeparator = text[i] == '\'' && i > 0 && char.IsAsciiDigit(text[i - 1]);

				if (isLineComment)
				{
					i = text.IndexOf('\n', i) is var end and >= 0 ? end : text.Length;
					Erase(result, start, i);
				}
				else if (isBlockComment)
				{
					i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length;
					Erase(result, start, i);
				}
				else if (text[i] == '"' && IsRawStringStart(text, i))
				{
					var open = text.IndexOf('(', i);
					var close = ")" + text[(i + 1)..open] + "\"";
					i = text.IndexOf(close, open, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length;
					if (literals) Erase(result, start, i);
				}
				else if ((text[i] == '"' || text[i] == '\'') && !isDigitSeparator)
				{
					var quote = text[i++];
					while (i < text.Length && text[i] != quote && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
					i = Math.Min(i + 1, text.Length);
					if (literals) Erase(result, start, i);
				}
				else
				{
					i++;
				}
			}

			return result.ToString();
		}

		private static bool IsRawStringStart(string text, int quote)
		{
			var prefixStart = quote;
			while (prefixStart > 0 && (char.IsAsciiLetterOrDigit(text[prefixStart - 1]) || text[prefixStart - 1] == '_')) prefixStart--;

			return text[prefixStart..quote] is "R" or "LR" or "uR" or "UR" or "u8R";
		}

		private static void Erase(StringBuilder text, int start, int end)
		{
			for (var i = start; i < end; i++)
			{
				if (text[i] != '\n' && text[i] != '\r') text[i] = ' ';
			}
		}
	}
}
