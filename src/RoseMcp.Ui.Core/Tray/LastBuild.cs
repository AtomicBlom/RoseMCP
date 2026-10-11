namespace RoseMcp.Ui.Core.Tray;

/// <summary>The build an install ran last, and when it started.</summary>
/// <param name="Commit">The commit it was built from.</param>
/// <param name="RanUtc">When it started, which decides which installs are forgotten first.</param>
public sealed record LastBuild(string Commit, DateTimeOffset RanUtc);
