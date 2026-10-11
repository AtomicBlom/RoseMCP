using System.Globalization;

using RoseMcp.Contracts;

namespace RoseMcp.Ui.Core.Views;

/// <summary>
/// The memory bar along the bottom of every view: Rose's own total, split into the broker, its workers and its
/// live-app hosts; the debugged apps apart from it; and the machine's total.
/// <para>
/// The debugged apps are apart because they are not Rose's cost. An app under a debugger holds what it would hold
/// anyway, and counted into Rose's figure it would make Rose look several times its size, which is the one
/// misreading this bar exists to prevent. Working sets throughout, because that is the figure Task Manager shows a
/// person beside it; private bytes and the managed heap are in the detail.
/// </para>
/// </summary>
/// <param name="Rose">Rose's total.</param>
/// <param name="Split">The broker, the workers and the live-app hosts, each with its share.</param>
/// <param name="Debugged">Each debugged app and what it holds; empty where nothing is being debugged.</param>
/// <param name="Machine">The machine's memory in use, of its total.</param>
/// <param name="Detail">Every process on its own line, with its private bytes and managed heap, and when it was sampled.</param>
public sealed record MemorySummary(string Rose, string Split, string Debugged, string Machine, string Detail)
{
	/// <summary>What the bar says before the first sample arrives.</summary>
	public static MemorySummary Unknown { get; } = new("—", string.Empty, string.Empty, string.Empty, "Not sampled yet.");

	public bool HasDebugged => Debugged.Length > 0;

	/// <summary>The bar for <paramref name="report"/>.</summary>
	public static MemorySummary Of(MemoryReport report)
	{
		var workers = report.Workers.Where(worker => worker.Process is not null).ToList();
		var hosts = report.LiveApps.Where(app => app.Host is not null).ToList();

		var workerBytes = workers.Sum(worker => worker.Process!.WorkingSetBytes);
		var hostBytes = hosts.Sum(app => app.Host!.WorkingSetBytes);
		var total = report.Broker.WorkingSetBytes + workerBytes + hostBytes;

		var split = new List<string> { $"broker {Format.Bytes(report.Broker.WorkingSetBytes)}" };
		split.Add($"{Format.Count(workers.Count, "worker")} {Format.Bytes(workerBytes)}");
		if (hosts.Count > 0) split.Add($"{Format.Count(hosts.Count, "live-app host")} {Format.Bytes(hostBytes)}");

		var debugged = report.LiveApps
			.Where(app => app.Target is not null)
			.Select(app => $"{app.TargetDescription} {Format.Bytes(app.Target!.WorkingSetBytes)}")
			.ToList();

		var machine = report.Machine is { } host
			? host.AvailableBytes is { } available
				? $"Machine: {Gigabytes(host.TotalBytes - available)} of {Gigabytes(host.TotalBytes)} GB in use"
				: $"Machine: {Gigabytes(host.TotalBytes)} GB"
			: string.Empty;

		return new MemorySummary(
			Format.Bytes(total),
			string.Join(Format.Separator, split),
			debugged.Count == 0 ? string.Empty : $"Debugged: {string.Join(Format.Separator, debugged)}",
			machine,
			Describe(report));
	}

	private static string Describe(MemoryReport report)
	{
		var lines = new List<string> { $"broker: {Process(report.Broker)}" };

		lines.AddRange(report.Workers.Select(worker =>
			$"{Format.FileLine(worker.Workspace, line: null)} ({worker.WorkspaceKey}): {(worker.Process is { } process ? Process(process) : "not running")}"));

		foreach (var app in report.LiveApps)
		{
			lines.Add($"{app.TargetDescription}, session {app.SessionId}: host {(app.Host is { } host ? Process(host) : "not read")}");
			if (app.Target is { } target) lines.Add($"{app.TargetDescription} itself: {Process(target)}");
		}

		var sampled = DateTime.SpecifyKind(report.SampledUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
		lines.Add($"Sampled at {sampled}.");

		return string.Join(Environment.NewLine, lines);
	}

	private static string Process(ProcessMemory process)
	{
		var facts = new List<string>
		{
			$"working set {Format.Bytes(process.WorkingSetBytes)}",
			$"private {Format.Bytes(process.PrivateMemoryBytes)}",
		};

		if (process.ManagedHeapBytes is { } heap) facts.Add($"managed heap {Format.Bytes(heap)}");
		facts.Add(Format.Pid(process.ProcessId));

		return string.Join(", ", facts);
	}

	private static string Gigabytes(long bytes) =>
		(bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture);
}
