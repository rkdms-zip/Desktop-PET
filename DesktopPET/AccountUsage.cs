using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CodexPet;

// Read-only RPCs through the installed Codex client; never read auth.json ourselves.
public sealed class AccountUsage : IDisposable
{
    Process? server;
    readonly CancellationTokenSource lifetime = new();
    int nextId;
    public Session? Current { get; private set; }
    public string? Error { get; private set; }
    public string? Diagnostic { get; private set; }

    public async Task Refresh()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            if (server == null || server.HasExited)
            {
                StopServer();
                timeout.Token.ThrowIfCancellationRequested();
                var start = new ProcessStartInfo(FindCodex(), "app-server")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                var process = Process.Start(start) ?? throw new IOException();
                server = process;
                process.ErrorDataReceived += (_, _) => { }; // Drain stderr without exposing account data.
                process.BeginErrorReadLine();
                await Request("initialize", new { clientInfo = new { name = "codex_pet", title = "Codex Pet", version = "0.2.0" } }, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            var response = await Request("account/rateLimits/read", null, timeout.Token).ConfigureAwait(false);
            Current = Parse(response);
            Error = null;
            Diagnostic = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException
            or System.ComponentModel.Win32Exception or JsonException or UnauthorizedAccessException)
        {
            Diagnostic = $"{ex.GetType().Name}: {ex.Message}";
            Error = ex is OperationCanceledException ? "계정 조회 시간 초과 · 재시도 중" : "계정 조회 실패 · Codex 로그인과 연결을 확인해 주세요";
            StopServer();
        }
    }

    async Task<JsonElement> Request(string method, object? parameters, CancellationToken token)
    {
        int id = ++nextId;
        var process = server ?? throw new IOException();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) ?? throw new IOException();
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            if (!row.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                || !responseId.TryGetInt32(out int value) || value != id) continue;
            if (row.TryGetProperty("error", out var error)) throw new IOException(
                error.TryGetProperty("message", out var message) ? message.GetString() : "Account RPC failed.");
            return row.GetProperty("result").Clone();
        }
    }

    public static Session Parse(JsonElement result)
    {
        JsonElement limits;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
        {
            // Never silently display a different model bucket as the Codex account quota.
            if (!buckets.TryGetProperty("codex", out limits)) throw new IOException("Codex quota unavailable.");
        }
        else if (!result.TryGetProperty("rateLimits", out limits) || limits.ValueKind != JsonValueKind.Object)
            throw new IOException("Quota unavailable.");
        var snapshot = new Session { UsageUpdated = DateTimeOffset.UtcNow };
        if (limits.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
        {
            snapshot.Primary = Number(primary, "usedPercent");
            snapshot.PrimaryReset = (long?)Number(primary, "resetsAt");
            snapshot.PrimaryMinutes = (int)(Number(primary, "windowDurationMins") ?? 300);
        }
        if (limits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
        {
            snapshot.Secondary = Number(secondary, "usedPercent");
            snapshot.SecondaryReset = (long?)Number(secondary, "resetsAt");
            snapshot.SecondaryMinutes = (int)(Number(secondary, "windowDurationMins") ?? 10080);
        }
        return snapshot;
    }
    static double? Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) ? n : null;

    static string FindCodex()
    {
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var file = Path.Combine(path.Trim('"'), "codex.exe");
            if (File.Exists(file)) return file;
        }
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled))
        {
            var file = Directory.EnumerateFiles(bundled, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file != null) return file;
        }
        throw new IOException("Codex executable unavailable.");
    }
    public void Dispose()
    {
        lifetime.Cancel();
        StopServer();
    }
    void StopServer()
    {
        var process = Interlocked.Exchange(ref server, null);
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}

