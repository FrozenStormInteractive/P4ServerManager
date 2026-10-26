namespace P4ServerManager.Cli.Api;

/// <summary>
/// Body of an error answer of the daemon: a problem details document, with the errors keyed by field for a request
/// that is not valid.
/// </summary>
/// <param name="Title">Short summary of the problem.</param>
/// <param name="Detail">Explanation of this occurrence of the problem.</param>
/// <param name="Status">HTTP status code.</param>
/// <param name="Errors">Validation errors, keyed by the JSON name of the field.</param>
internal sealed record ApiProblem(
    string? Title,
    string? Detail,
    int? Status,
    IReadOnlyDictionary<string, string[]>? Errors);
