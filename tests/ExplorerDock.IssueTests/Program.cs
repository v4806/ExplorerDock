using ExplorerDock.Services;
using ExplorerDock.Interop;
using ExplorerDock.Views;
using System.Reflection;
using System.ComponentModel;
using System.Threading;
using System.Text.Json;

var failures = 0;

Run("explorer override wins over stale takeover handle", () =>
{
    var explorer = new IntPtr(0x101);
    var app = new IntPtr(0x202);

    TakeoverState.Publish(new[] { explorer, app });
    TakeoverState.SetExplorerTakeover(false);

    try
    {
        False(
            "explorer override wins over stale handle",
            TakeoverState.IsTakeoverWindow(explorer, "CabinetWClass"));

        True(
            "other takeover handle remains",
            TakeoverState.IsTakeoverWindow(app, "Notepad"));
    }
    finally
    {
        TakeoverState.SetExplorerTakeover(true);
        TakeoverState.Publish(Array.Empty<IntPtr>());
    }
});

Run("unobserved physical Win cannot start clipboard hotkey", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int> { 0x5B, 0x56 };

    Equal(
        "physical Win alone is not authorization",
        ClipboardHotkeyDecision.Pass,
        machine.Process(Key(0x56, down: true), enabled: true, keys.Contains));
});

Run("observed left Win starts and completes one hotkey sequence", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    Equal("left Win down passes", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x5B, down: true), true, keys.Contains));

    Press(keys, 0x56);
    Equal("Win+V toggles", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));
    Equal("V repeat stays swallowed", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: true), true, keys.Contains));

    Release(keys, 0x56);
    Equal("V up swallowed", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: false), true, keys.Contains));

    Release(keys, 0x5B);
    Equal(
        "Win up restores physical state",
        ClipboardHotkeyDecision.SwallowAndRestoreWin,
        machine.Process(Key(0x5B, down: false), true, keys.Contains));
});

Run("stale observed Win is vetoed by physical state", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);
    Release(keys, 0x5B);

    Press(keys, 0x56);
    Equal(
        "physical Win up prevents trigger",
        ClipboardHotkeyDecision.Pass,
        machine.Process(Key(0x56, down: true), true, keys.Contains));
});

Run("right Win can start an independent sequence", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5C);
    machine.Process(Key(0x5C, down: true), true, keys.Contains);

    Press(keys, 0x56);
    Equal("right Win+V toggles", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));
});

Run("modifier veto prevents takeover", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int> { 0x11 };

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);

    Press(keys, 0x56);
    Equal("Ctrl+Win+V passes", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x56, down: true), true, keys.Contains));
});

Run("disabled takeover cannot start but completes consumed sequence", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);
    Press(keys, 0x56);
    Equal("started while enabled", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));

    Release(keys, 0x56);
    Equal("V up still swallowed after disable", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: false), false, keys.Contains));

    Release(keys, 0x5B);
    Equal(
        "Win up still restored after disable",
        ClipboardHotkeyDecision.SwallowAndRestoreWin,
        machine.Process(Key(0x5B, down: false), false, keys.Contains));
});

Run("release of the non-initiating Win does not consume sequence", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);
    Press(keys, 0x5C);
    machine.Process(Key(0x5C, down: true), true, keys.Contains);

    Press(keys, 0x56);
    Equal("latest observed Win initiates", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));

    Release(keys, 0x5B);
    Equal("other Win up passes", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x5B, down: false), true, keys.Contains));

    Release(keys, 0x56);
    Equal("V up swallowed", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: false), true, keys.Contains));

    Release(keys, 0x5C);
    Equal(
        "initiating Win up restored",
        ClipboardHotkeyDecision.SwallowAndRestoreWin,
        machine.Process(Key(0x5C, down: false), true, keys.Contains));
});

Run("swallowed V down is not mistaken for physical release", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);

    // V down 已被低级钩子吞掉，因此异步按键状态里可能仍没有 V。
    Equal("initial V triggers", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));
    machine.Process(Key(0x41, down: true), true, keys.Contains);
    Equal("V retry remains swallowed", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: true), true, keys.Contains));
});

Run("a passed V down stays passed until release", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int> { 0x11 };

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);
    Press(keys, 0x56);

    Equal("Ctrl+Win+V passes", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x56, down: true), true, keys.Contains));

    Release(keys, 0x11);
    Equal("V repeat stays passed", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x56, down: true), true, keys.Contains));

    Release(keys, 0x56);
    Equal("V up stays passed", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x56, down: false), true, keys.Contains));
});

Run("other Win down does not discard pending initiating Win up", () =>
{
    var machine = new ClipboardHotkeyStateMachine();
    var keys = new HashSet<int>();

    Press(keys, 0x5B);
    machine.Process(Key(0x5B, down: true), true, keys.Contains);
    Press(keys, 0x56);
    Equal("left Win+V triggers", ClipboardHotkeyDecision.ToggleClipboard, machine.Process(Key(0x56, down: true), true, keys.Contains));
    Release(keys, 0x56);
    Equal("V up swallowed", ClipboardHotkeyDecision.Swallow, machine.Process(Key(0x56, down: false), true, keys.Contains));

    Press(keys, 0x5C);
    Equal("right Win down passes", ClipboardHotkeyDecision.Pass, machine.Process(Key(0x5C, down: true), true, keys.Contains));
    Release(keys, 0x5B);
    Equal(
        "initiating left Win still restores",
        ClipboardHotkeyDecision.SwallowAndRestoreWin,
        machine.Process(Key(0x5B, down: false), true, keys.Contains));
});

