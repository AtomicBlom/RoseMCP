namespace RoseMcp.Contracts;

/// <summary>
/// One command's result as the XAML provider answers an apply, read by <see cref="XamlWire.ReadApplyRow"/>.
/// </summary>
/// <param name="Op">The command's op, as it was sent.</param>
/// <param name="Target">The command's target, as it was sent.</param>
/// <param name="Property">The command's property, as it was sent.</param>
/// <param name="Status"><c>applied</c>, or why not.</param>
/// <param name="Arg">The command's arg, as it was sent.</param>
/// <param name="Handle">The element the command landed on, or null where it reached none.</param>
/// <param name="Old">What the property said before the command, or null where the provider sent no such field.</param>
public sealed record XamlApplyRow(
	string Op,
	string Target,
	string Property,
	string Status,
	string Arg,
	ulong? Handle,
	string? Old);
