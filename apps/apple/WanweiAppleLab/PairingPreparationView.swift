import Foundation
import SwiftUI
import UniformTypeIdentifiers
#if canImport(WiFiAware) && canImport(DeviceDiscoveryUI)
import WiFiAware
import DeviceDiscoveryUI
#endif

struct PairingPreparationReport: Encodable {
    let schema = "wanwei.apple.pairing-preparation.v1"
    let sessionStartedAtUTC = Date()
    let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown"
    var role = "publisher"
    var capabilityProbeStatus = "not_captured"
    var systemPairingControlsConfigured = false
    var systemUIFallbackObserved = false
    var pairedDeviceObservationState = "not_started"
    var pairedDeviceCount: Int?
    var pairedDeviceCountObservedAtUTC: Date?
    var subscriberEndpointReturnCount = 0
    var lastSubscriberEndpointReturnedAtUTC: Date?
    let signingAuthorization = "not_verified_by_app"
    let newPairingSuccess = "not_inferred_from_count_or_endpoint"
    let connectionTest = "not_implemented"
    let originalWiFiContinuity = "not_tested"
    let applicationPayloadSent = false
    let wifiAwareDataSessionStarted = false
    let notes = [
        "只有用户操作系统控件后才可能发现、配对或授予已配对设备访问权限。",
        "配对计数可能包含既有设备；系统返回 endpoint 也不能证明新配对或数据连接成功。",
        "没有创建 NetworkListener、NetworkBrowser 或 NetworkConnection，不发送应用数据。",
        "不保存或导出配对设备名称、ID、endpoint、SSID、BSSID 或 MAC。"
    ]

    func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }
}

/// System pairing preparation only. It intentionally owns no data connection.
@MainActor
struct PairingPreparationView: View {
    @Environment(\.scenePhase) private var scenePhase
    @State private var capability: CapabilityReport?
    @State private var report = PairingPreparationReport()
    @State private var observationGeneration = UUID()
    @State private var role = "publisher"
    @State private var exportDocument = JSONReportDocument(data: Data())
    @State private var exporting = false
    @State private var exportStatus = ""

    var body: some View {
        List {
            Section {
                Text("先准备 iPhone 与 iPad 的系统配对")
                    .font(.headline)
                Text("两台设备均需运行本版，并由你亲自操作系统配对界面。本页只准备配对，不连接或收发文件。")
                Text("当前荣耀和 Windows 的 Wi-Fi Direct 不会因这里配对就变成兼容的 Wi-Fi Aware 对端。")
                    .foregroundStyle(.secondary)
            }
            Section("本机检查") {
                LabeledContent("能力检查", value: capability?.probeStatus ?? "正在检查")
                LabeledContent("系统配对控件", value: report.systemPairingControlsConfigured ? "已配置 · 待你操作" : "当前不可用")
                LabeledContent("签名授权", value: "仍需在 Mac 核验")
            }
            if gateAllowsPairing {
                Section {
                    Picker("角色", selection: $role) {
                        Text("等待另一台设备").tag("publisher")
                        Text("寻找等待中的设备").tag("subscriber")
                    }
                    .pickerStyle(.segmented)
                    .accessibilityIdentifier("pairing_preparation_role")
                    systemPairingControls
                } header: {
                    Text("选择本机角色")
                } footer: {
                    Text("一台选择等待，另一台选择寻找；再分别点系统配对按钮。系统 PIN 和确认由设备持有人操作。")
                }
            } else {
                Section {
                    Text("需要真机、系统报告支持 Wi-Fi Aware，且本应用服务登记完整后，才提供系统配对入口。签名是否被系统接受仍需另验。")
                        .foregroundStyle(.secondary)
                }
            }
            Section("仅记录观察，不判定互传成功") {
                LabeledContent("配对列表观察", value: report.pairedDeviceObservationState)
                LabeledContent("最近可访问的配对数量", value: report.pairedDeviceCount.map { String($0) } ?? "未观察")
                LabeledContent("系统返回订阅端 endpoint 次数", value: String(report.subscriberEndpointReturnCount))
                Text("数量可能来自已配对设备；endpoint 回调可能是既有授权。它们不证明新配对、已建立连接或文件已送达。")
                    .font(.footnote).foregroundStyle(.secondary)
                Text("数据连接、文件传输和原 Wi-Fi 保持均未测试。")
            }
            Section {
                Button("导出配对准备报告") { prepareExport() }
                    .accessibilityIdentifier("export_pairing_preparation_report")
                if !exportStatus.isEmpty { Text(exportStatus).font(.footnote) }
            }
        }
        .navigationTitle("系统配对准备")
        .onChange(of: role) { _, value in report.role = value }
        .task(id: scenePhase) { await observeAccessiblePairedDeviceCount() }
        .fileExporter(isPresented: $exporting, document: exportDocument, contentType: .json,
                      defaultFilename: "wanwei-apple-pairing-preparation") { result in
            switch result {
            case .success: exportStatus = "准备报告已保存到你选择的位置。"
            case .failure: exportStatus = "未能导出准备报告，请重新选择保存位置。"
            }
        }
    }

