using System.Reflection;

using RoseMcp.Contracts;

namespace RoseMcp.Broker;

/// <summary>
/// Whether a child process is the build its parent expects, asked at the one moment the answer is
/// already on the wire.
/// <para>
/// Every host sends <see cref="BuildIdentity.ToHandshake"/> as its <c>ServerInfo.Version</c> during
/// <c>initialize</c>: the version with the commit as build metadata. A worker is resolved from a
/// <c>bin</c> tree that can hold several builds and an environment variable can point anywhere, so a
/// child from before the change under test is an ordinary state rather than an exotic one -- and two
/// local builds of different code share a version, so only the commit tells them apart.
/// </para>
/// <para>
/// A mismatch otherwise surfaces as whichever symptom it happens to produce: a new field on a result
/// becomes "could not read the worker's result", a removed argument is ignored and the child does
/// the old thing, a renamed tool becomes an unknown tool. Each sends the reader to the tool rather
/// than to the binary, and the commit that answers it is already being exchanged.
/// </para>
/// <para>
/// Said, not refused. A half-updated install is a state a person can be in without meaning to, and
/// the protocol usually survives it; turning a working session into a hard failure over a build
/// would cost more than the confusion it prevents. The path is the actionable half of the sentence,
/// because the cause is nearly always a stale <c>bin</c> or an environment variable pointing at one.
/// </para>
/// </summary>
public static class ChildHostVersion
{
	/// <summary>
	/// What to say about a child reporting <paramref name="reported"/>, or null where it is the
	/// build this assembly expects. Both builds are named, each with its commit, or with the commit
	/// said to be unknown where a side sent none -- a child from before commits were sent, or an
	/// archive build -- in which case the versions are what is compared.
	/// </summary>
	/// <param name="reported">The child's <c>ServerInfo.Version</c>, as it arrived.</param>
	/// <param name="path">Where the child was resolved from.</param>
	/// <param name="parent">The assembly whose build the child should match.</param>
	public static string? Mismatch(string? reported, string path, Assembly parent)
	{
		var expected = BuildIdentity.Of(parent);
		var name = Path.GetFileName(path);

		if (string.IsNullOrWhiteSpace(reported))
		{
			return $"{name} reported no version, where this build is {expected.Describe()}. "
				+ $"It was resolved from {path}.";
		}

		var difference = BuildIdentity.Mismatch(name, BuildIdentity.FromHandshake(reported), expected);
		if (difference is null) return null;

		return $"{difference} So it is not the binary this one was built with. A missing field, an "
			+ "ignored argument or an unknown tool is the mismatch rather than the call. It was resolved "
			+ $"from {path}.";
	}
}
