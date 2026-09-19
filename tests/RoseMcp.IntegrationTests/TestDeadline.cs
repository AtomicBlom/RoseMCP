[assembly: Timeout(RoseMcp.IntegrationTests.TestDeadline.PerTest)]

namespace RoseMcp.IntegrationTests;

/// <summary>
/// The ceiling on a single test, so that a wait which never ends costs ten minutes rather than a
/// night.
/// <para>
/// It bounds one test and not the run. Written on the assembly because that is how a default for
/// every test is expressed, not because the session has a budget: a full suite is expected to take
/// several times this, and adding tests moves the run further from the ceiling rather than closer
/// to it.
/// </para>
/// <para>
/// The suite waits on things that can stop arriving rather than fail: a worker's MCP handshake, a
/// reload after a branch switch, a debugged app's startup event. None of those is bounded on its
/// own, so a test that loses one sits on it, and the runner goes on reprinting the same progress
/// line for as long as it is left alone. Ten minutes is about seven times the slowest test that
/// passes -- a live-app property read, at a minute and a half -- so the ceiling binds only on
/// something that is not coming back. A test with a reason to want longer says so with a
/// <c>TimeoutAttribute</c> of its own, which wins over this one.
/// </para>
/// <para>
/// It binds a test that awaits, whether or not that test takes a <see cref="CancellationToken"/>:
/// the runner races the task the test returns either way, and the token decides only whether the
/// abandoned work is told to stop. It does not bind a test that blocks its thread before its first
/// await, because until that call returns there is no task to race. Anything waiting on a process,
/// a socket or a pipe therefore has to await it rather than block on it.
/// </para>
/// </summary>
internal static class TestDeadline
{
	/// <summary>Milliseconds, which is what <c>TimeoutAttribute</c> is given.</summary>
	internal const int PerTest = 10 * 60 * 1000;
}
