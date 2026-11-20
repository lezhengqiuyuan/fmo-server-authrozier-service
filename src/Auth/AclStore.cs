using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sas.Logging;

namespace Sas.Auth;

/// <summary>A single deny rule emitted in the EMQX ACL.</summary>
public sealed record AclDenyRule
{
    [JsonPropertyName("action")]
    public string Action { get; init; } = "all";

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = "";
}

public sealed class AclStore
{
    private const string ResourcePrefix = "builtin.roles.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Dictionary<string, RoleAcl> _roles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BuiltinTemplate> _builtinTemplates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overridesWithBuiltin = new(StringComparer.OrdinalIgnoreCase);

    public AclStore(string rolesDir, bool allowEmptyAcl = false)
    {
        LoadBuiltinTemplates();
        if (_builtinTemplates.Count == 0)
        {
            throw new InvalidOperationException(
                "SAS built-in role templates are missing from the assembly. " +
                "The build is corrupt; refusing to start with undefined ACL rules.");
        }

        SeedBuiltinRoles(rolesDir);
        LoadRoles(rolesDir);
        ApplyBuiltinOverrides();

        if (_roles.Count == 0)
        {
            Logger.Warn("No role definitions loaded, using built-in templates");
            foreach (var template in _builtinTemplates.Values)
                _roles[template.Acl.Role] = template.Acl;
        }

        ValidateEmptyRoles(allowEmptyAcl);
    }

    private void ValidateEmptyRoles(bool allowEmptyAcl)
    {
        var emptyRoles = _roles.Values
            .Where(acl => (acl.All?.Length ?? 0) == 0 &&
                          (acl.Pub?.Length ?? 0) == 0 &&
                          (acl.Sub?.Length ?? 0) == 0)
            .Select(acl => acl.Role)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (emptyRoles.Length == 0)
            return;

        foreach (var role in emptyRoles)
        {
            Logger.Error(
                $"ACL roles: role '{role}' has no allow rules. " +
                "With EMQX authorization.no_match=allow this grants full access.");
        }

        if (!allowEmptyAcl)
        {
            throw new InvalidOperationException(
                $"ACL roles with no allow rules: {string.Join(", ", emptyRoles)}. " +
                "Fix the role file(s) or set acl.allowEmptyAcl=true to accept this explicitly.");
        }
    }

