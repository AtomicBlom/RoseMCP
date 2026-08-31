using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>
/// The opened workspace and the report describing how it went. The caller owns the workspace.
/// <para>
/// The properties it was opened under travel with it, so status reported later says the same thing
/// as status reported at load rather than re-deriving it from a file that may since have changed.
/// </para>
/// </summary>
public sealed record LoadResult(
	MSBuildWorkspace Workspace,
	Solution Solution,
	WorkspaceStatusReport Report,
	BuildProperties Build);
