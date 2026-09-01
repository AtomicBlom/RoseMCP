namespace Ui;

/// <summary>
/// An interface and an unsealed implementation, so that a find-references on Greet has to walk up
/// to the interface member and down again through derived types. That walk is what builds Roslyn's
/// per-project index, which checksums every analyzer reference the project has -- the path that a
/// custom AnalyzerReference used to make throw.
/// </summary>
public interface IGreeter
{
	string Greet();
}

public class Greeter : IGreeter
{
	public virtual string Greet() => "hello";
}

public sealed class LoudGreeter : Greeter
{
	public override string Greet() => base.Greet().ToUpperInvariant();
}
