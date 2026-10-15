import AppKit
import Foundation

// Native behavior of the host's own Avalonia windows: nonactivating previews, click-through
// overlays, alpha, z-order and pixel-exact geometry. Pointers are NSWindow or NSView handles.

private typealias SetBoolFn = @convention(c) (AnyObject, Selector, Bool) -> Void

private func callPrivateSetter(_ object: NSObject, _ name: String, _ value: Bool) -> Bool {
    let selector = Selector(name)
    guard object.responds(to: selector), let method = class_getInstanceMethod(type(of: object), selector) else { return false }
    let function = unsafeBitCast(method_getImplementation(method), to: SetBoolFn.self)
    function(object, selector, value)
    return true
}

/// Configures a preview, overlay or guide window. A nonactivating window receives clicks
/// without making this application active, so the EVE client keeps focus (WS_EX_NOACTIVATE).
@_cdecl("eom_window_configure")
public func eom_window_configure(_ handle: UnsafeMutableRawPointer?, _ clickThrough: Int32, _ nonactivating: Int32) -> Int32 {
    onMain {
        guard let window = resolveWindow(handle) else { return 0 }
        if nonactivating != 0 {
            _ = callPrivateSetter(window, "_setPreventsActivation:", true)
        }
        window.hidesOnDeactivate = false
        window.hasShadow = false
        window.ignoresMouseEvents = clickThrough != 0
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle, .stationary]
        window.isExcludedFromWindowsMenu = true
        window.acceptsMouseMovedEvents = true
        return 1
    }
}

@_cdecl("eom_window_set_alpha")
public func eom_window_set_alpha(_ handle: UnsafeMutableRawPointer?, _ alpha: Double) {
    onMain { resolveWindow(handle)?.alphaValue = CGFloat(max(0, min(1, alpha))) }
}

/// Shows and raises within its level without activating this application.
@_cdecl("eom_window_order_front")
public func eom_window_order_front(_ handle: UnsafeMutableRawPointer?, _ topmost: Int32) -> Int32 {
    onMain {
        guard let window = resolveWindow(handle) else { return 0 }
        if window.isMiniaturized { window.deminiaturize(nil) }
        if topmost != 0 && window.level.rawValue < NSWindow.Level.floating.rawValue { window.level = .floating }
        window.orderFrontRegardless()
        return 1
    }
}

@_cdecl("eom_window_order_above")
public func eom_window_order_above(_ handle: UnsafeMutableRawPointer?, _ relativeTo: UnsafeMutableRawPointer?) {
    onMain {
        guard let window = resolveWindow(handle), let other = resolveWindow(relativeTo) else { return }
        if window.level != other.level { window.level = other.level }
        window.order(.above, relativeTo: other.windowNumber)
    }
}

@_cdecl("eom_window_is_minimized")
public func eom_window_is_minimized(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    onMain { resolveWindow(handle)?.isMiniaturized == true ? 1 : 0 }
}

@_cdecl("eom_window_is_visible")
public func eom_window_is_visible(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    onMain { resolveWindow(handle)?.isVisible == true ? 1 : 0 }
}

@_cdecl("eom_window_number")
public func eom_window_number(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    onMain { Int32(resolveWindow(handle)?.windowNumber ?? 0) }
}

@_cdecl("eom_window_from_handle")
public func eom_window_from_handle(_ handle: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    onMain { resolveWindow(handle).map { Unmanaged.passUnretained($0).toOpaque() } }
}

/// Window and content frames of an own window, in global Quartz points.
@_cdecl("eom_window_frames")
public func eom_window_frames(_ handle: UnsafeMutableRawPointer?, _ frame: UnsafeMutablePointer<Double>?,
                              _ content: UnsafeMutablePointer<Double>?, _ scale: UnsafeMutablePointer<Double>?) -> Int32 {
    onMain {
        guard let window = resolveWindow(handle) else { return 0 }
        let outer = Quartz.fromCocoa(window.frame)
        let inner = Quartz.fromCocoa(window.contentRect(forFrameRect: window.frame))
        writeRect(frame, outer)
        writeRect(content, inner)
        scale?.pointee = Double(window.backingScaleFactor)
        return 1
    }
}

/// Positions an own window so its content rectangle has this origin/size (global Quartz points).
@_cdecl("eom_window_set_content_frame")
public func eom_window_set_content_frame(_ handle: UnsafeMutableRawPointer?, _ x: Double, _ y: Double,
                                         _ width: Double, _ height: Double, _ setOrigin: Int32, _ setSize: Int32) -> Int32 {
    onMain {
        guard let window = resolveWindow(handle) else { return 0 }
        var content = Quartz.fromCocoa(window.contentRect(forFrameRect: window.frame))
        if setOrigin != 0 { content.origin = CGPoint(x: x, y: y) }
        if setSize != 0 { content.size = CGSize(width: max(1, width), height: max(1, height)) }
        let frame = window.frameRect(forContentRect: Quartz.toCocoa(content))
        window.setFrame(frame, display: true, animate: false)
        return 1
    }
}

/// The host's own windows from front to back (as NSWindow pointers), for native z-order queries.
@_cdecl("eom_own_windows_front_to_back")
public func eom_own_windows_front_to_back(_ count: UnsafeMutablePointer<Int32>?) -> UnsafeMutablePointer<UnsafeMutableRawPointer?>? {
    onMain {
        let numbers = NSWindow.windowNumbers(options: []) ?? []
        let windows = numbers.compactMap { NSApp.window(withWindowNumber: $0.intValue) }
        count?.pointee = Int32(windows.count)
        guard !windows.isEmpty,
              let buffer = malloc(windows.count * MemoryLayout<UnsafeMutableRawPointer?>.stride)?
                .assumingMemoryBound(to: UnsafeMutableRawPointer?.self) else { return nil }
        for (index, window) in windows.enumerated() { buffer[index] = Unmanaged.passUnretained(window).toOpaque() }
        return buffer
    }
}

/// Accessory activation policy: no Dock icon or app switcher entry; a menu-bar item only.
@_cdecl("eom_set_accessory_policy")
public func eom_set_accessory_policy(_ accessory: Int32) {
    onMain { _ = NSApp.setActivationPolicy(accessory != 0 ? .accessory : .regular) }
}

/// Brings the settings window forward when the user explicitly opens it from the menu bar.
@_cdecl("eom_activate_self")
public func eom_activate_self() {
    onMain { NSApp.activate() }
}

@_cdecl("eom_beep")
public func eom_beep() { onMain { NSSound.beep() } }

/// Developer diagnostics: class, frame, visibility and occlusion of every window of this app.
@_cdecl("eom_debug_describe_windows")
public func eom_debug_describe_windows() -> UnsafeMutablePointer<CChar>? {
    onMain {
        let lines = NSApp.windows.map { window in
            "\(type(of: window)) #\(window.windowNumber) frame=\(window.frame) visible=\(window.isVisible) " +
            "onScreen=\(window.occlusionState.contains(.visible)) level=\(window.level.rawValue)"
        }
        return allocCString(lines.joined(separator: "\n"))
    }
}
