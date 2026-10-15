import AppKit
import CoreGraphics
import Foundation

// Global keyboard and pointer input. The keyboard tap is an active filter so matching shortcuts
// can be suppressed (like WH_KEYBOARD_LL); the pointer tap only listens. Both run on a dedicated
// thread with their own run loop; callbacks must return quickly and never block on the UI thread.

/// kind: 0 key down, 1 key up, 2 modifier change. flags: CGEventFlags. Return 1 to suppress.
public typealias EomKeyCallback = @convention(c) (Int32, UInt16, UInt64, Int32) -> Int32
/// kind: 0 move, 1 down, 2 up. button: 0 left, 1 right, 2 middle, 3+ other. Location in Quartz points.
public typealias EomPointerCallback = @convention(c) (Int32, Int32, Double, Double, UInt64) -> Void

private final class TapThread: Thread {
    private let ready = DispatchSemaphore(value: 0)
    private(set) var runLoop: CFRunLoop?

    override func main() {
        runLoop = CFRunLoopGetCurrent()
        // Keep the run loop alive with a port even before any tap source is attached.
        let port = Port()
        RunLoop.current.add(port, forMode: .default)
        ready.signal()
        while !isCancelled { CFRunLoopRunInMode(.defaultMode, 1.0, false) }
    }

    func startAndWait() { qualityOfService = .userInteractive; start(); ready.wait() }
}

private final class InputTaps {
    static let shared = InputTaps()
    private let thread = TapThread()
    private var keyTap: CFMachPort?
    private var keySource: CFRunLoopSource?
    private var pointerTap: CFMachPort?
    private var pointerSource: CFRunLoopSource?
    var keyCallback: EomKeyCallback?
    var pointerCallback: EomPointerCallback?

    init() { thread.startAndWait() }

    private func perform(_ work: @escaping () -> Void) {
        guard let loop = thread.runLoop else { return }
        let done = DispatchSemaphore(value: 0)
        CFRunLoopPerformBlock(loop, CFRunLoopMode.defaultMode.rawValue) { work(); done.signal() }
        CFRunLoopWakeUp(loop)
        done.wait()
    }

    func startKeys(_ callback: EomKeyCallback) -> Bool {
        keyCallback = callback
        var ok = false
        perform {
            if self.keyTap != nil { ok = true; return }
            let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue) | (1 << CGEventType.flagsChanged.rawValue)
            guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                              eventsOfInterest: CGEventMask(mask), callback: keyTapCallback, userInfo: nil) else { return }
            let source = CFMachPortCreateRunLoopSource(nil, tap, 0)
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .defaultMode)
            CGEvent.tapEnable(tap: tap, enable: true)
            self.keyTap = tap; self.keySource = source; ok = true
        }
        return ok
    }

    func stopKeys() {
        perform {
            if let tap = self.keyTap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
            if let source = self.keySource { CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .defaultMode) }
            self.keyTap = nil; self.keySource = nil
        }
        keyCallback = nil
    }

    func startPointer(_ callback: EomPointerCallback) -> Bool {
        pointerCallback = callback
        var ok = false
        perform {
            if self.pointerTap != nil { ok = true; return }
            let types: [CGEventType] = [.mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
                                        .otherMouseDown, .otherMouseUp, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged]
            let mask = types.reduce(0) { $0 | (1 << $1.rawValue) }
            guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly,
                                              eventsOfInterest: CGEventMask(mask), callback: pointerTapCallback, userInfo: nil) else { return }
            let source = CFMachPortCreateRunLoopSource(nil, tap, 0)
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .defaultMode)
            CGEvent.tapEnable(tap: tap, enable: true)
            self.pointerTap = tap; self.pointerSource = source; ok = true
        }
        return ok
    }

    func stopPointer() {
        perform {
            if let tap = self.pointerTap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
            if let source = self.pointerSource { CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .defaultMode) }
            self.pointerTap = nil; self.pointerSource = nil
        }
        pointerCallback = nil
    }

    func reenable(_ type: CGEventType, keys: Bool) {
        let tap = keys ? keyTap : pointerTap
        if let tap, type == .tapDisabledByTimeout || type == .tapDisabledByUserInput { CGEvent.tapEnable(tap: tap, enable: true) }
    }
}

