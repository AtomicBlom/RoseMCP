namespace RoseMcp.Ui.Core.Tray;

/// <summary>
/// What Windows says about starting this tray at sign-in, read when the menu opens.
/// </summary>
/// <param name="Enabled">Windows starts this exact executable at sign-in.</param>
/// <param name="Elsewhere">Windows starts some other copy: an install that has moved.</param>
/// <param name="Refused">The last change asked of Windows did not take.</param>
public sealed record StartupState(bool Enabled, bool Elsewhere, bool Refused);
