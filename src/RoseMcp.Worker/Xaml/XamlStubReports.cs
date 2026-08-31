using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker.Xaml;

/// <summary>
/// Where the generators leave word of what they did, keyed by project.
/// <para>
/// A source generator can only speak in generated source and diagnostics, neither of which suits a
/// status report. Since these generators are our own code rather than a loaded assembly, each is
/// handed a callback into here -- which is also why the key can be a ProjectId: one generator
/// instance per project, so multi-targeted projects sharing an assembly name stay distinct.
/// </para>
/// </summary>
public sealed class XamlStubReports
{
	private readonly ConcurrentDictionary<ProjectId, XamlStubReport> _byProject = [];

	/// <summary>Last write wins: a generator re-runs whenever the compilation changes.</summary>
	public void Record(ProjectId project, XamlStubReport report) => _byProject[project] = report;

	public XamlStubReport? For(ProjectId project) =>
		_byProject.TryGetValue(project, out var report) ? report : null;

	/// <summary>Called when a solution is reloaded; the previous snapshot's projects are gone.</summary>
	public void Clear() => _byProject.Clear();
}
