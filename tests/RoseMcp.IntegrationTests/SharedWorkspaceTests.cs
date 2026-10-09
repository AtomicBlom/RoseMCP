namespace RoseMcp.IntegrationTests;

/// <summary>
/// What keeps a shared workspace safe to share: a test that writes to it fails, rather than every
/// reader after it answering about a fixture that is no longer the one they were written against.
/// A workspace of its own rather than the run's, because this one is written to.
/// </summary>
public sealed class SharedWorkspaceTests
{
	[Test]
	public async Task Refuses_an_edit_and_then_a_read_once_a_file_appears_beside_it()
	{
		var cancellationToken = TestContext.Current!.Execution.CancellationToken;
		await using var workspace = new SharedWorkspace("Simple", "Simple.sln");

		var before = await workspace.ReadAsync(cancellationToken);

		// An edit to a file the fixture holds fails at the write, in the test that made it.
		var calculator = workspace.Path("Simple", "Core", "Calculator.cs");
		await Should.ThrowAsync<UnauthorizedAccessException>(
			() => File.AppendAllTextAsync(calculator, "// edited", cancellationToken));

		// A new file is the one write the copy cannot refuse, so the next read refuses instead.
		await File.WriteAllTextAsync(
			workspace.Path("Simple", "Core", "Added.cs"),
			"namespace Core;\r\n\r\npublic static class Added\r\n{\r\n}\r\n",
			cancellationToken);

		var refusal = await Should.ThrowAsync<InvalidOperationException>(() => workspace.ReadAsync(cancellationToken));

		refusal.Message.ShouldContain($"revision {before.Revision} then", Case.Sensitive);
		refusal.Message.ShouldContain("TestSession.OpenAsync", Case.Sensitive);
	}
}
