using System.Reflection;
using System.Runtime.Loader;

using static RoseMcp.Worker.WorkspaceStatusReporter;

namespace RoseMcp.UnitTests;

/// <summary>
/// Telling an assembly the worker's own code could not load from every other failure that reaches the
/// boundary. Too wide and a tool asked about a file that is not there tells the caller to restart the
/// worker; too narrow and the worker goes on calling itself healthy while two tools fail on every call.
/// </summary>
public sealed class AssemblyLoadFaultTests
{
	private const string Compression =
		"System.IO.Compression, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

	/// <summary>The shape it arrived in: the runtime naming a framework assembly by its display name.</summary>
	[Test]
	public void Reads_a_framework_assembly_that_would_not_load()
	{
		var fault = AssemblyLoadFault.From(Caught(ForeignCode.Throw), "rose_find_references", Path.GetTempPath());

		fault.ShouldNotBeNull();
		fault.Assembly.ShouldBe("System.IO.Compression");
		fault.Tool.ShouldBe("rose_find_references");
		fault.RuntimeDirectoryMissing.ShouldBeFalse();
	}

	/// <summary>A file the caller named not being there is the caller's mistake, and says so on its own.</summary>
	[Test]
	public void Leaves_a_missing_source_file_alone()
	{
		var missing = new FileNotFoundException("Could not find file.", @"D:\repo\src\Widget.cs");

		AssemblyLoadFault.From(missing, "rose_outline").ShouldBeNull();
		AssemblyLoadFault.From(new InvalidOperationException("Nothing is called Widget."), "rose_outline").ShouldBeNull();
	}

	[Test]
	public void Reads_an_assembly_that_was_found_and_would_not_load()
	{
		var load = new FileLoadException("Could not load file or assembly.", Compression);

		AssemblyLoadFault.From(load, "rose_symbol_info")!.Assembly.ShouldBe("System.IO.Compression");
	}

	/// <summary>Async code and reflection both wrap it, and the wrapper names nothing.</summary>
	[Test]
	public void Finds_it_inside_whatever_wrapped_it()
	{
		var wrapped = new AggregateException(new TargetInvocationException(Caught(ForeignCode.Throw)));

		AssemblyLoadFault.From(wrapped, "rose_symbol_info")!.Assembly.ShouldBe("System.IO.Compression");
	}

	/// <summary>
	/// A generator's own dependency missing is the generator's problem, which a rebuild fixes and
	/// analyzerLoadFailures reports. Telling the caller to restart the worker over it would be wrong advice.
	/// </summary>
	[Test]
	public void Leaves_a_failure_thrown_from_code_another_load_context_holds()
	{
		var context = new AssemblyLoadContext("an analyzer's load context", isCollectible: true);
		try
		{
			var foreign = context.LoadFromAssemblyPath(typeof(ForeignCode).Assembly.Location)
				.GetType(typeof(ForeignCode).FullName!)!
				.GetMethod(nameof(ForeignCode.Throw))!;

			var thrown = Caught(() => foreign.Invoke(null, null));

			thrown.ShouldBeOfType<TargetInvocationException>();
			AssemblyLoadFault.From(thrown, "rose_diagnostics").ShouldBeNull();
		}
		finally
		{
			context.Unload();
		}
	}

	/// <summary>
	/// A code fixer whose package did not ship one of its dependencies fails in whichever default-context frame
	/// called it, Roslyn's own included. The missing assembly is the package's, a fresh worker fails the same
	/// way, and degrading the workspace for life with restart advice over it would be wrong.
	/// </summary>
	[Test]
	public void Leaves_an_assembly_the_worker_was_not_started_with()
	{
		var missing = new FileNotFoundException(
			"Could not load file or assembly 'Some.Fixer.Helper, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'.",
			"Some.Fixer.Helper, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null");

		AssemblyLoadFault.From(Caught(() => throw missing), "rose_apply_code_fix").ShouldBeNull();
		AssemblyLoadFault.From(Caught(ForeignCode.Throw), "rose_apply_code_fix").ShouldNotBeNull(
			"System.IO.Compression is on the trusted platform list, so it is still the worker's");
	}

