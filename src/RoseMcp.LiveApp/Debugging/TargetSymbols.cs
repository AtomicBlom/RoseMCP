using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// What the target's loaded modules say about themselves: which files they are, what methods they
/// declare, and where those methods came from in source.
/// <para>
/// The debuggee is asked exactly one question -- which modules are loaded -- and everything after
/// that is metadata and symbols read off disk. That is what lets a name search answer while the
/// target is running, which is what an autocomplete needs, and while the target is wedged, which is
/// when somebody most wants to set a breakpoint.
/// </para>
/// <para>
/// The split runs through this class. The registry below holds state and is reached only with the
/// session's gate held; the reading is static, takes the paths it was given, and must be called
/// without the gate, because it opens every loaded module on the machine and a session that blocked
/// for the length of that would stop answering anything else.
/// </para>
/// </summary>
internal sealed class TargetSymbols(ILogger logger)
{
	private readonly List<string> _paths = [];

	/// <summary>
	/// Whether the modules already loaded when the session attached have been walked. The load
	/// callback covers everything after the attach; a process that was already running needs the one
	/// walk, and it is taken lazily so a session nobody searches never pays for it.
	/// </summary>
	internal bool Walked { get; private set; }

	/// <summary>The module files known so far, without going and looking for more.</summary>
	internal IReadOnlyList<string> Paths => [.. _paths];

