import AppKit
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit
import Vision

// Local alerts: a low-rate ScreenCaptureKit stream of one area of a client window at full backing
// resolution. The latest frame is kept and copied to the host on request, so the host reads pixels
// at its own scan rate, independent of whether the window is covered by other windows.

final class RegionSampler: NSObject, SCStreamOutput, SCStreamDelegate {
    let sourceId: CGWindowID
    private let area: CGRect
    private let queue = DispatchQueue(label: "eveo.alerts", qos: .utility)
    private let lock = NSLock()
    private var stream: SCStream?
    private var pixels = Data()
    private var width = 0, height = 0, stride = 0
    private var sequence: Int64 = 0
    private(set) var scale: Double = 2
    private var areaWidth: Double = 0
    private var loggedGeometry = false
    private var starting = false
    private(set) var failed = false

    init(sourceId: CGWindowID, area: CGRect) {
        self.sourceId = sourceId
        self.area = area
        super.init()
    }

    func start() {
        guard stream == nil, !starting else { return }
        starting = true
        Task { [weak self] in
            guard let self else { return }
            do {
                guard let window = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false)
                    .windows.first(where: { $0.windowID == self.sourceId }) else {
                    self.failed = true
                    self.starting = false
                    return
                }
                let scale = await MainActor.run { Self.backingScale(for: window.frame) }
                let bounds = CGRect(origin: .zero, size: window.frame.size)
                let clipped = self.area.intersection(bounds)
                guard !clipped.isNull, clipped.width >= 4, clipped.height >= 4 else {
                    log(.warning, "Alert area \(self.area) is outside window \(self.sourceId)")
                    self.failed = true
                    self.starting = false
                    return
                }
                let config = SCStreamConfiguration()
                config.sourceRect = clipped
                config.width = max(8, Int((clipped.width * scale).rounded()))
                config.height = max(8, Int((clipped.height * scale).rounded()))
                config.minimumFrameInterval = CMTime(value: 1, timescale: 4)
                config.pixelFormat = kCVPixelFormatType_32BGRA
                config.queueDepth = 3
                config.showsCursor = false
                config.scalesToFit = true
                config.capturesAudio = false
                self.lock.withLock { self.scale = scale; self.areaWidth = clipped.width }
                let stream = SCStream(filter: SCContentFilter(desktopIndependentWindow: window), configuration: config, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: self.queue)
                try await stream.startCapture()
                self.lock.withLock { self.stream = stream }
            } catch {
                log(.warning, "Alert capture for window \(self.sourceId) failed: \(error.localizedDescription)")
                self.failed = true
            }
            self.starting = false
        }
    }

    @MainActor static func backingScale(for quartzFrame: CGRect) -> Double {
        let cocoa = Quartz.toCocoa(quartzFrame)
        let screen = NSScreen.screens.max(by: { $0.frame.intersection(cocoa).width * $0.frame.intersection(cocoa).height
            < $1.frame.intersection(cocoa).width * $1.frame.intersection(cocoa).height })
        return Double(screen?.backingScaleFactor ?? 2)
    }

    func stop() {
        let stream: SCStream? = lock.withLock { let s = self.stream; self.stream = nil; return s }
        if let stream { Task { try? await stream.stopCapture() } }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let statusValue = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: statusValue) == .complete,
              let buffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        // Keep only the area itself: the buffer can be padded around it (see capturedContent).
        let crop = capturedContent(attachments.first, width: CVPixelBufferGetWidth(buffer), height: CVPixelBufferGetHeight(buffer))
        guard let (copy, w, h) = copyPixels(buffer, crop) else { return }
        if !loggedGeometry {
            loggedGeometry = true
            log(.verbose, "Alert capture \(sourceId): buffer \(CVPixelBufferGetWidth(buffer))x\(CVPixelBufferGetHeight(buffer)), content \(crop)")
        }
        lock.withLock {
            pixels = copy
            width = w; height = h; stride = w * 4
            // Pixels per window point, from the content actually delivered.
            if areaWidth > 0 { scale = Double(w) / areaWidth }
            sequence += 1
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        log(.information, "Alert capture for window \(sourceId) stopped: \(error.localizedDescription)")
        lock.withLock { if self.stream === stream { self.stream = nil } }
        failed = true
    }

    /// 0 no frame yet, 1 copied, 2 unchanged since `known`, 3 failed, 4 buffer too small (sizes written).
    func read(_ target: UnsafeMutablePointer<UInt8>?, _ capacity: Int, _ known: Int64,
              _ outWidth: UnsafeMutablePointer<Int32>?, _ outHeight: UnsafeMutablePointer<Int32>?, _ outStride: UnsafeMutablePointer<Int32>?,
              _ outSequence: UnsafeMutablePointer<Int64>?, _ outScale: UnsafeMutablePointer<Double>?) -> Int32 {
        if failed { return 3 }
        return lock.withLock {
            outWidth?.pointee = Int32(width); outHeight?.pointee = Int32(height); outStride?.pointee = Int32(stride)
            outSequence?.pointee = sequence; outScale?.pointee = scale
            if sequence == 0 { return 0 }
            if sequence == known { return 2 }
            guard let target, capacity >= pixels.count else { return 4 }
            pixels.copyBytes(to: target, count: pixels.count)
            return 1
        }
    }
}

