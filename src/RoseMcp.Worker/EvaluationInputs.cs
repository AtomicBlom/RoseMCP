using Microsoft.Build.Evaluation;
using Microsoft.Extensions.Logging;

namespace RoseMcp.Worker;

/// <summary>
/// The files each project's evaluation read besides the project itself: every <c>.props</c> and
/// <c>.targets</c> it imported, resolved under the properties the solution was loaded with.
/// <para>
/// Roslyn's build host reports a project's documents, references and compiler command line, and
/// nothing about what it imported. Without that, the only build files a worker can notice changing
/// are the ones it knows by name -- so an edit to a file brought in with <c>&lt;Import&gt;</c>, or to
/// a <c>Directory.Build.props</c> nearer a project than its solution, changes how the project builds
/// and leaves the workspace answering from the evaluation before the edit, with nothing to say so.
/// </para>
/// <para>
/// Evaluated here rather than read from anything cheaper, because evaluation is the only thing that
/// resolves an import the way the build does: through properties, conditions and SDK resolution. It
/// costs tens of milliseconds a project, against seconds for the design-time build that precedes it.
/// It lists only imports that exist, so a build file that appears still has to be caught by its name.
/// </para>
/// </summary>
public sealed class EvaluationInputs
{
	private EvaluationInputs(IReadOnlyDictionary<string, IReadOnlySet<string>> imports, IReadOnlyList<string> unevaluated)
	{
		Imports = imports;
		Unevaluated = unevaluated;
	}

	/// <summary>Nothing evaluated.</summary>
	public static EvaluationInputs None { get; } = new(new Dictionary<string, IReadOnlySet<string>>(), []);

	/// <summary>Each evaluated project's imports, keyed by the full path of its project file.</summary>
	public IReadOnlyDictionary<string, IReadOnlySet<string>> Imports { get; }

	/// <summary>
	/// Projects whose evaluation failed here, so what they import is unknown. The design-time build can
	/// still have loaded them: a project whose targets ship only with Visual Studio's MSBuild evaluates
	/// there and not against the SDK.
	/// </summary>
	public IReadOnlyList<string> Unevaluated { get; }

	/// <summary>Every file any project imported, each once.</summary>
	public IEnumerable<string> Files =>
		Imports.Values.SelectMany(files => files).Distinct(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Evaluates each project once under <paramref name="globalProperties"/>, which must be the
	/// properties the design-time build used, since a configuration can decide what is imported.
	/// MSBuild must already be registered through <see cref="MSBuildRegistration.Ensure"/>.
	/// </summary>
	/// <param name="projectPaths">The project files to evaluate. Repeats are evaluated once.</param>
	/// <param name="globalProperties">The properties the solution was loaded with.</param>
	/// <param name="logger">Where a project that cannot be evaluated is reported.</param>
	/// <param name="cancellationToken">Checked between projects.</param>
	public static EvaluationInputs Evaluate(
		IEnumerable<string> projectPaths,
		IReadOnlyDictionary<string, string> globalProperties,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		var imports = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
		var unevaluated = new List<string>();

		using var collection = new ProjectCollection(
			globalProperties.ToDictionary(property => property.Key, property => property.Value));

		foreach (var path in projectPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				var project = collection.LoadProject(path);

				imports[path] = project.Imports
					.Select(import => Path.GetFullPath(import.ImportedProject.FullPath))
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				collection.UnloadProject(project);
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Whatever evaluation throws means the same thing here: this project's imports are
				// unknown, and only the ambient walk watches its build files. An invalid project, a
				// missing SDK and a resolver error all end up here, and none of them should fail a
				// load the design-time build has already completed.
				unevaluated.Add(path);
				logger.LogWarning(
					exception,
					"Could not evaluate {Project} to read its imports; only its ambient build files are tracked.",
					path);
			}
		}

		return new EvaluationInputs(imports, unevaluated);
	}
}
