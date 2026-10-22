using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexPet;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return Tests.Run();
        if (args.Contains("--usage-check"))
        {
            using var usage = new AccountUsage();
            usage.Refresh().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "usage-check.txt"),
                usage.Error != null ? $"{usage.Error}\n{usage.Diagnostic}" : $"Account RPC OK · primary {usage.Current?.Primary}% · secondary {usage.Current?.Secondary}% · {usage.Current?.UsageUpdated:O}");
            return usage.Error == null ? 0 : 1;
        }
        if (args.Contains("--animation-preview")) { _ = new Application(); return PetDrawing.ExportPreview(); }
        using var mutex = new Mutex(true, "CodexPet.Desktop.v1", out bool first);
        if (!first && !args.Contains("--snapshot")) return 0;
        var app = new Application();
        var window = new PetWindow(args.Contains("--snapshot"));
        if (args.Contains("--snapshot"))
        {
            window.Show();
            window.LoadSnapshot();
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "preview.png"));
            encoder.Save(file);
            window.Close();
            return 0;
        }
        app.Run(window);
        return 0;
    }
}

public sealed class PetWindow : Window
{
    readonly CodexMonitor monitor = new();
    readonly AccountUsage account = new();
    readonly DispatcherTimer usagePolling = new() { Interval = TimeSpan.FromSeconds(15) };
    bool usageBusy;
    readonly PetDrawing pet = new();
    readonly Border panel;
    readonly StackPanel content = new();
    readonly TextBlock caption = new();
    readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(50) };
    Session[] sessions = [];
    bool busy;
    Point down;

    public PetWindow(bool snapshot = false)
    {
        Title = "Codex Pet";
        Width = 360; Height = Math.Min(690, SystemParameters.WorkArea.Height - 24);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = true;
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 16;
        var root = new Grid();
        Content = root;
        panel = new Border
        {
            Background = Brush("#F5E8D5"), BorderBrush = Brush("#E3CDB1"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(34), Padding = new Thickness(24), Margin = new Thickness(8, 0, 8, 260),
            Visibility = Visibility.Collapsed, Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden }
        };
        root.Children.Add(panel);
        var petArea = new Grid { Width = 270, Height = 255, VerticalAlignment = VerticalAlignment.Bottom };
        petArea.Children.Add(pet);
        caption.Foreground = Brush("#664A30"); caption.FontSize = 11; caption.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border { Background = Brush("#F5E8D5"), CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Child = caption };
        petArea.Children.Add(badge); root.Children.Add(petArea);
        petArea.ToolTip = "클릭: 작업 상태 · 드래그: 위치 이동 · 우클릭: 메뉴";
        petArea.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(this); petArea.CaptureMouse(); };
        petArea.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || !petArea.IsMouseCaptured) return;
            var now = e.GetPosition(this);
            if ((now - down).Length > 5) { petArea.ReleaseMouseCapture(); DragMove(); Clamp(); }
        };
        petArea.MouseLeftButtonUp += async (_, _) =>
        {
            if (!petArea.IsMouseCaptured) return;
            petArea.ReleaseMouseCapture();
            panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RefreshPanel();
            if (panel.Visibility == Visibility.Visible) await RefreshUsage();
        };
        var menu = new ContextMenu();
        var toggle = new MenuItem { Header = "작업 상태 열기 / 닫기" };
        toggle.Click += async (_, _) => { panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; RefreshPanel(); if (panel.Visibility == Visibility.Visible) await RefreshUsage(); };
        menu.Items.Add(toggle);
        var always = new MenuItem { Header = "항상 위에 표시", IsCheckable = true, IsChecked = true };
        always.Click += (_, _) => Topmost = always.IsChecked; menu.Items.Add(always);
        var quit = new MenuItem { Header = "펫 종료" }; quit.Click += (_, _) => Close(); menu.Items.Add(quit);
        petArea.ContextMenu = menu;
        polling.Tick += async (_, _) => await Poll();
        animation.Tick += (_, _) => pet.Advance();
        usagePolling.Tick += async (_, _) => await RefreshUsage();
        Loaded += async (_, _) => { if (!snapshot) { animation.Start(); polling.Start(); usagePolling.Start(); await Task.WhenAll(Poll(), RefreshUsage()); } };
        Closed += (_, _) => { polling.Stop(); animation.Stop(); usagePolling.Stop(); account.Dispose(); };
        caption.Text = "Codex 연결 확인 중";
    }
    void Clamp()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left - 90, area.Right - Width + 90);
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    public void ShowPanel() { panel.Visibility = Visibility.Visible; RefreshPanel(); }
    public void LoadSnapshot()
    {
        sessions = monitor.Poll();
        account.Refresh().GetAwaiter().GetResult();
        pet.State = sessions.FirstOrDefault()?.DisplayState ?? "idle";
        caption.Text = pet.State == "running" ? "Codex 작업 중" : "Codex Pet";
        ShowPanel();
    }
    async Task Poll()
    {
        if (busy) return;
        busy = true;
        try
        {
            sessions = await Task.Run(monitor.Poll);
            pet.State = monitor.Error != null ? "unknown" : sessions.FirstOrDefault()?.DisplayState ?? "idle";
            caption.Text = pet.State switch { "running" => "Codex 작업 중", "done" => "작업 완료", "error" => "확인이 필요해요", "unknown" => "상태 확인 필요", _ => "쉬는 중" };
            RefreshPanel();
        }
        catch { caption.Text = "연결 확인 필요"; pet.State = "unknown"; }
        finally { busy = false; }
    }
    async Task RefreshUsage()
    {
        if (usageBusy) return;
        usageBusy = true;
        try { await account.Refresh(); RefreshPanel(); }
        finally { usageBusy = false; }
    }
    void RefreshPanel()
    {
        if (panel.Visibility != Visibility.Visible) return;
        content.Children.Clear();
        Add("Desktop PET", 21, "#664A30", 7);
        var latest = account.Current;
        if (latest != null)
        {
            Add("계정 사용량", 13, "#664A30", 20);
            Usage(WindowName(latest.PrimaryMinutes), latest.Primary, latest.PrimaryReset);
            Usage(WindowName(latest.SecondaryMinutes), latest.Secondary, latest.SecondaryReset);
            Add(account.Error ?? $"계정 직접 조회 · {latest.UsageUpdated.ToLocalTime():HH:mm:ss} 갱신", 10, "#664A30", 7);
            if (account.Error != null) Add("위 수치는 마지막 성공 값입니다.", 10, "#664A30", 4);
            else Add("15초마다 갱신 · 패널을 열면 즉시 조회", 10, "#664A30", 4);
        }
        else Add(account.Error ?? "계정 사용량을 조회하고 있습니다.", 12, "#664A30", 18);
        var running = sessions.Where(s => s.DisplayState == "running").ToArray();
        Add($"현재 작업 중  ·  {running.Length}개", 13, "#664A30", 20);
        if (running.Length == 0) Add("지금은 쉬는 중이에요. 새 작업을 기다리고 있어요.", 12, "#664A30", 8);
        foreach (var s in running)
        {
            var state = s.DisplayState;
            Add(s.Title, 13, "#664A30", 13);
            Add(state == "unknown" ? "● 상태 확인 필요 · 최근 신호 없음" : "● " + s.Activity, 12,
                state == "running" ? "#664A30" : "#664A30", 4);
            var elapsed = state == "running" && s.Started != default
                ? $" · {(int)(DateTimeOffset.UtcNow - s.Started).TotalMinutes}분 경과" : "";
            Add($"{s.Model}{elapsed}\n누적 토큰 {s.Tokens:N0} · {s.Updated.ToLocalTime():MM/dd HH:mm}", 10, "#664A30", 4);
        }
        Add("클릭으로 접기 · 드래그로 이동 · 우클릭으로 종료", 10, "#664A30", 20);
    }
    static string WindowName(int minutes) => minutes == 10080 ? "주간" : minutes % 60 == 0 ? $"{minutes / 60}시간" : $"{minutes}분";
    void Usage(string name, double? percent, long? reset)
    {
        bool expired = reset.HasValue && DateTimeOffset.FromUnixTimeSeconds(reset.Value) < DateTimeOffset.UtcNow;
        Add($"{name}  {(expired ? "갱신 대기" : percent.HasValue ? $"{percent:0.#}% 사용" : "정보 없음")}", 12, "#664A30", 10);
        double used = expired ? 0 : Math.Clamp(percent ?? 0, 0, 100);
        var track = new Grid();
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(used, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - used, GridUnitType.Star) });
        track.Children.Add(new Border { Background = Brush("#664A30"), CornerRadius = new CornerRadius(5) });
        content.Children.Add(new Border { Height = 8, Margin = new Thickness(0, 8, 0, 0),
            Background = Brush("#E3CDB1"), CornerRadius = new CornerRadius(5), Child = track,
            ToolTip = expired ? "사용량 갱신 대기" : percent.HasValue ? $"{percent:0.#}% 사용" : "사용량 정보 없음" });
        if (reset.HasValue) Add($"초기화 {DateTimeOffset.FromUnixTimeSeconds(reset.Value).ToLocalTime():MM/dd HH:mm}", 10, "#664A30", 4);
    }
    void Add(string text, double size, string color, double top = 0) => content.Children.Add(new TextBlock
    { Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), FontFamily = new FontFamily("Malgun Gothic") });
    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}


