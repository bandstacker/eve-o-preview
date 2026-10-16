// swift-tools-version:5.9
// Native macOS services for the EVE-O Preview macOS host. Built as a dynamic library with a
// flat C ABI (see Sources/EveOMacNative/Exports.swift); the .NET host calls it through LibraryImport.
import PackageDescription

let package = Package(
    name: "EveOMacNative",
    platforms: [.macOS("14.2")],
    products: [
        .library(name: "EveOMacNative", type: .dynamic, targets: ["EveOMacNative"]),
    ],
    targets: [
        .target(
            name: "EveOMacNative",
            swiftSettings: [.unsafeFlags(["-Osize"])],
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("CoreAudio"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("Vision"),
            ]),
    ]
)