private var samplers: [Int64: RegionSampler] = [:]
private var nextSamplerId: Int64 = 1
private let samplersLock = NSLock()

/// Starts capturing an area (points, top-left origin, relative to the window) of a client window.
@_cdecl("eom_sampler_create")
public func eom_sampler_create(_ windowId: UInt32, _ x: Double, _ y: Double, _ width: Double, _ height: Double) -> Int64 {
    let sampler = RegionSampler(sourceId: windowId, area: CGRect(x: x, y: y, width: width, height: height))
    let id: Int64 = samplersLock.withLock {
        let id = nextSamplerId
        nextSamplerId += 1
        samplers[id] = sampler
        return id
    }
    sampler.start()
    return id
}

@_cdecl("eom_sampler_read")
public func eom_sampler_read(_ id: Int64, _ target: UnsafeMutablePointer<UInt8>?, _ capacity: Int32, _ known: Int64,
                             _ width: UnsafeMutablePointer<Int32>?, _ height: UnsafeMutablePointer<Int32>?, _ stride: UnsafeMutablePointer<Int32>?,
                             _ sequence: UnsafeMutablePointer<Int64>?, _ scale: UnsafeMutablePointer<Double>?) -> Int32 {
    guard let sampler = samplersLock.withLock({ samplers[id] }) else { return 3 }
    return sampler.read(target, Int(capacity), known, width, height, stride, sequence, scale)
}

@_cdecl("eom_sampler_destroy")
public func eom_sampler_destroy(_ id: Int64) {
    let sampler = samplersLock.withLock { samplers.removeValue(forKey: id) }
    sampler?.stop()
}

/// Recognised text lines of a BGRA image (Vision, on-device), newline separated; NULL when nothing was read.
/// The crop is enlarged and padded with its own background, and a word is placed before it: Vision misreads
/// glyphs touching the image edge (a clipped "]" becomes "1") and skips a lone digit.
@_cdecl("eom_recognize_text")
public func eom_recognize_text(_ pixels: UnsafePointer<UInt8>?, _ width: Int32, _ height: Int32, _ stride: Int32) -> UnsafeMutablePointer<CChar>? {
    guard let pixels, width > 0, height > 0, stride >= width * 4,
          let provider = CGDataProvider(data: Data(bytes: pixels, count: Int(stride) * Int(height)) as CFData),
          let image = CGImage(width: Int(width), height: Int(height), bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: Int(stride),
                              space: CGColorSpaceCreateDeviceRGB(),
                              bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
                              provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { return nil }
    // Background: the average of the four corner pixels (BGRA).
    var sum = (r: 0.0, g: 0.0, b: 0.0)
    for (x, y) in [(0, 0), (Int(width) - 1, 0), (0, Int(height) - 1), (Int(width) - 1, Int(height) - 1)] {
        let i = y * Int(stride) + x * 4
        sum.b += Double(pixels[i]); sum.g += Double(pixels[i + 1]); sum.r += Double(pixels[i + 2])
    }
    let background = NSColor(srgbRed: sum.r / 1020, green: sum.g / 1020, blue: sum.b / 1020, alpha: 1)
    let dark = (sum.r * 0.3 + sum.g * 0.59 + sum.b * 0.11) / 1020 < 0.5
    let factor = max(1, min(4, 96 / Double(height)))
    let cropWidth = Double(width) * factor, cropHeight = Double(height) * factor
    let pad = cropHeight * 0.5
    let label = NSAttributedString(string: "Count ", attributes: [
        .font: NSFont.systemFont(ofSize: cropHeight * 0.45, weight: .semibold),
        .foregroundColor: dark ? NSColor.white : NSColor.black])
    let labelWidth = label.size().width
    let canvasWidth = Int(pad + labelWidth + cropWidth + pad), canvasHeight = Int(cropHeight + 2 * pad)
    guard let context = CGContext(data: nil, width: canvasWidth, height: canvasHeight, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    context.setFillColor(background.cgColor)
    context.fill(CGRect(x: 0, y: 0, width: canvasWidth, height: canvasHeight))
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
    label.draw(at: NSPoint(x: pad, y: pad + (cropHeight - label.size().height) / 2))
    NSGraphicsContext.restoreGraphicsState()
    context.interpolationQuality = .high
    context.draw(image, in: CGRect(x: pad + labelWidth, y: pad, width: cropWidth, height: cropHeight))
    guard let composed = context.makeImage() else { return nil }
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = false
    do { try VNImageRequestHandler(cgImage: composed, options: [:]).perform([request]) } catch { return nil }
    let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
    return lines.isEmpty ? nil : allocCString(lines.joined(separator: "\n"))
}

private var playing: [NSSound] = []

/// Plays audio file data (for example WAV) at a volume from 0 to 1 without blocking.
@_cdecl("eom_play_sound")
public func eom_play_sound(_ data: UnsafePointer<UInt8>?, _ length: Int32, _ volume: Double) {
    guard let data, length > 0 else { return }
    let bytes = Data(bytes: data, count: Int(length))
    DispatchQueue.main.async {
        guard let sound = NSSound(data: bytes) else { return }
        sound.volume = Float(max(0, min(1, volume)))
        // NSSound must stay referenced while it plays.
        playing.removeAll { !$0.isPlaying }
        playing.append(sound)
        sound.play()
    }
}