private func keyTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent, userInfo: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    let taps = InputTaps.shared
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        taps.reenable(type, keys: true)
        return Unmanaged.passUnretained(event)
    }
    guard let callback = taps.keyCallback else { return Unmanaged.passUnretained(event) }
    let kind: Int32 = type == .keyDown ? 0 : type == .keyUp ? 1 : 2
    let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
    let repeatFlag = Int32(event.getIntegerValueField(.keyboardEventAutorepeat))
    let suppress = callback(kind, keyCode, event.flags.rawValue, repeatFlag)
    return suppress != 0 ? nil : Unmanaged.passUnretained(event)
}

private func pointerTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent, userInfo: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    let taps = InputTaps.shared
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        taps.reenable(type, keys: false)
        return Unmanaged.passUnretained(event)
    }
    guard let callback = taps.pointerCallback else { return Unmanaged.passUnretained(event) }
    let location = event.location
    let kind: Int32
    let button: Int32
    switch type {
    case .leftMouseDown: kind = 1; button = 0
    case .leftMouseUp: kind = 2; button = 0
    case .rightMouseDown: kind = 1; button = 1
    case .rightMouseUp: kind = 2; button = 1
    case .otherMouseDown: kind = 1; button = Int32(event.getIntegerValueField(.mouseEventButtonNumber))
    case .otherMouseUp: kind = 2; button = Int32(event.getIntegerValueField(.mouseEventButtonNumber))
    default: kind = 0; button = -1
    }
    callback(kind, button, location.x, location.y, event.flags.rawValue)
    return Unmanaged.passUnretained(event)
}

@_cdecl("eom_key_tap_start")
public func eom_key_tap_start(_ callback: EomKeyCallback?) -> Int32 {
    guard let callback else { return 0 }
    return InputTaps.shared.startKeys(callback) ? 1 : 0
}

@_cdecl("eom_key_tap_stop")
public func eom_key_tap_stop() { InputTaps.shared.stopKeys() }

@_cdecl("eom_pointer_tap_start")
public func eom_pointer_tap_start(_ callback: EomPointerCallback?) -> Int32 {
    guard let callback else { return 0 }
    return InputTaps.shared.startPointer(callback) ? 1 : 0
}

@_cdecl("eom_pointer_tap_stop")
public func eom_pointer_tap_stop() { InputTaps.shared.stopPointer() }

/// Current pointer location in Quartz points.
@_cdecl("eom_pointer_location")
public func eom_pointer_location(_ x: UnsafeMutablePointer<Double>?, _ y: UnsafeMutablePointer<Double>?) {
    let location = CGEvent(source: nil)?.location ?? .zero
    x?.pointee = location.x
    y?.pointee = location.y
}

@_cdecl("eom_pointer_warp")
public func eom_pointer_warp(_ x: Double, _ y: Double) {
    CGWarpMouseCursorPosition(CGPoint(x: x, y: y))
    CGAssociateMouseAndMouseCursorPosition(1)
}

/// Bit 0 left, bit 1 right, bit 2 middle, further bits other buttons.
@_cdecl("eom_pressed_buttons")
public func eom_pressed_buttons() -> Int32 { Int32(truncatingIfNeeded: NSEvent.pressedMouseButtons) }

@_cdecl("eom_modifier_flags")
public func eom_modifier_flags() -> UInt64 { CGEventSource.flagsState(.combinedSessionState).rawValue }

@_cdecl("eom_key_state")
public func eom_key_state(_ keyCode: UInt16) -> Int32 {
    CGEventSource.keyState(.combinedSessionState, key: CGKeyCode(keyCode)) ? 1 : 0
}

// MARK: application activation notifications

public typealias EomActivationCallback = @convention(c) (Int32) -> Void
private var activationObserver: NSObjectProtocol?
private var terminationObserver: NSObjectProtocol?

