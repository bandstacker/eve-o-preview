import AppKit
import CoreMedia
import CoreVideo
import Foundation
import QuartzCore
import ScreenCaptureKit

// Live previews: one ScreenCaptureKit stream per source window, scaled by the GPU to the preview
// size. Frames are IOSurfaces assigned to a layer above the preview window's content, which mirrors
// DWM's image-over-window presentation without copying pixels through the CPU.

/// A layer-backed view that never takes mouse events, so the Avalonia view below keeps input.
final class PassthroughImageView: NSView {
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.contentsGravity = .resize
        layer?.backgroundColor = NSColor.black.cgColor
        layer?.actions = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull()]
    }
    required init?(coder: NSCoder) { nil }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var isOpaque: Bool { true }
}

private enum ShareableContent {
    private static var cached: [CGWindowID: SCWindow] = [:]
    private static var lastRefresh = Date.distantPast
    private static let lock = NSLock()

    static func window(_ id: CGWindowID) async throws -> SCWindow? {
        let (hit, stale): (SCWindow?, Bool) = lock.withLock { (cached[id], Date().timeIntervalSince(lastRefresh) > 0.5) }
        if let hit { return hit }
        guard stale else { return nil }
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false)
        return lock.withLock {
            lastRefresh = Date()
            cached = Dictionary(content.windows.map { ($0.windowID, $0) }, uniquingKeysWith: { first, _ in first })
            return cached[id]
        }
    }

    static func invalidate(_ id: CGWindowID) { lock.withLock { _ = cached.removeValue(forKey: id) } }
}

final class LiveThumbnail: NSObject, SCStreamOutput, SCStreamDelegate {
    let sourceId: CGWindowID
    private weak var hostWindow: NSWindow?
    private let imageView = PassthroughImageView(frame: .zero)
    private let queue = DispatchQueue(label: "eveo.capture", qos: .userInteractive)
    private var stream: SCStream?
    private var pixelSize = CGSize(width: 256, height: 144)
    private var fps: Int32 = 15
    private var cropTop: Double = 0
    private var sourceRect: CGRect?
    private var currentFrame: CMSampleBuffer?
    private var starting = false
    private(set) var failed = false
    private(set) var hasFrame = false

    init(host: NSWindow, sourceId: CGWindowID) {
        self.hostWindow = host
        self.sourceId = sourceId
        super.init()
        if let content = host.contentView {
            imageView.frame = .zero
            content.addSubview(imageView, positioned: .above, relativeTo: nil)
        }
    }

    /// Places the image in the host content view; the rectangle is in points with a top-left origin.
    /// Frames are captured at the backing resolution of that rectangle.
    func setBounds(x: Double, y: Double, width: Double, height: Double) {
        guard let host = hostWindow, let content = host.contentView else { return }
        let scale = host.backingScaleFactor
        let frame = NSRect(x: x, y: content.bounds.height - (y + height), width: width, height: height)
        if imageView.frame != frame { imageView.frame = frame }
        let size = CGSize(width: max(16, (width * scale).rounded()), height: max(16, (height * scale).rounded()))
        if size != pixelSize { pixelSize = size; reconfigure() }
    }

    func setFrameRate(_ value: Int32) {
        let clamped = max(1, min(60, value))
        if clamped != fps { fps = clamped; reconfigure() }
    }

    /// Crops this many source points from the top (for example a title bar).
    func setCropTop(_ value: Double) {
        if value != cropTop { cropTop = max(0, value); reconfigure() }
    }

    /// Captures only this rectangle of the source window (points, top-left origin); nil captures all.
    func setSourceRect(_ rect: CGRect?) {
        if rect != sourceRect { sourceRect = rect; reconfigure() }
    }

