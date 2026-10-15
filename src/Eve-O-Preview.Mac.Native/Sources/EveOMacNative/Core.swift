import AppKit
import Foundation

// Shared helpers for the C ABI. Strings and buffers returned to the host are malloc-allocated
// and released by the host through eom_free.

public typealias EomLogCallback = @convention(c) (Int32, UnsafePointer<CChar>?) -> Void

private var logCallback: EomLogCallback?

enum LogLevel: Int32 { case verbose = 0, information = 1, warning = 2, error = 3 }

func log(_ level: LogLevel, _ message: @autoclosure () -> String) {
    guard let callback = logCallback else { return }
    message().withCString { callback(level.rawValue, $0) }
}

@_cdecl("eom_set_log_callback")
public func eom_set_log_callback(_ callback: EomLogCallback?) { logCallback = callback }

@_cdecl("eom_free")
public func eom_free(_ pointer: UnsafeMutableRawPointer?) { free(pointer) }

func allocCString(_ text: String) -> UnsafeMutablePointer<CChar>? { strdup(text) }

func allocJson<T: Encodable>(_ value: T) -> UnsafeMutablePointer<CChar>? {
    guard let data = try? JSONEncoder().encode(value), let text = String(data: data, encoding: .utf8) else { return nil }
    return allocCString(text)
}

func allocBuffer(_ data: Data, _ length: UnsafeMutablePointer<Int32>?) -> UnsafeMutablePointer<UInt8>? {
    length?.pointee = Int32(data.count)
    guard !data.isEmpty, let buffer = malloc(data.count)?.assumingMemoryBound(to: UInt8.self) else { return nil }
    data.copyBytes(to: buffer, count: data.count)
    return buffer
}

/// Runs work on the main thread synchronously. AppKit objects must only be touched there.
func onMain<T>(_ work: () -> T) -> T {
    if Thread.isMainThread { return work() }
    return DispatchQueue.main.sync(execute: work)
}

/// Avalonia's macOS platform handle may be an NSWindow or its content NSView.
func resolveWindow(_ pointer: UnsafeMutableRawPointer?) -> NSWindow? {
    guard let pointer else { return nil }
    let object = Unmanaged<AnyObject>.fromOpaque(pointer).takeUnretainedValue()
    if let window = object as? NSWindow { return window }
    if let view = object as? NSView { return view.window }
    return nil
}

/// Global Quartz coordinates use a top-left origin on the primary display, in points.
enum Quartz {
    static var primaryHeight: CGFloat { NSScreen.screens.first?.frame.height ?? 0 }
    static func fromCocoa(_ rect: NSRect) -> CGRect {
        CGRect(x: rect.origin.x, y: primaryHeight - rect.origin.y - rect.height, width: rect.width, height: rect.height)
    }
    static func toCocoa(_ rect: CGRect) -> NSRect {
        NSRect(x: rect.origin.x, y: primaryHeight - rect.origin.y - rect.height, width: rect.width, height: rect.height)
    }
}

/// Writes x, y, width, height into a host-provided double[4].
func writeRect(_ target: UnsafeMutablePointer<Double>?, _ rect: CGRect) {
    guard let target else { return }
    target[0] = rect.origin.x; target[1] = rect.origin.y; target[2] = rect.width; target[3] = rect.height
}
