import AppKit
import Foundation

// Mock EVE client: one window titled "EVE - <name>" (or "EVE" for a login screen) with animated
// content, optional periodic sound (for background-mute checks) and optional Gamelog output in the
// EVE format parsed by the combat-log augments.
//
//   ExeFile --name "Pilot One" [--login] [--sound] [--gamelog] [--x 100 --y 100 --width 1024 --height 640]

struct Options {
    var name = "Pilot One"
    var login = false
    var sound = false
    var gamelog = false
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

final class Delegate: NSObject, NSApplicationDelegate {
    let options: Options
    var window: NSWindow!
    var scene: SceneView!
    var gamelog: FileHandle?
    var tick = 0

    init(options: Options) { self.options = options }

    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: options.frame, styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = options.login ? "EVE" : "EVE - \(options.name)"
        scene = SceneView(name: options.login ? "Login screen" : options.name)
        window.contentView = scene
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
