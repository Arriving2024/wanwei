using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using Windows.Devices.WiFiDirect;

namespace Wanwei;

public sealed class MainWindow : Window, IPairingUi
{
    private readonly Button probe = MakeButton("检查本机能力", true);
    private readonly Button scan = MakeButton("扫描附近设备 · 15 秒", false);
    private readonly Button advertise = MakeButton("允许本次被发现 · 30 秒", false);
    private readonly Button pair = MakeButton("等待连接请求 · 60 秒", false);
    private readonly Button stop = MakeButton("停止", false);
    private readonly Button export = MakeButton("导出本次报告", false);
    private readonly TextBlock state = Text("准备就绪", 20, "#F4F7FF");
    private readonly TextBlock hint = Text("检查本机能力，再扫描附近设备或短时允许本机被发现。", 13, "#A7B7D1");
    private readonly TextBox details = new();
    private readonly TextBox activity = new();
    private readonly TextBlock result = Text("还没有测试结果", 16, "#C8D8F1");
    private readonly string reportDirectory;
    private CancellationTokenSource? operation;
    private LabReport? latest;
    private bool closeWhenDone;
    private bool restartRequired;
    private bool rejectAllRequests;
    private Border? pairingPrompt;
    private readonly CheckBox transferExperiment = new() { Content = "实验：1 MiB 安全互传 · 连接最多保留 180 秒", Margin = new Thickness(0, 5, 0, 5), Foreground = Brushes.LightBlue };
    private readonly P0Identity identity;
    private bool transferEnabledForRun;
    private readonly P0UsbLabBootstrap? labBootstrap;
    private bool labRunStarted;
    private bool labRunConsumed;

