using System.Text.Json.Serialization;

namespace Sas.Auth;

public sealed class EmqxResponseTemplate : IResponseTemplate
{
    public object BuildAllow(AuthResult result)
    {
        var acl = new List<EmqxAclEntry>();
        foreach (var topic in result.AllTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "all", Topic = topic });
        foreach (var topic in result.PubTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "publish", Topic = topic });
        foreach (var topic in result.SubTopics)
            acl.Add(new EmqxAclEntry { Permission = "allow", Action = "subscribe", Topic = topic });

        // Deny rules go last: EMQX matches in order and the first hit wins,
        // so a deny must never shadow an allow above it.
        foreach (var rule in result.DenyRules)
            acl.Add(new EmqxAclEntry { Permission = "deny", Action = rule.Action, Topic = rule.Topic });

        return new EmqxAllowResponse
        {
            Result = "allow",
            IsSuperuser = result.IsSuperuser,
            ClientAttrs = new EmqxClientAttrs
            {
                Callsign = result.Callsign,
                Uid = result.Uid.ToString(),
                RootFingerprint = result.RootFingerprint,
                UserFingerprint = result.UserFingerprint,
                CaContactEmail = result.CaContactEmail
            },
            ExpireAt = result.ExpireAt,
            Acl = acl
        };
    }

    public object BuildDeny()
    {
        return new { result = "deny" };
    }

    private sealed class EmqxAllowResponse
    {
        [JsonPropertyName("result")]
        public string Result { get; init; } = "allow";

        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; init; }

        [JsonPropertyName("client_attrs")]
        public EmqxClientAttrs ClientAttrs { get; init; } = new();

        [JsonPropertyName("expire_at")]
        public long ExpireAt { get; init; }

        [JsonPropertyName("acl")]
        public List<EmqxAclEntry> Acl { get; init; } = [];
    }

    private sealed class EmqxClientAttrs
    {
        [JsonPropertyName("callsign")]
        public string Callsign { get; init; } = "";

        [JsonPropertyName("uid")]
        public string Uid { get; init; } = "";

        [JsonPropertyName("fmo_root_fingerprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RootFingerprint { get; init; }

        [JsonPropertyName("fmo_user_fingerprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserFingerprint { get; init; }

        [JsonPropertyName("fmo_ca_contact_email")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CaContactEmail { get; init; }
    }

    private sealed class EmqxAclEntry
    {
        [JsonPropertyName("permission")]
        public string Permission { get; init; } = "allow";

        [JsonPropertyName("action")]
        public string Action { get; init; } = "all";

        [JsonPropertyName("topic")]
        public string Topic { get; init; } = "";
    }
}