Run("host clipboard takeover starts disabled until UI arms it", () =>
{
    using var keyboard = new HostKeyboard(_ => { });
    var field = typeof(HostKeyboard).GetField("_clipboardTakeover", BindingFlags.Instance | BindingFlags.NonPublic);
    var enabled = field?.GetValue(keyboard) as bool?;

    False("default host clipboard takeover", enabled ?? true);
});

Run("clipboard listener registration failure propagates", () =>
{
    Exception? captured = null;

    RunSta(() =>
    {
        try
        {
            using var window = new ClipboardMessageWindow(_ => false);
        }
        catch (Exception ex)
        {
            captured = ex;
        }
    });

    True("registration failure throws", captured is Win32Exception);
});

Run("reapply consumes one unconditional snapshot request atomically", () =>
{
    using var watcher = new ExplorerWatcher();
    watcher.ReapplyNow();

    True("first consume", watcher.ConsumeSnapshotRequestForTest());
    False("second consume", watcher.ConsumeSnapshotRequestForTest());
});

Run("fresh desktop probe overrides stale watcher snapshot", () =>
{
    var window = new IntPtr(0x303);

    using var probe = new VirtualDesktopProbe(query: _ => false);

    False(
        "fresh probe excludes old desktop",
        VirtualDesktop.IsOnCurrentDesktop(window, probe));

    True(
        "probe failure falls back to include",
        VirtualDesktop.IsOnCurrentDesktop(window, probe: null));
});

Run("search box owns Delete in every text state", () =>
{
    False(
        "empty search box does not delete history",
        ExplorerDock.Views.ClipboardPanel.ShouldDeleteSelectedHistory(
            searchBoxHasFocus: true,
            searchTextLength: 0));

    False(
        "non-empty search box does not delete history",
        ExplorerDock.Views.ClipboardPanel.ShouldDeleteSelectedHistory(
            searchBoxHasFocus: true,
            searchTextLength: 3));

    True(
        "list focus keeps history delete shortcut",
        ExplorerDock.Views.ClipboardPanel.ShouldDeleteSelectedHistory(
            searchBoxHasFocus: false,
            searchTextLength: 0));
});

Run("clipboard search matches text files paths and type words", () =>
{
    var text = new ClipItem { Kind = ClipKind.Text, Text = "Alpha BETA 示例" };
    var files = new ClipItem
    {
        Kind = ClipKind.Files,
        Files = new[] { @"C:\Temp\Report-Example.txt" },
    };
    var image = new ClipItem { Kind = ClipKind.Image, ImageFormat = "PNG" };

    True("text case insensitive", ClipboardPanel.MatchesSearchText(text, "beta"));
    True("file path", ClipboardPanel.MatchesSearchText(files, "report-example"));
    True("file type keyword", ClipboardPanel.MatchesSearchText(files, "文件"));
    True("image type keyword", ClipboardPanel.MatchesSearchText(image, "图片"));
    False("unrelated text", ClipboardPanel.MatchesSearchText(text, "missing"));
});

Run("mouse hover selection defaults off", () =>
{
    False("AltTabMouseHoverSelect default", new Settings().AltTabMouseHoverSelect);
});

Run("old settings missing hover field defaults off", () =>
{
    var loaded = JsonSerializer.Deserialize<Settings>("{\"Theme\":0}");
    False("missing AltTabMouseHoverSelect", loaded?.AltTabMouseHoverSelect ?? true);
});

Run("runtime Explorer switch reaches watcher and shared state", () =>
{
    var explorer = new IntPtr(0x404);
    TakeoverState.Publish(new[] { explorer });

    using var host = new LocalWindowHost(takeoverEnabled: true, takeoverExplorer: true);
    host.SetExplorerTakeover(false);

    var watcherField = typeof(LocalWindowHost).GetField("_watcher", BindingFlags.Instance | BindingFlags.NonPublic);
    var watcher = watcherField?.GetValue(host) as ExplorerWatcher;

    False("watcher Explorer preference", watcher?.TakeoverExplorer ?? true);
    False("shared Explorer preference", TakeoverState.IsTakeoverWindow(explorer, "CabinetWClass"));

    TakeoverState.SetExplorerTakeover(true);
    TakeoverState.Publish(Array.Empty<IntPtr>());
});

Run("actual virtual desktop COM probe is available", () =>
{
    RunSta(() =>
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) throw new InvalidOperationException("no foreground window to probe");

        using var probe = VirtualDesktop.CreateProbe();
        True("COM probe created", probe is not null);
        True("foreground window is treated as current desktop", probe!.IsOnCurrentDesktop(window));
    });
});

Console.WriteLine(failures == 0 ? "PASS all issue tests" : $"FAIL {failures} issue test(s)");
return failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

static void True(string name, bool actual)
{
    if (!actual) throw new InvalidOperationException($"{name}: expected True, actual False");
}

static void False(string name, bool actual)
{
    if (actual) throw new InvalidOperationException($"{name}: expected False, actual True");
}

static void Equal<T>(string name, T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, actual {actual}");
    }
}

static KeyStroke Key(int vk, bool down)
    => new(vk, down, altDown: false, injected: false);

static void Press(HashSet<int> keys, int vk) => keys.Add(vk);

static void Release(HashSet<int> keys, int vk) => keys.Remove(vk);

static void RunSta(Action action)
{
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            error = ex;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (error is not null) throw error;
}
