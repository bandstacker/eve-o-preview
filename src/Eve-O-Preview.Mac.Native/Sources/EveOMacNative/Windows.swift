import AppKit
import ApplicationServices
import Foundation

// Discovery and control of other applications' windows. Window identity is the CGWindowID;
// geometry is global Quartz points with a top-left origin.

struct WindowEntry: Encodable {
    let id: UInt32
    let pid: Int32
    let title: String
    let owner: String
    let bundleId: String
    let executable: String
    let x: Double, y: Double, width: Double, height: Double
    let layer: Int
    let onScreen: Bool
    let hidden: Bool
    let minimized: Bool
}

private struct AppInfo { let bundleId: String; let executable: String; let name: String }
private var appInfoCache: [pid_t: AppInfo] = [:]
private let appInfoLock = NSLock()

private func appInfo(_ pid: pid_t) -> AppInfo {
    appInfoLock.lock(); defer { appInfoLock.unlock() }
    if let cached = appInfoCache[pid] { return cached }
    let app = NSRunningApplication(processIdentifier: pid)
    let info = AppInfo(bundleId: app?.bundleIdentifier ?? "",
                       executable: app?.executableURL?.lastPathComponent ?? "",
                       name: app?.localizedName ?? "")
    if app != nil { appInfoCache[pid] = info }
    return info
}

@_cdecl("eom_forget_process")
public func eom_forget_process(_ pid: Int32) {
    appInfoLock.lock(); appInfoCache.removeValue(forKey: pid); appInfoLock.unlock()
    AX.forget(pid)
}

/// Lists layer-0 windows of other processes, including hidden and minimized ones.
@_cdecl("eom_list_windows")
public func eom_list_windows(_ includeTitlesFromAccessibility: Int32) -> UnsafeMutablePointer<CChar>? {
    let options: CGWindowListOption = [.optionAll, .excludeDesktopElements]
    guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return allocJson([WindowEntry]()) }
    let own = getpid()
    var result: [WindowEntry] = []
    var hiddenByPid: [pid_t: Bool] = [:]
    for item in list {
        guard let layer = item[kCGWindowLayer as String] as? Int, layer == 0,
              let number = item[kCGWindowNumber as String] as? UInt32,
              let pid = item[kCGWindowOwnerPID as String] as? pid_t, pid != own,
              let boundsDict = item[kCGWindowBounds as String] as? NSDictionary,
              let bounds = CGRect(dictionaryRepresentation: boundsDict as CFDictionary),
              bounds.width >= 64, bounds.height >= 64 else { continue }
        let info = appInfo(pid)
        var title = item[kCGWindowName as String] as? String ?? ""
        if title.isEmpty, includeTitlesFromAccessibility != 0 { title = AX.title(pid: pid, windowId: number) ?? "" }
        let hidden = hiddenByPid[pid] ?? (NSRunningApplication(processIdentifier: pid)?.isHidden ?? false)
        hiddenByPid[pid] = hidden
        let onScreen = item[kCGWindowIsOnscreen as String] as? Bool ?? false
        result.append(WindowEntry(id: number, pid: pid, title: title,
                                  owner: item[kCGWindowOwnerName as String] as? String ?? info.name,
                                  bundleId: info.bundleId, executable: info.executable,
                                  x: bounds.origin.x, y: bounds.origin.y, width: bounds.width, height: bounds.height,
                                  layer: layer, onScreen: onScreen, hidden: hidden,
                                  minimized: !onScreen && !hidden && AX.isMinimized(pid: pid, windowId: number)))
    }
    return allocJson(result)
}

struct RunningApp: Encodable { let pid: Int32; let name: String; let bundleId: String; let executable: String; let regular: Bool }

/// Running GUI applications, for the "Other applications" picker.
@_cdecl("eom_list_applications")
public func eom_list_applications() -> UnsafeMutablePointer<CChar>? {
    let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular && $0.processIdentifier != getpid() }
    return allocJson(apps.map { RunningApp(pid: $0.processIdentifier, name: $0.localizedName ?? "",
                                           bundleId: $0.bundleIdentifier ?? "",
                                           executable: $0.executableURL?.lastPathComponent ?? "",
                                           regular: $0.activationPolicy == .regular) })
}

