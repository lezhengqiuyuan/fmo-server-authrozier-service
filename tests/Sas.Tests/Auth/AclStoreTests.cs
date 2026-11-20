using System.Text.Json;
using Sas.Auth;
using Sas.Tests.Fixtures;

namespace Sas.Tests.Auth;

public sealed class AclStoreTests
{
    [Fact]
    public void EmptyRolesDirectory_ProvidesBuiltinRoles()
    {
        using var temp = new TempDirectory("acl-builtin");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var super = store.GetPermissions("super");
        var admin = store.GetPermissions("admin");
        var user = store.GetPermissions("user");

        Assert.NotEmpty(super.pub);
        Assert.NotEmpty(super.sub);
        Assert.Contains("FMO/MUTELIST", super.pub);
        Assert.Contains("FMO/PROFILE", super.sub);
        Assert.Contains("FMO/MUTELIST", super.sub);
        Assert.Contains("FMO/REMOTE_CONTROL", super.sub);
        Assert.Contains("FMO/SERVER_INFO/#", super.sub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, super.deny);

        Assert.DoesNotContain("FMO/MUTELIST", user.pub);
        Assert.Contains("FMO/QSO/UID/{uid}", user.sub);
        Assert.Contains("FMO/PROFILE", user.sub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, user.deny);
        Assert.Contains(new AclDenyRule { Action = "publish", Topic = "FMO/PROFILE" }, user.deny);

        Assert.Contains("FMO/MUTELIST", admin.pub);
        Assert.DoesNotContain("FMO/PROFILE", admin.pub);
        Assert.DoesNotContain("FMO/REMOTE_CONTROL", admin.pub);
        Assert.DoesNotContain("FMO/SERVER_INFO/#", admin.pub);
        Assert.Contains("FMO/PROFILE", admin.sub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, admin.deny);
        Assert.Contains(new AclDenyRule { Action = "publish", Topic = "FMO/PROFILE" }, admin.deny);
        Assert.Contains(new AclDenyRule { Action = "publish", Topic = "FMO/REMOTE_CONTROL" }, admin.deny);
        Assert.Contains(new AclDenyRule { Action = "publish", Topic = "FMO/SERVER_INFO/#" }, admin.deny);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/LATE/UID_V1/#" }, admin.deny);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-role")]
    public void UnknownOrEmptyRole_FallsBackToUser(string? role)
    {
        using var temp = new TempDirectory("acl-fallback");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var user = store.GetPermissions("user");
        var actual = store.GetPermissions(role!);

        Assert.Equal(user.all, actual.all);
        Assert.Equal(user.pub, actual.pub);
        Assert.Equal(user.sub, actual.sub);
        // deny 也必须一起回退，否则 SAFEPROBE / LATE 限制会丢失。
        Assert.Equal(user.deny, actual.deny);
        Assert.NotEmpty(actual.deny);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("ADMIN")]
    [InlineData("sUpEr")]
    public void RoleName_IsCaseInsensitive(string role)
    {
        using var temp = new TempDirectory("acl-role-case");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var expected = store.GetPermissions(role.ToLowerInvariant());
        var actual = store.GetPermissions(role);

        Assert.True(store.HasRole(role));
        Assert.Equal(expected.all, actual.all);
        Assert.Equal(expected.pub, actual.pub);
        Assert.Equal(expected.sub, actual.sub);
        Assert.Equal(expected.deny, actual.deny);
    }

    [Fact]
    public void GetPermissions_ReturnsCopy_CallerCannotMutateStore()
    {
        using var temp = new TempDirectory("acl-return-copy");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var first = store.GetPermissions("admin");
        first.all[0] = "FMO/MUTATED";
        first.pub[0] = "FMO/MUTATED";
        first.sub[0] = "FMO/MUTATED";
        first.deny[0] = new AclDenyRule { Action = "all", Topic = "FMO/MUTATED" };

        var second = store.GetPermissions("admin");

        Assert.DoesNotContain("FMO/MUTATED", second.all);
        Assert.DoesNotContain("FMO/MUTATED", second.pub);
        Assert.DoesNotContain("FMO/MUTATED", second.sub);
        Assert.DoesNotContain(new AclDenyRule { Action = "all", Topic = "FMO/MUTATED" }, second.deny);
    }

    [Fact]
    public void ResolveForUid_ReplacesEveryPlaceholder()
    {
        using var temp = new TempDirectory("acl-resolve");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var resolved = store.ResolveForUid(
            ["FMO/QSO/UID/{uid}", "FMO/LATE/UID_V1/{uid}", "FMO/STATIC", "FMO/X/{uid}/Y/{uid}"],
            5001);

        Assert.Equal(
            ["FMO/QSO/UID/5001", "FMO/LATE/UID_V1/5001", "FMO/STATIC", "FMO/X/5001/Y/5001"],
            resolved);
    }

    [Fact]
    public void CustomRole_IsLoaded_WithCaseInsensitivePropertyNames()
    {
        using var temp = new TempDirectory("acl-custom");
        var rolesDir = temp.CreateSubdirectory("roles");
        File.WriteAllText(Path.Combine(rolesDir, "custom.json"), """
            {
              "ROLE": "custom",
              "ALL": ["FMO/RAW"],
              "PUB": ["FMO/CUSTOM/PUB"],
              "SUB": ["FMO/CUSTOM/SUB"]
            }
            """);

        var store = new AclStore(rolesDir);
        var acl = store.GetPermissions("custom");

        Assert.Equal(["FMO/RAW"], acl.all);
        Assert.Equal(["FMO/CUSTOM/PUB"], acl.pub);
        Assert.Equal(["FMO/CUSTOM/SUB"], acl.sub);
        // 自定义角色不影响内置角色的可用性。
        Assert.Contains("FMO/MUTELIST", store.GetPermissions("super").pub);
    }

    [Fact]
    public void InvalidJsonFile_IsIgnored_OtherRolesStillWork()
    {
        using var temp = new TempDirectory("acl-invalid");
        var rolesDir = temp.CreateSubdirectory("roles");
        File.WriteAllText(Path.Combine(rolesDir, "broken.json"), "{ this is not json");
        File.WriteAllText(Path.Combine(rolesDir, "custom.json"), """
            {"role": "custom", "pub": ["FMO/CUSTOM/PUB"], "sub": ["FMO/CUSTOM/SUB"]}
            """);

        var store = new AclStore(rolesDir);

        Assert.Contains("FMO/CUSTOM/PUB", store.GetPermissions("custom").pub);
        Assert.Contains("FMO/MUTELIST", store.GetPermissions("admin").pub);
        Assert.NotEmpty(store.GetPermissions("user").sub);
    }

    [Fact]
    public void EmptyRoleNameInJson_IsIgnored_AndFallsBackToUser()
    {
        using var temp = new TempDirectory("acl-empty-role");
        var rolesDir = temp.CreateSubdirectory("roles");
        File.WriteAllText(Path.Combine(rolesDir, "empty-role.json"), """
            {"role": "", "pub": ["FMO/SHOULD_NOT_LOAD"], "sub": ["FMO/SHOULD_NOT_LOAD"]}
            """);

        var store = new AclStore(rolesDir);

        var user = store.GetPermissions("user");
        var fallback = store.GetPermissions("empty-role");

        Assert.Equal(user.pub, fallback.pub);
        Assert.Equal(user.sub, fallback.sub);
        Assert.DoesNotContain("FMO/SHOULD_NOT_LOAD", fallback.pub);
    }

    [Fact]
    public void ResolveForUid_ReplacesPlaceholderInDenyRules()
    {
        using var temp = new TempDirectory("acl-resolve-deny");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var resolved = store.ResolveForUid(
            [
                new AclDenyRule { Action = "all", Topic = "FMO/X/{uid}/#" },
                new AclDenyRule { Action = "publish", Topic = "FMO/STATIC" }
            ],
            5001);

        Assert.Equal(
            [
                new AclDenyRule { Action = "all", Topic = "FMO/X/5001/#" },
                new AclDenyRule { Action = "publish", Topic = "FMO/STATIC" }
            ],
            resolved);
    }

    [Theory]
    [InlineData("user.json")]
    [InlineData("admin.json")]
    [InlineData("super.json")]
    public void OlderShippedDefaultRoleFile_IsUpgradedAndBackedUp(string fileName)
    {
        using var temp = new TempDirectory("acl-sync-default");
        var rolesDir = temp.CreateSubdirectory("roles");
        var roleFile = Path.Combine(rolesDir, fileName);

        var v1 = fileName switch
        {
            "user.json" => UserV1Json,
            "admin.json" => AdminV1Json,
            "super.json" => SuperV1Json,
            _ => throw new ArgumentOutOfRangeException(nameof(fileName))
        };

        File.WriteAllText(roleFile, v1);

        var store = new AclStore(rolesDir);

        // 备份必须保留旧文件原样。
        Assert.Equal(v1, File.ReadAllText(roleFile + ".bak"));

        // 服务器文件必须已替换为当前内置模板。
        var builtinFile = Path.Combine(AppContext.BaseDirectory, "builtin", "roles", fileName);
        Assert.Equal(
            RoleJson.Hash(File.ReadAllText(builtinFile)),
            RoleJson.Hash(File.ReadAllText(roleFile)));

        using var doc = JsonDocument.Parse(File.ReadAllText(roleFile));
        Assert.Equal(ExpectedTemplateVersion(fileName), doc.RootElement.GetProperty("_version").GetInt32());

        // 加载结果必须反映升级后的规则。
        var role = doc.RootElement.GetProperty("role").GetString()!;
        var acl = store.GetPermissions(role);
        var expectedDenyCount = fileName switch
        {
            "user.json" => 6,
            "admin.json" => 5,
            "super.json" => 1,
            _ => 0
        };
        Assert.Equal(expectedDenyCount, acl.deny.Length);
    }

    [Theory]
    [InlineData("user.json", "user", 6)]
    [InlineData("admin.json", "admin", 5)]
    [InlineData("super.json", "super", 1)]
    public void VersionlessLegacyDefault_IsUpgradedAndBackedUp(string fileName, string role, int denyCount)
    {
        using var temp = new TempDirectory("acl-sync-legacy");
        var rolesDir = temp.CreateSubdirectory("roles");
        var roleFile = Path.Combine(rolesDir, fileName);

        // 最初开源版本：没有 _version、没有 deny、SERVER_INFO 不是通配符。
        // 线上仍有大量部署停在这份默认上，必须能被识别并升级。
        var legacy = fileName switch
        {
            "user.json" => UserV0Json,
            "admin.json" => AdminV0Json,
            "super.json" => SuperV0Json,
            _ => throw new ArgumentOutOfRangeException(nameof(fileName))
        };

        File.WriteAllText(roleFile, legacy);

        var store = new AclStore(rolesDir);

        Assert.Equal(legacy, File.ReadAllText(roleFile + ".bak"));

        var builtinFile = Path.Combine(AppContext.BaseDirectory, "builtin", "roles", fileName);
        Assert.Equal(
            RoleJson.Hash(File.ReadAllText(builtinFile)),
            RoleJson.Hash(File.ReadAllText(roleFile)));

        var acl = store.GetPermissions(role);
        Assert.Equal(denyCount, acl.deny.Length);
        Assert.Contains("FMO/SERVER_INFO/#", acl.sub.Concat(acl.pub));

        if (role != "super")
            Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/LATE/UID_V1/#" }, acl.deny);
    }

    [Fact]
    public void SuperFrom1_2_0_IsUpgraded()
    {
        using var temp = new TempDirectory("acl-sync-super-1-2-0");
        var rolesDir = temp.CreateSubdirectory("roles");
        var superFile = Path.Combine(rolesDir, "super.json");

        // 1.2.0 的 super：_version 2，但 sub 尚未补齐、也没有 SAFEPROBE deny。
        File.WriteAllText(superFile, SuperV2NoSubJson);

        var store = new AclStore(rolesDir);

        Assert.Equal(SuperV2NoSubJson, File.ReadAllText(superFile + ".bak"));

        var super = store.GetPermissions("super");
        Assert.Contains("FMO/PROFILE", super.sub);
        Assert.Contains("FMO/MUTELIST", super.sub);
        Assert.Contains("FMO/REMOTE_CONTROL", super.sub);
        Assert.Contains("FMO/SERVER_INFO/#", super.sub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, super.deny);
    }

    [Fact]
    public void SameVersionShippedDefault_MissedVersionBump_IsRepaired()
    {
        using var temp = new TempDirectory("acl-sync-missed-bump");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");

        // 历史默认被贴上了当前版本号：说明发版时漏了 _version 自增。
        // 内容仍然不是用户自定义，必须自动修复并备份。
        var misversioned = UserV1Json.Replace(
            "\"role\": \"user\",",
            "\"role\": \"user\",\n  \"_version\": 2,");

        File.WriteAllText(userFile, misversioned);

        var store = new AclStore(rolesDir);

        Assert.Equal(misversioned, File.ReadAllText(userFile + ".bak"));
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, store.GetPermissions("user").deny);
    }

