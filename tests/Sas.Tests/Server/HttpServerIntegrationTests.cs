using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sas.Auth;
using Sas.Server;
using Sas.Tests.Fixtures;
using Sas.Trust;

namespace Sas.Tests.Server;

[Trait("Category", "Integration")]
public sealed class HttpServerIntegrationTests
{
    private static AuthRateLimiter UnlimitedLimiter() =>
        new(new RateLimitConfig { GlobalPerMinute = 10_000, BreakerEnabled = false });

    private static StringContent Body(string username, string password) =>
        new(JsonSerializer.Serialize(new { username, password }), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Post_ValidCredentials_ReturnsAllowWithAcl()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var json = await ReadJson(response);
        Assert.Equal("allow", json.GetProperty("result").GetString());
        Assert.Equal(env.Username, json.GetProperty("client_attrs").GetProperty("callsign").GetString());
        Assert.True(json.GetProperty("acl").GetArrayLength() > 0);
        Assert.True(json.GetProperty("expire_at").GetInt64() > env.Now);
    }

    [Fact]
    public async Task Post_ValidCredentials_ReturnsVerifiedRootIssuerAndUserFingerprints()
    {
        using var env = new AuthTestEnvironment();
        // Use a distinct client certificate so a response using the target
        // server fingerprint instead cannot satisfy this test.
        var clientPki = TestPki.CreateWithRoot(env.Pki, new TestPkiOptions { Seed = 42, IssuerSn = 1042 });
        var password = clientPki.BuildPassword(
            env.ServerUid, env.Config.Server.Callsign, env.ServerUid,
            env.Config.Mqtt.Host, env.Config.Mqtt.Port, env.ServerFingerprint, env.Now, "user");
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(clientPki.User.Callsign, password));

