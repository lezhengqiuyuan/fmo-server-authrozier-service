using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sas;
using Sas.Auth;
using Sas.Cli;
using Sas.Logging;
using Sas.Server;
using Sas.Trust;

var foundCommands = args.Where(a => Config.ManagementCommands.Contains(a)).ToArray();

if (args.Any(a => a == "--help" || a == "-h"))
{
    Config.ShowHelp();
    Environment.Exit(0);
}

if (args.Any(a => a is "--version" or "-v"))
{
    Console.WriteLine(GetCurrentVersion().Split('+')[0]);
    return;
}

if (foundCommands.Length > 1)
{
    Console.Error.WriteLine("错误：不能同时使用多个管理命令。 / Cannot use multiple management commands at once.");
    Console.Error.WriteLine($"  你输入了 / You entered: {string.Join(" ", foundCommands)}");
    Console.Error.WriteLine("  请一次只运行一个命令 / Please run one command at a time:");
    Console.Error.WriteLine("    sas --add-cluster");
    Console.Error.WriteLine("    sas --list-clusters");
    Environment.Exit(1);
}

if (foundCommands.Length > 0 && args.Any(a => a == "--update"))
{
    Console.Error.WriteLine("错误：--update 不能与管理命令同时使用。 / --update cannot be used with management commands.");
    Console.Error.WriteLine("  请分开执行 / Please run them separately:");
    Console.Error.WriteLine("    sas --update");
    Console.Error.WriteLine($"    sas {foundCommands[0]}");
    Environment.Exit(1);
}

if (args.Any(a => a == "--update"))
{
    await CheckUpdateCommand(args.Any(a => a is "--yes" or "-y"));
    return;
}

if (args.Any(a => a == "--add-admin"))
{
    Config.AddAdminCommand(args);
    return;
}

if (args.Any(a => a == "--remove-admin"))
{
    Config.RemoveAdminCommand(args);
    return;
}

if (args.Any(a => a == "--list-admins"))
{
    Config.ListAdminsCommand(args);
    return;
}

if (args.Any(a => a == "--add-cluster"))
{
    Config.AddClusterCommand(args);
    return;
}

if (args.Any(a => a == "--remove-cluster"))
{
    Config.RemoveClusterCommand(args);
    return;
}

if (args.Any(a => a == "--list-clusters"))
{
    Config.ListClustersCommand(args);
    return;
}

var config = Config.Load(args);

var version = Assembly.GetEntryAssembly()?
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion ?? "unknown";

Logger.Info("        _____ __  __  ___");
Logger.Info("       |  ___|  \\/  |/ _ \\");
Logger.Info("       | |_  | |\\/| | | | |");
Logger.Info("       |  _| | |  | | |_| |");
Logger.Info("       |_|   |_|  |_|\\___/");
Logger.Info("");
Logger.Info("    Server Authorizer Service");
Logger.Info("");

Logger.Info($"Config: {Config.ConfigFilePath}");
Logger.Info($"SAS HTTP auth mode");
Logger.Info($"  MQTT: {config.Mqtt.Host}:{config.Mqtt.Port}");
if (config.Mqtt.Clusters.Length > 0)
{
    Logger.Info($"  Clusters: {config.Mqtt.Clusters.Length} entries");
    foreach (var c in config.Mqtt.Clusters)
        Logger.Info($"    - uid={c.Uid} callsign={c.Callsign} mqtt={c.MqttHost}:{c.MqttPort}");
}

Logger.Info($"  Server: uid={config.Server.Uid} callsign={config.Server.Callsign} certFingerprint={(string.IsNullOrEmpty(config.Server.CertFingerprint) ? "(not set)" : config.Server.CertFingerprint)}");
var adminsCount = config.Server.Admins.Length;
var isAutoAdmin = adminsCount == 1
                  && config.Server.Admins[0].Uid == config.Server.Uid
                  && string.Equals(config.Server.Admins[0].CertFingerprint, config.Server.CertFingerprint, StringComparison.Ordinal);
if (!isAutoAdmin)
{
    var adminsList = adminsCount == 0
        ? "(none)"
        : string.Join(", ", config.Server.Admins.Select(a =>
            $"uid={a.Uid} fp={a.CertFingerprint}"));
    Logger.Info($"  Admins: {adminsCount} {adminsList}");
}

