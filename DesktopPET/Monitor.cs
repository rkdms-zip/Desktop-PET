using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexPet;

public sealed class Session
{
    public string Id = "", Title = "Codex 작업", Model = "확인 중", Activity = "상태 확인 중";
    public string State = "unknown";
    public DateTimeOffset Updated, Started;
    public long Tokens;
    public double? Primary, Secondary;
    public long? PrimaryReset, SecondaryReset;
    public int PrimaryMinutes = 300, SecondaryMinutes = 10080;
    public DateTimeOffset UsageUpdated;
    public long Offset;
    public string DisplayState => State == "running" && DateTimeOffset.UtcNow - Updated > TimeSpan.FromMinutes(3)
        ? "unknown" : State;
}

public sealed class CodexMonitor
{
    readonly string root;
    readonly Dictionary<string, Session> sessions = new();
    public string? Error;
    public CodexMonitor(string? home = null) => root = home ?? Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public Session[] Poll()
    {
        try
        {
            var directory = Path.Combine(root, "sessions");
            if (!Directory.Exists(directory)) { Error = "Codex 세션 폴더를 찾을 수 없습니다."; return []; }
            var paths = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p)).OrderByDescending(p => p.LastWriteTimeUtc).Take(12).ToArray();
            foreach (var file in paths)
            {
                if (!sessions.TryGetValue(file.FullName, out var session))
                {
                    var stem = Path.GetFileNameWithoutExtension(file.Name);
                    sessions[file.FullName] = session = new() { Id = stem.Length >= 36 ? stem[^36..] : stem };
                }
                ReadIncrement(file.FullName, session);
            }
            foreach (var old in sessions.Keys.Except(paths.Select(p => p.FullName)).ToArray()) sessions.Remove(old);
            var index = Path.Combine(root, "session_index.jsonl");
            if (File.Exists(index))
            {
                using var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var id = Str(doc.RootElement, "id");
                        foreach (var session in sessions.Values.Where(s => s.Id == id))
                            session.Title = Str(doc.RootElement, "thread_name") ?? session.Title;
                    }
                    catch (JsonException) { }
                }
            }
            Error = null;
            return sessions.Values.OrderByDescending(s => s.DisplayState == "running").ThenByDescending(s => s.Updated).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "기록을 읽지 못했습니다. 다음 갱신 때 다시 시도합니다."; return sessions.Values.ToArray(); }
    }

    public static void ReadIncrement(string path, Session session)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < session.Offset) { session.Offset = 0; session.State = "unknown"; }
        // On initial attach, bound history work. Later reads consume only appended complete lines.
        bool skipFirst = session.Offset == 0 && stream.Length > 4 * 1024 * 1024;
        if (skipFirst) session.Offset = stream.Length - 4 * 1024 * 1024;
        stream.Position = session.Offset;
        var bytes = new byte[(int)Math.Min(stream.Length - session.Offset, 8 * 1024 * 1024)];
        int count = 0, read;
        while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
        int start = 0;
        for (int i = 0; i < count; i++)
        {
            if (bytes[i] != (byte)'\n') continue;
            if (!skipFirst) Apply(Encoding.UTF8.GetString(bytes, start, i - start), session);
            skipFirst = false;
            start = i + 1;
        }
        session.Offset += start;
    }

    public static void Apply(string line, Session s)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var row = doc.RootElement;
            if (!row.TryGetProperty("payload", out var p)) return;
            var time = DateTimeOffset.TryParse(Str(row, "timestamp"), out var t) ? t : s.Updated;
            var type = Str(row, "type");
            if (type == "session_meta") s.Id = Str(p, "id") ?? s.Id;
            if (type == "turn_context") s.Model = Str(p, "model") ?? s.Model;
            if (type == "event_msg")
            {
                switch (Str(p, "type"))
                {
                    case "task_started":
                    case "turn_started": s.State = "running"; s.Started = time; s.Activity = "요청 처리 중"; break;
                    case "task_complete":
                    case "turn_completed": s.State = "done"; s.Activity = "작업 완료"; break;
                    case "turn_aborted": s.State = "idle"; s.Activity = "작업 중단됨"; break;
                    case "error": s.State = "error"; s.Activity = "오류 발생 · Codex를 확인해 주세요"; break;
                    case "token_count":
                        if (p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                            && info.TryGetProperty("total_token_usage", out var usage))
                            s.Tokens = Long(usage, "total_tokens") ?? s.Tokens;
                        if (p.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                        {
                            if (limits.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
                            {
                                s.Primary = Number(primary, "used_percent"); s.PrimaryReset = Long(primary, "resets_at");
                                s.PrimaryMinutes = (int)(Long(primary, "window_minutes") ?? 300);
                            }
                            if (limits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                            {
                                s.Secondary = Number(secondary, "used_percent"); s.SecondaryReset = Long(secondary, "resets_at");
                                s.SecondaryMinutes = (int)(Long(secondary, "window_minutes") ?? 10080);
                            }
                            s.UsageUpdated = time;
                        }
                        break;
                }
            }
            if (type == "response_item" && s.State == "running")
            {
                var name = Str(p, "name") ?? "";
                if (Str(p, "type") == "function_call")
                    s.Activity = name.Contains("patch") ? "파일 수정 중" :
                        name.Contains("exec") || name.Contains("stdin") ? "명령 실행 중" :
                        name.Contains("search") || name.Contains("run") ? "정보 확인 중" : "도구 사용 중";
                else if (Str(p, "type") == "function_call_output") s.Activity = "결과 확인 중";
                else if (Str(p, "type") == "message") s.Activity = "응답 작성 중";
            }
            if (time > s.Updated) s.Updated = time;
        }
        catch (JsonException) { }
    }

    static string? Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static long? Long(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    static double? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : null;
}
