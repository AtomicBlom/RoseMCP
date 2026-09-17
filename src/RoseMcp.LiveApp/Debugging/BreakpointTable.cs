using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// Every breakpoint and tracepoint a session has been asked for, and the work of getting them onto
/// the methods they name.
/// <para>
/// A location is accepted before anything can carry it, because the module that declares it may not
/// be loaded and refusing would mean a caller could only set a breakpoint on code the target had
/// already reached. So a binding is a request first and a runtime object second, and this is what
/// closes the gap: against the modules already loaded when it was added, and again on each module
/// that loads afterwards.
/// </para>
/// <para>
/// Every member assumes the caller holds the session's gate. Nothing here locks. The session takes
/// the gate, async-breaks the target and hands the modules over; a table that could be bound against
/// from outside that lock is one whose bindings could be activated on a process that has moved.
/// </para>
/// </summary>
internal sealed class BreakpointTable(DebugEventBuffer buffer, ILogger logger)
{
	private readonly List<BreakpointBinding> _bindings = [];
	private int _nextId = 1;

	/// <summary>Whether every binding has a runtime breakpoint, so there is nothing left to bind.</summary>
	internal bool AllBound => _bindings.TrueForAll(binding => binding.Bound);

	/// <summary>
	/// Records a request for a location. The binding comes back unbound; the caller binds it against
	/// what is loaded.
	/// </summary>
	/// <exception cref="ArgumentException">The location does not parse.</exception>
	internal BreakpointBinding Add(
		string location,
		bool stopOnHit,
		string? logMessage,
		int? logEveryNthHit,
		int? autoContinueSeconds,
		string? condition)
	{
		var binding = new BreakpointBinding
		{
			Id = $"{(stopOnHit ? "bp" : "tp")}-{_nextId++}",
			Location = SymbolLocation.Parse(location),
			Raw = location,
			StopOnHit = stopOnHit,
			LogMessage = logMessage,
			LogEveryNthHit = logEveryNthHit,
			AutoContinueSeconds = autoContinueSeconds,
			ConditionText = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(),
			Condition = BreakpointCondition.Parse(condition),
			Detail = "not bound yet",
		};

		_bindings.Add(binding);
		return binding;
	}

	/// <summary>Drops a binding and deactivates whatever it had bound, reporting whether it was there.</summary>
	internal bool Remove(string id)
	{
		var binding = _bindings.FirstOrDefault(entry => entry.Id == id);
		if (binding is null) return false;

		try
		{
			binding.Breakpoint?.Activate(false);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Deactivating binding {Id} failed.", id);
		}

		_bindings.Remove(binding);
		return true;
	}

	internal IReadOnlyList<LiveTracepoint> Tracepoints() =>
		[.. _bindings.Where(binding => !binding.StopOnHit).Select(DescribeTracepoint)];

	internal IReadOnlyList<LiveBreakpoint> Breakpoints() =>
		[.. _bindings.Where(binding => binding.StopOnHit).Select(DescribeBreakpoint)];

	/// <summary>
	/// Binds everything still unbound against a set of modules read in one walk.
	/// <para>
	/// Bound after the walk rather than during it, because whether a name without its assembly is
	/// ambiguous is a question about every module, not the one in hand.
	/// </para>
	/// </summary>
	internal void BindAmongLoaded(IReadOnlyList<CorDebugModule> loaded)
	{
		foreach (var binding in _bindings)
		{
			if (!binding.Bound) BindAmong(binding, loaded);
		}
	}

	/// <summary>
	/// Takes in a module as it loads and binds anything waiting for a module that declares what it
	/// names. Called from a stopped callback.
	/// <para>
	/// A location without its assembly is where a second declaration of its type first becomes
	/// knowable, since the other modules are the ones already loaded. An unbound one is refused and told
	/// which modules declare it. A bound one is left where it is and the event stream says so, because
	/// deactivating a breakpoint that a thread may be parked on fail-fasts the target.
	/// </para>
	/// </summary>
	internal void BindNewModule(CorDebugModule module, IReadOnlyList<string> knownPaths)
	{
		if (TargetSymbols.FileOf(module) is not { } path) return;

		foreach (var binding in _bindings)
		{
			if (binding.Location.Assembly is not null)
			{
				if (!binding.Bound) TryBind(binding, module);
				continue;
			}

			var typeName = binding.Location.TypeName;
			if (!MethodTokens.DeclaresType(path, typeName)) continue;

			if (binding.Bound)
			{
				var sameModule = string.Equals(binding.ModulePath, path, StringComparison.OrdinalIgnoreCase);
				if (sameModule) continue;

				buffer.Append(
					LiveDebugEventKind.SessionNotice,
					$"{binding.Id} is bound in {Path.GetFileName(binding.ModulePath)}, and {Path.GetFileName(path)} also "
						+ $"declares {typeName}; give the assembly, as {Path.GetFileNameWithoutExtension(path)}!{binding.Raw}, "
						+ "to break in that one instead.");
				continue;
			}

			var others = knownPaths
				.Where(other => !string.Equals(other, path, StringComparison.OrdinalIgnoreCase))
				.Where(other => MethodTokens.DeclaresType(other, typeName))
				.ToList();

			if (others.Count > 0)
			{
				binding.Detail = TypeOwners.Ambiguity(typeName, [.. others, path], binding.Raw);
				continue;
			}

			TryBind(binding, module);
		}
	}

