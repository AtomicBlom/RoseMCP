using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

using RoseMcp.Contracts;

namespace RoseMcp.Worker;

/// <summary>The opened workspace and the report describing how it went. The caller owns the workspace.</summary>
public sealed record LoadResult(MSBuildWorkspace Workspace, Solution Solution, WorkspaceStatusReport Report);
