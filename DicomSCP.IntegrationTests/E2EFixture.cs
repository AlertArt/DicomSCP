using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DicomSCP.IntegrationTests;

[CollectionDefinition("E2E", DisableParallelization = true)]
public class E2ECollection : ICollectionFixture<E2EFixture>
{
}

/// <summary>
/// Publishes the server to a temp dir, remaps every port to a high range,
/// starts the process and waits until all DICOM/HTTP ports are listening.
/// </summary>
public sealed class E2EFixture : IAsyncLifetime
{
    public const int HttpPort = 12100;
    public const int StorePort = 12101;
    public const int WorklistPort = 12102;
    public const int QrPort = 12103;
    public const int PrintPort = 12104;
    public const int CommitmentPort = 12105;
    public const int MppsPort = 12106;
    public const int UpsPort = 12107;

    public const string CallingAe = "E2ESCU";
    public const string WorklistNodeName = "E2EWorklist";

    /// <summary>出厂口令被标记为必须修改，fixture 首次登录后改为此口令。</summary>
    public const string AdminPassword = "E2eAdmin!2026";

    public string Root { get; }
    public string DbPath => _dbPath;
    public string ServerLogPath { get; }
    public HttpClient Http { get; } = CreateHttpClient();

    /// <summary>出厂口令未改密时，受限 API 是否被服务端拒绝（强制改密生效）。</summary>
    public bool ForcedChangeBlockedApi { get; private set; }

    private readonly string _repoRoot;
    private readonly Process _server = new();
    private DirectoryInfo _tempDir = null!;
    private string _dbPath = "";
    private readonly object _logLock = new();

