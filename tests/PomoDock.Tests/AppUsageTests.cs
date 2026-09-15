using PomoDock.Core;

internal static class AppUsageTests
{
    public static void Run(Action<string, Action> test, Action<double, double> equal, Action<bool, string> assert)
    {
        test("foreground samples accumulate per process and cleaned title", () =>
        {
            var ledger = new AppUsageLedger();
            ledger.Add("brave", "lofi hip hop radio - YouTube - Brave", 120);
            ledger.Add("brave", "lofi hip hop radio - YouTube - Brave", 60);
            ledger.Add("Code", "focus-dock - Visual Studio Code", 90);
            var snap = ledger.Snapshot();
            equal(2, snap.Count);
            assert(snap[0].Process == "brave" && snap[0].Seconds == 180, "brave time is merged");
            assert(snap[0].Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase), "the page stays after stripping Brave");
            assert(snap[0].Label.StartsWith("Brave", StringComparison.Ordinal), "brave is named");
            var taken = ledger.Take();
            equal(2, taken.Count);
            equal(0, ledger.Snapshot().Count);
        });

        test("idle lock screens are grouped and explorer desktop is ignored", () =>
        {
            var ledger = new AppUsageLedger();
            ledger.Add("LockApp", "Windows Default Lock Screen", 30);
            ledger.Add("explorer", "", 10);
            ledger.Add("dwm", "DWM", 5);
            var snap = ledger.Snapshot();
            equal(1, snap.Count);
            assert(snap[0].Process == "idle" && snap[0].Seconds == 45, "idle windows share one bucket");
            assert(AppUsage.Label("idle", "") == "Inactivo", "idle has a readable name");
        });

        test("sessions combine into a period ranking", () =>
        {
            var a = new Session { Apps = [new() { Process = "code", Title = "dock", Seconds = 600 }, new() { Process = "brave", Title = "YouTube", Seconds = 120 }] };
            var b = new Session { Apps = [new() { Process = "code", Title = "dock", Seconds = 300 }] };
            var combined = AppUsage.CombineSessions([a, b]);
            equal(2, combined.Count);
            equal(900, combined[0].Seconds);
            assert(combined[0].Process == "code", "code leads");
            assert(AppUsage.Summarize(combined, 2).Contains("VS Code", StringComparison.Ordinal), "summary names vscode");
        });
    }
}
