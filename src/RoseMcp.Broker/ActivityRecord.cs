using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RoseMcp.Contracts;
using RoseMcp.Logging;
using RoseMcp.Solutions;

namespace RoseMcp.Broker;

/// <summary>
/// The activity record: every tool call a client made through this broker, attributed to the client, the
/// workspace or debug session it reached, with its arguments, its response and a write's diffs, kept on disk for
/// as long as <see cref="BrokerOptions.ActivityRetention"/> says. What a window reads to say what each agent did.
/// <para>
/// One file per broker run, rolled each UTC day and past <see cref="FileSizeLimit"/>, named for the run's instance
/// id, so the several brokers one machine can run at once -- the tray's and any stdio server's beside it -- share
/// the folder without ever sharing a file or a lock. A line is one <see cref="ActivityEntry"/> in JSON, appended
/// when its call ends; the first a run writes says the broker started, so a restart reads as a line rather than
/// as a gap. Reading takes in every file in the folder, the previous runs' and the other brokers' as well as this
/// one's, which is what makes the record survive a reload, a restart and a deploy: none of it is held only in the
/// memory of the process that wrote it.
/// </para>
/// <para>
/// A call is never held up by the record. An entry is handed to a bounded queue a loop of its own writes from,
/// and dropped -- counted and logged -- where the queue is full, since a disk that cannot keep up is not a reason
/// for a tool call to wait. The loop is <see cref="Detached"/>, prunes files past the retention when it starts,
/// when it rolls to a new file and every <see cref="PruneInterval"/>, and is ended by disposing the record, which
/// writes what is queued first: the host disposes it as it stops, and the host stopping flushes it before that,
/// for a host that stops without disposing its services. A write that fails loses its batch, counted and logged at
/// most once a minute, and the next batch appends to the same file again rather than starting another: on a full
/// disk a fresh file per batch would leave an empty file behind every few seconds for the whole retention. Nothing
/// here throws to a caller.
/// </para>
/// <para>
/// A listing reads the files incrementally: each file's entries are indexed once by where each line starts, parsed
/// without their bodies and with an error cut to an excerpt, and only what was appended since is read again. The
/// index holds at most <see cref="IndexCapacity"/> entries, the newest: the files are read newest first, a file
/// past the capacity is left unread, and an index grown past it by later appends drops its oldest entries. A
/// listing then says that older entries are on disk and not listed, and each can still be read by its id. A body
/// -- arguments, response and diffs -- is read back from its line when an entry is asked for by id, or when a
/// listing asks for bodies, which it does for at most <see cref="ActivityQuery.MaxLimitWithBodies"/> entries.
/// </para>
/// </summary>
public sealed class ActivityRecord : IAsyncDisposable
{
	/// <summary>
	/// The variable naming the directory the record is kept in, which wins over every default. It exists for a
	/// test run, which makes calls through brokers that never say where to keep a record, and must not write them
	/// into the one a person reads.
	/// </summary>
	public const string RootVariable = "ROSEMCP_ACTIVITY_ROOT";

	/// <summary>The size past which a run starts another file, so no one file is too large to read back.</summary>
	public const long FileSizeLimit = 64L * 1024 * 1024;

	/// <summary>How many entries may wait to be written before more are dropped rather than wait for the disk.</summary>
	public const int MaxQueued = 4096;

	/// <summary>
	/// The most entries a listing's index holds, the newest. A summary is a few hundred bytes, so this keeps the
	/// index to some megabytes however busy a week was, and is days of a busy agent's calls.
	/// </summary>
	public const int IndexCapacity = 20_000;

	/// <summary>The longest error a listing carries; the entry read by its id has what the record keeps of it.</summary>
	public const int ErrorExcerpt = 300;

	/// <summary>How often the files are pruned when nothing else -- a start, a new file -- has done it.</summary>
	public static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

	/// <summary>How long stopping waits for what is queued to be written.</summary>
	public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

	/// <summary>How often a run of failed writes is logged again at warning: once at its start, and then this often.</summary>
	public static readonly TimeSpan LossReportInterval = TimeSpan.FromMinutes(1);

	private const string Extension = ".jsonl";

