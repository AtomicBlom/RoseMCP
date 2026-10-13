using System.Collections.ObjectModel;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Inspector;

/// <summary>
/// One expression a person watches, as the code view shows it: the path, and what it read at the frame being looked
/// at, or why it read nothing.
/// </summary>
public sealed class WatchRow : Observable
{
	private string _value = string.Empty;
	private string _typeName = string.Empty;
	private string _detail;
	private bool _isReading;
	private NoteSubject? _subject;

	public WatchRow(string path)
	{
		Path = path;
		_detail = WatchList.NotRead;
	}

	/// <summary>The value path watched, in the grammar a local's own path is written in: <c>this._pen.Width</c>.</summary>
	public string Path { get; }

	/// <summary>What it read, empty until it has.</summary>
	public string Value
	{
		get => _value;
		private set => Set(ref _value, value);
	}

	public string TypeName
	{
		get => _typeName;
		private set => Set(ref _typeName, value);
	}

	/// <summary>Why there is no value: not read yet, the target running, or the host's own reason.</summary>
	public string Detail
	{
		get => _detail;
		private set
		{
			Set(ref _detail, value);
			OnPropertyChanged(nameof(HasDetail));
		}
	}

	public bool HasDetail => Detail.Length > 0;

	public bool IsReading
	{
		get => _isReading;
		private set => Set(ref _isReading, value);
	}

	/// <summary>What a note about this watch is about, once it has a value at a stop; null otherwise.</summary>
	public NoteSubject? Subject
	{
		get => _subject;
		private set
		{
			Set(ref _subject, value);
			OnPropertyChanged(nameof(CanAsk));
		}
	}

	public bool CanAsk => Subject is not null;

	internal void Reading()
	{
		IsReading = true;
		Detail = string.Empty;
	}

	internal void Read(LiveValueExpansion read, WatchContext at)
	{
		IsReading = false;

		if (read.Execution == LiveExecutionState.Running)
		{
			Forget(read.Detail ?? WatchList.Running);
			return;
		}

		TypeName = read.TypeName ?? string.Empty;
		Value = read.Value ?? (TypeName.Length > 0 ? $"{{{TypeName}}}" : string.Empty);
		Detail = read.Detail ?? string.Empty;
		Subject = AskAgent.Value(at.SessionId, at.ThreadId, at.Frame, Path, Value, TypeName, Path);
	}

	internal void Failed(string why)
	{
		IsReading = false;
		Forget(why);
	}

	internal void Forget(string why)
	{
		IsReading = false;
		Value = string.Empty;
		TypeName = string.Empty;
		Detail = why;
		Subject = null;
	}
}

/// <summary>Where a watch is read: the session, the stop, and the thread and frame the person is looking at.</summary>
/// <param name="SessionId">The debug session.</param>
/// <param name="StopSequence">The stop, so an answer from another one is dropped.</param>
/// <param name="ThreadId">The thread whose frame it is; null for the stopped thread.</param>
/// <param name="Frame">The frame, from the top of that thread's stack; null for the top.</param>
public sealed record WatchContext(string SessionId, long StopSequence, int? ThreadId, FrameRow? Frame);

/// <summary>
/// What a person watches in a debugged app: value paths read at whichever frame they are looking at, at every stop.
/// <para>
/// Per window and not kept anywhere: a watch is about what this person is looking at now, and a list carried between
/// apps would be paths that name nothing in the next one. Every value goes when the target moves -- a value read at a
/// stop is a fact about that stop, and shown after it would be a confident answer about somewhere the target no
/// longer is -- and the list is read again at the next stop. A read answered after the person moved to another frame,
/// thread or stop is dropped, since it answers a question nobody is asking any more.
/// </para>
/// </summary>
public sealed class WatchList
{
	/// <summary>What a watch says before it is read.</summary>
	public const string NotRead = "read at the next stop";

	/// <summary>What a watch says while the target runs.</summary>
	public const string Running = "the target is running; read at the next stop";

	private int _generation;

	/// <summary>The watches, in the order they were added.</summary>
	public ObservableCollection<WatchRow> Rows { get; } = [];

	/// <summary>
	/// Adds a watch on <paramref name="text"/>, refusing one that is not a value path, since the host would refuse it
	/// at every stop and say so at each.
	/// </summary>
	/// <returns>The row added, or null with <paramref name="problem"/> saying why.</returns>
	public WatchRow? Add(string? text, out string? problem)
	{
		var path = text?.Trim() ?? string.Empty;

		try
		{
			ValuePath.Parse(path);
		}
		catch (ArgumentException exception)
		{
			problem = $"Not a value path: {exception.Message} A watch takes a name or a slot -- this._pen.Width, local:2, arg:0[3] -- and reads fields; it runs no code.";
			return null;
		}

		problem = null;
		var row = new WatchRow(path);
		Rows.Add(row);

		return row;
	}

	/// <summary>Stops watching one.</summary>
	public void Remove(WatchRow row) => Rows.Remove(row);

	/// <summary>Forgets every value, for a target that has moved: the reads in flight are dropped when they land.</summary>
	public void Stale()
	{
		_generation++;
		foreach (var row in Rows) row.Forget(Running);
	}

	/// <summary>
	/// Reads every watch at <paramref name="at"/> through <paramref name="read"/>, one after another so a stop is not
	/// sent a burst. Whatever arrives after another read was started, or after <see cref="Stale"/>, is dropped.
	/// </summary>
	public async Task ReadAsync(
		WatchContext at,
		Func<string, WatchContext, CancellationToken, Task<LiveValueExpansion>> read,
		CancellationToken cancellationToken)
	{
		var generation = ++_generation;

		foreach (var row in Rows.ToList())
		{
			if (generation != _generation) return;

			row.Reading();

			try
			{
				var value = await read(row.Path, at, cancellationToken);
				if (generation != _generation) return;

				var otherStop = value.Execution != LiveExecutionState.Running && value.Stop?.EventSequence != at.StopSequence;
				if (otherStop)
				{
					row.Failed(Running);
					continue;
				}

				row.Read(value, at);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				if (generation == _generation) row.Failed(NotRead);
				throw;
			}
			catch (OperatorException exception)
			{
				// A path that names nothing in this frame is that watch's answer here, not a failure of the window.
				if (generation == _generation) row.Failed(exception.Message);
			}
		}
	}
}
