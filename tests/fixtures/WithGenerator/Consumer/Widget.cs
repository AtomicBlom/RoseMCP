using Gen;

namespace Consumer;

[Greetable("Hello")]
public partial class Widget
{
	public string Describe() => Greet();
}