    public MainWindow(string reportDirectory) : this(reportDirectory, null) { }
    internal MainWindow(string reportDirectory, P0UsbLabBootstrap? labBootstrap)
    {
        this.reportDirectory = reportDirectory;
        this.labBootstrap = labBootstrap;
        identity = P0Identity.Load(); identity.ExportPublic(reportDirectory);
        Title = "万维计划 · Windows 直连实验室";
        if (labBootstrap is not null) Title += " · USB 预授权 1 MiB 实验";
        Width = 1060; Height = 820; MinWidth = 900; MinHeight = 700;
        Background = Brush("#0C1323"); Foreground = Brush("#F4F7FF");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { Margin = new Thickness(32, 25, 32, 24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = height });
        Content = root;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 24) };
        var tag = Text("WINDOWS  /  P0.3.1", 12, "#84B9FF");
        tag.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(tag, Dock.Right); header.Children.Add(tag);
        header.Children.Add(Text("万维计划", 24, "#FFFFFF"));
        root.Children.Add(header);

        var intro = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        intro.Children.Add(Text("让附近设备建立直连。", 31, "#F4F7FF"));
        intro.Children.Add(Text("目标：附近直连，高速传输，保留原 Wi-Fi。", 15, "#BACBE3", new Thickness(0, 10, 0, 0)));
        var boundary = Text("可选 1 MiB 合成文件互传实验：完整指纹确认、双方认证、收件确认及哈希校验。真机互传待验收。", 13, "#FFCB88", new Thickness(0, 8, 0, 0));
        intro.Children.Add(boundary);
        if (labBootstrap is not null) intro.Children.Add(Text("USB 实验预授权已开启：仅一次连接、最多两次固定 1 MiB 数据。仅应用确认自动处理；PIN 与系统 WLAN 冲突仍需人工处理。连接最多 600 秒，可随时停止。", 13, "#FFCB88", new Thickness(0, 8, 0, 0)));
        Grid.SetRow(intro, 1); root.Children.Add(intro);

        var actionArea = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        var buttons = new WrapPanel();
        foreach (var button in new[] { probe, scan, advertise, pair, stop, export }) buttons.Children.Add(button);
        stop.IsEnabled = false; export.IsEnabled = false;
        if (labBootstrap is not null) probe.IsEnabled = scan.IsEnabled = advertise.IsEnabled = pair.IsEnabled = transferExperiment.IsEnabled = false;
        actionArea.Children.Add(buttons);
        actionArea.Children.Add(transferExperiment);
        actionArea.Children.Add(state); state.Margin = new Thickness(0, 18, 0, 5);
        hint.TextWrapping = TextWrapping.Wrap; actionArea.Children.Add(hint);
        Grid.SetRow(actionArea, 2); root.Children.Add(actionArea);

        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1.35, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(Panel("本机与接口", details, null));
        var right = Panel("发现与运行记录", activity, result);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        Grid.SetRow(body, 3); root.Children.Add(body);

        details.Text = "等待检查。\n\n将读取：\n• Windows 接口是否存在、能否调用\n• 无线网卡驱动能力\n• 原 Wi-Fi 的连接状态\n\n接口存在不等于硬件支持；驱动标志不等于跨端互通。";
        activity.Text = "扫描附近设备，或短时允许本机被发现。\n\n点击“等待连接请求”后，最多等待 60 秒。收到首请求后，两次确认与建立连接另计 45 秒；默认观察 20 秒。勾选互传实验后，连接最多保留 180 秒并打开测试窗口。可随时停止。\n\n系统配对不代替万维应用身份验证；传文件须当面确认完整公钥指纹和收件。";
        var footer = Text("本次报告使用临时匿名标识；不保存 SSID、BSSID 或附近设备名称。", 12, "#8395B0", new Thickness(0, 17, 0, 0));
        Grid.SetRow(footer, 4); root.Children.Add(footer);

        probe.Click += async (_, _) => await RunAsync(null);
        scan.Click += async (_, _) => await RunAsync(15);
        advertise.Click += async (_, _) => await RunAsync(null, 30);
        pair.Click += async (_, _) => await RunAsync(null, pairingSeconds: 60);
        stop.Click += (_, _) => { operation?.Cancel(); state.Text = "正在停止…"; stop.IsEnabled = false; };
        export.Click += (_, _) => Export();
        Closing += (_, e) => {
            if (operation is null) return;
            e.Cancel = true; closeWhenDone = true; operation.Cancel();
            state.Text = "正在停止本次无线操作，随后关闭…";
        };
        Closed += (_, _) => identity.Dispose();
    }

    private async Task RunAsync(int? seconds, int? advertisementSeconds = null, int? pairingSeconds = null, int? cancelAfterMs = null)
    {
        if (operation is not null || restartRequired) return;
        if (labBootstrap is not null)
        {
            if (labRunConsumed || seconds is not null || advertisementSeconds is not null || pairingSeconds is null) return;
            labRunConsumed = true;
        }
        operation = new();
        transferEnabledForRun = pairingSeconds is not null && transferExperiment.IsChecked == true;
        transferExperiment.IsEnabled = false;
        probe.IsEnabled = scan.IsEnabled = advertise.IsEnabled = pair.IsEnabled = export.IsEnabled = false;
        if (cancelAfterMs is not null) operation.CancelAfter(cancelAfterMs.Value);
        stop.IsEnabled = true;
        latest = null;
        result.Text = "运行中"; activity.Clear(); state.Text = "正在检查…";
        var updates = new Progress<string>(line => {
            activity.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n\n"); activity.ScrollToEnd();
        });
        try
        {
            latest = await Task.Run(() => ProbeRunner.RunAsync(seconds, operation.Token,
                ((IProgress<string>)updates).Report, advertisementSeconds, pairingSeconds, this));
            ShowReport(latest);
            export.IsEnabled = true;
            // Preserve each completed experiment before another run replaces the in-memory result.
            // An I/O failure must not overwrite the radio outcome or disable a manual retry.
            try { activity.AppendText("\n本次报告已自动保存：" + ReportStore.Save(latest, reportDirectory) + "\n"); }
            catch (Exception saveError) { activity.AppendText("\n报告自动保存失败：" + DirectDiscovery.ErrorCode(saveError) + "；可点击导出重试。\n"); }
        }
        catch (Exception ex)
        {
            state.Text = "本次检查未完成";
            hint.Text = "错误：" + DirectDiscovery.ErrorCode(ex) + "。可重试；失败不会记为验证通过。";
        }
        finally
        {
            operation.Dispose(); operation = null;
            transferEnabledForRun = false;
            if (DirectAdvertisement.RestartRequired)
            {
                restartRequired = true;
                state.Text = "本次无线资源释放尚未确认";
                hint.Text = "请导出报告，关闭程序后重新打开。当前禁止启动第二次无线操作。";
            }
            probe.IsEnabled = scan.IsEnabled = advertise.IsEnabled = pair.IsEnabled = !restartRequired && labBootstrap is null; stop.IsEnabled = false;
            transferExperiment.IsEnabled = !restartRequired && labBootstrap is null;
            if (closeWhenDone) Close();
        }
    }
    private void ShowReport(LabReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"版本：{report.AppVersion}  |  {report.Os}");
        text.AppendLine($"运行方式：{(report.PackageIdentity == "unpackaged" ? "普通桌面应用（未打包）" : report.PackageIdentity)}\n");
        foreach (var check in report.ApiChecks)
        {
            string shortName = check.Name.Replace("Windows.Devices.WiFiDirect.", "");
            text.AppendLine(shortName + "\n  " + check.Status);
        }
        text.AppendLine("\n原 Wi-Fi 采样：" + report.SampledApContinuity);
        foreach (var adapter in report.NetworkSamples[0].Adapters)
        {
            text.AppendLine($"{adapter.Description}\n  {adapter.State}");
            if (adapter.QueryError is not null) text.AppendLine($"  AP 查询错误：{adapter.QueryError}（5 常见于定位权限限制）");
        }
        text.AppendLine("\n驱动原始能力（不代表实际互通）：\n" + report.DriverEvidence);
        details.Text = text.ToString(); details.ScrollToHome();
        var discovery = report.Discovery;
        var advertisement = report.Advertisement;
        var pairing = report.PairingSession;
        state.Text = discovery is null ? "本机检查完成" : discovery.Outcome switch {
            "completed" => "扫描结束", "cancelled" => "已停止", _ => "扫描未完成"
        };
        if (report.OperationOutcome == "cancelled") state.Text = "已停止";
        if (discovery is null) result.Text = "还未运行附近扫描";
        else
        {
            result.Text = $"{discovery.Peers.Count(p => p.Present)} 个候选条目 · 尚未验证身份";
            activity.AppendText($"\n扫描状态：{discovery.Outcome}\n停止确认：{discovery.StopConfirmed}\n");
            foreach (var peer in discovery.Peers)
                activity.AppendText($"\n候选 {peer.Token[..8]}  |  {(peer.Present ? "当前枚举中" : "已移除")}\n系统已配对标志：{peer.IsPaired}\n");
            if (discovery.Error is not null) activity.AppendText("错误：" + discovery.Error);
        }
        if (advertisement is not null)
        {
            state.Text = advertisement.Outcome switch {
                "completed" => "本次广播结束", "cancelled" => "广播已停止", _ => "本次广播未完成"
            };
            result.Text = advertisement.StartedObserved ? "Windows 报告已启动 · 对端可见性待验证" : "Windows 尚未确认广播启动";
            activity.AppendText($"\n广播状态：{advertisement.Outcome}\n最终系统状态：{advertisement.FinalPublisherStatus}\n停止确认：{advertisement.StopConfirmed}\n");
            activity.AppendText($"连接请求：{advertisement.ConnectionRequestsReceived}；已释放：{advertisement.ConnectionRequestsReleased}\n");
            if (advertisement.Error is not null) activity.AppendText("错误：" + advertisement.Error + "\n");
            activity.AppendText("未进行配对或连接；未打开文件接收端口。\n");
        }
        if (pairing is not null)
        {
            state.Text = pairing.Outcome switch {
                "waiting_expired" => "本次等待结束 · 没有连接",
                "connected_observed" => "本次连接观测结束",
                "cancelled" => "本次配对模式已停止",
                "rejected" => "本次连接请求已拒绝",
                "disconnected_observed" => "本次连接已断开",
                _ => "本次配对或连接尚未完成"
            };
            result.Text = pairing.ConnectedObserved ? "系统曾报告 Direct 已连接 · 互传实验结果见独立报告" : "尚未观察到实际 Direct 连接";
            activity.AppendText($"\n配对模式：{pairing.Outcome}\n系统配对结果：{pairing.SystemPairingStatus}\n连接最终样本：{pairing.FinalConnectionStatus}\n");
            if (pairing.OsAssociationCheck is { } association)
                activity.AppendText($"系统关联查询：{association.QueryStatus}；本次连接处理：{pairing.EffectivePairingDecisionSource}。系统关联不代替应用身份认证。\n");
            activity.AppendText($"请求：{pairing.RequestsReceived}；已释放：{pairing.RequestsReleased}；额外拒绝：{pairing.ExtraRequestsRejected}\n");
            if (pairing.Timing is { } timing)
                activity.AppendText($"计时结束原因：{timing.EndReason}；等待、协商、观察分别计时。\n");
            activity.AppendText($"广播停止确认：{pairing.PublisherStopConfirmed}；本应用资源释放确认：{pairing.ResourceReleaseConfirmed}\n");
            foreach (var endpoint in pairing.EndpointPairs)
                activity.AppendText($"实际 Direct 端点：{endpoint.LocalAddress} → {endpoint.RemoteAddress}\n");
            if (pairing.Error is not null) activity.AppendText("错误：" + pairing.Error + "\n");
            activity.AppendText("传输结果另见 latest-transfer.json；本应用释放对象不代表系统已删除配对或所有连接已断开。\n");
        }
        hint.Text = report.SampledApContinuity == "same_at_observed_samples"
            ? "采样点的原接入点一致；尚未证明采样间无中断，也未测试传输时保网。"
            : "原接入点连续性尚未确认，查看报告中的采样与权限状态。";
        if (discovery is { StopConfirmed: false } || advertisement is { StopConfirmed: false } ||
            pairing is { PublisherStopConfirmed: false } || pairing is { ResourceReleaseConfirmed: false })
        {
            restartRequired = true;
            state.Text = "无线操作停止尚未确认";
            hint.Text = "请导出报告，关闭程序后重新打开。当前禁止启动第二次无线操作。";
        }
    }
    private void Export()
    {
        if (latest is null) return;
        try { hint.Text = "已保存：" + ReportStore.Save(latest, reportDirectory); }
        catch (Exception ex) { hint.Text = "报告保存失败：" + DirectDiscovery.ErrorCode(ex); }
    }
    public async Task<PairingDecision> ConfirmAsync(PairingPrompt prompt, CancellationToken cancellation)
    {
        // Smoke runs use the actual STA dispatcher but always decline requests.
        if (rejectAllRequests) return new(false);
        cancellation.ThrowIfCancellationRequested();
        if (labBootstrap is not null)
        {
            bool accepted = prompt.Kind is "ConnectionRequest" or "ConfirmOnly";
            await Dispatcher.InvokeAsync(() => {
                activity.AppendText($"USB 固定文件实验预授权：{prompt.Kind} · {(accepted ? "已接受应用提示" : "拒绝 PIN 或未支持提示")}\n");
            });
            return new(accepted);
        }
        var decision = new TaskCompletionSource<PairingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        Border? surface = null;
        await Dispatcher.InvokeAsync(() => {
            cancellation.ThrowIfCancellationRequested();
            if (pairingPrompt is not null) { decision.TrySetResult(new(false)); return; }
            var content = new StackPanel { Margin = new Thickness(25), MaxWidth = 470 };
            content.Children.Add(Text(prompt.Kind == "ConnectionRequest" ? "附近设备请求直连" : "确认本次系统配对", 23, "#FFFFFF"));
            content.Children.Add(Text("候选 " + prompt.PeerToken[..Math.Min(8, prompt.PeerToken.Length)] +
                " · 万维应用身份尚未认证", 13, "#FFCB88", new Thickness(0, 14, 0, 10)));
            string instruction = prompt.Kind switch {
                "ConnectionRequest" => "仅在你正用另一台设备发起连接时接受。本次连接计时结束即释放本应用的连接对象；系统可能保留配对记录。",
                "ConfirmOnly" => "系统要求确认配对。请同时检查手机上的连接提示。",
                "DisplayPin" => "请在对端输入或确认下方 PIN。PIN 仅显示在本次页面，不保存到报告。",
                "ConfirmPinMatch" => "请核对两端显示的 PIN 完全一致，再接受。",
                "ProvidePin" => "输入对端显示的 4 位或 8 位数字 PIN。PIN 不保存到报告。",
                _ => "此配对方式尚未支持，请拒绝。"
            };
            content.Children.Add(Text(instruction, 15, "#C8D8F1", new Thickness(0, 0, 0, 15)));
            if (prompt.RemainingStageMs is { } remaining)
                content.Children.Add(Text($"显示本提示时剩余约 {Math.Ceiling(remaining / 1000.0)} 秒；两次确认与建连共用同一时限。", 13, "#FFCB88", new Thickness(0, 0, 0, 15)));
            if (prompt.Kind is "DisplayPin" or "ConfirmPinMatch")
                content.Children.Add(Text(prompt.Pin ?? "PIN 未提供", 28, "#FFFFFF", new Thickness(0, 0, 0, 15)));
            var input = new TextBox { FontSize = 23, MaxLength = 8, Margin = new Thickness(0, 0, 0, 15) };
            if (prompt.Kind == "ProvidePin") content.Children.Add(input);
            var controls = new WrapPanel();
            var reject = MakeButton("拒绝本次请求", false); reject.IsCancel = true; reject.IsDefault = true;
            var accept = MakeButton("接受本次请求", true);
            reject.Click += (_, _) => decision.TrySetResult(new(false));
            accept.Click += (_, _) => {
                if (prompt.Kind == "ProvidePin" && !DirectPairing.ValidPin(input.Text)) { input.Focus(); return; }
                decision.TrySetResult(new(true, prompt.Kind == "ProvidePin" ? input.Text : null));
            };
            controls.Children.Add(reject); controls.Children.Add(accept); content.Children.Add(controls);
            surface = new Border { Background = Brush("#18273F"), BorderBrush = Brush("#84B9FF"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Child = content,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pairingPrompt = surface;
            Grid.SetRowSpan(surface, 5); System.Windows.Controls.Panel.SetZIndex(surface, 10);
            ((Grid)Content).Children.Add(surface); reject.Focus();
        }, DispatcherPriority.Normal, cancellation);
        try { return await decision.Task.WaitAsync(cancellation).ConfigureAwait(false); }
        finally
        {
            await Dispatcher.InvokeAsync(() => {
                if (surface is not null) ((Grid)Content).Children.Remove(surface);
                if (ReferenceEquals(pairingPrompt, surface)) pairingPrompt = null;
            });
        }
    }
    public async Task<Windows.Foundation.IAsyncOperation<WiFiDirectDevice>> BeginConnectionAsync(string deviceId, CancellationToken cancellation)
    {
        return await BeginOnUiAsync(() => WiFiDirectDevice.FromIdAsync(deviceId), cancellation);
    }
    public int ObservationSeconds => labBootstrap is not null ? 600 : transferEnabledForRun ? 180 : 20;
    public bool FixtureLabPreauthorized => labBootstrap is not null;
    public bool NativePeerAllowed(string deviceId, string? deviceAddress) => labBootstrap?.AllowsNativePeer(deviceId, deviceAddress) ?? true;
    internal async Task StartUsbFixtureLabAsync(int seconds)
    {
        if (labBootstrap is null || labRunStarted) return;
        labRunStarted = true;
        await RunAsync(null, pairingSeconds: seconds);
    }
    public async Task<bool> OnDirectConnectedAsync(IReadOnlyList<DirectEndpointEvidence> endpoints, Func<bool> pathVerified, CancellationToken cancellation)
    {
        if (labBootstrap is not null)
        {
            var path = P0Path.Create(endpoints, pathVerified);
            var controller = new P0UsbLabController(labBootstrap, identity, reportDirectory, line => Dispatcher.BeginInvoke(() => {
                activity.AppendText(line + "\n"); activity.ScrollToEnd();
            }));
            return await controller.RunAsync(path, cancellation).ConfigureAwait(false);
        }
        P0TransferWindow? window = null;
        await Dispatcher.InvokeAsync(() => {
            cancellation.ThrowIfCancellationRequested();
            var path = P0Path.Create(endpoints, pathVerified);
            window = new(identity, path, reportDirectory, cancellation) { Owner = this };
            window.Show();
        }, DispatcherPriority.Normal, cancellation);
        await window!.ClosedTask.ConfigureAwait(false);
        return window.ResourceReleaseConfirmed;
    }
    private async Task<T> BeginOnUiAsync<T>(Func<T> begin, CancellationToken cancellation)
    {
        // Never add WaitAsync(cancellation) around this handoff: once the native
        // call begins, its operation must reach the session's late-result owner.
        return await Dispatcher.InvokeAsync(() => {
            cancellation.ThrowIfCancellationRequested();
            return begin();
        }, DispatcherPriority.Normal, cancellation);
    }
    private static Border Panel(string title, TextBox content, TextBlock? summary)
    {
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Text(title, 16, "#F4F7FF", new Thickness(0, 0, 0, 14)));
        if (summary is not null) { summary.Margin = new Thickness(0, 0, 0, 12); summary.TextWrapping = TextWrapping.Wrap; Grid.SetRow(summary, 1); grid.Children.Add(summary); }
        content.IsReadOnly = true; content.TextWrapping = TextWrapping.Wrap; content.AcceptsReturn = true;
        content.Background = Brushes.Transparent; content.Foreground = Brush("#AFC1DB");
        content.BorderThickness = new Thickness(0); content.FontSize = 13; content.Padding = new Thickness(0, 0, 8, 0);
        content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(content, 2); grid.Children.Add(content);
        return new Border { Background = Brush("#131E32"), CornerRadius = new CornerRadius(14), Child = grid };
    }
    private static Button MakeButton(string text, bool primary) => new() {
        Content = text, Padding = new Thickness(16, 11, 16, 11), Margin = new Thickness(0, 0, 10, 8),
        FontSize = 14, Background = Brush(primary ? "#80B7FF" : "#26354E"),
        Foreground = Brush(primary ? "#0C1323" : "#F4F7FF"), BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand,
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border Name="surface" CornerRadius="7" Background="{TemplateBinding Background}"
                      Padding="{TemplateBinding Padding}" BorderBrush="Transparent" BorderThickness="1">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="surface" Property="Opacity" Value="0.85"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="surface" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter TargetName="surface" Property="Opacity" Value="0.4"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="surface" Property="BorderBrush" Value="#B9D8FF"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """)
    };
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Text(string value, double size, string color, Thickness? margin = null) => new() {
        Text = value, FontSize = size, Foreground = Brush(color), Margin = margin ?? new Thickness(0), TextWrapping = TextWrapping.Wrap
    };
    internal void RenderPreview(string path)
    {
        // Render this app's own initial view without opening or capturing other windows.
        var view = (FrameworkElement)Content;
        view.Measure(new Size(996, 724)); view.Arrange(new Rect(0, 0, 996, 724)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1060, 780, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) {
            context.DrawRectangle(Background, null, new Rect(0, 0, 1060, 780));
            context.DrawRectangle(new VisualBrush(view), null, new Rect(32, 25, 996, 724));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    internal async Task<string> SmokeAsync(string previewPath, int? advertisementSeconds = null,
        int? pairingSeconds = null, int? cancelAfterMs = null)
    {
        rejectAllRequests = pairingSeconds is not null;
        try { await RunAsync(null, advertisementSeconds, pairingSeconds, cancelAfterMs); }
        finally { rejectAllRequests = false; }
        if (latest is null || !probe.IsEnabled || !advertise.IsEnabled || !pair.IsEnabled || stop.IsEnabled || !export.IsEnabled)
            throw new InvalidOperationException("UI did not return to idle after probe.");
        if (advertisementSeconds is not null && latest.Advertisement is not { Outcome: "completed", StopConfirmed: true })
            throw new InvalidOperationException("UI advertisement did not complete with confirmed stop.");
        if (pairingSeconds is not null && latest.PairingSession is not { PublisherStopConfirmed: true, ResourceReleaseConfirmed: true })
            throw new InvalidOperationException("UI pairing did not end with confirmed resource release.");
        var path = ReportStore.Save(latest, reportDirectory);
        if (!File.Exists(path)) throw new IOException("UI report was not saved.");
        RenderPreview(previewPath);
        return path;
    }
    internal async Task PromptSmokeAsync(string previewPath)
    {
        // Synthetic prompt only; no publisher, device, pairing or socket API.
        using var cancel = new CancellationTokenSource();
        var pending = ConfirmAsync(new("synthetic-ui-preview", "ConfirmPinMatch", "12345678"), cancel.Token);
        await Task.Delay(100);
        if (pairingPrompt is null) throw new InvalidOperationException("Synthetic pairing prompt was not rendered.");
        RenderPreview(previewPath);
        cancel.Cancel();
        try { _ = await pending; throw new InvalidOperationException("Prompt cancellation was ignored."); }
        catch (OperationCanceledException) { }
        if (pairingPrompt is not null) throw new InvalidOperationException("Cancelled prompt remained visible.");
        try { _ = await ConfirmAsync(new("synthetic", "ConnectionRequest", null), cancel.Token); throw new InvalidOperationException("Pre-cancel prompt was shown."); }
        catch (OperationCanceledException) { }
        if (pairingPrompt is not null) throw new InvalidOperationException("Pre-cancel prompt leaked.");
        using var handoffCancel = new CancellationTokenSource();
        var simulatedOperation = new object();
        var handedOff = await BeginOnUiAsync(() => { handoffCancel.Cancel(); return simulatedOperation; }, handoffCancel.Token);
        if (!ReferenceEquals(handedOff, simulatedOperation)) throw new InvalidOperationException("An operation begun on the dispatcher was lost during cancellation.");
        bool started = false;
        try { _ = await BeginOnUiAsync(() => { started = true; return new object(); }, handoffCancel.Token); throw new InvalidOperationException("Pre-cancel dispatcher callback executed."); }
        catch (OperationCanceledException) { }
        if (started) throw new InvalidOperationException("Pre-cancel dispatcher callback executed.");
        Console.WriteLine("Synthetic pairing UI rendered; cancellation removed it; pre-cancel did not display a prompt. Dispatcher handoff retained a begun operation during cancellation and skipped pre-cancel. No radio tested.");
    }
}