    /// <summary>
    /// Keeps the legacy layout: roles live next to the roots directory ("&lt;parent&gt;/roles").
    /// </summary>
    public static AclStore FromRootsDir(string rootsDir, bool allowEmptyAcl = false)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootsDir));
        return new AclStore(Path.Combine(Path.GetDirectoryName(normalized)!, "roles"), allowEmptyAcl);
    }

    public (string[] all, string[] pub, string[] sub, AclDenyRule[] deny) GetPermissions(string role)
    {
        if (!string.IsNullOrEmpty(role) && _roles.TryGetValue(role, out var acl))
            return (acl.All.ToArray(), acl.Pub.ToArray(), acl.Sub.ToArray(), acl.Deny.ToArray());

        if (_roles.TryGetValue("user", out var userAcl))
            return (userAcl.All.ToArray(), userAcl.Pub.ToArray(), userAcl.Sub.ToArray(), userAcl.Deny.ToArray());

        return ([], [], [], []);
    }

    /// <summary>True when a role is explicitly defined by a loaded role file.</summary>
    public bool HasRole(string role) =>
        !string.IsNullOrEmpty(role) && _roles.ContainsKey(role);

    public string[] ResolveForUid(string[] topics, long uid)
    {
        return topics.Select(t => t.Replace("{uid}", uid.ToString(CultureInfo.InvariantCulture))).ToArray();
    }

    public AclDenyRule[] ResolveForUid(AclDenyRule[] rules, long uid)
    {
        return rules
            .Select(r => r with { Topic = r.Topic.Replace("{uid}", uid.ToString(CultureInfo.InvariantCulture)) })
            .ToArray();
    }

    /// <summary>
    /// Reads the role templates embedded in the assembly. These are the single
    /// authoritative source for built-in rules; the on-disk copies are only
    /// seeded artefacts and are never used as a fallback.
    /// </summary>
    private void LoadBuiltinTemplates()
    {
        var assembly = typeof(AclStore).Assembly;

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null)
                    continue;

                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                var acl = ParseRole(text);

                if (acl == null || string.IsNullOrEmpty(acl.Role))
                {
                    Logger.Error($"ACL roles: invalid embedded template {name}");
                    continue;
                }

                var fileName = name[ResourcePrefix.Length..];

                // Active templates must be flat "<role>.json". Anything else
                // (for example a mis-prefixed history resource) would be
                // seeded as if it were a role and must be rejected.
                if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    fileName[..^".json".Length].Contains('.'))
                {
                    Logger.Error($"ACL roles: unexpected embedded template {name}, ignoring");
                    continue;
                }

                _builtinTemplates[fileName] = new BuiltinTemplate(acl, text, RoleJson.Hash(text));
                Logger.Info($"Loaded built-in role: {acl.Role} ({fileName})");
            }
            catch (Exception ex)
            {
                Logger.Error($"ACL roles: failed to load embedded template {name}: {ex.Message}");
            }
        }
    }

    private void SeedBuiltinRoles(string rolesDir)
    {
        try
        {
            Directory.CreateDirectory(rolesDir);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ACL roles: cannot create {rolesDir}: {ex.Message}");
        }

        foreach (var (fileName, template) in _builtinTemplates)
        {
            var dest = Path.Combine(rolesDir, fileName);

            if (!File.Exists(dest))
            {
                try
                {
                    File.WriteAllText(dest, template.Text);
                    Logger.Info($"Seeded role: {fileName}");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"ACL roles: cannot seed {fileName}: {ex.Message}; using builtin rules in memory");
                    _overridesWithBuiltin.Add(template.Acl.Role);
                }

                continue;
            }

            TrySyncRole(fileName, dest, template);
        }
    }

    private void TrySyncRole(string name, string dest, BuiltinTemplate template)
    {
        var builtinAcl = template.Acl;

        string serverText;
        try
        {
            serverText = File.ReadAllText(dest);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ACL roles: cannot read {name}: {ex.Message}; using builtin rules in memory");
            _overridesWithBuiltin.Add(builtinAcl.Role);
            return;
        }

        RoleAcl? serverAcl;
        try
        {
            serverAcl = ParseRole(serverText);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ACL roles: {name} is not valid JSON ({ex.Message}), keeping as-is; using builtin rules in memory");
            _overridesWithBuiltin.Add(builtinAcl.Role);
            return;
        }

        // A built-in file must declare the matching role. A corrupt or renamed
        // role would otherwise leave the real role without permissions, which
        // (with EMQX no_match=allow) means full access.
        if (serverAcl == null ||
            string.IsNullOrEmpty(serverAcl.Role) ||
            !string.Equals(serverAcl.Role, builtinAcl.Role, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"ACL roles: {name} has an invalid or mismatched role, keeping as-is; using builtin rules in memory");
            _overridesWithBuiltin.Add(builtinAcl.Role);
            return;
        }

        if (serverAcl.Version > builtinAcl.Version)
        {
            Logger.Warn($"ACL roles: {name} version {serverAcl.Version} is newer than builtin {builtinAcl.Version}, keeping as-is");
            return;
        }

        var serverHash = RoleJson.Hash(serverText);
        if (serverHash == null)
        {
            Logger.Warn($"ACL roles: {name} is not valid JSON, keeping as-is; using builtin rules in memory");
            _overridesWithBuiltin.Add(builtinAcl.Role);
            return;
        }

        if (serverHash == template.Hash)
            return;

        var isShippedDefault = BuiltinRoleHistory.IsDefault(name, serverHash);

        if (serverAcl.Version == builtinAcl.Version)
        {
            if (!isShippedDefault)
            {
                Logger.Info($"ACL roles: {name} was customised (version {serverAcl.Version}), keeping as-is");
                return;
            }

            // Same version but a known shipped default means a release forgot
            // to bump _version. The content is not the owner's, so repair it.
            Logger.Warn(
                $"ACL roles: {name} matches a shipped default from an earlier build " +
                $"with the same version {serverAcl.Version}; syncing to builtin (backup: {name}.bak)");
            ApplyTemplate(name, dest, template, serverAcl.Version);
            return;
        }

        // The server file is older than the builtin template. Only replace it
        // when its canonical content matches a shipped default; otherwise the
        // owner has customised the file and it must be left alone.
        if (!isShippedDefault)
        {
            Logger.Info(
                $"ACL roles: {name} is not a known shipped default (version {serverAcl.Version}), " +
                $"keeping as-is; builtin version is {builtinAcl.Version}");
            return;
        }

        ApplyTemplate(name, dest, template, serverAcl.Version);
    }

    /// <summary>Backs up <paramref name="dest"/> and replaces it with the builtin template.</summary>
    private void ApplyTemplate(string name, string dest, BuiltinTemplate template, int fromVersion)
    {
        try
        {
            File.Copy(dest, dest + ".bak", overwrite: true);
            File.WriteAllText(dest, template.Text);
            Logger.Info($"ACL roles: synced {name} v{fromVersion} -> v{template.Acl.Version} (backup: {name}.bak)");
        }
        catch (Exception ex)
        {
            Logger.Warn($"ACL roles: cannot update {name} ({ex.Message}); using builtin rules in memory");
            _overridesWithBuiltin.Add(template.Acl.Role);
        }
    }

    private void ApplyBuiltinOverrides()
    {
        foreach (var role in _overridesWithBuiltin)
        {
            var template = _builtinTemplates.Values.FirstOrDefault(t => t.Acl.Role == role);
            if (template == null)
                continue;

            _roles[role] = template.Acl;
            Logger.Warn($"ACL roles: using built-in {role} rules in memory");
        }
    }

    private void LoadRoles(string rolesDir)
    {
        if (!Directory.Exists(rolesDir))
            return;

        foreach (var file in Directory.GetFiles(rolesDir, "*.json"))
        {
            var fileName = Path.GetFileName(file);

            try
            {
                var acl = ParseRole(File.ReadAllText(file));

                if (acl == null || string.IsNullOrEmpty(acl.Role))
                {
                    Logger.Error($"Failed to load role {fileName}: missing role name");
                    continue;
                }

                // A built-in file may only declare its own role. Otherwise a
                // corrupted or renamed user.json could overwrite another role.
                if (_builtinTemplates.TryGetValue(fileName, out var template) &&
                    !string.Equals(acl.Role, template.Acl.Role, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warn($"ACL roles: {fileName} declares role '{acl.Role}' but must be '{template.Acl.Role}', ignoring");
                    continue;
                }

                _roles[acl.Role] = acl;
                Logger.Info($"Loaded role: {acl.Role} ({fileName})");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load role {fileName}: {ex.Message}");
            }
        }
    }

    private static RoleAcl? ParseRole(string text)
    {
        var acl = JsonSerializer.Deserialize<RoleAcl>(text, JsonOptions);
        if (acl != null)
        {
            acl.All ??= [];
            acl.Pub ??= [];
            acl.Sub ??= [];
            acl.Deny ??= [];
        }

        return acl;
    }

    private sealed record BuiltinTemplate(RoleAcl Acl, string Text, string? Hash);

    private sealed class RoleAcl
    {
        public string Role { get; set; } = "";

        [JsonPropertyName("_version")]
        public int Version { get; set; }

        public string[] All { get; set; } = [];
        public string[] Pub { get; set; } = [];
        public string[] Sub { get; set; } = [];
        public List<AclDenyRule> Deny { get; set; } = [];
    }
}
