namespace ArkaCode;

using System.Collections.Generic;

public sealed record AiModel(string Id, string DisplayName, string Provider, string EndpointPath)
{
    public string Label => $"{Provider}  ·  {DisplayName}";
}

public sealed record WorkspaceFile(string RelativePath, string Content);
public sealed record FileOperation(string Path, string Content, string Action = "write");
public sealed record AgentResult(string Summary, string Explanation, IReadOnlyList<FileOperation> Operations, string Raw);

public sealed class ChatEntry
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public string Meta { get; init; } = "";
    public bool IsAgent => Role == "agent";
}