if (adminsCount == 0)
{
    Logger.Warn("  No admins configured: super is granted only to the certificate whose " +
                "fingerprint equals server.certFingerprint");
}

Logger.Info($"  Version: {version}");
Logger.Info($"  Archive: {(config.Archive.Enabled ? config.Archive.Dir : "(disabled)")}");
Logger.Info($"  Rate limit: global={config.Http.RateLimit.GlobalPerMinute}/min breaker={(config.Http.RateLimit.BreakerEnabled ? "on" : "off")}");

var rootsDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.Trust.RootsDir));
Logger.Info($"  Roots: {rootsDir}");
Logger.Info($"  Roles: {Path.Combine(Path.GetDirectoryName(rootsDir)!, "roles")}");
Logger.Info($"  CRL: {Path.Combine(Path.GetDirectoryName(rootsDir)!, "crl")}");

var rootStore = new RootCaStore(rootsDir);
var verifier = new CertVerifier();
var crlManager = new CrlManager(rootsDir, config.Crl.RefreshSec,
    allowInsecureHttp: config.Crl.AllowInsecure);
var aclStore = AclStore.FromRootsDir(rootsDir, config.Acl?.AllowEmptyAcl ?? false);
var archive = new AuthArchive(config.Archive);
var rateLimiter = new AuthRateLimiter(config.Http.RateLimit);

Logger.Info("SAS HTTP auth listening on:");
if (config.Http.Addr == "0.0.0.0" || config.Http.Addr == "+")
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
        {
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                Logger.Info($"  http://{ua.Address}:{config.Http.Port}/auth");
        }
    }
}
else
{
    Logger.Info($"  http://{config.Http.Addr}:{config.Http.Port}/auth");
}

crlManager.Initialize(rootStore.AllRoots.ToArray());

var httpHandler = new HttpAuthHandler(config, rootStore, verifier, crlManager, aclStore, archive);
var template = CreateResponseTemplate(config.Http.ResponseTemplate);
var httpServer = new HttpServer(config.Http.Addr, config.Http.Port, config.Http.MaxBodyBytes,
    config.Http.MaxPasswordBytes, config.Http.MaxConcurrent, httpHandler, template, rateLimiter);
httpServer.Start();

if (config.Update.Enabled)
    _ = CheckVersionAsync(version);

using var cts = new CancellationTokenSource();

ConsoleCancelEventHandler cancelHandler = (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
Console.CancelKeyPress += cancelHandler;

using var sigterm = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
        ctx => { ctx.Cancel = true; cts.Cancel(); });
using var sigint = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGINT,
        ctx => { ctx.Cancel = true; cts.Cancel(); });

try
{
    await httpServer.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
    await httpServer.StopAsync(TimeSpan.FromSeconds(5));
    archive.Flush();
    Logger.Info("SAS stopped.");
    Logger.Flush();
    crlManager.Dispose();
}

return;

static IResponseTemplate CreateResponseTemplate(string name)
{
    return name.ToLowerInvariant() switch
    {
        "emqx" => new EmqxResponseTemplate(),
        _ => new EmqxResponseTemplate()
    };
}

static string GetCurrentVersion()
{
    return Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "unknown";
}