    [Fact]
    public void CurrentContentWithOlderVersion_IsNotRewritten()
    {
        using var temp = new TempDirectory("acl-same-content-older-version");
        var rolesDir = temp.CreateSubdirectory("roles");
        var superFile = Path.Combine(rolesDir, "super.json");

        // 1.2.1 播种的 super 内容与当前模板一致，只是 _version 还是 2。
        // 规则已经相同，不应该改写文件，也不应生成 .bak。
        var text = ReadEmbeddedTemplate("super.json").Replace("\"_version\": 3", "\"_version\": 2");
        File.WriteAllText(superFile, text);

        var store = new AclStore(rolesDir);

        Assert.Equal(text, File.ReadAllText(superFile));
        Assert.False(File.Exists(superFile + ".bak"));
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, store.GetPermissions("super").deny);
    }

    [Fact]
    public void HistoryTemplates_AreNotSeededIntoRolesDirectory()
    {
        using var temp = new TempDirectory("acl-history-not-seeded");
        var rolesDir = temp.CreateSubdirectory("roles");

        _ = new AclStore(rolesDir);

        // 历史模板只能用于哈希比对，绝不能出现在生效目录里。
        var names = Directory.GetFiles(rolesDir, "*.json")
            .Select(n => Path.GetFileName(n)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["admin.json", "super.json", "user.json"], names);
    }

    private static int ExpectedTemplateVersion(string fileName) =>
        fileName == "super.json" ? 3 : 2;

    [Fact]
    public void CustomisedRoleFile_IsNotUpgraded()
    {
        using var temp = new TempDirectory("acl-sync-custom");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");

        var custom = UserV1Json.Replace(
            "\"FMO/QSO/+\"",
            "\"FMO/QSO/+\", \"FMO/CUSTOM/#\"");
        File.WriteAllText(userFile, custom);

        var store = new AclStore(rolesDir);

        var user = store.GetPermissions("user");
        Assert.Contains("FMO/CUSTOM/#", user.pub);
        Assert.Empty(user.deny);
        Assert.False(File.Exists(userFile + ".bak"));
    }

    [Fact]
    public void NewerRoleFileVersion_IsKept()
    {
        using var temp = new TempDirectory("acl-sync-newer");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");
        File.WriteAllText(userFile, """
            {
              "role": "user",
              "_version": 99,
              "all": ["FMO/FUTURE"],
              "pub": ["FMO/FUTURE/PUB"],
              "sub": ["FMO/FUTURE/SUB"],
              "deny": []
            }
            """);

        var store = new AclStore(rolesDir);

        var user = store.GetPermissions("user");
        Assert.Equal(["FMO/FUTURE"], user.all);
        Assert.False(File.Exists(userFile + ".bak"));
    }

    [Fact]
    public void SameVersionCustomisedRoleFile_IsKept()
    {
        using var temp = new TempDirectory("acl-sync-same-version");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");
        File.WriteAllText(userFile, """
            {
              "role": "user",
              "_version": 2,
              "all": ["FMO/RAW"],
              "pub": ["FMO/CUSTOM/PUB"],
              "sub": ["FMO/CUSTOM/SUB"],
              "deny": []
            }
            """);

        var store = new AclStore(rolesDir);

        var user = store.GetPermissions("user");
        Assert.Equal(["FMO/CUSTOM/PUB"], user.pub);
        Assert.False(File.Exists(userFile + ".bak"));
    }

    [Fact]
    public void CorruptBuiltinRoleFile_IsKeptButBuiltinRulesUsedInMemory()
    {
        using var temp = new TempDirectory("acl-corrupt-builtin");
        var rolesDir = temp.CreateSubdirectory("roles");
        Directory.CreateDirectory(rolesDir);

        // 先把内置文件播种出来，再把 user.json 改坏，模拟用户手工编辑失败。
        _ = new AclStore(rolesDir);
        var userFile = Path.Combine(rolesDir, "user.json");
        const string corrupt = "{ this is not json";
        File.WriteAllText(userFile, corrupt);

        // 损坏文件不能阻塞启动。
        var store = new AclStore(rolesDir);

        // 文件必须原样保留，便于用户修复。
        Assert.Equal(corrupt, File.ReadAllText(userFile));

        // 内存中必须回退到内置规则，而不是空 ACL（空 ACL 等于全放行）。
        var user = store.GetPermissions("user");
        Assert.Contains("FMO/QSO/+", user.pub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, user.deny);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/LATE/UID_V1/#" }, user.deny);
    }

    [Fact]
    public void MismatchedRoleName_IsKeptButBuiltinRulesUsedInMemory()
    {
        using var temp = new TempDirectory("acl-mismatched-role");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");

        // user.json 声明成 admin，试图冒充管理员角色。
        File.WriteAllText(userFile, """
            {
              "role": "admin",
              "_version": 2,
              "all": ["FMO/INJECTED"],
              "pub": ["FMO/INJECTED/PUB"],
              "sub": ["FMO/INJECTED/SUB"],
              "deny": []
            }
            """);

        var store = new AclStore(rolesDir);

        // 文件保留，但不会被当成 admin 加载。
        Assert.Contains("FMO/INJECTED", File.ReadAllText(userFile));
        Assert.DoesNotContain("FMO/INJECTED", store.GetPermissions("admin").all);

        // 真正的 user 角色必须回退到内置规则。
        var user = store.GetPermissions("user");
        Assert.Contains("FMO/QSO/+", user.pub);
        Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/LATE/UID_V1/#" }, user.deny);
    }

    [Fact]
    public void SecondStartup_DoesNotRewriteOrBackup()
    {
        using var temp = new TempDirectory("acl-idempotent");
        var rolesDir = temp.CreateSubdirectory("roles");

        _ = new AclStore(rolesDir);
        var userFile = Path.Combine(rolesDir, "user.json");
        var before = File.ReadAllText(userFile);
        var stampBefore = File.GetLastWriteTimeUtc(userFile);

        _ = new AclStore(rolesDir);

        Assert.Equal(before, File.ReadAllText(userFile));
        Assert.Equal(stampBefore, File.GetLastWriteTimeUtc(userFile));
        Assert.False(File.Exists(userFile + ".bak"));
    }

    [Fact]
    public void SameVersionEquivalentContent_IsNotRewritten()
    {
        using var temp = new TempDirectory("acl-same-version-equivalent");
        var rolesDir = temp.CreateSubdirectory("roles");

        // 先取得当前内置 user 模板，再以不同空白和键顺序写回。
        var seededDir = temp.CreateSubdirectory("seeded");
        _ = new AclStore(seededDir);
        var builtinText = File.ReadAllText(Path.Combine(seededDir, "user.json"));

        var equivalent = JsonSerializer.Serialize(
            JsonDocument.Parse(builtinText).RootElement,
            new JsonSerializerOptions { WriteIndented = true });
        var userFile = Path.Combine(rolesDir, "user.json");
        File.WriteAllText(userFile, equivalent);

        _ = new AclStore(rolesDir);

        // 规范化后内容相同，必须视为未修改，既不重写也不备份。
        Assert.Equal(equivalent, File.ReadAllText(userFile));
        Assert.False(File.Exists(userFile + ".bak"));
    }

    [Fact]
    public void ReadOnlyRoleFile_FallsBackToBuiltinInMemory()
    {
        using var temp = new TempDirectory("acl-readonly");
        var rolesDir = temp.CreateSubdirectory("roles");
        var userFile = Path.Combine(rolesDir, "user.json");
        File.WriteAllText(userFile, UserV1Json);
        File.SetAttributes(userFile, FileAttributes.ReadOnly);

        try
        {
            // 版本落后且匹配历史默认，会尝试升级；只读导致写入失败。
            var store = new AclStore(rolesDir);

            // 规则仍然必须生效（内存使用内置模板）。
            var user = store.GetPermissions("user");
            Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/SAFEPROBE/#" }, user.deny);
            Assert.Contains(new AclDenyRule { Action = "all", Topic = "FMO/LATE/UID_V1/#" }, user.deny);

            // 原文件保持旧内容，没有被部分写入。
            Assert.Equal(UserV1Json, File.ReadAllText(userFile));
        }
        finally
        {
            File.SetAttributes(userFile, FileAttributes.Normal);
        }
    }

    private const string UserV1Json = """
        {
          "role": "user",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ]
        }
        """;

    private const string AdminV1Json = """
        {
          "role": "admin",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/MUTELIST"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ]
        }
        """;

    private const string SuperV1Json = """
        {
          "role": "super",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}"
          ]
        }
        """;

    // 最初开源版本的原样内容，用于验证历史默认识别与自动升级。
    private const string UserV0Json = """
        {
          "role": "user",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO"
          ]
        }
        """;

    private const string AdminV0Json = """
        {
          "role": "admin",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/MUTELIST"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO"
          ]
        }
        """;

    private const string SuperV0Json = """
        {
          "role": "super",
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}"
          ]
        }
        """;

    // 1.2.0 首次带 _version: 2 的 super：sub 未补齐、deny 为空。
    private const string SuperV2NoSubJson = """
        {
          "role": "super",
          "_version": 2,
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}"
          ],
          "deny": []
        }
        """;

    // 补齐 sub、但尚未加入 SAFEPROBE deny 的 v2 super。
    private const string SuperV2NoDenyJson = """
        {
          "role": "super",
          "_version": 2,
          "all": ["FMO/RAW", "FMO/TELE", "FMO/USER_RIG"],
          "pub": [
            "FMO/QSO/+",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ],
          "sub": [
            "FMO/QSO/UID/{uid}",
            "FMO/LATE/UID_V1/{uid}",
            "FMO/PROFILE",
            "FMO/MUTELIST",
            "FMO/REMOTE_CONTROL",
            "FMO/SERVER_INFO/#"
          ],
          "deny": []
        }
        """;

    [Fact]
    public void SeededBuiltinRoles_MatchBuiltinJsonFiles()
    {
        using var temp = new TempDirectory("acl-seeded");
        var rolesDir = temp.CreateSubdirectory("roles");

        var store = new AclStore(rolesDir);

        // 构造时会把内置 roles/*.json 播种到空目录。
        Assert.NotEmpty(Directory.GetFiles(rolesDir, "*.json"));

        var builtinAdmin = Path.Combine(AppContext.BaseDirectory, "builtin", "roles", "admin.json");
        Assert.True(File.Exists(builtinAdmin));

        using var doc = JsonDocument.Parse(File.ReadAllText(builtinAdmin));
        var expectedPub = doc.RootElement.GetProperty("pub").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        var expectedSub = doc.RootElement.GetProperty("sub").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();

        var admin = store.GetPermissions("admin");
        Assert.Equal(expectedPub, admin.pub);
        Assert.Equal(expectedSub, admin.sub);
        Assert.Contains("FMO/PROFILE", admin.sub);
        Assert.DoesNotContain("FMO/PROFILE", admin.pub);
        Assert.DoesNotContain("FMO/REMOTE_CONTROL", admin.pub);
        Assert.DoesNotContain("FMO/SERVER_INFO/#", admin.pub);
    }

    [Fact]
    public void Admin_CannotPublishManagementTopics()
    {
        using var temp = new TempDirectory("acl-admin-publish");
        var store = new AclStore(temp.CreateSubdirectory("roles"));

        var admin = store.GetPermissions("admin");

        // 管理员只能发布禁言主题。
        Assert.Contains("FMO/MUTELIST", admin.pub);

        // 三个管理主题必须由 deny 阻断；仅从 pub 删除不足，因为 EMQX
        // no_match 默认 allow。
        var denied = admin.deny
            .Where(r => r.Action == "publish")
            .Select(r => r.Topic)
            .ToArray();

        Assert.Contains("FMO/PROFILE", denied);
        Assert.Contains("FMO/REMOTE_CONTROL", denied);
        Assert.Contains("FMO/SERVER_INFO/#", denied);

        // 订阅仍然保留，管理端可以观察这些主题。
        Assert.Contains("FMO/PROFILE", admin.sub);
        Assert.Contains("FMO/REMOTE_CONTROL", admin.sub);
        Assert.Contains("FMO/SERVER_INFO/#", admin.sub);
    }

    [Theory]
    [InlineData("user.json", "user")]
    [InlineData("admin.json", "admin")]
    [InlineData("super.json", "super")]
    public void EmbeddedTemplate_IsWellFormed(string fileName, string expectedRole)
    {
        var text = ReadEmbeddedTemplate(fileName);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // 文件名与角色名必须一致，否则加载时会被拒绝并回退内置规则。
        Assert.Equal(expectedRole, root.GetProperty("role").GetString());

        // 当前发布版本必须带 _version，升级逻辑依赖它。
        Assert.Equal(ExpectedTemplateVersion(fileName), root.GetProperty("_version").GetInt32());

        foreach (var key in new[] { "all", "pub", "sub", "deny" })
        {
            if (!root.TryGetProperty(key, out var rules))
                continue;

            foreach (var rule in rules.EnumerateArray())
            {
                if (key == "deny")
                {
                    var action = rule.GetProperty("action").GetString();
                    Assert.Contains(action, new[] { "all", "publish", "subscribe" });
                    Assert.False(string.IsNullOrWhiteSpace(rule.GetProperty("topic").GetString()));
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(rule.GetString()));
                }
            }
        }
    }

    [Theory]
    [InlineData("user.json")]
    [InlineData("admin.json")]
    [InlineData("super.json")]
    public void BuiltinRoleHistory_DoesNotContainCurrentTemplate(string fileName)
    {
        var text = ReadEmbeddedTemplate(fileName);
        var hash = RoleJson.Hash(text);

        Assert.NotNull(hash);

        // 当前模板绝不能同时算作“历史默认”。否则已经是最新版本的部署
        // 也会被判定为可升级，覆盖掉主人正在使用的文件。
        Assert.False(BuiltinRoleHistory.IsDefault(fileName, hash!));
    }

    [Fact]
    public void BuiltinRoleHistory_RejectsUnknownFile()
    {
        Assert.False(BuiltinRoleHistory.IsDefault("no-such-role.json", "any"));
    }

    [Theory]
    [InlineData("user.json", UserV0Json)]
    [InlineData("user.json", UserV1Json)]
    [InlineData("admin.json", AdminV0Json)]
    [InlineData("admin.json", AdminV1Json)]
    [InlineData("super.json", SuperV0Json)]
    [InlineData("super.json", SuperV1Json)]
    [InlineData("super.json", SuperV2NoSubJson)]
    [InlineData("super.json", SuperV2NoDenyJson)]
    public void BuiltinRoleHistory_ContainsEveryShippedDefault(string fileName, string content)
    {
        var hash = RoleJson.Hash(content);

        Assert.NotNull(hash);
        Assert.True(
            BuiltinRoleHistory.IsDefault(fileName, hash!),
            $"{fileName} 的历史默认缺少这一版内容，停留在该版本的部署将无法升级。");
    }

    [Theory]
    [InlineData("user.json", UserV1Json)]
    [InlineData("admin.json", AdminV1Json)]
    [InlineData("super.json", SuperV1Json)]
    public void BuiltinRoleHistory_DoesNotMatchCustomisedContent(string fileName, string shipped)
    {
        // 在官方默认上增删一条规则即为自定义，绝不能被视为可覆盖。
        var custom = shipped.Replace("\"FMO/QSO/+\"", "\"FMO/QSO/+\", \"FMO/CUSTOM/#\"");
        var hash = RoleJson.Hash(custom);

        Assert.NotNull(hash);
        Assert.False(BuiltinRoleHistory.IsDefault(fileName, hash!));
    }

    private static string ReadEmbeddedTemplate(string fileName)
    {
        var assembly = typeof(AclStore).Assembly;
        using var stream = assembly.GetManifestResourceStream("builtin.roles." + fileName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void EmptyAllowRole_RefusesToStartByDefault()
    {
        using var temp = new TempDirectory("acl-empty-role");
        var rolesDir = temp.CreateSubdirectory("roles");
        File.WriteAllText(Path.Combine(rolesDir, "user.json"),
            """{"role":"user","_version":2,"all":[],"pub":[],"sub":[]}""");

        var ex = Assert.Throws<InvalidOperationException>(() => new AclStore(rolesDir));

        Assert.Contains("user", ex.Message);
        Assert.Contains("allowEmptyAcl", ex.Message);
    }

    [Fact]
    public void EmptyAllowRole_StartsWithExplicitOverride()
    {
        using var temp = new TempDirectory("acl-empty-role-allow");
        var rolesDir = temp.CreateSubdirectory("roles");
        File.WriteAllText(Path.Combine(rolesDir, "user.json"),
            """{"role":"user","_version":2,"all":[],"pub":[],"sub":[]}""");

        var store = new AclStore(rolesDir, allowEmptyAcl: true);

        Assert.Empty(store.GetPermissions("user").all);
        Assert.Empty(store.GetPermissions("user").pub);
        Assert.Empty(store.GetPermissions("user").sub);
    }

    [Fact]
    public void FromRootsDir_NormalizesTrailingSeparator()
    {
        using var temp = new TempDirectory("acl-path-normalize");
        var rootsA = temp.CreateSubdirectory("a", "roots");
        var rootsB = temp.CreateSubdirectory("b", "roots");

        _ = AclStore.FromRootsDir(rootsA);
        _ = AclStore.FromRootsDir(rootsB + Path.DirectorySeparatorChar);

        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(rootsA)!, "roles")));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(rootsB)!, "roles")));
        Assert.False(Directory.Exists(Path.Combine(rootsB, "roles")));
    }
}
