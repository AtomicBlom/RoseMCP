using System.Text.RegularExpressions;

using RoseMcp.Contracts;

namespace RoseMcp.UnitTests;

/// <summary>
/// Which XAML provider requests change the app, which is what decides whether a caller whose wait
/// expired is told the request may still land.
/// <para>
/// Pinned here because the code that composes the message cannot be reached: it is internal to the
/// live-app host, which this project does not reference and the Windows integration tests see only
/// the public surface of. The
/// failure being guarded is not a wrong sentence but a missing one -- a verb added to the provider
/// that nobody classified, timing out, and reporting a plain failure for a change that then lands.
/// </para>
/// </summary>
public sealed class XamlRequestKindTests
{
	/// <summary>
	/// Every verb the provider serves, and whether it changes the app. The provider's own dispatch is
	/// the authority for the left column, and <see cref="The_provider_serves_no_verb_this_has_not_classified"/>
	/// is what keeps this in step with it.
	/// </summary>
	private static readonly Dictionary<string, bool> Verbs = new(StringComparer.Ordinal)
	{
		["tree"] = false,
		["properties"] = false,
		["selection"] = false,
		["select"] = true,
		["selecthandle"] = true,
		["idle"] = true,
		["deselect"] = true,
		["apply"] = true,
		["detach"] = true,
	};

	/// <summary>Where the provider decides what a request means, which is the list this must match.</summary>
	private const string ProviderDispatch = "src/RoseMcp.Xaml.Tap/tap_object.h";

	[Test]
	public void Classifies_every_verb_the_provider_serves()
	{
		foreach (var (verb, mutates) in Verbs)
		{
			XamlRequestKind.Mutates(verb).ShouldBe(mutates);
		}
	}

	/// <summary>
	/// The real spellings, arguments and all, because the verb is what decides and a handle or a
	/// batch of commands after it must not.
	/// </summary>
	[Test]
	[Arguments("tree", false)]
	[Arguments("properties 1234", false)]
	[Arguments("properties 1234 all", false)]
	[Arguments("selection", false)]
	[Arguments("select", true)]
	[Arguments("select rulers all myxaml", true)]
	[Arguments("selecthandle 1234", true)]
	[Arguments("apply\nSetProperty\tGrid[0]\tBackground", true)]
	public void Arguments_do_not_change_the_verb(string request, bool mutates)
	{
		XamlRequestKind.Mutates(request).ShouldBe(mutates);
	}

	/// <summary>
	/// The direction the default has to fail in. A verb nobody classified is warned about, so the
	/// cost of forgetting is a sentence on a read rather than silence on a change that landed.
	/// </summary>
	[Test]
	public void A_verb_nobody_classified_is_treated_as_mutating()
	{
		XamlRequestKind.Mutates("scrollto 1234").ShouldBeTrue();
		XamlRequestKind.Mutates(string.Empty).ShouldBeTrue();
	}

	[Test]
	public void Only_a_mutating_request_is_caveated()
	{
		XamlRequestKind.Caveat("tree").ShouldBe(string.Empty);
		XamlRequestKind.Caveat("selecthandle 1234").ShouldContain(XamlRequestKind.MayStillLand, Case.Sensitive);
	}

	/// <summary>
	/// The guard that matters: the provider's dispatch and the classification are two lists that have
	/// to hold the same verbs, and only one of them is compiled against anything. A verb added there
	/// fails here until somebody says which kind it is.
	/// </summary>
	[Test]
	public void The_provider_serves_no_verb_this_has_not_classified()
	{
		var dispatch = File.ReadAllText(
			Path.Combine(RepositoryRoot(), ProviderDispatch.Replace('/', Path.DirectorySeparatorChar)));

		// Both spellings the dispatch uses: an exact match for a verb taking no arguments, and a
		// prefix match for one that does. The lowercase class is what keeps " all" -- a suffix the
		// properties verb tests for separately -- from reading as a verb of its own.
		var served = Regex.Matches(dispatch, @"request\s*(?:== L""([a-z]+)""|\.rfind\(L""([a-z]+)[ \\])")
			.Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
			.ToHashSet(StringComparer.Ordinal);

		served.ShouldNotBeEmpty();

		// Joined rather than asserted empty so a failure names the verb instead of a count.
		string.Join(", ", served.Where(verb => !Verbs.ContainsKey(verb)).Order()).ShouldBe(string.Empty);
		string.Join(", ", Verbs.Keys.Where(verb => !served.Contains(verb)).Order()).ShouldBe(string.Empty);
	}

	private static string RepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "RoseMcp.slnx"))) return directory.FullName;
		}

		throw new InvalidOperationException($"No RoseMcp.slnx above {AppContext.BaseDirectory}.");
	}
}
