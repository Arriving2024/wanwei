import Foundation
import UIKit
#if canImport(WiFiAware)
import WiFiAware
#endif

struct CapabilityReport: Codable {
    static let serviceName = "_wanwei-lab._tcp"

    let schema: String
    let generatedAtUTC: Date
    let appVersion: String
    let platform: String
    let osVersion: String
    let deviceClass: String
    let physicalDevice: Bool
    let probeStatus: String
    let wifiAwareSupported: Bool?
    let serviceConfigurationValid: Bool
    let publishableServiceVisible: Bool?
    let subscribableServiceVisible: Bool?
    let signingAuthorization: String
    let pairingAuthorization: String
    let connectionTest: String
    let originalWiFiContinuity: String
    let applicationPayloadSent: Bool
    let notes: [String]

    @MainActor
    static func capture() -> CapabilityReport {
        let bundle = Bundle.main
        let configured = validServiceConfiguration(bundle)
        var supported: Bool?
        var publishable: Bool?
        var subscribable: Bool?
        var status: String
        var notes = [
            "本报告仅记录本机能力与应用配置。",
            "配对准备页另有独立报告；本机能力报告不记录整个应用的配对历史。",
            "源码 entitlement 不等于签名中已获授权；需在 Mac 核验已签名应用。",
            "未启动广播、扫描、配对、连接或文件传输。",
            "未读取 SSID、BSSID、MAC、设备序列号、个人设备名或已配对设备。"
        ]

        #if targetEnvironment(simulator)
        let physical = false
        status = "simulator_not_device_evidence"
        notes.append("模拟器仅供界面预览，不能作为 Wi-Fi Aware 真机能力证据。")
        #else
        let physical = true
        if !configured {
            status = "configuration_invalid"
            notes.append("服务配置不符合本探测器的约定，已跳过 Wi-Fi Aware API 查询。")
        } else {
            #if canImport(WiFiAware)
            if #available(iOS 26.0, *) {
                let hasAware = WACapabilities.supportedFeatures.contains(.wifiAware)
                supported = hasAware
                status = hasAware ? "supported_at_capture" : "not_supported_at_capture"
                if hasAware {
                    // Reading these registries does not start a publisher or subscriber.
                    publishable = WAPublishableService.allServices[serviceName] != nil
                    subscribable = WASubscribableService.allServices[serviceName] != nil
                }
            } else {
                status = "os_api_unavailable"
            }
            #else
            status = "sdk_framework_unavailable"
            #endif
        }
        #endif

        return CapabilityReport(
            schema: "wanwei.apple.capability.v1",
            generatedAtUTC: Date(),
            appVersion: bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown",
            platform: UIDevice.current.systemName,
            osVersion: UIDevice.current.systemVersion,
            deviceClass: UIDevice.current.model,
            physicalDevice: physical,
            probeStatus: status,
            wifiAwareSupported: supported,
            serviceConfigurationValid: configured,
            publishableServiceVisible: publishable,
            subscribableServiceVisible: subscribable,
            signingAuthorization: "not_verified_by_app",
            pairingAuthorization: "not_requested_by_capability_probe",
            connectionTest: "not_tested",
            originalWiFiContinuity: "not_tested",
            applicationPayloadSent: false,
            notes: notes
        )
    }

    // Validate our exact one-service configuration before asking the framework to
    // parse it. This intentionally rejects accidental extra or malformed services.
    private static func validServiceConfiguration(_ bundle: Bundle) -> Bool {
        guard let services = bundle.object(forInfoDictionaryKey: "WiFiAwareServices") as? [String: Any],
              services.count == 1,
              let service = services[serviceName] as? [String: Any],
              service.count == 2,
              let publish = service["Publishable"] as? [String: Any], publish.isEmpty,
              let subscribe = service["Subscribable"] as? [String: Any], subscribe.isEmpty else {
            return false
        }
        return true
    }

    func encoded() throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(self)
    }
}