@_cdecl("eom_window_bounds")
public func eom_window_bounds(_ windowId: UInt32, _ rect: UnsafeMutablePointer<Double>?) -> Int32 {
    guard let list = CGWindowListCopyWindowInfo([.optionIncludingWindow], windowId) as? [[String: Any]],
          let item = list.first, let dict = item[kCGWindowBounds as String] as? NSDictionary,
          let bounds = CGRect(dictionaryRepresentation: dict as CFDictionary) else { return 0 }
    writeRect(rect, bounds)
    return 1
}

@_cdecl("eom_frontmost_pid")
public func eom_frontmost_pid() -> Int32 { NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0 }

/// Window id of the focused window of the frontmost other application (0 when unknown).
@_cdecl("eom_frontmost_window")
public func eom_frontmost_window(_ pidOut: UnsafeMutablePointer<Int32>?) -> UInt32 {
    guard let app = NSWorkspace.shared.frontmostApplication else { pidOut?.pointee = 0; return 0 }
    let pid = app.processIdentifier
    pidOut?.pointee = pid
    if pid == getpid() { return 0 }
    return AX.focusedWindowId(pid: pid) ?? 0
}

@_cdecl("eom_own_key_window")
public func eom_own_key_window() -> UnsafeMutableRawPointer? {
    onMain { NSApp.keyWindow.map { Unmanaged.passUnretained($0).toOpaque() } }
}

// MARK: activation

private typealias SetFrontProcessFn = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, CGWindowID, UInt32) -> CGError
private typealias PostEventRecordFn = @convention(c) (UnsafeMutablePointer<ProcessSerialNumber>, UnsafeMutablePointer<UInt8>) -> CGError
private typealias GetProcessForPidFn = @convention(c) (pid_t, UnsafeMutablePointer<ProcessSerialNumber>) -> OSStatus

private let skyLight: UnsafeMutableRawPointer? = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_LAZY)
private let setFrontProcess: SetFrontProcessFn? = skyLight.flatMap { dlsym($0, "_SLPSSetFrontProcessWithOptions") }.map { unsafeBitCast($0, to: SetFrontProcessFn.self) }
private let postEventRecord: PostEventRecordFn? = skyLight.flatMap { dlsym($0, "SLPSPostEventRecordTo") }.map { unsafeBitCast($0, to: PostEventRecordFn.self) }
private let getProcessForPid: GetProcessForPidFn? = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "GetProcessForPID").map { unsafeBitCast($0, to: GetProcessForPidFn.self) }

/// Makes the window key within its process, so keyboard input goes to the exact client window.
private func makeKeyWindow(_ psn: inout ProcessSerialNumber, _ windowId: CGWindowID) {
    guard let post = postEventRecord else { return }
    var bytes = [UInt8](repeating: 0, count: 0xf8)
    bytes[0x04] = 0xf8
    bytes[0x3a] = 0x10
    withUnsafeBytes(of: windowId) { id in for i in 0..<4 { bytes[0x3c + i] = id[i] } }
    for i in 0..<0x10 { bytes[0x20 + i] = 0xff }
    bytes[0x08] = 0x01
    _ = post(&psn, &bytes)
    bytes[0x08] = 0x02
    _ = post(&psn, &bytes)
}

/// Brings one window of another process to the front and gives it keyboard focus.
/// The window server call runs first (one frame); Accessibility raise/main follow off the caller's thread.
private let axQueue = DispatchQueue(label: "eveo.ax", qos: .userInteractive)

@_cdecl("eom_activate_window")
public func eom_activate_window(_ pid: Int32, _ windowId: UInt32) -> Int32 {
    if NSRunningApplication(processIdentifier: pid)?.isHidden == true {
        NSRunningApplication(processIdentifier: pid)?.unhide()
    }
    var activated = false
    if let setFront = setFrontProcess, let getPsn = getProcessForPid {
        var psn = ProcessSerialNumber()
        if getPsn(pid, &psn) == noErr, setFront(&psn, windowId, 0x200) == .success {
            makeKeyWindow(&psn, windowId)
            activated = true
        }
    }
    if activated {
        axQueue.async { raiseWithAccessibility(pid, windowId) }
        return 1
    }
    // Fallback without the window server call: Accessibility and cooperative activation.
    raiseWithAccessibility(pid, windowId)
    AXUIElementSetAttributeValue(AX.application(pid), kAXFrontmostAttribute as CFString, kCFBooleanTrue)
    return NSRunningApplication(processIdentifier: pid)?.activate(options: []) == true ? 1 : 0
}