    private func configuration(for window: SCWindow?) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        config.width = Int(pixelSize.width)
        config.height = Int(pixelSize.height)
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.queueDepth = 4
        config.showsCursor = false
        config.scalesToFit = true
        config.preservesAspectRatio = false
        config.capturesAudio = false
        if let rect = sourceRect, let window {
            // Keep the selection inside the window if the client shrank since it was drawn.
            let bounds = CGRect(origin: .zero, size: window.frame.size)
            let clipped = rect.intersection(bounds)
            if !clipped.isNull, clipped.width >= 4, clipped.height >= 4 { config.sourceRect = clipped }
        } else if cropTop > 0, let window, window.frame.height > cropTop {
            config.sourceRect = CGRect(x: 0, y: cropTop, width: window.frame.width, height: window.frame.height - cropTop)
        }
        return config
    }

    func start() {
        guard stream == nil, !starting else { return }
        starting = true
        Task { @MainActor [weak self] in
            guard let self else { return }
            do {
                guard let window = try await ShareableContent.window(self.sourceId) else {
                    self.starting = false
                    return
                }
                let filter = SCContentFilter(desktopIndependentWindow: window)
                let stream = SCStream(filter: filter, configuration: self.configuration(for: window), delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: self.queue)
                try await stream.startCapture()
                self.stream = stream
                self.failed = false
                // Bounds or frame-rate changes that arrived while starting.
                try? await stream.updateConfiguration(self.configuration(for: window))
            } catch {
                log(.warning, "Preview capture for window \(self.sourceId) failed: \(error.localizedDescription)")
                self.failed = true
                ShareableContent.invalidate(self.sourceId)
            }
            self.starting = false
        }
    }

    private func reconfigure() {
        guard let stream else { return }
        Task { @MainActor [weak self] in
            guard let self else { return }
            let window = try? await ShareableContent.window(self.sourceId)
            try? await stream.updateConfiguration(self.configuration(for: window))
        }
    }

    func stop() {
        let stream = self.stream
        self.stream = nil
        if let stream { Task { try? await stream.stopCapture() } }
        imageView.removeFromSuperview()
        currentFrame = nil
    }

    var isHealthy: Bool { !failed && (stream != nil || starting) }

    private var loggedGeometry = false

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let info = attachments.first,
              let statusValue = info[.status] as? Int,
              SCFrameStatus(rawValue: statusValue) == .complete,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              let surface = CVPixelBufferGetIOSurface(pixelBuffer)?.takeUnretainedValue() else { return }
        // Window capture keeps the source aspect ratio inside the buffer; show only the content
        // rectangle, stretched to the preview like a DWM thumbnail.
        let bufferWidth = Double(CVPixelBufferGetWidth(pixelBuffer)), bufferHeight = Double(CVPixelBufferGetHeight(pixelBuffer))
        var contents = CGRect(x: 0, y: 0, width: 1, height: 1)
        if let rectDict = info[.contentRect] as? NSDictionary, let rect = CGRect(dictionaryRepresentation: rectDict as CFDictionary),
           rect.width > 0, rect.height > 0, bufferWidth > 0, bufferHeight > 0 {
            let scale = (info[.scaleFactor] as? Double) ?? 1
            // contentRect is in points; the buffer is in pixels.
            let factor = rect.width * scale <= bufferWidth + 1 ? scale : 1
            contents = CGRect(x: rect.origin.x * factor / bufferWidth, y: rect.origin.y * factor / bufferHeight,
                              width: min(1, rect.width * factor / bufferWidth), height: min(1, rect.height * factor / bufferHeight))
            if !loggedGeometry {
                loggedGeometry = true
                log(.verbose, "Capture \(sourceId): buffer \(bufferWidth)x\(bufferHeight), content \(rect), scale \(scale), contentsRect \(contents)")
            }
        }
        DispatchQueue.main.async { [weak self] in
            guard let self, self.imageView.superview != nil else { return }
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            self.imageView.layer?.contents = surface
            self.imageView.layer?.contentsRect = contents
            CATransaction.commit()
            // Keep the buffer alive while its surface is on screen so the pool does not reuse it.
            self.currentFrame = sampleBuffer
            self.hasFrame = true
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log(.information, "Preview capture for window \(sourceId) stopped: \(error.localizedDescription)")
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if self.stream === stream { self.stream = nil }
            self.failed = true
            ShareableContent.invalidate(self.sourceId)
        }
    }
}

private var thumbnails: [Int64: LiveThumbnail] = [:]
private var nextThumbnailId: Int64 = 1

@_cdecl("eom_thumbnail_create")
public func eom_thumbnail_create(_ host: UnsafeMutableRawPointer?, _ sourceId: UInt32) -> Int64 {
    onMain {
        guard let window = resolveWindow(host) else { return 0 }
        let thumbnail = LiveThumbnail(host: window, sourceId: sourceId)
        let id = nextThumbnailId
        nextThumbnailId += 1
        thumbnails[id] = thumbnail
        thumbnail.start()
        return id
    }
}