/// Notifies the host when another application becomes active (pid) or terminates (-pid).
@_cdecl("eom_observe_applications")
public func eom_observe_applications(_ callback: EomActivationCallback?) {
    onMain {
        let center = NSWorkspace.shared.notificationCenter
        if let activationObserver { center.removeObserver(activationObserver) }
        if let terminationObserver { center.removeObserver(terminationObserver) }
        activationObserver = nil; terminationObserver = nil
        guard let callback else { return }
        activationObserver = center.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { note in
            if let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication { callback(app.processIdentifier) }
        }
        terminationObserver = center.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { note in
            if let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication {
                eom_forget_process(app.processIdentifier)
                callback(-app.processIdentifier)
            }
        }
    }
}

/// Developer diagnostics: presses the modifiers in `flags` (Command/Option/Shift/Control), then the key,
/// then releases everything, like a physical keyboard.
@_cdecl("eom_post_key")
public func eom_post_key(_ keyCode: UInt16, _ flags: UInt64) {
    let source = CGEventSource(stateID: .hidSystemState)
    let modifiers: [(mask: UInt64, key: CGKeyCode, device: UInt64)] = [
        (0x100000, 0x37, 0x8), (0x80000, 0x3A, 0x20), (0x20000, 0x38, 0x2), (0x40000, 0x3B, 0x1)]
    var held: UInt64 = 0
    func post(_ key: CGKeyCode, _ down: Bool, _ eventFlags: UInt64) {
        guard let event = CGEvent(keyboardEventSource: source, virtualKey: key, keyDown: down) else { return }
        event.flags = CGEventFlags(rawValue: eventFlags)
        event.post(tap: .cghidEventTap)
        usleep(12_000)
    }
    for modifier in modifiers where flags & modifier.mask != 0 {
        held |= modifier.mask | modifier.device
        post(modifier.key, true, held)
    }
    post(CGKeyCode(keyCode), true, held)
    post(CGKeyCode(keyCode), false, held)
    for modifier in modifiers.reversed() where flags & modifier.mask != 0 {
        held &= ~(modifier.mask | modifier.device)
        post(modifier.key, false, held)
    }
}

/// Developer diagnostics: mouse gesture at Quartz points. kind: 0 left click, 1 right click,
/// 2 move only, 3 left drag from (x, y) to (x2, y2). flags are held modifier flags.
@_cdecl("eom_post_mouse")
public func eom_post_mouse(_ kind: Int32, _ x: Double, _ y: Double, _ x2: Double, _ y2: Double, _ flags: UInt64) {
    let source = CGEventSource(stateID: .hidSystemState)
    func post(_ type: CGEventType, _ point: CGPoint, _ button: CGMouseButton) {
        guard let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button) else { return }
        event.flags = CGEventFlags(rawValue: flags)
        event.post(tap: .cghidEventTap)
        usleep(20_000)
    }
    let start = CGPoint(x: x, y: y)
    post(.mouseMoved, start, .left)
    usleep(80_000)
    switch kind {
    case 0: post(.leftMouseDown, start, .left); post(.leftMouseUp, start, .left)
    case 1: post(.rightMouseDown, start, .right); post(.rightMouseUp, start, .right)
    case 3:
        post(.leftMouseDown, start, .left)
        for step in 1...12 {
            let t = Double(step) / 12
            post(.leftMouseDragged, CGPoint(x: x + (x2 - x) * t, y: y + (y2 - y) * t), .left)
        }
        post(.leftMouseUp, CGPoint(x: x2, y: y2), .left)
    default: break
    }
}

private var debugMonitor: Any?

/// Developer diagnostics: logs mouse-downs delivered to this application's windows.
@_cdecl("eom_debug_monitor_mouse")
public func eom_debug_monitor_mouse() {
    onMain {
        guard debugMonitor == nil else { return }
        debugMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { event in
            let view = event.window?.contentView?.hitTest(event.locationInWindow)
            log(.information, "mouse down: window \(event.window?.windowNumber ?? -1) level \(event.window?.level.rawValue ?? -1) " +
                "key=\(event.window?.isKeyWindow ?? false) appActive=\(NSApp.isActive) hit=\(view.map { String(describing: type(of: $0)) } ?? "nil") " +
                "firstMouse=\(view?.acceptsFirstMouse(for: event) ?? false)")
            return event
        }
    }
}