    public E2EFixture()
    {
        _repoRoot = FindRepoRoot();
        _tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "dicomscp_e2e_" + Guid.NewGuid().ToString("N")));
        _tempDir.Create();
        Root = _tempDir.FullName;
        ServerLogPath = Path.Combine(Root, "server.out.log");
    }

    public async Task InitializeAsync()
    {
        var appDir = Path.Combine(Root, "app");
        Directory.CreateDirectory(appDir);
        _dbPath = Path.Combine(appDir, "db", "dicom.db");
        await RunAsync("dotnet", $"publish \"{Path.Combine(_repoRoot, "DicomSCP.csproj")}\" -c Release -o \"{appDir}\" -v q");
        PatchAppSettings(Path.Combine(appDir, "appsettings.json"));

        var exe = Path.Combine(appDir, "DicomSCP.dll");
        _server.StartInfo.FileName = "dotnet";
        _server.StartInfo.Arguments = $"\"{exe}\"";
        _server.StartInfo.WorkingDirectory = appDir;
        _server.StartInfo.UseShellExecute = false;
        _server.StartInfo.RedirectStandardOutput = true;
        _server.StartInfo.RedirectStandardError = true;
        _server.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _server.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        if (!_server.Start())
        {
            throw new InvalidOperationException("Failed to start DicomSCP server process.");
        }
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();

var started = await WaitForPortsAsync(TimeSpan.FromSeconds(60));
        if (!started)
        {
            throw new InvalidOperationException(
                $"Server ports did not come up within 60s. Process exited={(HasExited() ? "yes" : "no")}. Log tail:\n{ReadLogTail(200)}");
        }

        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        // 1) 出厂口令登录：新装库会被标记为必须改密
        using (var first = await Http.PostAsJsonAsync("/api/Auth/login", new { username = "admin", password = "admin" }))
        {
            if (!first.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"E2E admin login failed with {(int)first.StatusCode}. Log tail:\n{ReadLogTail(200)}");
            }
        }

        // 1.5) 改密前受限 API 必须被拒绝，验证服务端强制改密生效
        using (var blocked = await Http.GetAsync("/api/Worklist"))
        {
            ForcedChangeBlockedApi = blocked.StatusCode == HttpStatusCode.Forbidden;
        }

        // 2) 完成强制改密（服务端会注销当前会话）
        using (var change = await Http.PostAsJsonAsync(
            "/api/auth/change-password",
            new { oldPassword = "admin", newPassword = AdminPassword }))
        {
            if (!change.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"E2E force password change failed with {(int)change.StatusCode}. Log tail:\n{ReadLogTail(200)}");
            }
        }

        // 3) 用新口令重新登录，取得不带改密标记的会话
        using var login = await Http.PostAsJsonAsync(
            "/api/Auth/login",
            new { username = "admin", password = AdminPassword });
        if (!login.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"E2E admin re-login failed with {(int)login.StatusCode}. Log tail:\n{ReadLogTail(200)}");
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (!_server.HasExited)
            {
                _server.Kill(entireProcessTree: true);
                await _server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch
        {
            // best effort
        }
        _server.Dispose();
        Http.Dispose();
        var keepTemp = Environment.GetEnvironmentVariable("E2E_KEEP_TEMP") == "1";
        try
        {
            if (_tempDir.Exists && !keepTemp)
            {
                _tempDir.Delete(recursive: true);
            }
        }
        catch
        {
            // files may be momentarily locked; cleanup is best effort
        }
    }

    public string ReadLogTail(int lines = 300)
    {
        try
        {
            if (!File.Exists(ServerLogPath))
            {
                return "(no server log)";
            }
            var all = File.ReadAllLines(ServerLogPath);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return $"(server log unavailable: {ex.Message})";
        }
    }

    private void AppendLog(string? line)
    {
        if (line == null)
        {
            return;
        }
        try
        {
            lock (_logLock)
            {
                using var fs = new FileStream(ServerLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(line);
            }
        }
        catch
        {
            // best effort
        }
    }

    public IDicomClient CreateClient(int port, string calledAe)
    {
        var client = DicomClientFactory.Create("127.0.0.1", port, false, CallingAe, calledAe);
        client.NegotiateAsyncOps();
        return client;
    }

    public async Task<bool> WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 20000, int pollMs = 500)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (await condition())
                {
                    return true;
                }
            }
            catch
            {
                // treat transient errors (connection refused, busy db) as not-yet-ready
            }
            await Task.Delay(pollMs);
        }
        return false;
    }

    public Task<bool> WaitForFileAsync(string fileName, int timeoutMs = 15000) =>
        WaitForAsync(
            () => Task.FromResult(
                Directory.Exists(Root) && Directory.GetFiles(Root, fileName, SearchOption.AllDirectories).Length > 0),
            timeoutMs);

    public Task<T?> QueryScalarAsync<T>(string sql, object? param = null)
    {
        using var conn = new SqliteConnection("Data Source=" + DbPath);
        return conn.QueryFirstOrDefaultAsync<T>(sql, param);
    }

    public async Task<int> QueryCountAsync(string sql, object? param = null)
    {
        using var conn = new SqliteConnection("Data Source=" + DbPath);
        return await conn.ExecuteScalarAsync<int>(sql, param);
    }

    private bool HasExited()
    {
        try
        {
            return _server.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DicomSCP.csproj")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("DicomSCP.csproj not found above test output directory.");
    }

    private static async Task RunAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} {arguments} exited with code {p.ExitCode}.");
        }
    }

    private void PatchAppSettings(string appSettingsPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(appSettingsPath))!.AsObject();

        root["Kestrel"]!["Endpoints"]!["Http"]!["Url"] = $"http://127.0.0.1:{HttpPort}";

        var dicom = root["DicomSettings"]!.AsObject();
        dicom["StoreSCPPort"] = StorePort;
        dicom["WorklistSCP"]!["Port"] = WorklistPort;
        dicom["QRSCP"]!["Port"] = QrPort;
        dicom["PrintSCP"]!["Port"] = PrintPort;
        dicom["StorageCommitmentSCP"]!["Port"] = CommitmentPort;
        dicom["MppsSCP"]!["Port"] = MppsPort;
        dicom["UpsSCP"]!["Port"] = UpsPort;

        var nodes = root["QueryRetrieveConfig"]!["RemoteNodes"]!.AsArray();
        nodes.Clear();
        nodes.Add(new JsonObject
        {
            ["Name"] = WorklistNodeName,
            ["AeTitle"] = "WORKLISTSCP",
            ["HostName"] = "127.0.0.1",
            ["Port"] = WorklistPort,
            ["Type"] = "worklist"
        });

        File.WriteAllText(appSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task<bool> WaitForPortsAsync(TimeSpan timeout)
    {
        var ports = new[]
        {
            HttpPort, StorePort, WorklistPort, QrPort, PrintPort, CommitmentPort, MppsPort, UpsPort
        };
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var allUp = true;
            foreach (var port in ports)
            {
                if (!await PortOpenAsync(port))
                {
                    allUp = false;
                    break;
                }
            }
            if (allUp)
            {
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    private static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(IPAddress.Loopback, port);
            await task.WaitAsync(TimeSpan.FromSeconds(1));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true
        };
        return new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{HttpPort}") };
    }
}