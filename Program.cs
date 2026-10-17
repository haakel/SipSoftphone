using System.Runtime.InteropServices;
using System.Windows;

namespace SipSoftphone;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);

    private const int AttachParentProcess = -1;

    [STAThread]
    public static int Main(string[] args)
    {
        var selftest = args.Contains("--selftest");
        var headless = args.Contains("--headless");
        var configPath = args.FirstOrDefault(a => !a.StartsWith("--"));

        if (selftest || headless)
        {
            AttachConsole(AttachParentProcess);
            if (!Console.IsOutputRedirected)
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
            var cfg = AppConfig.Load(configPath);
            Diag.Sink = msg =>
            {
                Console.WriteLine(msg);
                System.Diagnostics.Trace.WriteLine(msg);
            };
            return selftest ? RunSelfTest(cfg) : RunHeadless(cfg);
        }

        var config = AppConfig.Load(configPath);
        var app = new App();
        app.InitializeComponent();
        return app.Run(new MainWindow(config));
    }

    // ---------- self test: hook chain only (SSE + JSONL + webhook + trigger) ----------
    private static int RunSelfTest(AppConfig config)
    {
        Console.WriteLine($"SipSoftphone selftest (config: {config.ConfigPath})");

        SseServer? sse = null;
        if (config.Hook.SseEnabled)
        {
            try
            {
                sse = new SseServer(config.Hook.SsePort);
                sse.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[sse] start failed: {ex.Message}");
                sse = null;
            }
        }

        using var bus = new CallEventBus(config.Hook, sse);

        var ok = true;
        var testPhone = "9121234567";
        string? received = null;

        var listener = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var resp = await http.GetAsync($"http://127.0.0.1:{config.Hook.SsePort}/events", HttpCompletionOption.ResponseHeadersRead);
                using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line == null) break;
                    if (line.StartsWith("data: ")) { received = line[6..]; break; }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[selftest] sse listener error: {ex.Message}"); }
        });

        Thread.Sleep(700);
        bus.Emit("call_received", testPhone, "SELFTEST", "simulated incoming");
        bus.Emit("call_answered", testPhone, "SELFTEST", "simulated answer");
        ChromeTrigger.Fire(config.Trigger, testPhone, "call_answered");

        listener.Wait();
        Console.WriteLine(received != null
            ? $"[selftest] SSE received: {received}"
            : "[selftest] FAIL: no SSE event within 8s");
        if (received == null) ok = false;

        var logPath = Path.IsPathRooted(config.Hook.LogFile) ? config.Hook.LogFile : Path.Combine(AppContext.BaseDirectory, config.Hook.LogFile);
        var logOk = File.Exists(logPath) && File.ReadAllText(logPath).Contains("call_answered");
        Console.WriteLine(logOk ? $"[selftest] JSONL ok: {logPath}" : $"[selftest] FAIL: JSONL missing/empty at {logPath}");
        if (!logOk) ok = false;

        Console.WriteLine(ok ? "[selftest] PASS" : "[selftest] FAIL");
        sse?.Dispose();
        return ok ? 0 : 1;
    }

    // ---------- headless: full engine, no UI ----------
    private static int RunHeadless(AppConfig config)
    {
        Console.WriteLine($"SipSoftphone headless (config: {config.ConfigPath})");

        using var engine = new PhoneEngine(config);
        engine.Log += Console.WriteLine;
        if (!engine.Start()) return 1;

        Console.WriteLine("commands: A=answer  R=reject  H=hangup  S=status  Q=quit");
        var interactive = !Console.IsInputRedirected;
        if (!interactive)
            Console.WriteLine("[console] stdin redirected — running headless (events still fire, keys disabled)");

        while (true)
        {
            if (!interactive)
            {
                Thread.Sleep(500);
                continue;
            }
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(150);
                continue;
            }

            var k = Console.ReadKey(true).Key;
            if (k == ConsoleKey.Q) break;
            else if (k == ConsoleKey.A) { _ = engine.AnswerPendingAsync(); }
            else if (k == ConsoleKey.R) engine.RejectPending();
            else if (k == ConsoleKey.H) engine.Hangup();
            else if (k == ConsoleKey.S)
            {
                var c = engine.CurrentCall;
                Console.WriteLine($"status: reg={engine.Registration.Text} call={c.State} pending={c.RawNumber} sse={(engine.IsSseRunning ? engine.SseUrl : "off")}");
            }
        }

        Console.WriteLine("shutting down...");
        return 0;
    }
}
