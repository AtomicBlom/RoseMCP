using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RoseMcp.Logging;

/// <summary>
/// The id of the tool call this process is working on, carried across every hop the call makes and
/// written on every log line written for it.
/// <para>
/// One call crosses up to four processes -- a stdio relay, the tray's broker, and a worker or a
/// live-app host -- and each writes its own file. Matching lines between them by timestamp and tool
/// name fails the moment two sessions ask one worker the same thing, which is the ordinary case for a
/// tray serving several agents. So the outermost Rose process to see a call mints an id, every hop
/// sends it on in <c>_meta</c> under <see cref="MetaKey"/>, and each process that receives it takes
/// it as its own: one search over the log folder then finds the call in every file it touched.
/// </para>
/// <para>
/// Not the JSON-RPC request id, which is chosen afresh by each client on each hop and so names a
/// different number in every process. And ambient rather than a parameter, for the reason the origin
/// directory is: a log line is written by code that knows nothing of the call it serves, so the only
/// way every line carries the id is for none of them to have to ask for it.
/// </para>
/// <para>
/// The id names a call only while that call is in flight. Work a call starts -- a refresh, a reload,
/// a transport's read loop -- inherits the execution context it was started from, and with it this
/// ambient, so an id that simply stayed set would go on tagging a poll loop's lines for the life of
/// the process with a call that ended hours before. A call's scope ends its id when it closes, and
/// everything that inherited it stops reporting it at that moment.
/// </para>
/// </summary>
public static class CallCorrelation
{
	/// <summary>
	/// The <c>_meta</c> key carrying the id between processes. Namespaced, as MCP asks of metadata that
	/// is not its own, beside <c>rosemcp/originDirectory</c>.
	/// </summary>
	public const string MetaKey = "rosemcp/correlationId";

	/// <summary>Six random bytes, as twelve hex digits: unique across any log folder a person reads.</summary>
	private const int MintedBytes = 6;

	private static readonly AsyncLocal<Call?> Ambient = new();

	/// <summary>The id of the call in flight on this execution context, or null outside one.</summary>
	public static string? Id => Ambient.Value is { IsOver: false } call ? call.Id : null;

	/// <summary>
	/// Starts a call: the id the previous hop sent in <paramref name="meta"/> when it sent a well-formed
	/// one, else a fresh one, so a process driven directly -- a worker under a test, a broker with no
	/// relay in front of it -- is the outermost process and mints. Dispose the result when the call
	/// ends; that ends the id for everything the call started too.
	/// </summary>
	public static IDisposable Begin(JsonObject? meta) => new Scope(Read(meta) ?? Mint());

	/// <summary>
	/// The id <paramref name="meta"/> carries, or null when it carries none or one that is not
	/// well-formed. A client may put anything it likes in <c>_meta</c>, and what is read here is
	/// written into every log line of the call, so anything but the shape a Rose process mints is
	/// replaced rather than repeated.
	/// </summary>
	public static string? Read(JsonObject? meta)
	{
		if (meta?[MetaKey] is not JsonValue value) return null;

		return value.TryGetValue(out string? id) && IsWellFormed(id) ? id : null;
	}

	/// <summary>
	/// Whether <paramref name="id"/> is lowercase hex of a length a Rose process could have minted.
	/// Inert in a log line by construction: no separator, no newline, nothing a reader could mistake
	/// for the start of another line.
	/// </summary>
	public static bool IsWellFormed([NotNullWhen(true)] string? id) =>
		id is { Length: >= 8 and <= 32 } && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

	/// <summary>A fresh id, in the shape <see cref="IsWellFormed"/> accepts.</summary>
	public static string Mint() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(MintedBytes));

	/// <summary>
	/// The call in flight on this execution context, for work handed to a queue that a long-lived loop
	/// runs later on a context of its own. Resuming it there files that work's lines under the call that
	/// queued it and is waiting on it, and under nothing once that call has ended -- and it moves only
	/// this id onto the loop, none of the caller's other ambient state.
	/// </summary>
	public static Captured Capture() => new(Ambient.Value);

	/// <summary>A call taken from one execution context to resume on another. See <see cref="Capture"/>.</summary>
	public readonly struct Captured
	{
		// Held as an object because the call's own type is private to the class, and a struct the
		// class's callers can hold cannot name it in a member they could see.
		private readonly object? _call;

		internal Captured(object? call) => _call = call;

		/// <summary>
		/// Makes the captured call the one in flight until the result is disposed, then puts back whatever
		/// was. Resuming does not end the call: the call it came from is the one that ends it.
		/// </summary>
		public IDisposable Resume() => new Resumed(_call as Call);
	}

	/// <summary>A captured call resumed on another context, restored on dispose without ending it.</summary>
	private sealed class Resumed : IDisposable
	{
		private readonly Call? _previous;

		public Resumed(Call? call)
		{
			_previous = Ambient.Value;
			Ambient.Value = call;
		}

		public void Dispose() => Ambient.Value = _previous;
	}

	/// <summary>
	/// One call's id, shared by reference with everything that inherited the execution context it was
	/// set on, which is what lets ending it reach them all.
	/// </summary>
	private sealed class Call(string id)
	{
		private volatile bool _over;

		public string Id { get; } = id;

		public bool IsOver => _over;

		public void End() => _over = true;
	}

	private sealed class Scope : IDisposable
	{
		private readonly Call? _previous;
		private readonly Call _call;

		public Scope(string id)
		{
			_previous = Ambient.Value;
			_call = new Call(id);
			Ambient.Value = _call;
		}

		public void Dispose()
		{
			_call.End();
			Ambient.Value = _previous;
		}
	}
}
