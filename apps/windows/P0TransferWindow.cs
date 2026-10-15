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
    private volatile bool cleanupUnconfirmed;
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
        content.Children.Add(Label("首次仍需核对完整指纹；发送前，请先让对方点击“接收”。端口会通过本次直连自动交换。"));
        content.Children.Add(Label("当前手机端自动交换仅支持手机作为 Direct 客户端、电脑建组的实验角色。", 12));
        endpoint.Text = $"本次直连：{path.Local} → {path.Remote}\n接收等待最多 30 秒，自动取得端口最多 10 秒；取得端口后仍须验证双方身份。";
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
        trust.IsEnabled = pin.IsEnabled = idle;
        receive.IsEnabled = send.IsEnabled = idle && confirmedPin is not null && path.Verify();
        cancel.IsEnabled = transfer is not null;
    }
    private async Task StartAsync(bool receiver)
    {
        if (transfer is not null || confirmedPin is null) return;
        string trustedPeer = confirmedPin;
        transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateButtons();
        try
        {
            int remotePort = 0;
            if (!receiver)
            {
                status.Text = "正在自动取得对方接收端口…请先让对方点击“接收”。";
                remotePort = await P0Rendezvous.ResolveAsync(path, identity.Fingerprint, trustedPeer, transfer.Token);
                // This is only an unauthenticated hint. P0Transfer still verifies
                // the native socket endpoints and the complete pinned certificate.
            }
            var session = new P0Transfer(identity, reportDirectory, value => {
                // Gate retries before RunAsync can return, even if the UI queue
                // has not yet rendered the final cleanup status.
                if (value.CleanupFinished && (!value.SocketClosed || !value.TemporaryFileCleanupConfirmed)) cleanupUnconfirmed = true;
                Dispatcher.BeginInvoke(() => {
                status.Text = value.State switch {
                    "listening" => $"已等待接收，端口自动交换中 · 请让手机点击发送（本机端口 {value.SocketLocalPort}）", "tls_authenticating" => "正在验证双方身份…",
                    "awaiting_receive_confirmation" => "等待你确认接收", "receiving" => "正在接收…", "sending" => "正在发送…",
                    "complete" => value.LocalSaved ? "已接收并保存，内容校验通过。" : "对端已确认保存，内容校验通过。",
                    "rejected" => "本次接收已拒绝", "cancelled" => "本次传输已取消",
                    "failed" when value.ErrorCode == "RENDEZVOUS_UNAVAILABLE" => "无法开放本次接收端口交换，本轮接收已停止。",
                    "failed" => "传输失败：" + value.ErrorCode,
                    _ => value.State
                };
                log.Text = JsonSerializer.Serialize(value, ReportStore.Json);
                UpdateButtons();
                });
            });
            await session.RunAsync(receiver, path, trustedPeer, remotePort, ConfirmOfferAsync, transfer.Token,
                listenerReady: receiver ? (readyPort, token) => P0RendezvousResponder.Start(path, identity.Fingerprint, trustedPeer, readyPort, token) : null,
                portExchange: "native_udp_rendezvous_untrusted_hint");
        }
        catch (OperationCanceledException) when (transfer.IsCancellationRequested) { status.Text = "本次传输已取消。"; }
        catch (P0Exception e)
        {
            if (e.Code == "RENDEZVOUS_CLEANUP_UNCONFIRMED") cleanupUnconfirmed = true;
            status.Text = e.Code == "RENDEZVOUS_TIMEOUT" ? "未取得对方接收端口。请确认对方已核对指纹并点击接收，且当前直连仍有效。" : "实验未完成：" + e.Code;
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