@_cdecl("eom_thumbnail_set_bounds")
public func eom_thumbnail_set_bounds(_ id: Int64, _ x: Double, _ y: Double, _ width: Double, _ height: Double) {
    onMain { thumbnails[id]?.setBounds(x: x, y: y, width: width, height: height) }
}

@_cdecl("eom_thumbnail_set_frame_rate")
public func eom_thumbnail_set_frame_rate(_ id: Int64, _ fps: Int32) { onMain { thumbnails[id]?.setFrameRate(fps) } }

@_cdecl("eom_thumbnail_set_source_rect")
public func eom_thumbnail_set_source_rect(_ id: Int64, _ x: Double, _ y: Double, _ width: Double, _ height: Double) {
    onMain { thumbnails[id]?.setSourceRect(width > 0 && height > 0 ? CGRect(x: x, y: y, width: width, height: height) : nil) }
}

@_cdecl("eom_thumbnail_set_crop_top")
public func eom_thumbnail_set_crop_top(_ id: Int64, _ points: Double) { onMain { thumbnails[id]?.setCropTop(points) } }

/// 1 while the stream is running or starting; 0 once it failed (the host re-registers).
@_cdecl("eom_thumbnail_update")
public func eom_thumbnail_update(_ id: Int64) -> Int32 {
    onMain {
        guard let thumbnail = thumbnails[id] else { return 0 }
        if thumbnail.failed { return 0 }
        thumbnail.start()
        return 1
    }
}

@_cdecl("eom_thumbnail_destroy")
public func eom_thumbnail_destroy(_ id: Int64) {
    onMain {
        thumbnails[id]?.stop()
        thumbnails.removeValue(forKey: id)
    }
}

/// The pixels of a captured buffer that show the source. ScreenCaptureKit keeps the source's aspect ratio
/// and pads the rest of the buffer (EVE clients fill as little as 76% of the width). contentRect is in points
/// and is scaled by scaleFactor when that still fits the buffer, as for live previews.
func capturedContent(_ info: [SCStreamFrameInfo: Any]?, width: Int, height: Int) -> CGRect {
    let full = CGRect(x: 0, y: 0, width: width, height: height)
    guard let info, let dictionary = info[.contentRect] as? NSDictionary,
          let rect = CGRect(dictionaryRepresentation: dictionary as CFDictionary), rect.width > 0, rect.height > 0 else { return full }
    let scale = (info[.scaleFactor] as? Double) ?? 1
    let factor = rect.width * scale <= Double(width) + 1 ? scale : 1
    let pixels = CGRect(x: rect.origin.x * factor, y: rect.origin.y * factor, width: rect.width * factor, height: rect.height * factor)
        .integral.intersection(full)
    return pixels.width >= 1 && pixels.height >= 1 ? pixels : full
}

/// Copies a pixel rectangle of a BGRA buffer into tightly packed rows.
func copyPixels(_ buffer: CVPixelBuffer, _ crop: CGRect) -> (data: Data, width: Int, height: Int)? {
    CVPixelBufferLockBaseAddress(buffer, .readOnly)
    defer { CVPixelBufferUnlockBaseAddress(buffer, .readOnly) }
    guard let base = CVPixelBufferGetBaseAddress(buffer) else { return nil }
    let rowBytes = CVPixelBufferGetBytesPerRow(buffer)
    let x = Int(crop.minX), y = Int(crop.minY), width = Int(crop.width), height = Int(crop.height)
    guard width > 0, height > 0 else { return nil }
    var data = Data(count: width * height * 4)
    data.withUnsafeMutableBytes { target in
        for row in 0..<height {
            memcpy(target.baseAddress! + row * width * 4, base + (y + row) * rowBytes + x * 4, width * 4)
        }
    }
    return (data, width, height)
}

