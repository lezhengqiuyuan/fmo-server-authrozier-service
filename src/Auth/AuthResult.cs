namespace Sas.Auth;

public sealed class AuthResult
{
    public string Callsign { get; init; } = "";
    public long Uid { get; init; }
    public string? RootFingerprint { get; init; }
    public string? UserFingerprint { get; init; }
    public string? CaContactEmail { get; init; }
    public string Role { get; init; } = "user";
    public bool IsSuperuser { get; init; }
    public long IssuedAt { get; init; }
    public long ExpireAt { get; init; }
    public string[] AllTopics { get; init; } = [];
    public string[] PubTopics { get; init; } = [];
    public string[] SubTopics { get; init; } = [];
    public AclDenyRule[] DenyRules { get; init; } = [];
}
