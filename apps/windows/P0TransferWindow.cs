using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Wanwei;

internal sealed class P0TransferWindow : Window
{
    private readonly P0Identity identity;
    private readonly P0Path path;
    private readonly string reportDirectory;
    private readonly CancellationTokenSource lifetime;
    private readonly TextBox pin = new() { FontSize = 14, MaxLength = 64, Margin = new Thickness(0, 6, 0, 10) };
    private readonly TextBox port = new() { FontSize = 17, MaxLength = 5, Text = "", Margin = new Thickness(0, 6, 0, 10) };
    private readonly TextBlock status = Label("请当面核对两端完整指纹，再确认本次信任。");
    private readonly TextBlock endpoint = Label("");
    private readonly TextBox log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button trust = Button("确认本次信任");
    private readonly Button receive = Button("接收 1 MiB 测试文件");
    private readonly Button send = Button("发送 1 MiB 测试文件");
    private readonly Button cancel = Button("取消本次传输");
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid root = new();
    private string? confirmedPin;
    private CancellationTokenSource? transfer;
    private bool closing;
    private bool cleanupUnconfirmed;
    internal Task ClosedTask => closed.Task;
    internal bool ResourceReleaseConfirmed => !cleanupUnconfirmed;

    internal P0TransferWindow(P0Identity identity, P0Path path, string reportDirectory, CancellationToken cancellation)
    {
        this.identity = identity; this.path = path; this.reportDirectory = reportDirectory;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Title = "万维计划 · 1 MiB 安全互传实验"; Width = 850; Height = 760; MinWidth = 700; MinHeight = 650;
        Background = new SolidColorBrush(Color.FromRgb(12, 19, 35)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        root.Margin = new Thickness(25); Content = root;
        var content = new StackPanel(); var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content }; root.Children.Add(scroll);
        content.Children.Add(Label("本轮只传合成测试数据，双方验证身份后仍需接收方确认。", 20));
        content.Children.Add(Label("本机完整公钥指纹（请在手机上比对）："));
        content.Children.Add(new TextBox { Text = identity.Fingerprint, IsReadOnly = true, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 12) });
        content.Children.Add(Label("对端完整公钥指纹：")); content.Children.Add(pin); content.Children.Add(trust);
        content.Children.Add(Label("发送时填写对端页面显示的接收端口：")); content.Children.Add(port);
        endpoint.Text = $"本次直连：{path.Local} → {path.Remote}\n点击接收后显示本机动态端口；监听最多等待 30 秒。";
        content.Children.Add(endpoint);
        var buttons = new WrapPanel(); foreach (var b in new[] { receive, send, cancel }) buttons.Children.Add(b); content.Children.Add(buttons);
        status.Margin = new Thickness(0, 10, 0, 10); content.Children.Add(status);
        log.Height = 190; content.Children.Add(log);
        content.Children.Add(Label("收件保存到本机应用测试目录；停止、关闭或原直连结束会关闭套接字。不能据 1 MiB 测试宣传峰值速度。", 12));
        trust.Click += (_, _) => {
            string candidate = pin.Text.Trim();
            if (!P0Protocol.IsFingerprint(candidate) || candidate == identity.Fingerprint) { status.Text = "请输入对端完整的 64 位小写指纹，并核对与手机一致。"; return; }
            confirmedPin = candidate; status.Text = "已确认本次信任。每次收件仍需另行同意。"; UpdateButtons();
        };
        pin.TextChanged += (_, _) => { if (confirmedPin != pin.Text.Trim()) confirmedPin = null; UpdateButtons(); };
        receive.Click += async (_, _) => await StartAsync(true);
        send.Click += async (_, _) => await StartAsync(false);
        cancel.Click += (_, _) => transfer?.Cancel();
        Closing += (_, e) => {
            if (transfer is null) return;
            e.Cancel = true; closing = true; transfer.Cancel(); status.Text = "正在关闭传输及清理临时文件…";
        };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); closed.TrySetResult(); };
        _ = lifetime.Token.Register(() => Dispatcher.BeginInvoke(() => { closing = true; transfer?.Cancel(); if (transfer is null) Close(); }));
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        bool idle = transfer is null && !lifetime.IsCancellationRequested && !cleanupUnconfirmed;
        trust.IsEnabled = pin.IsEnabled = idle; port.IsEnabled = idle;
        receive.IsEnabled = send.IsEnabled = idle && confirmedPin is not null && path.Verify();
        cancel.IsEnabled = transfer is not null;
    }
    private async Task StartAsync(bool receiver)
    {
        if (transfer is not null || confirmedPin is null) return;
        int remotePort = 0;
        if (!receiver && (!int.TryParse(port.Text, out remotePort) || remotePort is < 1 or > 65535)) { status.Text = "先填写手机显示的接收端口。"; return; }
        transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateButtons();
        try
        {
            var session = new P0Transfer(identity, reportDirectory, value => Dispatcher.BeginInvoke(() => {
                if (value.CleanupFinished && (!value.SocketClosed || !value.TemporaryFileCleanupConfirmed)) cleanupUnconfirmed = true;
                status.Text = value.State switch {
                    "listening" => $"本机接收端口：{value.SocketLocalPort} · 请让手机发送", "tls_authenticating" => "正在验证双方身份…",
                    "awaiting_receive_confirmation" => "等待你确认接收", "receiving" => "正在接收…", "sending" => "正在发送…",
                    "complete" => value.LocalSaved ? "已接收并保存，内容校验通过。" : "对端已确认保存，内容校验通过。",
                    "rejected" => "本次接收已拒绝", "cancelled" => "本次传输已取消", "failed" => "传输失败：" + value.ErrorCode,
                    _ => value.State
                };
                log.Text = JsonSerializer.Serialize(value, ReportStore.Json);
                UpdateButtons();
            }));
            await session.RunAsync(receiver, path, confirmedPin, remotePort, ConfirmOfferAsync, transfer.Token);
        }
        catch (Exception e) { status.Text = "实验未完成：" + DirectDiscovery.ErrorCode(e); }
        finally
        {
            transfer.Dispose(); transfer = null; UpdateButtons();
            if (closing || lifetime.IsCancellationRequested) Close();
        }
    }
    private async Task<bool> ConfirmOfferAsync(string authenticatedPeer, CancellationToken cancellation)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); Border? surface = null;
        await Dispatcher.InvokeAsync(() => {
            cancellation.ThrowIfCancellationRequested();
            var panel = new StackPanel { Margin = new Thickness(25), MaxWidth = 620 };
            panel.Children.Add(Label("是否接收本次 1 MiB 测试文件？", 24));
            panel.Children.Add(Label("已验证对端完整指纹：\n" + authenticatedPeer));
            panel.Children.Add(Label("仅接收固定的合成数据，校验后保存到万维应用测试目录。"));
            var buttons = new WrapPanel(); var decline = Button("拒绝接收"); var accept = Button("同意接收本次文件");
            decline.IsDefault = true; decline.Click += (_, _) => decision.TrySetResult(false); accept.Click += (_, _) => decision.TrySetResult(true);
            buttons.Children.Add(decline); buttons.Children.Add(accept); panel.Children.Add(buttons);
            surface = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 44, 65)), BorderBrush = Brushes.LightBlue, BorderThickness = new Thickness(1), Child = panel,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Panel.SetZIndex(surface, 10); root.Children.Add(surface); decline.Focus();
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellation);
        try { return await decision.Task.WaitAsync(cancellation).ConfigureAwait(false); }
        finally { await Dispatcher.InvokeAsync(() => { if (surface is not null) root.Children.Remove(surface); }); }
    }
    private static TextBlock Label(string text, int size = 14) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private static Button Button(string text) => new() { Content = text, FontSize = 14, Margin = new Thickness(0, 5, 10, 5), Padding = new Thickness(14, 9, 14, 9) };
}
