using System.Text;
using System.Windows;

namespace Wanwei;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            var options = Options.Parse(args);
            if (options.Mode == "self-test") return SelfTests.Run();
            if (options.Mode is "local-wifi-lab" or "local-wifi-fixture-lab")
            {
                using var stop = new CancellationTokenSource();
                ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stop.Cancel(); };
                Console.CancelKeyPress += cancelHandler;
                try { return options.Mode == "local-wifi-fixture-lab" ?
                    LocalWifiFixtureLab.RunAsync(options.Directory, stop.Token).GetAwaiter().GetResult() :
                    LocalWifiLab.RunAsync(options.Directory, options.Seconds, stop.Token).GetAwaiter().GetResult(); }
                finally { Console.CancelKeyPress -= cancelHandler; }
            }
            if (options.Mode is "probe" or "discover" or "advertise")
            {
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                if (options.CancelAfterMs is not null) stop.CancelAfter(options.CancelAfterMs.Value);
                var report = Task.Run(() => ProbeRunner.RunAsync(options.Mode == "discover" ? options.Seconds : null,
                    stop.Token, advertisementSeconds: options.Mode == "advertise" ? options.Seconds : null)).GetAwaiter().GetResult();
                string path = ReportStore.Save(report, options.Directory);
                Console.WriteLine(path);
                Console.WriteLine(ReportStore.Serialize(report));
                return report.OperationOutcome == "failed" ? 2 : 0;
            }
            var app = new Application();
            P0UsbLabBootstrap? lab = options.LabBootstrapPath is null ? null : P0UsbLabBootstrap.Load(options.LabBootstrapPath);
            lab?.ConsumeAuthorization(options.Directory);
            var window = new MainWindow(options.Directory, lab);
            if (lab is not null) window.Loaded += async (_, _) => await window.StartUsbFixtureLabAsync(options.Seconds);
            if (options.Mode == "render-preview") { window.RenderPreview(options.PreviewPath!); return 0; }
            if (options.Mode is "ui-smoke" or "ui-advertise-smoke" or "ui-pairing-smoke" or "ui-pairing-prompt-smoke")
            {
                int code = 0;
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.Startup += async (_, _) => {
                    try {
                        if (options.Mode == "ui-pairing-prompt-smoke")
                        {
                            // Unlike offscreen render-preview, this no-radio
                            // smoke needs a genuinely shown native STA window.
                            window.Show();
                            await window.PromptSmokeAsync(options.PreviewPath!);
                        }
                        else Console.WriteLine(await window.SmokeAsync(options.PreviewPath!,
                            options.Mode == "ui-advertise-smoke" ? options.Seconds : null,
                            options.Mode == "ui-pairing-smoke" ? options.Seconds : null, options.CancelAfterMs));
                    }
                    catch (Exception ex) { Console.Error.WriteLine(DirectDiscovery.ErrorCode(ex)); code = 1; }
                    finally { app.Shutdown(); }
                };
                app.Run();
                return code;
            }
            app.Run(window);
            return 0;
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 64; }
        catch (Exception ex) { Console.Error.WriteLine(args.Contains("--self-test") ? ex.Message : DirectDiscovery.ErrorCode(ex)); return 1; }
    }
}

internal sealed record Options(string Mode, int Seconds, string Directory, string? PreviewPath, int? CancelAfterMs, string? LabBootstrapPath = null)
{
    public static Options Parse(string[] args)
    {
        string mode = "gui";
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WanweiLab", "Reports");
        string? preview = null; int seconds = 15; int? cancel = null;
        bool modeSet = false, secondsSet = false; string? labBootstrap = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("缺少参数值。");
            switch (args[i])
            {
                case "--usb-p0-lab":
                    if (modeSet) throw new ArgumentException("只允许一个运行模式。");
                    mode = "usb-p0-lab"; modeSet = true; labBootstrap = Path.GetFullPath(Value()); break;
                case "--local-wifi-fixture-lab": case "--local-wifi-lab": case "--probe": case "--discover": case "--advertise": case "--self-test": case "--render-preview": case "--ui-smoke": case "--ui-advertise-smoke": case "--ui-pairing-smoke": case "--ui-pairing-prompt-smoke":
                    if (modeSet) throw new ArgumentException("只允许一个运行模式。");
                    mode = args[i][2..]; modeSet = true;
                    if (mode is "render-preview" or "ui-smoke" or "ui-advertise-smoke" or "ui-pairing-smoke" or "ui-pairing-prompt-smoke") preview = Value();
                    break;
                case "--seconds":
                    secondsSet = true;
                    if (!int.TryParse(Value(), out seconds) || seconds is < 1 or > 60)
                        throw new ArgumentException("无线操作时长必须为 1–60 秒。");
                    break;
                case "--out": directory = Path.GetFullPath(Value()); break;
                case "--cancel-after-ms":
                    if (!int.TryParse(Value(), out int ms) || ms is < 1 or > 60000) throw new ArgumentException("取消时间必须为 1–60000 毫秒。");
                    cancel = ms; break;
                default: throw new ArgumentException("未知参数。支持 --probe、--discover、--advertise、--seconds 15、--cancel-after-ms 1800、--out 目录、--self-test、--ui-pairing-smoke 预览路径。配对接受只在可见窗口进行。");
            }
        }
        if (mode == "usb-p0-lab" && !secondsSet) seconds = 60;
        if (mode == "local-wifi-lab" && (seconds > 15 || cancel is not null))
            throw new ArgumentException("本地 Wi-Fi 实验仅允许 1–15 秒观察，不接受界面实验取消参数。");
        if (mode == "local-wifi-fixture-lab" && (secondsSet || cancel is not null))
            throw new ArgumentException("本地 Wi-Fi 文件实验使用固定期限，不接受时长或界面实验取消参数。");
        return new(mode, seconds, directory, preview, cancel, labBootstrap);
    }
}
