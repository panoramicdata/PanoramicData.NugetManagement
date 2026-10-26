using PanoramicData.NugetManagement.Rules;

namespace PanoramicData.NugetManagement.Web.Services.Playbooks;

/// <summary>
/// HTTP-02: give the client options an <c>IsReadOnly</c> property and enforce it in the HTTP pipeline.
/// </summary>
/// <remarks>
/// Needs a playbook because the rule can only see the options file; the handler that enforces the option
/// differs in every package, so the model has to find the pipeline and match it.
/// </remarks>
public sealed class ClientOptionsIsReadOnlyPlaybook : IRuleAiPlaybook
{
	/// <inheritdoc />
	public string RuleId => "HTTP-02";

	/// <inheritdoc />
	public string Goal
		=> "Add an IsReadOnly option to the API client options, default false, and make the HTTP pipeline refuse every non-read request when it is true.";

	/// <inheritdoc />
	public IReadOnlyList<string> Files
		=> ["the *ClientOptions.cs files named under 'options_files' in the facts", "the DelegatingHandler or HttpClient setup that sends requests"];

	/// <inheritdoc />
	public string ExpectedEndState
		=> "The options class has 'public bool IsReadOnly { get; set; }' with no initialiser, so it defaults to false. "
			+ "If it had a 'ReadOnly' property, that is now a read-write alias marked [Obsolete(\"Use IsReadOnly\")] "
			+ "and every use of it inside the repository uses IsReadOnly instead. A handler reads IsReadOnly on each "
			+ "request and throws InvalidOperationException before sending anything other than GET, HEAD or OPTIONS, "
			+ "allowing by name any POST the API uses for a read. A test shows a write is refused when IsReadOnly is true "
			+ "and a read still works. The solution builds with no new warnings.";

	/// <inheritdoc />
	public string WorkedExample
		=> """
			Options:

			  public bool IsReadOnly { get; set; }

			  [Obsolete("Use IsReadOnly")]
			  public bool ReadOnly
			  {
			      get => IsReadOnly;
			      set => IsReadOnly = value;
			  }

			Handler, before the request is sent:

			  if (_options.IsReadOnly && request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
			  {
			      throw new InvalidOperationException("The client options have been configured to only allow read actions");
			  }

			Omit the ReadOnly alias if the options never had one. Never change the default to true.
			If the options are read once at construction, read IsReadOnly per request so a later change takes effect.
			""";
}