static async Task CheckVersionAsync(string currentVersion)
{
    try
    {
        Logger.Info("─── Checking for updates ───");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var json = await http.GetStringAsync("https://bg5esn.com/share/fmo/sas.json");
        var doc = JsonDocument.Parse(json).RootElement;
        var latest = doc.GetProperty("version").GetString() ?? "";

        if (!ConfigParser.TryParseCleanVersion(latest, out var remote) ||
            !ConfigParser.TryParseCleanVersion(currentVersion, out var local))
        {
            Logger.Info("  Update check skipped (failed to parse version).");
            return;
        }

        if (remote <= local)
        {
            Logger.Info("  Already up to date.");
            return;
        }

        var url = doc.TryGetProperty("url", out var u)
            ? u.GetString()
            : null;
        var notes = doc.TryGetProperty("notes", out var n)
            ? n.GetString()
            : null;

        Logger.Warn("┌─────────────────────────────────────────────────────────────────────────────");
        Logger.Warn($"│  New SAS version available: {latest}");
        if (url != null)
            Logger.Warn($"│  {url}");
        if (!string.IsNullOrWhiteSpace(notes))
            Logger.Warn($"│  {notes}");

        if (ConfigParser.IsRunningInDocker())
        {
            Logger.Warn("│  Update: docker pull fmo/sas:latest && docker compose up -d");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Logger.Warn("│  Download: https://github.com/BG5ESN/fmo-server-authrozier-service/releases");
        }
        else
        {
            Logger.Warn("│  Upgrade: curl -fsSL --proto '=https' --proto-redir '=https' https://bg5esn.com/share/fmo/sas-installer/install.sh -o /tmp/install.sh && sudo bash /tmp/install.sh --upgrade");
        }

        Logger.Warn("└─────────────────────────────────────────────────────────────────────────────");
    }
    catch (Exception ex)
    {
        Logger.Info($"  Update check failed: {ex.Message}");
    }
}

static async Task CheckUpdateCommand(bool autoYes)
{
    var version = GetCurrentVersion();
    Console.WriteLine($"SAS version: {version.Split('+')[0]}");

    if (ConfigParser.IsRunningInDocker())
    {
        Console.WriteLine();
        Console.WriteLine("Running in Docker. Update with:");
        Console.WriteLine("  docker pull fmo/sas:latest && docker compose up -d");
        return;
    }

    const string installerUrl = "https://bg5esn.com/share/fmo/sas-installer/install.sh";
    var command = $"curl -fsSL --proto '=https' --proto-redir '=https' {installerUrl} -o /tmp/install.sh && sudo bash /tmp/install.sh --upgrade";

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        Console.WriteLine();
        Console.WriteLine("SAS 升级请下载最新版本并替换 sas.exe / Download the latest release:");
        Console.WriteLine("  https://github.com/BG5ESN/fmo-server-authrozier-service/releases");
        return;
    }

    Console.Write("Checking for updates... ");

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync("https://bg5esn.com/share/fmo/sas.json");
        var doc = JsonDocument.Parse(json).RootElement;
        var latest = doc.GetProperty("version").GetString() ?? "";

        if (ConfigParser.TryParseCleanVersion(latest, out var remote) &&
            ConfigParser.TryParseCleanVersion(version, out var local))
        {
            Console.WriteLine(remote <= local
                ? "Already up to date."
                : $"v{latest} available!");
        }
        else
        {
            Console.WriteLine("Failed to parse version, continuing.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"version check failed ({ex.Message}), continuing.");
    }

    Console.WriteLine();
    Console.WriteLine("SAS 升级通过官方安装脚本完成（覆盖程序，保留配置）/ Upgrade with the official installer:");
    Console.WriteLine($"  {command}");

    if (!autoYes)
    {
        Console.WriteLine();
        Console.WriteLine("提示：加 --yes 可直接执行升级脚本 / Add --yes to run it now.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("Running upgrade script...");

    try
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"sas-install-{Guid.NewGuid():N}.sh");
        try
        {
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                using var response = await http.GetAsync(installerUrl);
                response.EnsureSuccessStatusCode();
                var script = await response.Content.ReadAsStringAsync();
                if (!script.StartsWith("#!", StringComparison.Ordinal))
                    throw new InvalidDataException("upgrade script does not look like a shell script");
                await File.WriteAllTextAsync(scriptPath, script);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var isRoot = string.Equals(Environment.UserName, "root", StringComparison.Ordinal);
            var psi = isRoot
                ? new System.Diagnostics.ProcessStartInfo("bash", $"\"{scriptPath}\" --upgrade --yes")
                : new System.Diagnostics.ProcessStartInfo("sudo", $"bash \"{scriptPath}\" --upgrade --yes");
            psi.UseShellExecute = false;
            var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit();
            if (process != null && process.ExitCode != 0)
            {
                Console.WriteLine($"升级脚本退出码: {process.ExitCode}");
                Console.WriteLine("Please run manually:");
                Console.WriteLine($"  {command}");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to run upgrade script: {ex.Message}");
        Console.WriteLine("Please run manually:");
        Console.WriteLine($"  {command}");
    }
}