	private static readonly string Build = BuildIdentity.Of(typeof(ActivityRecord).Assembly).ToHandshake();

	/// <summary>One line per entry: nothing indented, every newline in a value escaped, and code spelled as it was written.</summary>
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = ToolJson.Encoder,
	};

	/// <summary>
	/// The same, reading an entry without its bodies: the arguments, the response and each diff are stepped over by
	/// the parser rather than made into strings, which is most of what indexing a file would otherwise allocate.
	/// </summary>
	private static readonly JsonSerializerOptions SummaryJson = new(Json)
	{
		TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WithoutBodyProperties } },
	};

	private readonly BrokerOptions _options;
	private readonly TimeProvider _time;
	private readonly ILogger _logger;

	private readonly Channel<object> _queue = Channel.CreateBounded<object>(
		new BoundedChannelOptions(MaxQueued) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

	/// <summary>Calls still running, so a listing shows what an agent is doing as well as what it has done.</summary>
	private readonly ConcurrentDictionary<string, CallRecording> _running = new(StringComparer.Ordinal);

	/// <summary>Calls that have ended and are queued but not yet written, so a call never drops out of a listing between the two.</summary>
	private readonly ConcurrentDictionary<string, ActivityEntry> _unwritten = new(StringComparer.Ordinal);

	/// <summary>Held by a reader for the index, which only readers touch; the writer has a file of its own and touches no index.</summary>
	private readonly Lock _reading = new();

	private readonly Dictionary<string, FileIndex> _files = new(PathCasing.Comparer);

	private readonly Task _writing;
	private readonly CancellationTokenRegistration _stopping;

	// The writer's own, touched only by its loop. The path outlives a failed write, so the next batch appends to the
	// same file rather than starting another.
	private Stream? _file;
	private string? _filePath;
	private long _fileLength;
	private DateOnly _fileDay;
	private bool _losing;
	private long _lossReportedAt = long.MinValue;

	// The readers', under _reading.
	private int _indexed;
	private bool _olderNotListed;

	private long _dropped;
	private long _lost;
	private int _warned;
	private int _disposed;

	/// <summary>
	/// Starts the record, its writer, and its broker-started entry. <paramref name="lifetime"/>, where the host has
	/// one, flushes the queue when the host stops.
	/// </summary>
	public ActivityRecord(
		IOptions<BrokerOptions> options,
		RelayConnections relays,
		ConnectedClients clients,
		ILogger<ActivityRecord> logger,
		IHostApplicationLifetime? lifetime = null)
	{
		_options = options.Value;
		_time = _options.TimeProvider;
		_logger = logger;

		Clients = clients;
		Broker = relays.Instance;
		Directory = _options.ActivityDirectory ?? DirectoryFor(_options.LocalAppData);

		_queue.Writer.TryWrite(new ActivityEntry
		{
			Id = MintId(),
			Kind = ActivityEntryKind.BrokerStarted,
			Broker = Broker,
			StartedUtc = relays.StartedUtc,
			BrokerBuild = Build,
			BrokerProcessId = Environment.ProcessId,
		});

		_writing = Detached.Run(WriteLoopAsync);

		if (lifetime is not null) _stopping = lifetime.ApplicationStopped.Register(() => Flush(StopWait));
	}

	/// <summary>The broker run writing this record: the instance id relays are given.</summary>
	public string Broker { get; }

	/// <summary>Where the record is kept.</summary>
	public string Directory { get; }

	/// <summary>The table each entry names its client from.</summary>
	public ConnectedClients Clients { get; }

	/// <summary>How many entries were dropped because the queue was full or the record had stopped, never reaching the disk.</summary>
	public long Dropped => Interlocked.Read(ref _dropped);

	/// <summary>How many entries were taken from the queue and lost to a write that failed.</summary>
	public long Lost => Interlocked.Read(ref _lost);

	/// <summary>How long an entry is kept: the setting, or a week where it says nothing usable.</summary>
	public TimeSpan Retention
	{
		get
		{
			var fallback = TimeSpan.FromDays(Settings.RoseSettings.DefaultActivityRetentionDays);

			try
			{
				var chosen = _options.ActivityRetention();
				return chosen > TimeSpan.Zero ? chosen : fallback;
			}
			catch (Exception exception)
			{
				Failed(exception);
				return fallback;
			}
		}
	}

	internal DateTimeOffset Now => _time.GetUtcNow();

	/// <summary>
	/// Where the record is kept when nothing configures it: the directory <see cref="RootVariable"/> names, else
	/// <c>Activity</c> beside each process's logs, under the directory <see cref="RoseLogFile.RootVariable"/> names
	/// for a test run, else under the profile's -- <paramref name="localAppData"/> where it is given.
	/// </summary>
	public static string DirectoryFor(string? localAppData)
	{
		var root = Environment.GetEnvironmentVariable(RootVariable);
		if (!string.IsNullOrWhiteSpace(root)) return root;

		return RoseLogFile.DirectoryFor("Activity", localAppData, Environment.GetEnvironmentVariable(RoseLogFile.RootVariable));
	}

	/// <summary>An entry's id: eight random bytes, unique across every run a record keeps.</summary>
	public static string MintId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

	/// <summary>
	/// Starts recording a tool call and makes it the one in flight on this execution context until the result is
	/// disposed. Finish it with what it answered, or say it failed or was cancelled; disposing an unfinished one
	/// records it as cancelled.
	/// </summary>
	/// <param name="tool">The tool called.</param>
	/// <param name="arguments">The arguments as the client sent them.</param>
	/// <param name="relayed">The client a relay named, as <see cref="CallSession.Of"/> gives it with no transport session; null for none.</param>
	/// <param name="transportSession">The transport's own session, where it has one.</param>
	/// <param name="clientName">The name in the transport's <c>clientInfo</c>, kept for a client no relay names.</param>
	/// <param name="origin">The directory the calling session works in, as its relay sent it.</param>
	public CallRecording Begin(
		string? tool,
		IDictionary<string, JsonElement>? arguments,
		string? relayed,
		string? transportSession,
		string? clientName,
		string? origin)
	{
		var recording = new CallRecording(this, tool, arguments, relayed, transportSession, clientName, origin);
		_running[recording.Id] = recording;

		return recording;
	}

	/// <summary>
	/// The newest entries matching <paramref name="query"/>, the calls still running among them. Reads what each
	/// file gained since the last read, under the readers' lock, and keeps only the newest of what matches as it goes
	/// rather than sorting everything; never throws for a file it cannot read, which is left out.
	/// </summary>
	public ActivityPage Query(ActivityQuery query)
	{
		var limit = Math.Clamp(query.Limit, 1, query.Bodies ? ActivityQuery.MaxLimitWithBodies : ActivityQuery.MaxLimit);
		var newest = new Newest(limit);
		bool olderNotListed;

		lock (_reading)
		{
			Refresh(query.Since);

			foreach (var (path, index) in _files)
			{
				foreach (var indexed in index.Entries)
				{
					// A copy still queued is the same entry, and is the one taken below.
					if (_unwritten.ContainsKey(indexed.Summary.Id) || !Matches(indexed.Summary, query)) continue;

					newest.Offer(new Found(indexed.Summary, path, indexed.Offset, indexed.Length));
				}
			}

			olderNotListed = _olderNotListed;
		}

		foreach (var entry in _unwritten.Values)
		{
			if (Matches(entry, query)) newest.Offer(new Found(entry, null, 0, 0));
		}

		foreach (var recording in _running.Values)
		{
			if (Running(recording) is { } entry && Matches(entry, query)) newest.Offer(new Found(entry, null, 0, 0));
		}

		var page = newest.Descending().Select(found => query.Bodies ? WithBodies(found) : Summary(found.Entry));

		return new ActivityPage
		{
			Entries = [.. page],
			Bodies = query.Bodies,
			Matched = newest.Offered,
			OlderNotListed = olderNotListed,
			Directory = Directory,
			Retention = Retention,
		};
	}

	/// <summary>
	/// The entry with <paramref name="id"/>, bodies and all, running or written; null where the record has none. An
	/// entry older than the index reaches is looked for in the files themselves.
	/// </summary>
	public ActivityEntry? Find(string id)
	{
		if (_running.TryGetValue(id, out var running) && Running(running) is { } snapshot) return snapshot;
		if (_unwritten.TryGetValue(id, out var unwritten)) return unwritten;

		lock (_reading)
		{
			Refresh(since: null);

			foreach (var (path, index) in _files)
			{
				foreach (var indexed in index.Entries)
				{
					if (string.Equals(indexed.Summary.Id, id, StringComparison.Ordinal))
					{
						return WithBodies(new Found(indexed.Summary, path, indexed.Offset, indexed.Length));
					}
				}
			}

			if (_olderNotListed && Scan(id) is { } older) return older;
		}

		return _unwritten.GetValueOrDefault(id);
	}

	/// <summary>Waits until everything queued before this call has been written and flushed to the file.</summary>
	public async Task FlushAsync(CancellationToken cancellationToken = default)
	{
		var request = new FlushRequest();

		try
		{
			await _queue.Writer.WriteAsync(request, cancellationToken);
		}
		catch (ChannelClosedException)
		{
			return;
		}

		await request.Done.Task.WaitAsync(cancellationToken);
	}

	/// <summary>Writes what is queued and stops the writer, waiting up to <see cref="StopWait"/> for it.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

		await _stopping.DisposeAsync();
		_queue.Writer.TryComplete();

		try
		{
			await _writing.WaitAsync(StopWait);
		}
		catch (TimeoutException)
		{
			_logger.LogWarning("The activity record was still writing after {Wait}; what it had queued may be lost.", StopWait);
		}
	}

	/// <summary>Takes a call's entry from the running calls to the queue, never waiting for room in it.</summary>
	internal void Finished(CallRecording recording, ActivityEntry entry)
	{
		_unwritten[entry.Id] = entry;
		_running.TryRemove(recording.Id, out _);

		if (_queue.Writer.TryWrite(entry)) return;

		_unwritten.TryRemove(entry.Id, out _);

		var dropped = Interlocked.Increment(ref _dropped);
		if (dropped == 1 || dropped % 1000 == 0)
		{
			_logger.LogWarning(
				"The activity record dropped {Tool}'s entry, and {Dropped} in all: its queue of {Max} is full, or it has stopped.",
				entry.Tool,
				dropped,
				MaxQueued);
		}
	}

	/// <summary>Lets go of a call whose entry could not be made, so it is not listed as running for the life of the broker.</summary>
	internal void Forget(CallRecording recording) => _running.TryRemove(recording.Id, out _);

	/// <summary>Logs something that went wrong in recording, at warning the first time and at debug after.</summary>
	internal void Failed(Exception exception)
	{
		if (Interlocked.Exchange(ref _warned, 1) == 0)
		{
			_logger.LogWarning(exception, "The activity record could not keep an entry; later failures are logged at debug.");
			return;
		}

		_logger.LogDebug(exception, "The activity record could not keep an entry.");
	}

	private static void WithoutBodyProperties(JsonTypeInfo type)
	{
		string[] bodies = type.Type == typeof(ActivityEntry)
			? [nameof(ActivityEntry.Arguments), nameof(ActivityEntry.Response)]
			: type.Type == typeof(ActivityFileChange) ? [nameof(ActivityFileChange.Diff)] : [];

		if (bodies.Length == 0) return;

		var names = bodies.Select(JsonNamingPolicy.CamelCase.ConvertName).ToHashSet(StringComparer.Ordinal);

		for (var index = type.Properties.Count - 1; index >= 0; index--)
		{
			if (names.Contains(type.Properties[index].Name)) type.Properties.RemoveAt(index);
		}
	}

	private static bool Matches(ActivityEntry entry, ActivityQuery query)
	{
		var inWindow = (query.Since is not { } since || entry.StartedUtc >= since)
			&& (query.Until is not { } until || entry.StartedUtc < until);
		if (!inWindow) return false;

		// A broker starting is part of every client's and every workspace's story: it is why a session's calls
		// pause, and why the client's row has a new id after it. It belongs to no tool.
		if (entry.Kind == ActivityEntryKind.BrokerStarted) return query.Tool is null;

		var client = query.Client is null
			|| string.Equals(entry.ClientId, query.Client, StringComparison.Ordinal)
			|| string.Equals(entry.ClientKey, query.Client, StringComparison.Ordinal);

		var workspace = query.Workspace is null
			|| (entry.Workspace is not null && PathCasing.Comparer.Equals(entry.Workspace, query.Workspace))
			|| string.Equals(entry.WorkspaceKey, query.Workspace, StringComparison.OrdinalIgnoreCase);

		var session = query.DebugSession is null || string.Equals(entry.DebugSession, query.DebugSession, StringComparison.Ordinal);
		var tool = query.Tool is null || string.Equals(entry.Tool, query.Tool, StringComparison.Ordinal);

		return client && workspace && session && tool;
	}

	/// <summary>The entry as a listing carries it: without its bodies, and with its error cut to <see cref="ErrorExcerpt"/>.</summary>
	private static ActivityEntry Summary(ActivityEntry entry)
	{
		var (error, fullLength) = ActivityBodies.Cut(entry.Error, ErrorExcerpt);

		return entry with
		{
			Arguments = null,
			Response = null,
			Error = error,
			ErrorFullLength = fullLength ?? entry.ErrorFullLength,
			Changes = entry.Changes.Any(change => change.Diff is not null)
				? [.. entry.Changes.Select(change => change with { Diff = null })]
				: entry.Changes,
		};
	}

	private ActivityEntry? Running(CallRecording recording)
	{
		try
		{
			return recording.Snapshot(ActivityOutcome.Running);
		}
		catch (Exception exception)
		{
			Failed(exception);
			return null;
		}
	}

	/// <summary>The entry with its bodies, read back from its line where it was written, or as it is where it was not.</summary>
	private static ActivityEntry WithBodies(Found found)
	{
		if (found.Path is null) return found.Entry;

		try
		{
			return ReadLine(found.Path, found.Offset, found.Length) ?? found.Entry;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return found.Entry with { Notes = [.. found.Entry.Notes, $"The entry's bodies could not be read back: {exception.Message}"] };
		}
	}

	/// <summary>The whole entry written at <paramref name="offset"/> in <paramref name="path"/>.</summary>
	private static ActivityEntry? ReadLine(string path, long offset, int length)
	{
		using var stream = Open(path);
		stream.Seek(offset, SeekOrigin.Begin);

		var line = new byte[length];
		stream.ReadExactly(line);

		return JsonSerializer.Deserialize<ActivityEntry>(line, Json);
	}

	private static FileStream Open(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

	/// <summary>
	/// Brings the index up to date with the folder: forgets files that are gone, and reads what each file has gained
	/// since it was last read, the newest files first. A file not yet indexed whose last write is before
	/// <paramref name="since"/> is left until a query reaches back that far, since every entry in it started before
	/// it was written; one met once the index is full is left unread for good, and an index grown well past its
	/// capacity by appends is trimmed back to it. Under <see cref="_reading"/>.
	/// </summary>
	private void Refresh(DateTimeOffset? since)
	{
		string[] present;

		try
		{
			present = System.IO.Directory.Exists(Directory) ? System.IO.Directory.GetFiles(Directory, "*" + Extension) : [];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			present = [];
		}

		var kept = present.ToHashSet(PathCasing.Comparer);
		foreach (var gone in _files.Keys.Where(path => !kept.Contains(path)).ToList())
		{
			_indexed -= _files[gone].Entries.Count;
			_files.Remove(gone);
		}

		// A file is named for when it was started, so by name is newest last: read backwards, the capacity is spent
		// on the newest entries.
		foreach (var path in present.OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
		{
			try
			{
				if (!_files.TryGetValue(path, out var index))
				{
					var tooOld = since is { } from && File.GetLastWriteTimeUtc(path) < from.UtcDateTime;
					if (tooOld) continue;

					index = new FileIndex { Skipped = _indexed >= IndexCapacity };
					_files[path] = index;
					_olderNotListed |= index.Skipped;
				}

				if (index.Skipped) continue;

				// The length from an open handle rather than the directory's listing, which a file another process
				// is still writing can report short of.
				using var stream = Open(path);

				if (stream.Length < index.Consumed)
				{
					_indexed -= index.Entries.Count;
					index.Clear();
				}

				if (stream.Length == index.Consumed) continue;

				stream.Seek(index.Consumed, SeekOrigin.Begin);
				_indexed += Read(stream, index);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Gone since the listing, or held in a way that refuses a reader: it is read on a later pass.
			}
		}

		// A tenth of slack, so a full index is trimmed once per few thousand calls rather than on every listing.
		if (_indexed > IndexCapacity + (IndexCapacity / 10)) Trim();
	}

	/// <summary>Drops the oldest entries from the index until it holds <see cref="IndexCapacity"/>. Under <see cref="_reading"/>.</summary>
	private void Trim()
	{
		var starts = _files.Values.SelectMany(index => index.Entries).Select(indexed => indexed.Summary.StartedUtc).ToList();
		starts.Sort();

		var cutoff = starts[starts.Count - IndexCapacity];

		_indexed = 0;
		foreach (var index in _files.Values)
		{
			index.Entries.RemoveAll(indexed => indexed.Summary.StartedUtc < cutoff);
			_indexed += index.Entries.Count;
		}

		_olderNotListed = true;
	}

	/// <summary>
	/// Looks through every file for the entry with <paramref name="id"/>, for one the index does not hold. Only a line
	/// that names the id is parsed. Under <see cref="_reading"/>.
	/// </summary>
	private ActivityEntry? Scan(string id)
	{
		var needle = Encoding.UTF8.GetBytes($"\"id\":\"{id}\"");

		foreach (var path in _files.Keys)
		{
			var at = new List<(long Offset, int Length)>();

			try
			{
				using var stream = Open(path);
				Lines(stream, 0, (line, offset, length) =>
				{
					if (line.IndexOf(needle) >= 0) at.Add((offset, length));
				});
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (var (offset, length) in at)
			{
				if (ReadLine(path, offset, length) is { } entry && string.Equals(entry.Id, id, StringComparison.Ordinal)) return entry;
			}
		}

		return null;
	}

	/// <summary>Indexes each whole line from where the stream stands, and says how many entries it added.</summary>
	private static int Read(FileStream stream, FileIndex index)
	{
		var added = 0;

		index.Consumed = Lines(stream, index.Consumed, (line, offset, length) =>
		{
			if (Index(line) is { } summary)
			{
				index.Entries.Add(new Indexed(summary, offset, length));
				added++;
			}
		});

		return added;
	}

	/// <summary>
	/// Hands each whole line from where the stream stands to <paramref name="visit"/>, with where it starts, and
	/// answers where the next read should begin. A line not yet ended -- the writer is mid-append -- is left for the
	/// next read.
	/// </summary>
	private static long Lines(FileStream stream, long start, LineVisitor visit)
	{
		var buffer = new byte[64 * 1024];
		using var pending = new MemoryStream();
		var lineStart = start;
		int read;

		while ((read = stream.Read(buffer)) > 0)
		{
			var from = 0;

			for (var at = 0; at < read; at++)
			{
				if (buffer[at] != (byte)'\n') continue;

				pending.Write(buffer, from, at - from);

				var length = (int)pending.Length;
				visit(pending.GetBuffer().AsSpan(0, length), lineStart, length);

				lineStart += length + 1;
				pending.SetLength(0);
				from = at + 1;
			}

			pending.Write(buffer, from, read - from);
		}

		return lineStart;
	}

	/// <summary>
	/// One line as the index keeps it, or null for a blank one or one that does not parse, which is stepped over:
	/// a partly written file from a process that died is still worth the lines it finished.
	/// </summary>
	private static ActivityEntry? Index(ReadOnlySpan<byte> line)
	{
		if (line.Trim((byte)' ').IsEmpty) return null;

		try
		{
			return JsonSerializer.Deserialize<ActivityEntry>(line, SummaryJson) is { } entry ? Summary(entry) : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Writes what is queued until the queue is completed, pruning on the way. Every step that touches the disk
	/// catches what it throws, and a batch that fails anyway still answers the flushes waiting on it: a loop that
	/// ended here would leave every later entry queued and every flush waiting for the life of the broker.
	/// </summary>
	private async Task WriteLoopAsync()
	{
		Prune();

		var reader = _queue.Reader;

		while (true)
		{
			bool more;

			using (var tick = new CancellationTokenSource(PruneInterval, _time))
			{
				try
				{
					more = await reader.WaitToReadAsync(tick.Token);
				}
				catch (OperationCanceledException)
				{
					Prune();
					continue;
				}
			}

			if (!more) break;

			var written = new List<string>();
			var flushes = new List<FlushRequest>();
			var appended = 0;
			var lost = 0;
			Exception? failure = null;

			try
			{
				while (reader.TryRead(out var item))
				{
					if (item is FlushRequest flush)
					{
						flushes.Add(flush);
						continue;
					}

					if (item is not ActivityEntry entry) continue;

					written.Add(entry.Id);

					if (Append(entry) is { } refused)
					{
						failure = refused;
						lost++;
						continue;
					}

					appended++;
				}

				if (Persist() is { } unflushed)
				{
					// What was appended since the last flush may be in the file or may not; it is counted as lost.
					failure = unflushed;
					lost += appended;
				}
			}
			catch (Exception exception)
			{
				Failed(exception);
			}
			finally
			{
				foreach (var id in written) _unwritten.TryRemove(id, out _);
				foreach (var flush in flushes) flush.Done.TrySetResult();
			}

			Account(lost, failure);
		}

		Close();
	}

	/// <summary>
	/// Counts what a batch lost and says so: at warning when a run of failures starts and then at most once every
	/// <see cref="LossReportInterval"/>, so a full disk is heard about without a line per batch; and once, when a
	/// batch is written again after a run of failures.
	/// </summary>
	private void Account(int lost, Exception? failure)
	{
		if (lost == 0)
		{
			if (!_losing) return;

			_losing = false;
			_logger.LogInformation("The activity record is writing again, having lost {Lost} entries in all.", Lost);
			return;
		}

		var total = Interlocked.Add(ref _lost, lost);
		var now = Environment.TickCount64;
		var due = !_losing || now - _lossReportedAt >= (long)LossReportInterval.TotalMilliseconds;
		_losing = true;

		if (!due)
		{
			_logger.LogDebug(failure, "The activity record could not write {Count} entries.", lost);
			return;
		}

		_lossReportedAt = now;
		_logger.LogWarning(
			failure,
			"The activity record could not write {Count} entries to {Directory}, and has lost {Lost} in all; it goes on trying with each batch.",
			lost,
			Directory,
			total);
	}

	/// <summary>Appends one entry's line, answering the failure rather than throwing it.</summary>
	private Exception? Append(ActivityEntry entry)
	{
		try
		{
			var line = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
			var file = Current(line.Length + 1);

			file.Write(line);
			file.WriteByte((byte)'\n');
			_fileLength += line.Length + 1;

			return null;
		}
		catch (Exception exception)
		{
			Close();
			return exception;
		}
	}

	/// <summary>Flushes the file, answering the failure rather than throwing it.</summary>
	private Exception? Persist()
	{
		try
		{
			_file?.Flush();
			return null;
		}
		catch (Exception exception)
		{
			Close();
			return exception;
		}
	}

	/// <summary>
	/// The file to append to: the one open; else the last one, opened again to append where a write to it failed;
	/// else a new one, on a new UTC day or where the last is full. Only a new day, a full file, or a run with no file
	/// yet starts one, so failing writes cannot leave a trail of empty files.
	/// </summary>
	private Stream Current(int adding)
	{
		var now = Now;
		var today = DateOnly.FromDateTime(now.UtcDateTime);

		if (_file is { } open && today == _fileDay && _fileLength + adding <= FileSizeLimit) return open;

		if (_file is null && _filePath is { } last && today == _fileDay && File.Exists(last))
		{
			var length = new FileInfo(last).Length;
			if (length + adding <= FileSizeLimit) return Reopen(last, length);
		}

		Close();
		System.IO.Directory.CreateDirectory(Directory);

		var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		var run = Broker.Length > 12 ? Broker[..12] : Broker;

		for (var attempt = 1; ; attempt++)
		{
			var suffix = attempt == 1 ? string.Empty : $"-{attempt}";
			var candidate = Path.Combine(Directory, $"{stamp}-{run}{suffix}{Extension}");

			try
			{
				var created = _options.OpenActivityFile(candidate, FileMode.CreateNew);
				_file = created;
				_filePath = candidate;
				_fileLength = 0;
				_fileDay = today;

				Prune();

				return created;
			}
			catch (IOException) when (attempt < 100 && File.Exists(candidate))
			{
			}
		}
	}

	/// <summary>
	/// The file a failed write left, opened again to append. A line a failure cut short is ended first, so the next
	/// entry is a line of its own rather than the tail of one that no longer parses.
	/// </summary>
	private Stream Reopen(string path, long length)
	{
		var unended = false;

		if (length > 0)
		{
			using var reading = Open(path);
			reading.Seek(-1, SeekOrigin.End);
			unended = reading.ReadByte() != '\n';
		}

		var reopened = _options.OpenActivityFile(path, FileMode.Append);
		_file = reopened;
		_fileLength = length;

		if (unended)
		{
			reopened.WriteByte((byte)'\n');
			_fileLength++;
		}

		return reopened;
	}

	/// <summary>Closes the file, keeping its path so the next write appends to it.</summary>
	private void Close()
	{
		try
		{
			_file?.Dispose();
		}
		catch (Exception exception)
		{
			Failed(exception);
		}

		_file = null;
	}

	/// <summary>
	/// Deletes every file last written longer ago than the retention, this run's own file aside. A file another
	/// process holds open is left for a later pass, and nothing here throws: a record that cannot prune goes on
	/// recording.
	/// </summary>
	private void Prune()
	{
		try
		{
			if (!System.IO.Directory.Exists(Directory)) return;

			var cutoff = (Now - Retention).UtcDateTime;

			foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*" + Extension))
			{
				if (_filePath is not null && PathCasing.Comparer.Equals(path, _filePath)) continue;

				try
				{
					if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		catch (Exception exception)
		{
			Failed(exception);
		}
	}

	private void Flush(TimeSpan wait)
	{
		try
		{
			FlushAsync().Wait(wait);
		}
		catch (Exception exception) when (exception is AggregateException or ObjectDisposedException)
		{
			Failed(exception);
		}
	}

	private delegate void LineVisitor(ReadOnlySpan<byte> line, long offset, int length);

	/// <summary>A request in the queue to be told once everything before it is written.</summary>
	private sealed class FlushRequest
	{
		public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	/// <summary>
	/// What a reader knows of one file: how far into it it has read, and each entry there as a listing carries it; or
	/// that it was met with the index full and is not read.
	/// </summary>
	private sealed class FileIndex
	{
		public long Consumed { get; set; }

		public bool Skipped { get; init; }

		public List<Indexed> Entries { get; } = [];

		public void Clear()
		{
			Consumed = 0;
			Entries.Clear();
		}
	}

	/// <summary>
	/// The newest of what a query matched, up to its limit, kept in a heap as the matches are offered so a listing
	/// never sorts everything it read; and how many were offered.
	/// </summary>
	private sealed class Newest(int limit)
	{
		private static readonly Comparer<Found> Oldest = Comparer<Found>.Create((left, right) =>
		{
			var byTime = left.Entry.StartedUtc.CompareTo(right.Entry.StartedUtc);
			return byTime != 0 ? byTime : string.CompareOrdinal(left.Entry.Id, right.Entry.Id);
		});

		private readonly PriorityQueue<Found, Found> _heap = new(Oldest);

		public int Offered { get; private set; }

		public void Offer(Found found)
		{
			Offered++;

			if (_heap.Count < limit)
			{
				_heap.Enqueue(found, found);
				return;
			}

			if (Oldest.Compare(found, _heap.Peek()) > 0) _heap.EnqueueDequeue(found, found);
		}

		public IEnumerable<Found> Descending()
		{
			var kept = new List<Found>(_heap.Count);
			while (_heap.TryDequeue(out var found, out _)) kept.Add(found);

			kept.Reverse();
			return kept;
		}
	}

	/// <summary>One entry as a listing carries it, and where its line is in its file.</summary>
	private readonly record struct Indexed(ActivityEntry Summary, long Offset, int Length);

	/// <summary>An entry a query found, and where its line is where it was written; a null path for one not written yet.</summary>
	private readonly record struct Found(ActivityEntry Entry, string? Path, long Offset, int Length);
}
