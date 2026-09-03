namespace Library;

/// <summary>What a signature change has to move through.</summary>
public interface INotifier
{
	/// <summary>Sends one message.</summary>
	/// <param name="message">What to say.</param>
	string Notify(string message);
}

/// <summary>The base, with an override under it and an interface above it.</summary>
public class Notifier : INotifier
{
	/// <summary>Sends one message.</summary>
	/// <param name="message">What to say.</param>
	public virtual string Notify(string message) => message;
}

/// <summary>An override that calls its parameter something else, as an override may.</summary>
public sealed class LoudNotifier : Notifier
{
	public override string Notify(string text) => text.ToUpperInvariant();
}

/// <summary>
/// The layer that made this tool necessary: a forwarder that compiles perfectly while quietly
/// passing the old default.
/// </summary>
public static class Forwarder
{
	public static string Send(INotifier notifier, string message) => notifier.Notify(message);

	public static string SendTwice(string message) => new Notifier().Notify(message) + new LoudNotifier().Notify(message);
}
