namespace ADOPullRequestAgent;

public class AgentOptions
{
    /// <summary>
    /// Gets or sets the TCP port number used for CLI (Command-Line Interface) connections.
    /// </summary>
    public int CliPort { get; set; }

    public PlatformID CliOsPlatform { get; set; } = PlatformID.Unix;

    /// <summary>
    /// Gets or sets the name or identifier of the model.
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// Gets or sets the local directory path where the repository source code is cloned.
    /// The agent uses this path to run git commands and read source files during the review.
    /// </summary>
    public required string SourcesDirectory { get; set; }
}