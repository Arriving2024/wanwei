import Foundation
import SwiftUI
import UniformTypeIdentifiers

struct JSONReportDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.json] }
    var data: Data

    init(data: Data) { self.data = data }

    init(configuration: ReadConfiguration) throws {
        guard let contents = configuration.file.regularFileContents else {
            throw CocoaError(.fileReadCorruptFile)
        }
        data = contents
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}

@MainActor
struct ContentView: View {
    @State private var report: CapabilityReport?
    @State private var exportDocument = JSONReportDocument(data: Data())
    @State private var exporting = false
    @State private var exportStatus = ""

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text("先确认这台设备开放了哪些能力")
                        .font(.headline)
                    Text("本版只做本机检查。直连、文件传输和原有 Wi-Fi 保持情况，需要后续分别实测。")
                        .foregroundStyle(.secondary)
                }

                if let report {
                    Section("设备与能力") {
                        row("系统", "\(report.platform) \(report.osVersion)")
                        row("运行环境", report.physicalDevice ? "真机" : "模拟器 · 不作能力证据")
                        row("Wi-Fi Aware", capabilityText(report))
                        row("探测时间", report.generatedAtUTC.formatted(date: .abbreviated, time: .standard))
                    }
                    Section("应用配置") {
                        row("服务声明", report.serviceConfigurationValid ? "有效" : "需要修正")
                        row("发布服务登记", optionalText(report.publishableServiceVisible))
                        row("订阅服务登记", optionalText(report.subscribableServiceVisible))
                        row("签名授权", "待 Mac 核验")
                        row("配对授权", "本版未请求")
                    }
                    Section("验证进度") {
                        row("实际直连", "尚未测试")
                        row("原有 Wi-Fi 保持", "尚未测试")
                        row("文件传输", "尚未实现")
                    }
                    Section {
                        NavigationLink("系统配对准备 · 不传文件") { PairingPreparationView() }
                            .accessibilityIdentifier("open_pairing_preparation")
                    } footer: {
                        Text("该页只提供用户主动操作的系统配对入口。能力、签名、配对、数据连接和文件保存分别验收。")
                    }
                    Section {
                        Button("重新检查本机") { refresh() }
                            .accessibilityIdentifier("refresh_capability")
                        Button("导出 JSON 报告") { prepareExport(report) }
                            .accessibilityIdentifier("export_report")
                        if !exportStatus.isEmpty {
                            Text(exportStatus).font(.footnote).foregroundStyle(.secondary)
                        }
                    } footer: {
                        Text("报告只含系统版本、能力布尔值和验证状态，不含设备序列号、Wi-Fi 名称或地址。导出时由你选择保存位置。")
                    }
                } else {
                    ProgressView("读取本机能力…")
                }
            }
            .navigationTitle("万维实验室")
            .task { if report == nil { refresh() } }
            .fileExporter(
                isPresented: $exporting,
                document: exportDocument,
                contentType: .json,
                defaultFilename: "wanwei-apple-capability"
            ) { result in
                switch result {
                case .success:
                    exportStatus = "报告已保存到你选择的位置。"
                case .failure(let error):
                    exportStatus = "未能导出：\(error.localizedDescription)"
                }
            }
        }
    }

    private func row(_ title: String, _ value: String) -> some View {
        LabeledContent(title) {
            Text(value).multilineTextAlignment(.trailing)
        }
    }

    private func refresh() {
        report = CapabilityReport.capture()
        exportStatus = ""
    }

    private func prepareExport(_ captured: CapabilityReport) {
        do {
            exportDocument = JSONReportDocument(data: try captured.encoded())
            exportStatus = ""
            exporting = true
        } catch {
            exportStatus = "报告生成失败：\(error.localizedDescription)"
        }
    }

    private func optionalText(_ value: Bool?) -> String {
        guard let value else { return "未查询" }
        return value ? "已登记" : "未找到"
    }

    private func capabilityText(_ report: CapabilityReport) -> String {
        if let supported = report.wifiAwareSupported {
            return supported ? "系统报告支持 · 直连待测" : "本次系统未报告支持"
        }
        switch report.probeStatus {
        case "simulator_not_device_evidence": return "需要在真机检查"
        case "configuration_invalid": return "请先修正服务配置"
        case "os_api_unavailable": return "需要 iOS / iPadOS 26 或更高版本"
        case "sdk_framework_unavailable": return "构建 SDK 缺少 WiFiAware 框架"
        default: return "未检查"
        }
    }
}