private func raiseWithAccessibility(_ pid: pid_t, _ windowId: CGWindowID) {
    guard let window = AX.window(pid: pid, windowId: windowId) else { return }
    if AX.bool(window, kAXMinimizedAttribute) == true {
        AXUIElementSetAttributeValue(window, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
    }
    AXUIElementPerformAction(window, kAXRaiseAction as CFString)
    AXUIElementSetAttributeValue(window, kAXMainAttribute as CFString, kCFBooleanTrue)
}

@_cdecl("eom_hide_application")
public func eom_hide_application(_ pid: Int32) -> Int32 {
    NSRunningApplication(processIdentifier: pid)?.hide() == true ? 1 : 0
}

@_cdecl("eom_unhide_application")
public func eom_unhide_application(_ pid: Int32) -> Int32 {
    NSRunningApplication(processIdentifier: pid)?.unhide() == true ? 1 : 0
}

@_cdecl("eom_is_application_hidden")
public func eom_is_application_hidden(_ pid: Int32) -> Int32 {
    NSRunningApplication(processIdentifier: pid)?.isHidden == true ? 1 : 0
}

@_cdecl("eom_minimize_window")
public func eom_minimize_window(_ pid: Int32, _ windowId: UInt32, _ minimize: Int32) -> Int32 {
    guard let window = AX.window(pid: pid, windowId: windowId) else { return 0 }
    return AXUIElementSetAttributeValue(window, kAXMinimizedAttribute as CFString,
                                        minimize != 0 ? kCFBooleanTrue : kCFBooleanFalse) == .success ? 1 : 0
}

@_cdecl("eom_is_window_minimized")
public func eom_is_window_minimized(_ pid: Int32, _ windowId: UInt32) -> Int32 {
    AX.isMinimized(pid: pid, windowId: windowId) ? 1 : 0
}

/// Moves/resizes another process's window (Quartz points).
@_cdecl("eom_set_window_frame")
public func eom_set_window_frame(_ pid: Int32, _ windowId: UInt32, _ x: Double, _ y: Double, _ width: Double, _ height: Double) -> Int32 {
    guard let window = AX.window(pid: pid, windowId: windowId) else { return 0 }
    var point = CGPoint(x: x, y: y)
    var size = CGSize(width: width, height: height)
    let position = AXValueCreate(.cgPoint, &point)!
    let extent = AXValueCreate(.cgSize, &size)!
    // Size, position, size: a window moving between displays may clamp its first resize.
    AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, extent)
    let moved = AXUIElementSetAttributeValue(window, kAXPositionAttribute as CFString, position)
    AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, extent)
    return moved == .success ? 1 : 0
}

/// "Zoom" the window to the visible frame of its current screen.
@_cdecl("eom_maximize_window")
public func eom_maximize_window(_ pid: Int32, _ windowId: UInt32) -> Int32 {
    var rect = [Double](repeating: 0, count: 4)
    guard eom_window_bounds(windowId, &rect) != 0 else { return 0 }
    let center = CGPoint(x: rect[0] + rect[2] / 2, y: rect[1] + rect[3] / 2)
    let screen = onMain { NSScreen.screens.first { Quartz.fromCocoa($0.frame).contains(center) } ?? NSScreen.main }
    guard let screen else { return 0 }
    let visible = onMain { Quartz.fromCocoa(screen.visibleFrame) }
    return eom_set_window_frame(pid, windowId, visible.origin.x, visible.origin.y, visible.width, visible.height)
}

// MARK: Accessibility helpers

@_silgen_name("_AXUIElementGetWindow")
private func _AXUIElementGetWindow(_ element: AXUIElement, _ windowId: UnsafeMutablePointer<CGWindowID>) -> AXError

enum AX {
    private static var applications: [pid_t: AXUIElement] = [:]
    private static let lock = NSLock()

    static func application(_ pid: pid_t) -> AXUIElement {
        lock.lock(); defer { lock.unlock() }
        if let cached = applications[pid] { return cached }
        let element = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(element, 0.25)
        applications[pid] = element
        return element
    }

    static func forget(_ pid: pid_t) {
        lock.lock()
        if let app = applications.removeValue(forKey: pid) {
            windowCache = windowCache.filter { _, element in
                var owner: pid_t = 0
                return AXUIElementGetPid(element, &owner) == .success && owner != pid && app != element
            }
        }
        lock.unlock()
    }