        var json = await ReadJson(response);
        Assert.Equal("allow", json.GetProperty("result").GetString());
        var attrs = json.GetProperty("client_attrs");
        Assert.True(attrs.TryGetProperty("fmo_root_fingerprint", out var root), "Root fingerprint is missing");
        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(env.Pki.Root.Fingerprint()), root.GetString());
        Assert.True(attrs.TryGetProperty("fmo_issuer_fingerprint", out var issuer), "Issuer fingerprint is missing");
        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(clientPki.Intermediate.Fingerprint()), issuer.GetString());
        Assert.True(attrs.TryGetProperty("fmo_user_fingerprint", out var user), "User fingerprint is missing");
        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(clientPki.User.Fingerprint()), user.GetString());
    }

    [Fact]
    public async Task Post_ValidCredentials_AclEmitsDenyAfterAllow()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));
        var acl = (await ReadJson(response)).GetProperty("acl").EnumerateArray().ToArray();

        var lastAllow = Array.FindLastIndex(acl,
            e => e.GetProperty("permission").GetString() == "allow");
        var firstDeny = Array.FindIndex(acl,
            e => e.GetProperty("permission").GetString() == "deny");

        Assert.True(lastAllow >= 0);
        Assert.True(firstDeny > lastAllow, "deny rules must come after allow rules");
        Assert.Contains(acl, e =>
            e.GetProperty("permission").GetString() == "deny" &&
            e.GetProperty("topic").GetString() == "FMO/SAFEPROBE/#");
    }

    [Theory]
    [InlineData("root@example.test", "issuer@example.test", "root@example.test")]
    [InlineData("", "issuer@example.test", "issuer@example.test")]
    [InlineData("CA <root@example.test>", "issuer@example.test", "issuer@example.test")]
    [InlineData("root@example.test, other@example.test", "", null)]
    [InlineData("", "broken-address", null)]
    public async Task Post_ValidCredentials_ReturnsFirstUsableVerifiedCaContact(
        string rootEmail, string intermediateEmail, string? expected)
    {
        using var env = new AuthTestEnvironment(pki: new TestPkiOptions
        {
            RootEmail = rootEmail,
            IntermediateEmail = intermediateEmail,
        });
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var json = await ReadJson(await client.PostAsync("auth", Body(env.Username, env.ValidPassword())));
        Assert.Equal("allow", json.GetProperty("result").GetString());
        var attrs = json.GetProperty("client_attrs");
        var hasContact = attrs.TryGetProperty("fmo_ca_contact_email", out var contact);
        Assert.Equal(expected != null, hasContact);
        if (hasContact) Assert.Equal(expected, contact.GetString());
    }

    [Fact]
    public async Task Get_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.GetAsync("auth");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task WrongPath_IsRejectedBeforeReachingTheHandler()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("other", Body(env.Username, env.ValidPassword()));

        // The HttpListener prefix only covers /auth/, so other paths never get
        // a JSON deny body from SAS itself.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TrailingSlashPath_IsAccepted()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth/", Body(env.Username, env.ValidPassword()));

        Assert.Equal("allow", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task MalformedJson_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth",
            new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("BG6VMZ", "")]
    public async Task MissingCredentials_AreDenied(string username, string password)
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(username, password));

        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task IllegalCallsign_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body("BG6 VMZ", env.ValidPassword()));

        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task PasswordLongerThanLimit_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(),
            UnlimitedLimiter(), maxPasswordBytes: 16);
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task BodyLargerThanLimit_Returns413()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(),
            UnlimitedLimiter(), maxBodyBytes: 64);
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task GlobalRateLimitExceeded_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        var limiter = new AuthRateLimiter(new RateLimitConfig
        {
            GlobalPerMinute = 1,
            BreakerEnabled = false
        });
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), limiter);
        using var client = server.CreateClient();

        var first = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));
        var second = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal("allow", (await ReadJson(first)).GetProperty("result").GetString());
        Assert.Equal("deny", (await ReadJson(second)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task BannedCallsign_IsDenied()
    {
        using var env = new AuthTestEnvironment();
        var limiter = new AuthRateLimiter(new RateLimitConfig
        {
            GlobalPerMinute = 10_000,
            BreakerEnabled = true,
            BreakerFailures = 2,
            BreakerWindowSec = 300,
            BreakerBanSec = 600
        });
        limiter.RecordFailure(env.Username, "setup-1");
        limiter.RecordFailure(env.Username, "setup-2");

        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), limiter);
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Post_UserRevokedByIntermediateCrl_ReturnsDeny()
    {
        using var env = new AuthTestEnvironment(
            pki: new TestPkiOptions { IssuerCrlUrl = "http://crl.test/int.crl" },
            crlFactory: rootsDir => new CrlManager(rootsDir, refreshSec: -1,
                FakeHttpMessageHandler.NotFound().CreateClient()));
        var crl = (CrlManager)env.CrlPolicy;

        TestCrl.WriteIntermediateCache(env.Temp, env.Pki.Intermediate,
            TestCrl.IntermediateCrlJson(env.Pki, 1,
            [
                (env.Pki.User.Uid, env.Pki.User.Fingerprint(),
                    1_700_000_000, TestCrl.ReasonKeyCompromise)
            ]));
        crl.Initialize([env.Pki.Root]);

        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, env.ValidPassword()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("deny", json.GetProperty("result").GetString());
        Assert.False(json.TryGetProperty("client_attrs", out _));
    }

    [Fact]
    public async Task WrongPasswordPayload_ReturnsDeny()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());
        using var client = server.CreateClient();

        var response = await client.PostAsync("auth", Body(env.Username, "not-base64!!!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("deny", (await ReadJson(response)).GetProperty("result").GetString());
    }

    [Fact]
    public async Task ChunkedBody_IsAcceptedAndDenied()
    {
        using var env = new AuthTestEnvironment();
        await using var server = new HttpServerHarness(env.Handler, new EmqxResponseTemplate(), UnlimitedLimiter());

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = tcp.GetStream();

        var request =
            "POST /auth HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Content-Type: application/json\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            "2\r\n{}\r\n" +
            "0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync();

        Assert.Contains("200", response.Split('\r')[0]);
        Assert.Contains("\"result\":\"deny\"", response);
    }
}
