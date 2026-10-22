using System.IO;
using System.Text;

namespace CodexPet;

static class Tests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        var s = new Session();
        Check(PetDrawing.FrameAt("running", 0) == 0 && PetDrawing.FrameAt("running", 150) == 1
            && PetDrawing.FrameAt("running", 300) == 2 && PetDrawing.FrameAt("running", 450) == 3
            && PetDrawing.FrameAt("running", 600) == 0, "typing loop timing");
        Check(new[] { "idle", "done", "error", "unknown" }.All(state => PetDrawing.FrameAt(state, 300) == 1),
            "typing stops outside running state");
        string Event(string type) => System.Text.Json.JsonSerializer.Serialize(new
            { timestamp = DateTimeOffset.UtcNow, type = "event_msg", payload = new { type } });
        CodexMonitor.Apply(Event("task_started"), s);
        Check(s.DisplayState == "running", "start");
        CodexMonitor.Apply("""{"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":1234}},"rate_limits":{"primary":{"used_percent":25,"window_minutes":300,"resets_at":2000000000},"secondary":{"used_percent":60,"window_minutes":10080,"resets_at":2000000000}}}}""", s);
        Check(s.Tokens == 1234 && s.Primary == 25 && s.Secondary == 60, "usage");
        CodexMonitor.Apply(Event("task_complete"), s);
        Check(s.State == "done", "complete");
        CodexMonitor.Apply(Event("task_started"), s);
        s.Updated = DateTimeOffset.UtcNow.AddMinutes(-4);
        Check(s.DisplayState == "unknown", "stale");
        CodexMonitor.Apply(Event("turn_aborted"), s);
        Check(s.State == "idle", "abort");
        CodexMonitor.Apply("{bad json", s);
        Check(s.State == "idle", "malformed");
        using (var quota = System.Text.Json.JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":42,"windowDurationMins":300,"resetsAt":2000000000},"secondary":null}}}"""))
        {
            var actual = AccountUsage.Parse(quota.RootElement);
            Check(actual.Primary == 42 && actual.Secondary == null, "prefer Codex bucket; absent is unknown");
            Check(actual.PrimaryMinutes == 300 && actual.PrimaryReset == 2000000000, "account reset metadata");
        }
        using (var quota = System.Text.Json.JsonDocument.Parse("""{"rateLimits":{"primary":null,"secondary":{"usedPercent":12,"windowDurationMins":10080}}}"""))
        {
            var actual = AccountUsage.Parse(quota.RootElement);
            Check(actual.Primary == null && actual.Secondary == 12, "legacy response");
        }
        using (var quota = System.Text.Json.JsonDocument.Parse("""{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":50}}}}"""))
        {
            bool rejected = false;
            try { AccountUsage.Parse(quota.RootElement); } catch (IOException) { rejected = true; }
            Check(rejected, "do not substitute another quota bucket");
        }
        var path = Path.GetTempFileName();
        try
        {
            var tail = new Session();
            var line = Event("task_started");
            File.WriteAllText(path, line[..20], new UTF8Encoding(false));
            CodexMonitor.ReadIncrement(path, tail);
            Check(tail.Offset == 0 && tail.State == "unknown", "partial line deferred");
            File.AppendAllText(path, line[20..] + "\n", new UTF8Encoding(false));
            CodexMonitor.ReadIncrement(path, tail);
            Check(tail.State == "running", "partial line completed");
            File.AppendAllText(path, Event("task_complete") + "\n");
            CodexMonitor.ReadIncrement(path, tail);
            Check(tail.State == "done", "incremental completion");
            File.WriteAllText(path, Event("turn_aborted") + "\n");
            CodexMonitor.ReadIncrement(path, tail);
            Check(tail.State == "idle", "truncation");
        }
        finally { File.Delete(path); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-results.txt"), $"{checks} checks passed.");
        return 0;
    }
}
