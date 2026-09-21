using ClrDebug;

using Microsoft.Extensions.Logging;

using RoseMcp.Contracts;
using RoseMcp.Symbols;

namespace RoseMcp.LiveApp.Debugging;

/// <summary>
/// The stopped state a read is answered from: the process, the thread being held, and what the stop
/// itself was.
/// <para>
/// Captured by the session under its own lock and handed over whole, so a read cannot see the three
/// disagree. It holds references to live COM objects rather than copies of their contents, so the
/// caller must still be holding that lock: the debuggee has to stay stopped for the length of the
/// read, and it is the session that knows whether it is.
/// </para>
/// </summary>
internal readonly record struct StoppedTarget(CorDebugProcess Process, CorDebugThread? Held, LiveStop Stop);

/// <summary>
/// Reads a stopped process: its threads, a thread's frames, the arguments and locals in a frame,
/// what is inside a value, and a field-access expression evaluated against one.
/// <para>
/// It knows nothing about how the stop happened, how it ends, or what a breakpoint is. That is the
/// whole of why it is separate: reading a stopped target is a question about the target, while
/// holding one is a question about the session, and the second was what made the first hard to find
/// among three thousand lines.
/// </para>
/// <para>
/// No debuggee code is ever run. Every value here is read out of memory, so a property with a getter
/// is not among a value's children and a method is never called to render one -- which is what makes
/// a read safe to do at a breakpoint in somebody else's application.
/// </para>
/// </summary>
internal sealed class CorDebugInspector(ILogger logger)
{
	private const int MaxVariables = 64;
	private const int MaxStructuredFrames = 200;
	private const int MaxChildren = 100;

