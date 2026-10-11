using System.ComponentModel;
using System.Diagnostics;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Reads a process's memory from the process table, which answers for a process that has stopped answering
/// anything else -- a wedged worker is exactly the one somebody is looking at. Each read opens the process and
/// disposes it, and none throws: a process that is gone, or that this one may not read, has no figures.
/// </summary>
public static class ProcessMemorySampler
{
	/// <summary>
	/// The memory of process <paramref name="processId"/>, or null where it is gone or cannot be read.
	/// <para>
	/// Windows hands a dead process's id to the next process it starts, so a caller holding an id only says it is
	/// the process it means while that process runs. Where the caller knows when its process started by,
	/// <paramref name="startedByUtc"/> refuses one that started later, which is a different process with the id.
	/// </para>
	/// </summary>
	/// <param name="processId">The process.</param>
	/// <param name="managedHeapBytes">The managed heap, where the caller knows it.</param>
	/// <param name="startedByUtc">The latest the process can have started, where the caller knows it.</param>
	public static ProcessMemory? Read(int processId, long? managedHeapBytes = null, DateTime? startedByUtc = null)
	{
		try
		{
			using var process = Process.GetProcessById(processId);

			var recycled = startedByUtc is { } by && process.StartTime.ToUniversalTime() > by;
			if (recycled) return null;

			return new ProcessMemory
			{
				ProcessId = processId,
				WorkingSetBytes = process.WorkingSet64,
				PrivateMemoryBytes = process.PrivateMemorySize64,
				ManagedHeapBytes = managedHeapBytes,
			};
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
		{
			// Gone, or not this process's to read. No figures is more honest than stale ones.
			return null;
		}
	}

	/// <summary>This process's memory, its managed heap included.</summary>
	public static ProcessMemory Current()
	{
		using var process = Process.GetCurrentProcess();

		return new ProcessMemory
		{
			ProcessId = process.Id,
			WorkingSetBytes = process.WorkingSet64,
			PrivateMemoryBytes = process.PrivateMemorySize64,
			ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
		};
	}
}
