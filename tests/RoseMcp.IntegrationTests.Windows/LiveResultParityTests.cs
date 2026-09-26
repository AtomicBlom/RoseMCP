using System.Reflection;

using ModelContextProtocol.Server;

using RoseMcp.Contracts;
using RoseMcp.LiveApp;

namespace RoseMcp.IntegrationTests.Windows;

/// <summary>
/// Every answer the live-app host can send carries the event cursor, and this is the half of that
/// guarantee no filter can give.
/// <para>
/// <c>CursorStamp</c> writes the number onto every serialized answer, so no tool has to remember to.
/// What it cannot do is make the number survive the broker, which deserializes the host's answer into
/// the tool's own result type and re-serializes that -- a property the type does not declare is
/// dropped there, with nothing to show for it but a caller holding an answer with no position in it.
/// Deriving from <see cref="LiveResult"/> is what declares it, and this is what notices a result type
/// that does not.
/// </para>
/// </summary>
public sealed class LiveResultParityTests
{
	/// <summary>
	/// Reflection rather than a list kept by hand, because a list is a thing to update and the point
	/// of the rule is that a tool added later inherits it without anyone doing anything.
	/// </summary>
	[Test]
	public void Every_live_app_tool_answers_with_a_live_result()
	{
		var answers = ToolAnswers();

		// A test that found no tools would pass while proving nothing, and would go on passing if the
		// attribute or the assembly ever moved.
		(answers.Count > 10).ShouldBeTrue($"only {answers.Count} live-app tools were found, so this proved nothing");

		var offenders = answers
			.Where(answer => !typeof(LiveResult).IsAssignableFrom(answer.Answer))
			.Select(answer => $"{answer.Tool} answers with {answer.Answer.Name}")
			.ToList();

		(offenders.Count == 0).ShouldBeTrue(
			"a live-app tool's result type has to derive from LiveResult, or the cursor stamped on the "
				+ "answer is dropped where the broker reads it, and the caller cannot wait for what its "
				+ $"own action caused: {string.Join("; ", offenders)}");
	}

	/// <summary>Every tool the host exposes, with the type it answers with.</summary>
	private static List<(string Tool, Type Answer)> ToolAnswers() =>
	[
		.. from type in typeof(LiveAppSessionHost).Assembly.GetTypes()
		   where type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
		   from method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
		   where method.GetCustomAttribute<McpServerToolAttribute>() is not null
		   select ($"{type.Name}.{method.Name}", Answered(method.ReturnType)),
	];

	/// <summary>
	/// What a tool answers with, past whatever it wrapped the answer in. A tool that does its work
	/// synchronously returns the result itself, and the rule is about the result either way.
	/// </summary>
	private static Type Answered(Type returned)
	{
		if (!returned.IsGenericType) return returned;

		var wrapper = returned.GetGenericTypeDefinition();

		return wrapper == typeof(Task<>) || wrapper == typeof(ValueTask<>)
			? returned.GetGenericArguments()[0]
			: returned;
	}
}
