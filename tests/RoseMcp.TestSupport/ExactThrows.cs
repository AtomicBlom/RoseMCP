using Shouldly;

namespace RoseMcp.TestSupport;

/// <summary>
/// Shouldly's <c>Should.ThrowAsync&lt;T&gt;</c> passes for any exception assignable to <c>T</c>. A test
/// that names a type means that type: a <c>TaskCanceledException</c> where an
/// <c>OperationCanceledException</c> was asked for is a different failure, and passing it hides one.
/// </summary>
public static class ExactThrows
{
	/// <summary>
	/// The exception <paramref name="thrown"/> caught, required to be exactly <typeparamref name="T"/>
	/// rather than something derived from it.
	/// </summary>
	public static async Task<T> OfExactType<T>(this Task<T> thrown)
		where T : Exception
	{
		var exception = await thrown.ConfigureAwait(false);

		return exception.ShouldBeOfType<T>();
	}
}
