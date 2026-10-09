namespace RoseMcp.Worker;

/// <summary>One file's diff, and the lines it changed as the file now reads them.</summary>
/// <param name="Text">The unified diff, empty where no line's content changed.</param>
/// <param name="Lines">The changed lines as ranges, or null where none changed.</param>
public sealed record FileDiff(string Text, string? Lines);