	/// <summary>
	/// Notes a module's file, so its metadata and symbols can be read without touching the target
	/// again. A dynamic or in-memory module is skipped: there is no file to read it out of.
	/// </summary>
	internal void Remember(CorDebugModule module)
	{
		if (FileOf(module) is not { } path) return;
		if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) _paths.Add(path);
	}

	/// <summary>
	/// Records that something else has already walked every loaded module, so this need not walk them
	/// again. The bind path takes that walk for its own reasons, and a second one would stop the
	/// target twice to learn what is already known.
	/// </summary>
	internal void MarkWalked() => Walked = true;

	/// <summary>
	/// Walks the target's modules once, async-breaking it to a synchronized state to do so.
	/// <para>
	/// The stop and the continue are a pair, which is what makes this safe to call while the target
	/// is held at a breakpoint: the stop count goes up and back down and the target stays exactly as
	/// stopped as it was.
	/// </para>
	/// </summary>
	internal void Walk(CorDebugProcess process)
	{
		var stopped = false;
		try
		{
			process.Stop(0);
			stopped = true;

			foreach (var module in EnumerateModules(process))
			{
				Remember(module);
			}

			Walked = true;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Enumerating the target's loaded modules failed.");
		}
		finally
		{
			if (stopped)
			{
				try
				{
					process.Continue(fIsOutOfBand: false);
				}
				catch (Exception exception)
				{
					logger.LogDebug(exception, "Continue after enumerating modules failed.");
				}
			}
		}
	}

	/// <summary>
	/// Finds methods by name across <paramref name="paths"/>, best first, for choosing somewhere to
	/// put a breakpoint without an IDE to browse.
	/// </summary>
	internal static LiveMethodMatches Search(IReadOnlyList<string> paths, string? query, int limit)
	{
		var typed = query?.Trim() ?? string.Empty;
		var found = MethodSearch.Search(paths, typed, limit);

		return new LiveMethodMatches
		{
			Query = typed,
			Matches = [.. found.Matches.Select(DescribeMatch)],
			Total = found.Total,
			ModulesSearched = found.ModulesSearched,
			Detail = SearchDetail(typed, paths.Count, found),
		};
	}

	/// <summary>
	/// A method's source and every position inside it a breakpoint can be set at.
	/// <para>
	/// The positions cover the lambdas, local functions and state machines written inside the method
	/// as well as the method itself, because that is where the instructions for those lines actually
	/// live. Each carries the location that breaks there, so picking a line inside a lambda produces
	/// a breakpoint in the lambda without anybody having to know its name.
	/// </para>
	/// <para>
	/// Every way this can come up short -- no such module, no such method, no symbols, symbols from
	/// another build, a source file this machine never had -- is a sentence and an empty listing
	/// rather than a refusal, because a breakpoint at the method's first instruction is still
	/// available in all of them.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The location does not parse.</exception>
	internal static LiveMethodSource ReadSource(IReadOnlyList<string> loaded, string location)
	{
		var parsed = SymbolLocation.Parse(location);
		var displayName = MethodDisplayName.Of(parsed.TypeName, parsed.MethodName);
		var owners = TypeOwners.Of(loaded, parsed.TypeName, parsed.Assembly);

		if (WhyNoModule(parsed, location, loaded, owners) is { } unresolved)
		{
			return NoMethodSource(location, displayName, parsed.Assembly ?? string.Empty, LiveSymbolState.NoSymbols, unresolved);
		}

		var modulePath = owners[0];
		var module = Path.GetFileNameWithoutExtension(modulePath);

		var symbols = SymbolCache.Shared.For(modulePath);
		var token = MethodTokens.Find(modulePath, parsed.TypeName, parsed.MethodName);
		if (symbols is null || token is null)
		{
			return NoMethodSource(
				location,
				displayName,
				module,
				LiveSymbolState.NoSymbols,
				$"{Path.GetFileName(modulePath)} declares no method {parsed.TypeName}.{parsed.MethodName}.");
		}

		if (symbols.Pdb is null)
		{
			var state = symbols.PdbState == PdbState.Mismatched
				? LiveSymbolState.SymbolsMismatched
				: LiveSymbolState.NoSymbols;

			return NoMethodSource(
				location,
				displayName,
				module,
				state,
				(symbols.PdbProblem ?? $"{Path.GetFileName(modulePath)} has no symbols on this machine.")
					+ " A breakpoint on this method still stops at its first instruction.");
		}

		var region = MethodRegion.Of(symbols.Pdb.Extents(), token.Value);
		if (MethodRegion.Lines(region) is not { } span)
		{
			return NoMethodSource(
				location,
				displayName,
				module,
				LiveSymbolState.NoSequencePoint,
				"The symbols record no source for this method, which is what an abstract, external or "
					+ "generated one looks like. A breakpoint on it still stops at its first instruction.");
		}

		var excerpt = SourceLines.Read(span.File, span.FirstLine, span.LastLine);

		return new LiveMethodSource
		{
			Location = location,
			DisplayName = displayName,
			Module = module,
			Symbols = LiveSymbolState.Resolved,
			File = span.File,
			FirstLine = excerpt.FirstLine,
			Lines = excerpt.Lines,
			Positions = PositionsIn(region, modulePath, module, span.File),
			Detail = excerpt.HasText
				? excerpt.Problem
				: $"{excerpt.Problem} The positions below are still exact; only the text is missing.",
		};
	}

	/// <summary>Where an instruction came from in source, or null when the symbols cannot say.</summary>
	internal static LiveSourcePosition? SourceAt(string modulePath, int methodToken, int ilOffset)
	{
		if (SymbolCache.Shared.For(modulePath)?.Pdb?.Position(methodToken, ilOffset) is not { } at) return null;

		return new LiveSourcePosition
		{
			File = at.File,
			Line = at.Line,
			Column = at.Column,
			EndLine = at.EndLine,
			EndColumn = at.EndColumn,
		};
	}

	/// <summary>
	/// A module's file, or null where there is none to read: a dynamic or in-memory module, or one that
	/// cannot describe itself.
	/// </summary>
	internal static string? FileOf(CorDebugModule module)
	{
		try
		{
			if (module.IsDynamic || module.IsInMemory) return null;

			return string.IsNullOrWhiteSpace(module.Name) ? null : module.Name;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>Every module the target has loaded, across its app domains and assemblies.</summary>
	internal static IEnumerable<CorDebugModule> EnumerateModules(CorDebugProcess process)
	{
		foreach (var appDomain in process.AppDomains)
		{
			foreach (var assembly in appDomain.Assemblies)
			{
				foreach (var module in assembly.Modules)
				{
					yield return module;
				}
			}
		}
	}

	/// <summary>
	/// Why a location cannot be read from one module, or null when it can and the first owner is that
	/// module. A stated assembly takes the first module of its name, as binding does; a location with none
	/// is refused when several modules declare the type rather than read from whichever came first.
	/// </summary>
	private static string? WhyNoModule(
		SymbolLocation parsed,
		string location,
		IReadOnlyList<string> loaded,
		IReadOnlyList<string> owners)
	{
		var stated = parsed.Assembly;

		if (owners.Count == 1) return null;
		if (owners.Count > 1 && stated is not null) return null;
		if (owners.Count > 1) return TypeOwners.Ambiguity(parsed.TypeName, owners, location) + ".";
		if (stated is null) return $"No loaded module declares {parsed.TypeName}.";

		return loaded.Any(path => TypeOwners.Admits(path, stated))
			? $"{stated} declares no type {parsed.TypeName}."
			: $"No loaded module is named {stated}.";
	}

	private static LiveMethodMatch DescribeMatch(MethodCandidate candidate) => new()
	{
		Location = candidate.Location,
		DisplayName = candidate.DisplayName,
		Signature = candidate.Signature,
		Module = candidate.Module,
		HasSymbols = candidate.HasSymbols,
	};

	/// <summary>
	/// What a reader needs to know about a search that the list of matches does not say. Null when
	/// the answer speaks for itself, since a caption that is always there is one nobody reads.
	/// </summary>
	private static string? SearchDetail(string query, int modules, MethodSearchResult found)
	{
		if (!MethodQuery.IsWorthSearching(query))
		{
			return $"Type at least {MethodQuery.ShortestQuery} characters. A shorter query matches most "
				+ "of a framework, and reading every loaded module to say so is the cost of the answer.";
		}

		if (modules == 0)
		{
			return "No modules are known yet. The target reports them as it loads them, so this fills in "
				+ "once it is running.";
		}

		return found.ModulesUnreadable > 0
			? $"{found.ModulesUnreadable} of {modules} loaded modules were not searched: a native library "
				+ "or one built in memory has no metadata on disk to read."
			: null;
	}

	/// <summary>
	/// Every place in a region where execution can stop, in source order, each naming the method its
	/// instructions belong to rather than the one somebody was reading.
	/// </summary>
	private static IReadOnlyList<LiveMethodPosition> PositionsIn(
		IReadOnlyList<MethodExtent> region,
		string modulePath,
		string module,
		string file)
	{
		var positions = new List<LiveMethodPosition>();

		foreach (var extent in region)
		{
			var parts = MethodTokens.MethodParts(modulePath, extent.MethodToken);
			var owner = parts is { } named ? $"{module}!{named.TypeName}.{named.MethodName}" : null;
			var label = parts is { } shown ? MethodDisplayName.Of(shown.TypeName, shown.MethodName) : null;

			// A method whose metadata will not name it cannot be addressed, so its lines are not
			// offered. Offering a position nothing can be set at is worse than leaving the line plain.
			if (owner is null || label is null) continue;

			foreach (var point in extent.Points)
			{
				if (point.Position is not { } at) continue;
				if (!string.Equals(at.File, file, StringComparison.OrdinalIgnoreCase)) continue;

				positions.Add(new LiveMethodPosition
				{
					Location = $"{owner}@IL_{point.Offset:X4}",
					DisplayName = label,
					IlOffset = point.Offset,
					Line = at.Line,
					Column = at.Column,
					EndLine = at.EndLine,
					EndColumn = at.EndColumn,
				});
			}
		}

		return [.. positions.OrderBy(position => position.Line).ThenBy(position => position.Column)];
	}

	/// <summary>A method that cannot be shown, saying why. Its first instruction is still breakable at.</summary>
	private static LiveMethodSource NoMethodSource(
		string location,
		string displayName,
		string module,
		LiveSymbolState symbols,
		string detail) => new()
		{
			Location = location,
			DisplayName = displayName,
			Module = module,
			Symbols = symbols,
			FirstLine = 0,
			Lines = [],
			Positions = [],
			Detail = detail,
		};
}
