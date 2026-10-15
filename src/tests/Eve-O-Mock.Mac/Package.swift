// swift-tools-version:5.9
// Stand-in EVE client for exercising the macOS host without the game. The executable is named
// ExeFile so discovery classifies it as an EVE client. See README.md.
import PackageDescription

let package = Package(
    name: "EveOMockMac",
    platforms: [.macOS("14.2")],
    products: [.executable(name: "ExeFile", targets: ["EveMockClient"])],
    targets: [.executableTarget(name: "EveMockClient", linkerSettings: [.linkedFramework("AppKit")])]
)
