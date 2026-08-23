using Microsoft.CodeAnalysis;

namespace RoseMcp.Worker;

/// <summary>One file the synchronizer watches, and what it looked like last time.</summary>
internal readonly record struct TrackedDocument(DocumentId Id, string Path, TrackedDocumentKind Kind, FileStamp? Stamp);
