using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// for a host that stops without disposing its services. Nothing here throws to a caller.
/// </para>
/// <para>
/// A listing reads the files incrementally: each file's entries are indexed once, without their bodies, by where
/// each line starts, and only what was appended since is read again. A body -- arguments, response and diffs -- is
/// read back from its line when an entry is asked for by id, or when a listing asks for bodies.
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

	/// <summary>How often the files are pruned when nothing else -- a start, a new file -- has done it.</summary>
	public static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

	/// <summary>How long stopping waits for what is queued to be written.</summary>
	public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(5);

	private const string Extension = ".jsonl";

	private static readonly string Build = BuildIdentity.Of(typeof(ActivityRecord).Assembly).ToHandshake();

	/// <summary>One line per entry: nothing indented, every newline in a value escaped, and code spelled as it was written.</summary>
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = ToolJson.Encoder,
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

	// The writer's own, touched only by its loop.
	private FileStream? _file;
	private string? _filePath;
	private DateOnly _fileDay;

	private int _dropped;
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
	/// file gained since the last read, under the readers' lock; never throws for a file it cannot read, which is
	/// left out.
	/// </summary>
	public ActivityPage Query(ActivityQuery query)
	{
		var limit = Math.Clamp(query.Limit, 1, ActivityQuery.MaxLimit);
		var found = new List<Found>();

		lock (_reading)
		{
			Refresh(query.Since);

			foreach (var (path, index) in _files)
			{
				found.AddRange(index.Entries.Select(indexed => new Found(indexed.Summary, path, indexed.Offset, indexed.Length)));
			}
		}

		var written = found.Select(entry => entry.Entry.Id).ToHashSet(StringComparer.Ordinal);
		found.AddRange(_unwritten.Values.Where(entry => !written.Contains(entry.Id)).Select(entry => new Found(entry, null, 0, 0)));
		found.AddRange(_running.Values.Select(Running).OfType<ActivityEntry>().Select(entry => new Found(entry, null, 0, 0)));

		var matching = found
			.Where(entry => Matches(entry.Entry, query))
			.OrderByDescending(entry => entry.Entry.StartedUtc)
			.ThenByDescending(entry => entry.Entry.Id, StringComparer.Ordinal)
			.ToList();

		var page = matching.Take(limit).Select(entry => query.Bodies ? WithBodies(entry) : WithoutBodies(entry.Entry));

		return new ActivityPage
		{
			Entries = [.. page],
			Bodies = query.Bodies,
			Matched = matching.Count,
			Directory = Directory,
			Retention = Retention,
		};
	}

	/// <summary>The entry with <paramref name="id"/>, bodies and all, running or written; null where the record has none.</summary>
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

	/// <summary>Takes a call's entry from the running calls to the queue.</summary>
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

	private static ActivityEntry WithoutBodies(ActivityEntry entry) => entry with
	{
		Arguments = null,
		Response = null,
		Changes = [.. entry.Changes.Select(change => change with { Diff = null })],
	};

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
	private ActivityEntry WithBodies(Found found)
	{
		if (found.Path is null) return found.Entry;

		try
		{
			using var stream = Open(found.Path);
			stream.Seek(found.Offset, SeekOrigin.Begin);

			var line = new byte[found.Length];
			stream.ReadExactly(line);

			return JsonSerializer.Deserialize<ActivityEntry>(line, Json) ?? found.Entry;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return found.Entry with { Notes = [.. found.Entry.Notes, $"The entry's bodies could not be read back: {exception.Message}"] };
		}
	}

	private static FileStream Open(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

	/// <summary>
	/// Brings the index up to date with the folder: forgets files that are gone, and reads what each file has
	/// gained since it was last read. A file not yet indexed whose last write is before <paramref name="since"/> is
	/// left until a query reaches back that far, since every entry in it started before it was written. Under
	/// <see cref="_reading"/>.
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
		foreach (var gone in _files.Keys.Where(path => !kept.Contains(path)).ToList()) _files.Remove(gone);

		foreach (var path in present)
		{
			try
			{
				if (!_files.TryGetValue(path, out var index))
				{
					var tooOld = since is { } from && File.GetLastWriteTimeUtc(path) < from.UtcDateTime;
					if (tooOld) continue;

					_files[path] = index = new FileIndex();
				}

				// The length from an open handle rather than the directory's listing, which a file another process
				// is still writing can report short of.
				using var stream = Open(path);

				if (stream.Length < index.Consumed) index.Clear();
				if (stream.Length == index.Consumed) continue;

				stream.Seek(index.Consumed, SeekOrigin.Begin);
				Read(stream, index);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Gone since the listing, or held in a way that refuses a reader: it is read on a later pass.
			}
		}
	}

	/// <summary>
	/// Indexes each whole line from where the stream stands. A line not yet ended -- the writer is mid-append -- is
	/// left for the next read; a line that does not parse is stepped over, since a partly written file from a
	/// process that died is still worth the lines it finished.
	/// </summary>
	private static void Read(FileStream stream, FileIndex index)
	{
		var buffer = new byte[64 * 1024];
		using var pending = new MemoryStream();
		var lineStart = index.Consumed;
		int read;

		while ((read = stream.Read(buffer)) > 0)
		{
			var start = 0;

			for (var at = 0; at < read; at++)
			{
				if (buffer[at] != (byte)'\n') continue;

				pending.Write(buffer, start, at - start);

				var length = (int)pending.Length;
				Index(pending.GetBuffer().AsSpan(0, length), lineStart, length, index);

				lineStart += length + 1;
				pending.SetLength(0);
				start = at + 1;
			}

			pending.Write(buffer, start, read - start);
		}

		index.Consumed = lineStart;
	}

	private static void Index(ReadOnlySpan<byte> line, long offset, int length, FileIndex index)
	{
		if (line.Trim((byte)' ').IsEmpty) return;

		try
		{
			if (JsonSerializer.Deserialize<ActivityEntry>(line, Json) is { } entry)
			{
				index.Entries.Add(new Indexed(WithoutBodies(entry), offset, length));
			}
		}
		catch (JsonException)
		{
		}
	}

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

			while (reader.TryRead(out var item))
			{
				if (item is FlushRequest flush)
				{
					flushes.Add(flush);
					continue;
				}

				if (item is not ActivityEntry entry) continue;

				Append(entry);
				written.Add(entry.Id);
			}

			Persist();

			foreach (var id in written) _unwritten.TryRemove(id, out _);
			foreach (var flush in flushes) flush.Done.TrySetResult();
		}

		Close();
	}

	private void Append(ActivityEntry entry)
	{
		try
		{
			var line = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
			var file = Current(line.Length + 1);

			file.Write(line);
			file.WriteByte((byte)'\n');
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
		{
			Close();
			Failed(exception);
		}
	}

	private void Persist()
	{
		try
		{
			_file?.Flush();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Close();
			Failed(exception);
		}
	}

	/// <summary>The file to append to: this run's, or a new one on a new UTC day or where this one is full.</summary>
	private FileStream Current(int adding)
	{
		var now = Now;
		var today = DateOnly.FromDateTime(now.UtcDateTime);

		if (_file is { } open && today == _fileDay && open.Length + adding <= FileSizeLimit) return open;

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
				var created = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
				_file = created;
				_filePath = candidate;
				_fileDay = today;

				Prune();

				return created;
			}
			catch (IOException) when (attempt < 100 && File.Exists(candidate))
			{
			}
		}
	}

	private void Close()
	{
		try
		{
			_file?.Dispose();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Failed(exception);
		}

		_file = null;
		_filePath = null;
	}

	/// <summary>
	/// Deletes every file last written longer ago than the retention, this run's own open file aside. A file
	/// another process holds open is left for a later pass, and nothing here throws: a record that cannot prune
	/// goes on recording.
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
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

	/// <summary>A request in the queue to be told once everything before it is written.</summary>
	private sealed class FlushRequest
	{
		public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	/// <summary>What a reader knows of one file: how far into it it has read, and each entry there without its bodies.</summary>
	private sealed class FileIndex
	{
		public long Consumed { get; set; }

		public List<Indexed> Entries { get; } = [];

		public void Clear()
		{
			Consumed = 0;
			Entries.Clear();
		}
	}

	/// <summary>One entry without its bodies, and where its line is in its file.</summary>
	private readonly record struct Indexed(ActivityEntry Summary, long Offset, int Length);

	/// <summary>An entry a query found, and where its line is where it was written; a null path for one not written yet.</summary>
	private readonly record struct Found(ActivityEntry Entry, string? Path, long Offset, int Length);
}