/// Receives the first complete frame of a short stream, with its content-rectangle attachments
/// (a screenshot carries none, so a padded window could not be cropped).
private final class FrameGrabber: NSObject, SCStreamOutput {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<CMSampleBuffer?, Never>?

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let status = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: status) == .complete else { return }
        finish(sampleBuffer)
    }

    func finish(_ sample: CMSampleBuffer?) {
        lock.withLock { let waiting = continuation; continuation = nil; return waiting }?.resume(returning: sample)
    }

    static func grab(_ window: SCWindow, _ config: SCStreamConfiguration) async -> CMSampleBuffer? {
        let grabber = FrameGrabber()
        let stream = SCStream(filter: SCContentFilter(desktopIndependentWindow: window), configuration: config, delegate: nil)
        do { try stream.addStreamOutput(grabber, type: .screen, sampleHandlerQueue: DispatchQueue(label: "eveo.still")) } catch { return nil }
        let sample: CMSampleBuffer? = await withCheckedContinuation { continuation in
            grabber.lock.withLock { grabber.continuation = continuation }
            Task {
                do { try await stream.startCapture() } catch { grabber.finish(nil) }
            }
            Task {
                try? await Task.sleep(nanoseconds: 2_000_000_000)
                grabber.finish(nil)
            }
        }
        try? await stream.stopCapture()
        return sample
    }
}

/// One still image of a window as PNG, scaled to fit maxWidth x maxHeight pixels and cropped to the window content.
/// A client resized in the background (for example by restored client layouts) can keep drawing at its old size
/// until it is activated; the capture is retried for up to two seconds until the content fills the window frame.
@_cdecl("eom_capture_window_png")
public func eom_capture_window_png(_ windowId: UInt32, _ maxWidth: Int32, _ maxHeight: Int32,
                                   _ length: UnsafeMutablePointer<Int32>?) -> UnsafeMutablePointer<UInt8>? {
    let semaphore = DispatchSemaphore(value: 0)
    var result: Data?
    Task.detached {
        defer { semaphore.signal() }
        var still: (data: Data, width: Int, height: Int)?
        for attempt in 0..<9 {
            if attempt > 0 { try? await Task.sleep(nanoseconds: 250_000_000) }
            ShareableContent.invalidate(windowId)
            guard let window = try? await ShareableContent.window(windowId) else { continue }
            let config = SCStreamConfiguration()
            let scale = min(Double(maxWidth) / window.frame.width, Double(maxHeight) / window.frame.height, 2)
            config.width = max(16, Int(window.frame.width * scale))
            config.height = max(16, Int(window.frame.height * scale))
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.showsCursor = false
            config.scalesToFit = true
            config.capturesAudio = false
            guard let sample = await FrameGrabber.grab(window, config), let buffer = CMSampleBufferGetImageBuffer(sample) else { continue }
            let info = (CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]])?.first
            let bufferWidth = CVPixelBufferGetWidth(buffer), bufferHeight = CVPixelBufferGetHeight(buffer)
            let crop = capturedContent(info, width: bufferWidth, height: bufferHeight)
            still = copyPixels(buffer, crop)
            let settled = abs(crop.width - Double(bufferWidth)) <= 4 && abs(crop.height - Double(bufferHeight)) <= 4
            log(.verbose, "Still \(windowId) attempt \(attempt): frame \(window.frame), buffer \(bufferWidth)x\(bufferHeight), content \(crop)")
            if settled { break }
        }
        guard let (data, width, height) = still,
              let provider = CGDataProvider(data: data as CFData),
              let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { return }
        result = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }
    if semaphore.wait(timeout: .now() + 6) == .timedOut { length?.pointee = 0; return nil }
    guard let data = result else { length?.pointee = 0; return nil }
    return allocBuffer(data, length)
}

/// Developer diagnostics: PNG of the whole main display (requires Screen Recording).
@_cdecl("eom_capture_main_display_png")
public func eom_capture_main_display_png(_ length: UnsafeMutablePointer<Int32>?) -> UnsafeMutablePointer<UInt8>? {
    let semaphore = DispatchSemaphore(value: 0)
    var result: Data?
    Task.detached {
        defer { semaphore.signal() }
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true),
              let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else { return }
        let config = SCStreamConfiguration()
        config.width = display.width
        config.height = display.height
        config.showsCursor = true
        guard let image = try? await SCScreenshotManager.captureImage(
            contentFilter: SCContentFilter(display: display, excludingWindows: []), configuration: config) else { return }
        result = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])
    }
    if semaphore.wait(timeout: .now() + 5) == .timedOut { length?.pointee = 0; return nil }
    guard let data = result else { length?.pointee = 0; return nil }
    return allocBuffer(data, length)
}
