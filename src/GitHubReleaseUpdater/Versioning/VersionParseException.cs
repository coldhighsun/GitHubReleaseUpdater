namespace GitHubReleaseUpdater.Versioning;

/// <summary>Thrown when a string cannot be parsed as a <see cref="SemanticVersion"/>.</summary>
public sealed class VersionParseException : FormatException
{
    /// <summary>The original input that failed to parse.</summary>
    public string Input { get; }

    /// <inheritdoc />
    public VersionParseException() : base("Invalid semantic version.") { Input = string.Empty; }

    /// <inheritdoc />
    public VersionParseException(string message, Exception? innerException) : base(message, innerException) { Input = string.Empty; }

    /// <summary>Creates a new instance for the given input.</summary>
    public VersionParseException(string input)
        : base($"'{input}' is not a valid semantic version.")
    {
        Input = input;
    }
}
