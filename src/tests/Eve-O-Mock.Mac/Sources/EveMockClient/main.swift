import AppKit
import Foundation

// Mock EVE client: one window titled "EVE - <name>" (or "EVE" for a login screen) with animated
// content, optional periodic sound (for background-mute checks) and optional Gamelog output in the
// EVE format parsed by the combat-log augments.
//
//   ExeFile --name "Pilot One" [--login] [--sound] [--gamelog] [--local FILE] [--x 100 --y 100 --width 1024 --height 640]

struct Options {
    var name = "Pilot One"
    var login = false
    var sound = false
    var gamelog = false
    var local: String?
    var frame = NSRect(x: 120, y: 120, width: 1024, height: 640)

    init(_ args: [String]) {
        var i = 1
        func next() -> String? { i += 1; return i < args.count ? args[i] : nil }
        while i < args.count {
            switch args[i] {
            case "--name": name = next() ?? name
            case "--login": login = true
            case "--sound": sound = true
            case "--gamelog": gamelog = true
            case "--local": local = next()
            case "--x": frame.origin.x = Double(next() ?? "") ?? frame.origin.x
            case "--y": frame.origin.y = Double(next() ?? "") ?? frame.origin.y
            case "--width": frame.size.width = Double(next() ?? "") ?? frame.width
            case "--height": frame.size.height = Double(next() ?? "") ?? frame.height
            default: break
            }
            i += 1
        }
    }
}

final class SceneView: NSView {
    let name: String
    let hue: CGFloat
    var phase: CGFloat = 0
    var frames = 0

    init(name: String) {
        self.name = name
        hue = CGFloat(abs(name.hashValue % 360)) / 360
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedHue: hue, saturation: 0.55, brightness: 0.25, alpha: 1).setFill()
        bounds.fill()
        // A moving bar and a frame counter make live capture and frame rate visible in previews.
        let x = (sin(phase) * 0.5 + 0.5) * (bounds.width - 120)
        NSColor(calibratedHue: hue, saturation: 0.8, brightness: 0.95, alpha: 1).setFill()
        NSRect(x: x, y: bounds.height * 0.25, width: 120, height: 40).fill()
        let title = NSAttributedString(string: name, attributes: [.font: NSFont.boldSystemFont(ofSize: 56), .foregroundColor: NSColor.white])
        title.draw(at: NSPoint(x: 40, y: bounds.height - 120))
        let info = NSAttributedString(string: "Mock EVE client  frame \(frames)  pid \(getpid())",
                                      attributes: [.font: NSFont.monospacedSystemFont(ofSize: 18, weight: .regular), .foregroundColor: NSColor.white])
        info.draw(at: NSPoint(x: 40, y: 40))
    }
}

/// A stand-in Local member list for alert checks: a "Local [N]" header and one row per pilot with a
/// standing icon drawn like the client's (filled square, lighter rim, white glyph). Rows come from a
/// text file re-read every second, one "STANDING Name" per line (red, orange, yellow, neutral, green,
/// blue, lightblue, purple, none); an optional "count N" line overrides the member count.
final class LocalPanelView: NSView {
    var rows: [(standing: String, name: String)] = []
    var count = 1
    override var isFlipped: Bool { true }

    static let colors: [String: (fill: NSColor, rim: NSColor, glyph: String)] = [
        "red": (rgb(0x750A0A), rgb(0x950606), "-"), "orange": (rgb(0xB85C00), rgb(0xD06A00), "-"),
        "yellow": (rgb(0xB8A000), rgb(0xD0B400), "!"), "neutral": (rgb(0x6E6E6E), rgb(0x8C8C8C), "="),
        "green": (rgb(0x2F7A1E), rgb(0x3A9426), "*"), "blue": (rgb(0x0B3D91), rgb(0x134EB0), "+"),
        "lightblue": (rgb(0x2A7FC4), rgb(0x3792D8), "+"), "purple": (rgb(0x7A2A8F), rgb(0x9133AA), "*"),
    ]
    static func rgb(_ value: Int) -> NSColor {
        NSColor(srgbRed: CGFloat(value >> 16 & 255) / 255, green: CGFloat(value >> 8 & 255) / 255, blue: CGFloat(value & 255) / 255, alpha: 1)
    }