	/// <summary>
	/// The binding a hit belongs to, and which hit of it this is. Null when nothing claims it, which
	/// is what a breakpoint set by something other than this session looks like.
	/// </summary>
	internal (BreakpointBinding? Binding, long Ordinal) Match(CorDebugFunctionBreakpoint? breakpoint)
	{
		var binding = Claim(breakpoint);

		// With one binding bound, an unidentified hit is unambiguously it.
		binding ??= _bindings.Count(entry => entry.Bound) == 1 ? _bindings.First(entry => entry.Bound) : null;

		return (binding, binding is null ? 0 : ++binding.HitCount);
	}

	/// <summary>
	/// The binding that owns a hit, or null when no single one does.
	/// <para>
	/// The IL offset is what separates two bindings in one method, and two in one method is a pairing
	/// the position listing invites a caller into: a named method binds at its first instruction and a
	/// picked position binds inside the IL, and both are function breakpoints on the same metadata
	/// token. Matching on the token alone hands every hit to whichever was registered first, so a stop
	/// meant to hold the target logs and continues instead, under another binding's id.
	/// </para>
	/// <para>
	/// The offset rather than the breakpoint object's identity, because ClrDebug's interfaces are
	/// source-generated ComWrappers rather than classic RCWs: the same COM pointer is not promised to
	/// come back as the same managed object, so identity here would work until it quietly did not.
	/// An offset that cannot be read falls back to the token, which is ambiguous for two bindings in
	/// one method and no worse than not looking.
	/// </para>
	/// </summary>
	private BreakpointBinding? Claim(CorDebugFunctionBreakpoint? breakpoint)
	{
		if (breakpoint is null) return null;

		var (token, moduleName, offset) = TryFunctionIdentity(breakpoint);
		if (token is null) return null;

		return _bindings.FirstOrDefault(entry =>
			entry.Bound
			&& entry.Token == token
			&& (moduleName is null
				|| string.Equals(moduleName, entry.ModulePath, StringComparison.OrdinalIgnoreCase))
			&& (offset is null || (entry.Location.IlOffset ?? 0) == offset));
	}

	/// <summary>
	/// Deactivates every bound breakpoint so the target can be let go with no patches left in its
	/// code. Called with the gate held and the process stopped.
	/// <para>
	/// A binding stays in the list and describes itself as unbound, because the session is ending and
	/// the caller may still list what it had set; the runtime object is what goes.
	/// </para>
	/// </summary>
	internal void ReleaseForDetach()
	{
		foreach (var binding in _bindings)
		{
			var breakpoint = binding.Breakpoint;
			if (breakpoint is null) continue;

			try
			{
				breakpoint.Activate(false);
			}
			catch (Exception exception)
			{
				logger.LogDebug(exception, "Deactivating binding {Id} before detaching failed.", binding.Id);
			}

			binding.Breakpoint = null;
			binding.Detail = "released on detach";
		}
	}

	/// <summary>
	/// Says why each still-unbound binding is unbound, against the module files that are actually loaded.
	/// <para>
	/// A binding that says only "not bound yet", whatever went wrong, points its caller at waiting when
	/// the spelling is what needs changing. So a location naming its assembly is told whether that module
	/// is loaded, and one naming none is told that no loaded module declares its type -- which is also
	/// what a module still to load looks like, so it says it binds when one that does arrives.
	/// </para>
	/// <para>
	/// Only ever narrows: a detail already set by <see cref="TryBind"/> or <see cref="BindAmong"/> is a
	/// real finding about a module that matched, and is left alone.
	/// </para>
	/// </summary>
	internal void ExplainUnbound(IReadOnlyList<string> loadedModulePaths)
	{
		foreach (var binding in _bindings)
		{
			if (binding.Bound) continue;
			if (binding.Detail is not (null or "not bound yet")) continue;

			var typeName = binding.Location.TypeName;

			if (binding.Location.Assembly is not { } assembly)
			{
				binding.Detail = $"no loaded module declares {typeName} ({loadedModulePaths.Count} searched); "
					+ "it binds when a module that does loads";
				continue;
			}

			if (loadedModulePaths.Any(path => TypeOwners.Admits(path, assembly)))
			{
				// The module is loaded and TryBind said nothing, so the type is what is missing --
				// the method-level miss is reported by TryBind itself.
				binding.Detail = $"no type {typeName} in {assembly}";
				continue;
			}

			binding.Detail = $"module {assembly} is not loaded ({loadedModulePaths.Count} others are)";
		}
	}