	/// <summary>A thread's id, or null when the runtime will not give one -- a thread that has gone, or one
	/// mid-transition. Asked rather than assumed, because a throw here would lose the whole answer over
	/// one row of it.</summary>
	internal static int? TryThreadId(CorDebugThread thread)
	{
		try
		{
			return thread.Id;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>The innermost managed frame of a thread, or null when it has none.</summary>
	internal static CorDebugILFrame? FindTopILFrame(CorDebugThread thread)
	{
		foreach (var chain in thread.EnumerateChains())
		{
			foreach (var frame in chain.EnumerateFrames())
			{
				if (frame is CorDebugILFrame ilFrame) return ilFrame;
			}
		}

		return null;
	}

	public LiveStackFrames Frames(StoppedTarget target, int? threadId, int offset, int limit)
	{
		var page = Math.Min(limit, MaxStructuredFrames);

		var thread = FindThread(target, threadId);
		var walked = WalkFrames(thread);
		var active = threadId is null || threadId == TryThreadId(thread);
		var id = TryThreadId(thread) ?? 0;

		var frames = new List<LiveStackFrame>();
		for (var index = offset; index < walked.Frames.Count && frames.Count < page; index++)
		{
			frames.Add(DescribeStackFrame(walked.Frames[index], index, id, active && index == 0));
		}

		return new LiveStackFrames
		{
			Execution = target.Stop.State,
			Stop = target.Stop,
			ThreadId = id,
			Frames = frames,
			Offset = offset,
			Total = walked.Frames.Count,
			Truncated = walked.Truncated,
		};
	}

	/// <summary>
	/// One frame's arguments and locals. The frame is named by its index in the stack this session
	/// would report now, so a caller reads a stack and then asks about a row of it.
	/// </summary>
	/// <exception cref="ArgumentException">The index is negative, the thread is not there, or there is no such frame.</exception>
	public LiveFrameVariables Variables(StoppedTarget target, int frameIndex, int? threadId)
	{
		var thread = FindThread(target, threadId);
		var frame = FrameAt(thread, frameIndex);
		var (module, token) = FrameIdentity(frame);
		var (variables, truncated) = VariablesOf(frame);

		return new LiveFrameVariables
		{
			Execution = target.Stop.State,
			Stop = target.Stop,
			FrameIndex = frameIndex,
			ThreadId = TryThreadId(thread),
			MethodFullName = module is null || token is null ? null : MethodTokens.MethodFullName(module, token.Value),
			Symbols = SymbolsOf(module, token, IlOffsetOf(frame)).State,
			Variables = variables,
			Truncated = truncated,
		};
	}

	/// <summary>
	/// What is inside a value: an object's fields, or an array's elements, addressed by the same
	/// <see cref="LiveVariable.Path"/> the value was reported under.
	/// <para>
	/// No debuggee code runs, so a property with a getter is not among the children -- what comes
	/// back is what the object holds. Statics are out of scope: they belong to a type rather than to
	/// the value in hand, and a caller asking about a value has not asked about its type.
	/// </para>
	/// </summary>
	/// <exception cref="ArgumentException">The path does not parse, the frame is not there, or the path resolves to nothing.</exception>
	public LiveValueExpansion Expand(StoppedTarget target, ValuePath parsed, string path, int frameIndex, int? threadId)
	{
		var frame = FrameAt(FindThread(target, threadId), frameIndex);
		var value = Resolve(frame, parsed, out var failure)
			?? throw new ArgumentException(failure ?? $"'{path}' does not resolve in frame {frameIndex}.");

		var (typeName, rendered, hasChildren) = ValueReader.Read(value);
		var (children, total, truncated) = hasChildren ? ChildrenOf(value, path) : ([], 0, false);

		return new LiveValueExpansion
		{
			Execution = target.Stop.State,
			Stop = target.Stop,
			Path = path,
			TypeName = typeName,
			Value = rendered,
			Children = children,
			Total = total,
			Truncated = truncated,
		};
	}

	/// <summary>
	/// Every managed thread of the stopped target, the held one first. Only while stopped: reading
	/// threads needs the runtime synchronized, and synchronizing it to answer would stop the app.
	/// </summary>
	public LiveThreadList Threads(StoppedTarget target)
	{
		var held = target.Held is null ? null : TryThreadId(target.Held);
		var threads = new List<LiveThread>();

		try
		{
			foreach (var thread in target.Process.EnumerateThreads())
			{
				var id = TryThreadId(thread);
				if (id is null) continue;

				threads.Add(new LiveThread
				{
					Id = id.Value,
					UserState = UserStateOf(thread),
					IsStopped = id == held,
					TopFrame = TopFrameName(thread),
				});
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Enumerating the target's threads failed.");
		}

		return new LiveThreadList
		{
			Execution = target.Stop.State,
			Stop = target.Stop,

			// The held thread first: it is the one every other answer is about by default, and a
			// reader scanning forty pool threads for it should not have to.
			Threads = [.. threads.OrderByDescending(thread => thread.IsStopped).ThenBy(thread => thread.Id)],
		};
	}

	/// <summary>The thread a caller named, or the one being held when it named none.</summary>
	/// <exception cref="ArgumentException">No thread of that id, or nothing is held and none was named.</exception>
	private static CorDebugThread FindThread(StoppedTarget target, int? threadId)
	{
		if (threadId is not { } wanted)
		{
			return target.Held ?? throw new ArgumentException("No thread is being held, so there is no default thread to read.");
		}

		foreach (var thread in target.Process.EnumerateThreads())
		{
			if (TryThreadId(thread) == wanted) return thread;
		}

		throw new ArgumentException($"The target has no thread {wanted}. rose_live_app_threads lists the ones it has.");
	}

	/// <summary>
	/// A thread's managed IL frames, innermost first, with how many unrepresentable frames preceded
	/// each -- native, internal or dynamic frames the runtime gives no IL frame for.
	/// <para>
	/// Counting them rather than dropping them is what keeps the stack honest: a stack silently
	/// missing three frames reads as a complete stack with a surprising caller.
	/// </para>
	/// </summary>
	public FrameWalk WalkFrames(CorDebugThread thread)
	{
		var frames = new List<WalkedFrame>();
		var skipped = 0;

		try
		{
			foreach (var chain in thread.EnumerateChains())
			{
				foreach (var frame in chain.EnumerateFrames())
				{
					if (frames.Count >= MaxStructuredFrames) return new FrameWalk(frames, Truncated: true);

					if (frame is CorDebugILFrame ilFrame)
					{
						frames.Add(new WalkedFrame(ilFrame, skipped));
						skipped = 0;
						continue;
					}

					skipped++;
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Walking a thread's frames failed.");
		}

		return new FrameWalk(frames, Truncated: false);
	}

	/// <exception cref="ArgumentException">There is no frame at that index on this thread.</exception>
	private CorDebugILFrame FrameAt(CorDebugThread thread, int index)
	{
		var walked = WalkFrames(thread);
		if (index < walked.Frames.Count) return walked.Frames[index].Frame;

		throw new ArgumentException(
			$"Frame {index} is past the end of this thread's stack, which has {walked.Frames.Count} managed frame(s).");
	}

	private LiveStackFrame DescribeStackFrame(WalkedFrame walked, int index, int threadId, bool isActive)
	{
		var frame = walked.Frame;
		var (module, token) = FrameIdentity(frame);
		var offset = IlOffsetOf(frame);
		var (state, source) = SymbolsOf(module, token, offset);
		var parts = module is null || token is null ? null : MethodTokens.MethodParts(module, token.Value);

		return new LiveStackFrame
		{
			Index = index,
			ThreadId = threadId,
			Module = module is null ? null : Path.GetFileName(module),
			MethodFullName = parts is { } named ? $"{named.TypeName}.{named.MethodName}" : null,

			// Composed from the parts rather than by joining the two fields above, which cannot be
			// taken apart again: a type name is full of dots and a constructor's name begins with one.
			Location = module is null || parts is not { } addressed
				? null
				: $"{Path.GetFileNameWithoutExtension(module)}!{addressed.TypeName}.{addressed.MethodName}",
			IlOffset = offset,
			Mapping = MappingOf(frame),
			Symbols = state,
			Source = source,
			IsActive = isActive,
			SkippedBefore = walked.SkippedBefore,
		};
	}

	/// <summary>The module path and method token behind a frame, either null when it cannot be read.</summary>
	private static (string? Module, int? Token) FrameIdentity(CorDebugILFrame frame)
	{
		try
		{
			var function = frame.Function;
			return (TryModuleName(function), TryFunctionToken(function));
		}
		catch (Exception)
		{
			return (null, null);
		}
	}

	private static int? IlOffsetOf(CorDebugILFrame frame)
	{
		try
		{
			return frame.IP.pnOffset;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static LiveIlMapping MappingOf(CorDebugILFrame frame)
	{
		try
		{
			var mapping = frame.IP.pMappingResult;

			// Tested in order of how much a reader can rely on the line beside it, because the
			// runtime sets these as flags and more than one can be on at once.
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT)) return LiveIlMapping.Exact;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_APPROXIMATE)) return LiveIlMapping.Approximate;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_PROLOG)) return LiveIlMapping.Prolog;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_EPILOG)) return LiveIlMapping.Epilog;
			if (mapping.HasFlag(CorDebugMappingResult.MAPPING_UNMAPPED_ADDRESS)) return LiveIlMapping.UnmappedAddress;

			return LiveIlMapping.NoInfo;
		}
		catch (Exception)
		{
			return LiveIlMapping.NoInfo;
		}
	}

	/// <summary>
	/// Whether a module's symbols could name this instruction's source, and the position when they
	/// could. A mismatched PDB is its own state rather than "no symbols", because it is the one a
	/// person can act on.
	/// </summary>
	private static (LiveSymbolState State, LiveSourcePosition? Source) SymbolsOf(string? module, int? token, int? ilOffset)
	{
		if (module is null || token is not { } methodToken) return (LiveSymbolState.NoSymbols, null);

		var symbols = SymbolCache.Shared.For(module);
		if (symbols is null) return (LiveSymbolState.NoSymbols, null);
		if (symbols.PdbState == PdbState.Mismatched) return (LiveSymbolState.SymbolsMismatched, null);
		if (symbols.Pdb is not { } pdb) return (LiveSymbolState.NoSymbols, null);

		if (ilOffset is not { } offset) return (LiveSymbolState.NoSequencePoint, null);
		if (pdb.Position(methodToken, offset) is not { } position) return (LiveSymbolState.NoSequencePoint, null);

		return (LiveSymbolState.Resolved, new LiveSourcePosition
		{
			File = position.File,
			Line = position.Line,
			Column = position.Column,
			EndLine = position.EndLine,
			EndColumn = position.EndColumn,
		});
	}

	/// <summary>The runtime's thread state as words, without the <c>USER_</c> every one of them carries.</summary>
	private static IReadOnlyList<string> UserStateOf(CorDebugThread thread)
	{
		try
		{
			var state = thread.UserState;

			return
			[
				.. Enum.GetValues<CorDebugUserState>()
					.Where(flag => flag != 0 && state.HasFlag(flag))
					.Select(flag => flag.ToString().Replace("USER_", string.Empty, StringComparison.Ordinal))
					.Select(Titled),
			];
		}
		catch (Exception)
		{
			return [];
		}
	}

	/// <summary><c>WAIT_SLEEP_JOIN</c> as <c>WaitSleepJoin</c>, which is how .NET spells it everywhere else.</summary>
	private static string Titled(string screaming) =>
		string.Concat(screaming.Split('_').Select(word => word.Length == 0
			? word
			: char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

	private string? TopFrameName(CorDebugThread thread)
	{
		var walked = WalkFrames(thread);
		if (walked.Frames.Count == 0) return null;

		var (module, token) = FrameIdentity(walked.Frames[0].Frame);

		return module is null || token is null ? null : MethodTokens.MethodFullName(module, token.Value);
	}

	/// <summary>
	/// A frame's arguments and locals, named and addressed. Reading is defensive per variable, so
	/// one unreadable value does not lose the rest of the frame.
	/// </summary>
	public (IReadOnlyList<LiveVariable> Variables, bool Truncated) VariablesOf(CorDebugILFrame frame)
	{
		var variables = new List<LiveVariable>();
		var total = 0;

		try
		{
			var (module, token) = FrameIdentity(frame);
			var (isStatic, parameterNames) = token is { } methodToken && module is not null
				? MethodTokens.ParameterNames(module, methodToken)
				: (true, (IReadOnlyList<string>)[]);

			var arguments = frame.EnumerateArguments().ToList();
			var named = LocalNamesOf(frame);
			var locals = frame.EnumerateLocalVariables().ToList();
			total = arguments.Count + locals.Count;

			for (var i = 0; i < arguments.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value, hasChildren) = ValueReader.Read(arguments[i]);
				variables.Add(new LiveVariable
				{
					Name = ArgumentName(i, isStatic, parameterNames),
					Kind = "argument",
					TypeName = typeName,
					Value = value,
					Path = ValuePath.Argument(i),
					HasChildren = hasChildren,
				});
			}

			for (var i = 0; i < locals.Count && variables.Count < MaxVariables; i++)
			{
				var (typeName, value, hasChildren) = ValueReader.Read(locals[i]);
				variables.Add(new LiveVariable
				{
					Name = LocalName(i, named),
					Kind = "local",
					TypeName = typeName,
					Value = value,
					Path = ValuePath.Local(i),
					HasChildren = hasChildren,
				});
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a frame's variables failed.");
		}

		return (variables, variables.Count < total);
	}

	/// <summary>
	/// Walks a parsed path from a frame to the value it names, or null with a sentence saying which
	/// step failed. The sentence names the step rather than the whole path, because a five-segment
	/// path that resolves four segments and fails on the fifth is a different problem from one that
	/// was wrong from the start.
	/// </summary>
	private CorDebugValue? Resolve(CorDebugILFrame frame, ValuePath path, out string? failure)
	{
		failure = null;
		var current = ResolveValueRoot(frame, path, out failure);
		if (current is null) return null;

		var walked = path.Kind switch
		{
			ValuePathRoot.Argument => ValuePath.Argument(path.Slot),
			ValuePathRoot.Local => ValuePath.Local(path.Slot),
			_ => path.Name ?? string.Empty,
		};

		foreach (var step in path.Steps)
		{
			if (step.Field is { } field)
			{
				current = ResolveField(current, field);
				if (current is null)
				{
					failure = $"'{walked}' has no readable field '{field}' (not a field of that value, or it is null).";
					return null;
				}

				walked = ValuePath.Field(walked, field);
				continue;
			}

			var index = step.Index ?? 0;
			current = ResolveElement(current, index, out failure);
			if (current is null) return null;

			walked = ValuePath.Element(walked, index);
		}

		return current;
	}

	/// <summary>Resolves a path's root: an argument slot, a local slot, or a name in the frame.</summary>
	private CorDebugValue? ResolveValueRoot(CorDebugILFrame frame, ValuePath path, out string? failure)
	{
		failure = null;

		try
		{
			if (path.Kind == ValuePathRoot.Argument)
			{
				var arguments = frame.EnumerateArguments().ToList();
				if (path.Slot < arguments.Count) return arguments[path.Slot];

				failure = $"This frame has {arguments.Count} argument(s), so there is no arg:{path.Slot}.";
				return null;
			}

			if (path.Kind == ValuePathRoot.Local)
			{
				var locals = frame.EnumerateLocalVariables().ToList();
				if (path.Slot < locals.Count) return locals[path.Slot];

				failure = $"This frame has {locals.Count} local(s), so there is no local:{path.Slot}.";
				return null;
			}

			var name = path.Name ?? string.Empty;
			var found = ResolveNamed(frame, name);
			if (found is not null) return found;

			failure = $"'{name}' is not an argument or local in this frame.";
			return null;
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Resolving a value path's root failed.");
			failure = exception.Message;
			return null;
		}
	}

	/// <summary>Matches a bare name against the frame's arguments and then its locals.</summary>
	private static CorDebugValue? ResolveNamed(CorDebugILFrame frame, string name)
	{
		var (module, token) = FrameIdentity(frame);
		var (isStatic, parameterNames) = token is { } methodToken && module is not null
			? MethodTokens.ParameterNames(module, methodToken)
			: (true, (IReadOnlyList<string>)[]);

		var arguments = frame.EnumerateArguments().ToList();
		for (var i = 0; i < arguments.Count; i++)
		{
			if (ArgumentName(i, isStatic, parameterNames) == name) return arguments[i];
		}

		// Matched on the same name the variables were reported under, so a caller can pass back what
		// they were shown. A slot number still resolves where there are no symbols to give a name.
		var named = LocalNamesOf(frame);
		var locals = frame.EnumerateLocalVariables().ToList();
		for (var i = 0; i < locals.Count; i++)
		{
			if (LocalName(i, named) == name) return locals[i];
		}

		return null;
	}

	/// <summary>Reads one element of an array value, dereferencing to reach the array first.</summary>
	private static CorDebugValue? ResolveElement(CorDebugValue value, int index, out string? failure)
	{
		failure = null;
		var unwrapped = Unwrap(value);

		if (unwrapped is not CorDebugArrayValue array)
		{
			failure = $"[{index}] needs an array, and this value is not one.";
			return null;
		}

		if (index >= array.Count)
		{
			failure = $"[{index}] is past the end of an array of {array.Count}.";
			return null;
		}

		return array.GetElementAtPosition(index);
	}

	/// <summary>
	/// What is inside a value, with each child carrying the path that expands it in turn.
	/// <para>
	/// An object's fields are read from the exact type and every base above it, each level's fields
	/// through that level's own class -- <c>GetFieldValue</c> wants the class the field was declared
	/// on, not the value's. A base can live in another module, which is why the chain is walked
	/// through the runtime's types rather than read out of one module's metadata.
	/// </para>
	/// </summary>
	private (IReadOnlyList<LiveVariable> Children, int Total, bool Truncated) ChildrenOf(CorDebugValue value, string basePath)
	{
		var children = new List<LiveVariable>();
		var total = 0;

		try
		{
			var unwrapped = Unwrap(value);

			if (unwrapped is CorDebugArrayValue array)
			{
				total = array.Count;
				for (var i = 0; i < total && children.Count < MaxChildren; i++)
				{
					var (typeName, rendered, hasChildren) = ValueReader.Read(array.GetElementAtPosition(i));
					children.Add(new LiveVariable
					{
						Name = $"[{i}]",
						Kind = "element",
						TypeName = typeName,
						Value = rendered,
						Path = ValuePath.Element(basePath, i),
						HasChildren = hasChildren,
					});
				}

				return (children, total, children.Count < total);
			}

			if (unwrapped is not CorDebugObjectValue objectValue) return (children, 0, false);

			for (var type = ExactTypeOf(unwrapped); type is not null; type = BaseOf(type))
			{
				var cls = type.Class;
				var fields = MethodTokens.Fields(cls.Module.Name, cls.Token);

				foreach (var field in fields)
				{
					// Statics belong to the type rather than to the value in hand, and a caller
					// asking what an object holds has not asked about its type.
					if (field.IsStatic) continue;

					total++;
					if (children.Count >= MaxChildren) continue;

					children.Add(DescribeField(objectValue, cls, field, basePath));
				}
			}
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Listing the children of '{Path}' failed.", basePath);
		}

		return (children, total, children.Count < total);
	}

	private static LiveVariable DescribeField(CorDebugObjectValue objectValue, CorDebugClass cls, FieldMember field, string basePath)
	{
		try
		{
			// GetFieldValue wants the raw ICorDebugClass; the ClrDebug wrapper is not it, and casting
			// the wrapper to the interface throws.
			var (typeName, rendered, hasChildren) = ValueReader.Read(objectValue.GetFieldValue(cls.Raw, field.Token));

			return new LiveVariable
			{
				Name = field.Name,
				Kind = "field",
				TypeName = typeName,
				Value = rendered,
				Path = ValuePath.Field(basePath, field.Name),
				HasChildren = hasChildren,
			};
		}
		catch (Exception)
		{
			// A field the runtime will not read -- a literal, or one optimised out -- is reported as
			// unreadable rather than dropped, so a reader is not left wondering where it went.
			return new LiveVariable
			{
				Name = field.Name,
				Kind = "field",
				Value = "(unreadable)",
				Path = ValuePath.Field(basePath, field.Name),
				HasChildren = false,
			};
		}
	}

	/// <summary>Follows a reference to what it points at, and a box to what it holds.</summary>
	private static CorDebugValue Unwrap(CorDebugValue value)
	{
		if (value is CorDebugReferenceValue reference && !reference.IsNull) value = reference.Dereference();
		if (value is CorDebugBoxValue box) value = box.Object;

		return value;
	}

	private static CorDebugType? ExactTypeOf(CorDebugValue value)
	{
		try
		{
			return value.ExactType;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static CorDebugType? BaseOf(CorDebugType type)
	{
		try
		{
			return type.Base;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A thread's IL frames and whether the walk stopped at its own limit.</summary>
	internal sealed record FrameWalk(IReadOnlyList<WalkedFrame> Frames, bool Truncated);

	/// <summary>One IL frame, with the count of unrepresentable frames immediately below it.</summary>
	internal sealed record WalkedFrame(CorDebugILFrame Frame, int SkippedBefore);

	/// <summary>
	/// One already-parsed path read against a frame the caller has in hand, rendered the way every
	/// other surface renders a value. It is the whole of an evaluation bar finding the frame, which
	/// is what a tracepoint's interpolation needs: a message naming four values walks the stack once
	/// rather than four times, on a callback the target is stopped for.
	/// </summary>
	internal LogValue ReadPath(CorDebugILFrame frame, ValuePath path)
	{
		try
		{
			var value = Resolve(frame, path, out var failure);
			if (value is null) return LogValue.Unavailable(failure ?? "could not be read");

			var (typeName, rendered, _) = ValueReader.Read(value);
			return LogValue.Read(rendered, typeName);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Reading a value path for a log message failed.");
			return LogValue.Unavailable(exception.Message);
		}
	}

	/// <summary>
	/// Evaluates a value path against the stopped frame (issue #7): an argument or local -- by name,
	/// or as <c>arg:0</c> / <c>local:2</c> -- then <c>.field</c> and <c>[3]</c> into the object graph,
	/// read directly from memory. It runs none of the debuggee's own code -- no property getters, no
	/// method calls -- so it cannot hang or corrupt the target; those need func-eval, a deliberate
	/// non-goal. Returns an error, not a throw, when nothing is stopped or the path does not resolve.
	/// <para>
	/// The same grammar and the same resolver as an expansion, so a path a caller was shown in a
	/// variable evaluates without translation, and the two cannot disagree about what one means.
	/// </para>
	/// </summary>
	public LiveEvaluation Evaluate(CorDebugThread held, string expression)
	{
		try
		{
			var path = ValuePath.Parse(expression);
			var frame = FindTopILFrame(held);
			if (frame is null)
			{
				return new LiveEvaluation { Expression = expression, Error = "The held thread has no managed frame to evaluate against." };
			}

			var value = Resolve(frame, path, out var failure);
			if (value is null) return new LiveEvaluation { Expression = expression, Error = failure };

			var (typeName, rendered, _) = ValueReader.Read(value);
			return new LiveEvaluation { Expression = expression, TypeName = typeName, Value = rendered };
		}
		catch (ArgumentException exception)
		{
			return new LiveEvaluation { Expression = expression, Error = exception.Message };
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Evaluating '{Expression}' failed.", expression);
			return new LiveEvaluation { Expression = expression, Error = exception.Message };
		}
	}

	/// <summary>
	/// Reads a field off a value by name: dereference a reference, unwrap a box, then read the field
	/// directly.
	/// <para>
	/// The type chain is walked rather than the value's own class asked, because a field declared on
	/// a base is as much a part of the object -- and it is what an expansion lists, so a path taken
	/// from one has to resolve back here or the two surfaces disagree about what an object holds.
	/// </para>
	/// </summary>
	private static CorDebugValue? ResolveField(CorDebugValue value, string fieldName)
	{
		var unwrapped = Unwrap(value);
		if (unwrapped is not CorDebugObjectValue objectValue) return null;

		for (var type = ExactTypeOf(unwrapped); type is not null; type = BaseOf(type))
		{
			if (FieldOn(objectValue, type.Class, fieldName) is { } found) return found;
		}

		// A value whose exact type the runtime will not give still has a class, and for anything
		// without a base that is the whole answer.
		return FieldOn(objectValue, objectValue.Class, fieldName);
	}

	/// <summary>Reads a field declared on one level of a value's type chain, or null if it is not there.</summary>
	private static CorDebugValue? FieldOn(CorDebugObjectValue objectValue, CorDebugClass cls, string fieldName)
	{
		try
		{
			var token = MethodTokens.FieldToken(cls.Module.Name, (int)cls.Token, fieldName);
			if (token is null) return null;

			// GetFieldValue wants the raw ICorDebugClass; the ClrDebug wrapper is not it (casting the
			// wrapper to the interface throws), so hand over its Raw.
			return objectValue.GetFieldValue(cls.Raw, token.Value);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// What a frame's locals are called, by slot, read from its module's portable PDB at the IL
	/// offset the frame is stopped at. Empty when there are no symbols for the module.
	/// <para>
	/// The offset is not decoration. A slot is reused by locals in sibling blocks, so the names in
	/// scope depend on where execution is -- asking for every name in the method would hand back two
	/// names for one slot and pick between them arbitrarily.
	/// </para>
	/// </summary>
	private static IReadOnlyDictionary<int, string> LocalNamesOf(CorDebugILFrame frame)
	{
		var none = new Dictionary<int, string>();

		try
		{
			var function = frame.Function;
			var moduleName = TryModuleName(function);
			var methodToken = TryFunctionToken(function);

			if (moduleName is null || methodToken is not { } token) return none;
			if (SymbolCache.Shared.For(moduleName)?.Pdb is not { } pdb) return none;

			return pdb.LocalNames(token, frame.IP.pnOffset);
		}
		catch (Exception)
		{
			return none;
		}
	}

	/// <summary>
	/// A local's name: what the PDB calls it, or its slot when there are no symbols.
	/// <para>
	/// The fallback is a real answer rather than a placeholder. A release build, a framework
	/// assembly, or a PDB belonging to another build all leave a debugger with nothing but slots, and
	/// a slot number is at least true -- which is why a mismatched PDB is refused rather than read.
	/// </para>
	/// </summary>
	private static string LocalName(int slot, IReadOnlyDictionary<int, string> named) =>
		named.TryGetValue(slot, out var name) ? name : $"local_{slot}";

	private static string ArgumentName(int index, bool isStatic, IReadOnlyList<string> parameterNames)
	{
		if (!isStatic)
		{
			if (index == 0) return "this";
			var parameter = index - 1;
			return parameter < parameterNames.Count ? parameterNames[parameter] : $"arg_{index}";
		}

		return index < parameterNames.Count ? parameterNames[index] : $"arg_{index}";
	}

	private static string? TryModuleName(CorDebugFunction function)
	{
		try
		{
			return function.Module.Name;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static int? TryFunctionToken(CorDebugFunction function)
	{
		try
		{
			return (int)function.Token;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