	/// <summary>
	/// .NET updated underneath a running worker takes its runtime directory away, which is the one cause of a
	/// framework assembly failing to load that the worker can see for itself, so it is said when it holds.
	/// </summary>
	[Test]
	public void Says_when_the_runtime_directory_has_gone()
	{
		var gone = Path.Combine(Path.GetTempPath(), $"rosemcp-no-runtime-{Guid.NewGuid():N}");

		var fault = AssemblyLoadFault.From(Caught(ForeignCode.Throw), "rose_symbol_info", gone);

		fault.ShouldNotBeNull();
		fault.RuntimeDirectoryMissing.ShouldBeTrue();
		fault.Refusal.ShouldContain(AssemblyLoadFault.RuntimeGone, Case.Sensitive);
	}

	/// <summary>
	/// The caller is told which tool, which assembly, that it is the worker and not the solution, and what
	/// gets a worker that can load it -- not only the loader's sentence about a file.
	/// </summary>
	[Test]
	public void The_refusal_says_the_worker_needs_replacing()
	{
		var refusal = AssemblyLoadFault.From(Caught(ForeignCode.Throw), "rose_find_references", Path.GetTempPath())!.Refusal;

		refusal.ShouldStartWith("rose_find_references could not run, because this worker could not load System.IO.Compression", Case.Sensitive);
		refusal.ShouldContain("Could not load file or assembly", Case.Sensitive);
		refusal.ShouldContain("not anything in the solution", Case.Sensitive);
		refusal.ShouldContain("rose_workspace_reload starts a fresh worker", Case.Sensitive);
		refusal.ShouldNotContain(AssemblyLoadFault.RuntimeGone, Case.Sensitive);
	}

	/// <summary>One assembly is one broken path through the worker, so the tools that hit it are named under it.</summary>
	[Test]
	public void Status_folds_faults_by_assembly_and_names_the_tools()
	{
		var reason = AssemblyLoadReason(
		[
			Fault("System.IO.Compression", "rose_find_references"),
			Fault("System.IO.Compression", "rose_symbol_info"),
			Fault("System.Memory", "rose_rename_symbol"),
		]);

		reason.ShouldNotBeNull();
		reason.ShouldStartWith("This worker could not load 2 assemblies its own code needs", Case.Sensitive);
		reason.ShouldContain("System.IO.Compression (rose_find_references, rose_symbol_info)", Case.Sensitive);
		reason.ShouldContain("System.Memory (rose_rename_symbol)", Case.Sensitive);
		reason.ShouldContain("rose_workspace_reload starts a fresh worker", Case.Sensitive);
	}

	[Test]
	public void Status_says_nothing_when_no_call_failed_to_load_an_assembly() =>
		AssemblyLoadReason([]).ShouldBeNull();

	private static AssemblyLoadFault Fault(string assembly, string tool) => new()
	{
		Assembly = assembly,
		Tool = tool,
		Message = $"Could not load file or assembly '{assembly}'.",
		RuntimeDirectoryMissing = false,
	};

	private static Exception Caught(Action action)
	{
		try
		{
			action();
		}
		catch (Exception exception)
		{
			return exception;
		}

		throw new InvalidOperationException("Nothing was thrown.");
	}

	/// <summary>
	/// Throws what the runtime throws when it cannot find a framework assembly. Public and static so a test
	/// can load this assembly a second time into a context of its own and call it there.
	/// </summary>
	public static class ForeignCode
	{
		public static void Throw() =>
			throw new FileNotFoundException(
				$"Could not load file or assembly '{Compression}'. The system cannot find the file specified.",
				Compression);
	}
}