    override func draw(_ dirtyRect: NSRect) {
        NSColor(srgbRed: 0.07, green: 0.07, blue: 0.08, alpha: 1).setFill()
        bounds.fill()
        let header = NSAttributedString(string: "Local [\(count)]", attributes: [.font: NSFont.systemFont(ofSize: 13, weight: .semibold), .foregroundColor: NSColor.white])
        header.draw(at: NSPoint(x: 10, y: 6))
        NSColor(white: 0.3, alpha: 1).setFill()
        NSRect(x: 0, y: 27, width: bounds.width, height: 1).fill()
        for (index, row) in rows.enumerated() {
            let y = 34 + CGFloat(index) * 20
            if let style = Self.colors[row.standing] {
                let icon = NSRect(x: 10, y: y + 3, width: 12, height: 12)
                style.rim.setFill(); icon.fill()
                style.fill.setFill(); icon.insetBy(dx: 1, dy: 1).fill()
                NSColor.white.setFill()
                switch style.glyph {
                case "=": NSRect(x: 13, y: y + 6, width: 6, height: 2).fill(); NSRect(x: 13, y: y + 10, width: 6, height: 2).fill()
                case "-": NSRect(x: 13, y: y + 8, width: 6, height: 2).fill()
                case "+": NSRect(x: 13, y: y + 8, width: 6, height: 2).fill(); NSRect(x: 15, y: y + 6, width: 2, height: 6).fill()
                case "!": NSRect(x: 15, y: y + 5, width: 2, height: 5).fill(); NSRect(x: 15, y: y + 11, width: 2, height: 2).fill()
                default: NSRect(x: 14, y: y + 7, width: 4, height: 4).fill()
                }
            }
            let name = NSAttributedString(string: row.name, attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor(white: 0.85, alpha: 1)])
            name.draw(at: NSPoint(x: 30, y: y + 2))
        }
    }

    func reload(from path: String) {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return }
        var parsed: [(String, String)] = []
        var explicitCount: Int?
        for line in text.split(separator: "\n") {
            let parts = line.trimmingCharacters(in: .whitespaces).split(separator: " ", maxSplits: 1).map(String.init)
            guard let first = parts.first else { continue }
            if first == "count", parts.count > 1 { explicitCount = Int(parts[1]); continue }
            parsed.append((first, parts.count > 1 ? parts[1] : "Pilot"))
        }
        let newCount = explicitCount ?? parsed.count + 1
        if newCount != count || parsed.map({ "\($0.0) \($0.1)" }) != rows.map({ "\($0.standing) \($0.name)" }) {
            rows = parsed.map { (standing: $0.0, name: $0.1) }
            count = newCount
            needsDisplay = true
        }
    }
}

final class Delegate: NSObject, NSApplicationDelegate {
    let options: Options
    var window: NSWindow!
    var scene: SceneView!
    var local: LocalPanelView?
    var gamelog: FileHandle?
    var tick = 0

    init(options: Options) { self.options = options }

    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: options.frame, styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = options.login ? "EVE" : "EVE - \(options.name)"
        scene = SceneView(name: options.login ? "Login screen" : options.name)
        window.contentView = scene
        if let path = options.local {
            // Top-right of the content area: x = width - 300, 40 points below the content top.
            let panel = LocalPanelView(frame: NSRect(x: options.frame.width - 300, y: options.frame.height - 440, width: 280, height: 400))
            panel.autoresizingMask = [.minXMargin, .minYMargin]
            panel.reload(from: path)
            scene.addSubview(panel)
            local = panel
        }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
        if options.gamelog { openGamelog() }
        Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.scene.phase += 0.08
            self.scene.frames += 1
            self.scene.needsDisplay = true
        }
        Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.everySecond() }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    private func everySecond() {
        tick += 1
        if options.sound { NSSound(named: "Tink")?.play() }
        if let path = options.local { self.local?.reload(from: path) }
        if let gamelog, tick % 2 == 0 {
            let damage = Int.random(in: 20...180)
            let outgoing = tick % 4 == 0
            let line = outgoing
                ? "[ \(timestamp()) ] (combat) <color=0xff00ffff><b>\(damage)</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Mock Rat</b><font size=10><color=0x77ffffff> - 200mm Light 'Scout' Autocannon I - Hits\n"
                : "[ \(timestamp()) ] (combat) <color=0xffcc0000><b>\(damage / 4)</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>Mock Rat</b><font size=10><color=0x77ffffff> - Scourge Rocket - Hits\n"
            gamelog.write(Data(line.utf8))
        }
    }

    private func timestamp(_ date: Date = Date()) -> String {
        let formatter = DateFormatter()
        formatter.timeZone = TimeZone(identifier: "UTC")
        formatter.dateFormat = "yyyy.MM.dd HH:mm:ss"
        return formatter.string(from: date)
    }

    private func openGamelog() {
        let folder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Documents/EVE/logs/Gamelogs")
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let fileStamp = timestamp().replacingOccurrences(of: ".", with: "").replacingOccurrences(of: ":", with: "").replacingOccurrences(of: " ", with: "_")
        let url = folder.appendingPathComponent("\(fileStamp)_mock\(getpid()).txt")
        let header = "------------------------------------------------------------\n  Gamelog\n  Listener: \(options.name)\n  Session Started: \(timestamp())\n------------------------------------------------------------\n"
        FileManager.default.createFile(atPath: url.path, contents: Data(header.utf8))
        gamelog = try? FileHandle(forWritingTo: url)
        gamelog?.seekToEndOfFile()
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let delegate = Delegate(options: Options(CommandLine.arguments))
app.delegate = delegate
app.run()
