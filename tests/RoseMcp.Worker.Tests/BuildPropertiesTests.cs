namespace RoseMcp.Worker.Tests;

public sealed class BuildPropertiesTests
{
	[Fact]
	public void Leaves_msbuild_alone_when_the_solution_declares_nothing()
	{
		var build = BuildProperties.Select(Options(), SolutionConfigurations.None);

		Assert.Null(build.Configuration);
		Assert.Null(build.Platform);
		Assert.Empty(build.AsGlobalProperties());
		Assert.Null(build.Notice);
	}

	[Fact]
	public void Leaves_msbuild_alone_when_the_solution_declares_the_defaults()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug", "Release"],
			Platforms = ["AnyCPU", "x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		Assert.Null(build.Configuration);
		Assert.Null(build.Platform);
		Assert.Null(build.Notice);
	}

	[Fact]
	public void Picks_a_declared_configuration_when_the_solution_has_no_plain_Debug()
	{
		var declared = new SolutionConfigurations
		{
			Configurations = ["Debug-2024", "Debug-2025", "Release"],
			Platforms = ["x64"],
		};

		var build = BuildProperties.Select(Options(), declared);

		Assert.Equal("Debug-2024", build.Configuration);
		Assert.Equal("x64", build.Platform);
		Assert.Equal("Debug-2024|x64", build.Describe());

		// The notice has to name both the choice and the alternatives, because the choice is a guess
		// and the caller is the only one who can correct it.
		Assert.Contains("Debug-2024", build.Notice);
		Assert.Contains("Debug-2025", build.Notice);
		Assert.Contains("x64", build.Notice);
	}

	[Fact]
	public void Honours_a_requested_configuration_the_solution_does_not_declare()
	{
		var declared = new SolutionConfigurations { Configurations = ["Debug-2024"], Platforms = ["x64"] };
		var options = new WorkerOptions { SolutionPath = "S.slnx", Configuration = "Debug-2027" };

		var build = BuildProperties.Select(options, declared);

		Assert.Equal("Debug-2027", build.Configuration);
		Assert.Contains("Debug-2027", build.Notice);
		Assert.Contains("not one this solution declares", build.Notice);
	}

	[Fact]
	public void Carries_pinned_properties_into_both_the_build_and_the_restore()
	{
		var options = new WorkerOptions
		{
			SolutionPath = "S.slnx",
			Configuration = "Release",
			Properties = new Dictionary<string, string> { ["RevitVersion"] = "2027" },
		};

		var build = BuildProperties.Select(options, SolutionConfigurations.None);

		Assert.Equal("2027", build.AsGlobalProperties()["RevitVersion"]);
		Assert.Equal("Release", build.AsGlobalProperties()["Configuration"]);
		Assert.Contains("-p:RevitVersion=2027", build.AsRestoreArguments());
		Assert.Contains("RevitVersion=2027", build.Describe());
	}

	private static WorkerOptions Options() => new() { SolutionPath = "S.slnx" };
}