    private var gateAllowsPairing: Bool {
        #if canImport(WiFiAware) && canImport(DeviceDiscoveryUI)
        guard let capability else { return false }
        return capability.physicalDevice && capability.wifiAwareSupported == true &&
            capability.serviceConfigurationValid && capability.publishableServiceVisible == true &&
            capability.subscribableServiceVisible == true
        #else
        return false
        #endif
    }

    @ViewBuilder
    private var systemPairingControls: some View {
        #if canImport(WiFiAware) && canImport(DeviceDiscoveryUI)
        if role == "publisher", let service = WAPublishableService.allServices[CapabilityReport.serviceName] {
            DevicePairingView(.wifiAware(.connecting(to: service, from: .userSpecifiedDevices))) {
                Label("打开系统等待配对界面", systemImage: "plus")
            } fallback: { pairingFallback }
        } else if role == "subscriber", let service = WASubscribableService.allServices[CapabilityReport.serviceName] {
            DevicePicker(.wifiAware(.connecting(to: .userSpecifiedDevices, from: service))) { _ in
                // Do not retain, log or use the endpoint to create a connection.
                report.subscriberEndpointReturnCount += 1
                report.lastSubscriberEndpointReturnedAtUTC = Date()
            } label: {
                Label("打开系统设备选择器", systemImage: "magnifyingglass")
            } fallback: { pairingFallback }
        }
        #else
        pairingFallback
        #endif
    }

    private var pairingFallback: some View {
        Text("系统未提供此配对入口。请核实真机、签名授权和系统条件；本页不会改走其他网络。")
            .foregroundStyle(.secondary)
            .onAppear { report.systemUIFallbackObserved = true }
    }

    private func observeAccessiblePairedDeviceCount() async {
        let generation = UUID()
        observationGeneration = generation
        guard scenePhase == .active else {
            report.pairedDeviceObservationState = "paused_inactive"
            // Keep the system controls mounted: their own modal/PIN interface can
            // temporarily make the app inactive. Only the list observer pauses.
            return
        }
        capability = CapabilityReport.capture()
        report.capabilityProbeStatus = capability?.probeStatus ?? "not_captured"
        guard gateAllowsPairing else {
            report.systemPairingControlsConfigured = false
            report.pairedDeviceObservationState = "capability_gate_unavailable"
            return
        }
        #if canImport(WiFiAware) && canImport(DeviceDiscoveryUI)
        report.systemPairingControlsConfigured = true
        report.pairedDeviceObservationState = "observing"
        do {
            for try await devices in WAPairedDevice.allDevices {
                try Task.checkCancellation()
                guard observationGeneration == generation else { return }
                report.pairedDeviceCount = devices.count
                report.pairedDeviceCountObservedAtUTC = Date()
                report.pairedDeviceObservationState = "snapshot_received"
            }
            if observationGeneration == generation { report.pairedDeviceObservationState = "observation_ended" }
        } catch {
            if observationGeneration == generation {
                report.pairedDeviceObservationState = Task.isCancelled ? "observation_cancelled" : "framework_error"
            }
        }
        #else
        report.systemPairingControlsConfigured = false
        report.pairedDeviceObservationState = "sdk_framework_unavailable"
        #endif
    }

    private func prepareExport() {
        do {
            exportDocument = JSONReportDocument(data: try report.encoded())
            exportStatus = ""
            exporting = true
        } catch { exportStatus = "准备报告生成失败，请重新打开此页后重试。" }
    }
}