	internal static LiveTracepoint DescribeTracepoint(BreakpointBinding binding) => new()
	{
		Id = binding.Id,
		Location = binding.Raw,
		Bound = binding.Bound,
		IlOffset = binding.Location.IlOffset,
		Source = binding.Source,
		HitCount = binding.HitCount,
		LogMessage = binding.LogMessage,
		LogEveryNthHit = binding.LogEveryNthHit,
		Condition = binding.ConditionText,
		Detail = binding.Bound ? null : binding.Detail,
	};

	internal static LiveBreakpoint DescribeBreakpoint(BreakpointBinding binding) => new()
	{
		Id = binding.Id,
		Location = binding.Raw,
		StopOnHit = binding.StopOnHit,
		Bound = binding.Bound,
		IlOffset = binding.Location.IlOffset,
		Source = binding.Source,
		HitCount = binding.HitCount,
		AutoContinueSeconds = binding.AutoContinueSeconds ?? StopRecord.DefaultAutoContinueSeconds,
		Condition = binding.ConditionText,
		Detail = binding.Bound ? null : binding.Detail,
	};

	private void BindAmong(BreakpointBinding binding, IReadOnlyList<CorDebugModule> modules)
	{
		if (binding.Location.Assembly is not null)
		{
			foreach (var module in modules)
			{
				TryBind(binding, module);
				if (binding.Bound) return;
			}

			return;
		}

		var owners = modules
			.Where(module => TargetSymbols.FileOf(module) is { } path && MethodTokens.DeclaresType(path, binding.Location.TypeName))
			.ToList();

		if (owners.Count > 1)
		{
			binding.Detail = TypeOwners.Ambiguity(binding.Location.TypeName, [.. owners.Select(module => module.Name)], binding.Raw);
			return;
		}

		if (owners.Count == 1) TryBind(binding, owners[0]);
	}

	private void TryBind(BreakpointBinding binding, CorDebugModule module)
	{
		if (binding.Bound) return;

		try
		{
			if (module.IsDynamic || module.IsInMemory) return;
		}
		catch (Exception)
		{
			return; // A module that cannot describe itself is not one we can read metadata from.
		}

		if (!TypeOwners.Admits(module.Name, binding.Location.Assembly)) return;

		var token = MethodTokens.Find(module.Name, binding.Location.TypeName, binding.Location.MethodName);
		if (token is null)
		{
			binding.Detail = $"no method {binding.Location.TypeName}.{binding.Location.MethodName} in {Path.GetFileName(module.Name)}";
			return;
		}

		var offset = binding.Location.IlOffset;

		try
		{
			var function = module.GetFunctionFromToken(token.Value);

			// A named method binds at its first instruction; a picked position binds inside the IL,
			// which is the only way to stop on a line that is not the method's first.
			var breakpoint = offset is { } instruction
				? function.ILCode.CreateBreakpoint(instruction)
				: function.CreateBreakpoint();

			breakpoint.Activate(true);

			binding.Breakpoint = breakpoint;
			binding.Token = token.Value;
			binding.ModulePath = module.Name;
			binding.Source = TargetSymbols.SourceAt(module.Name, token.Value, offset ?? 0);
			binding.Detail = null;
			buffer.Append(LiveDebugEventKind.SessionNotice, $"{binding.Id} bound at {binding.Raw}.");
			logger.LogInformation("Binding {Id} bound at {Location} (token 0x{Token:x8}).", binding.Id, binding.Raw, token.Value);
		}
		catch (Exception exception)
		{
			// An offset the method's IL does not contain is the failure worth naming apart. It comes
			// back as an HRESULT about setting a breakpoint, which says nothing about the number
			// being wrong, and it is the one thing a caller composing a location by hand gets wrong.
			binding.Detail = offset is { } bad
				? $"bind failed at IL_{bad:X4}: {exception.Message}. The offset must be one this method's symbols report."
				: $"bind failed: {exception.Message}";
			logger.LogDebug(exception, "Binding {Id} at {Location} failed.", binding.Id, binding.Raw);
		}
	}

	/// <summary>
	/// What a hit's breakpoint says about itself: the method it is in, the module that declares it,
	/// and where inside the IL it sits. Any of them can be unavailable, and each is reported as
	/// unknown rather than guessed.
	/// </summary>
	private (int? Token, string? Module, int? Offset) TryFunctionIdentity(CorDebugFunctionBreakpoint breakpoint)
	{
		int? offset = null;
		try
		{
			offset = breakpoint.Offset;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a breakpoint's IL offset failed.");
		}

		try
		{
			var function = breakpoint.Function;
			return ((int)function.Token, function.Module.Name, offset);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a breakpoint's function identity failed.");
			return (null, null, offset);
		}
	}
}