    static func windowId(_ element: AXUIElement) -> CGWindowID? {
        var id: CGWindowID = 0
        return _AXUIElementGetWindow(element, &id) == .success && id != 0 ? id : nil
    }

    static func windows(_ pid: pid_t) -> [AXUIElement] {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application(pid), kAXWindowsAttribute as CFString, &value) == .success else { return [] }
        return value as? [AXUIElement] ?? []
    }

    private static var windowCache: [CGWindowID: AXUIElement] = [:]

    static func window(pid: pid_t, windowId: CGWindowID) -> AXUIElement? {
        lock.lock()
        let cached = windowCache[windowId]
        lock.unlock()
        // A cached element stays valid while its window exists; verify cheaply by its id.
        if let cached, self.windowId(cached) == windowId { return cached }
        let found = windows(pid).first { self.windowId($0) == windowId }
        lock.lock()
        windowCache[windowId] = found
        lock.unlock()
        return found
    }

    static func focusedWindowId(pid: pid_t) -> CGWindowID? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(application(pid), kAXFocusedWindowAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return windowId(value as! AXUIElement)
    }

    static func bool(_ element: AXUIElement, _ attribute: String) -> Bool? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return (value as? NSNumber)?.boolValue
    }

    static func title(pid: pid_t, windowId: CGWindowID) -> String? {
        guard let window = window(pid: pid, windowId: windowId) else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(window, kAXTitleAttribute as CFString, &value) == .success else { return nil }
        return value as? String
    }

    static func isMinimized(pid: pid_t, windowId: CGWindowID) -> Bool {
        guard let window = window(pid: pid, windowId: windowId) else { return false }
        return bool(window, kAXMinimizedAttribute) ?? false
    }
}

// MARK: permissions

/// 0 = screen recording, 1 = accessibility. Returns 1 when granted.
@_cdecl("eom_permission_granted")
public func eom_permission_granted(_ kind: Int32) -> Int32 {
    switch kind {
    case 0: return CGPreflightScreenCaptureAccess() ? 1 : 0
    case 1: return AXIsProcessTrusted() ? 1 : 0
    default: return 0
    }
}

/// Shows the system prompt (once per app identity) or opens the relevant Settings pane.
@_cdecl("eom_request_permission")
public func eom_request_permission(_ kind: Int32) {
    switch kind {
    case 0:
        if !CGRequestScreenCaptureAccess() {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    case 1:
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
        }
    case 2:
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AudioCapture")!)
    default: break
    }
}

// MARK: displays

struct ScreenEntry: Encodable {
    let id: UInt32
    let name: String
    let x: Double, y: Double, width: Double, height: Double
    let visibleX: Double, visibleY: Double, visibleWidth: Double, visibleHeight: Double
    let scale: Double
    let primary: Bool
}

@_cdecl("eom_list_screens")
public func eom_list_screens() -> UnsafeMutablePointer<CChar>? {
    let screens: [ScreenEntry] = onMain {
        NSScreen.screens.enumerated().map { index, screen in
            let frame = Quartz.fromCocoa(screen.frame), visible = Quartz.fromCocoa(screen.visibleFrame)
            let id = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
            return ScreenEntry(id: id, name: screen.localizedName,
                               x: frame.origin.x, y: frame.origin.y, width: frame.width, height: frame.height,
                               visibleX: visible.origin.x, visibleY: visible.origin.y,
                               visibleWidth: visible.width, visibleHeight: visible.height,
                               scale: screen.backingScaleFactor, primary: index == 0)
        }
    }
    return allocJson(screens)
}

/// Title of one window through Accessibility (used when the window server omits titles).
@_cdecl("eom_window_title")
public func eom_window_title(_ pid: Int32, _ windowId: UInt32) -> UnsafeMutablePointer<CChar>? {
    guard let title = AX.title(pid: pid, windowId: windowId) else { return nil }
    return allocCString(title)
}

/// Owning process of a window (0 when the window no longer exists).
@_cdecl("eom_window_pid")
public func eom_window_pid(_ windowId: UInt32) -> Int32 {
    guard let list = CGWindowListCopyWindowInfo([.optionIncludingWindow], windowId) as? [[String: Any]],
          let pid = list.first?[kCGWindowOwnerPID as String] as? pid_t else { return 0 }
    return pid
}
