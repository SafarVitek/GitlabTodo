// Renders the app icon into App/Assets.xcassets/AppIcon.appiconset.
// Usage: swift scripts/make-icon.swift
import AppKit
import SwiftUI

struct Icon: View {
    var body: some View {
        let accent = Color(red: 0.26, green: 0.33, blue: 0.93)
        ZStack {
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .fill(LinearGradient(
                    colors: [Color(red: 0.42, green: 0.49, blue: 1.0), Color(red: 0.2, green: 0.24, blue: 0.78)],
                    startPoint: .top,
                    endPoint: .bottom))
                .frame(width: 824, height: 824)
                .shadow(color: .black.opacity(0.25), radius: 18, y: 10)
            VStack(alignment: .leading, spacing: 64) {
                ForEach(1...3, id: \.self) { number in
                    HStack(spacing: 46) {
                        ZStack {
                            Circle()
                                .fill(number == 1 ? Color.white : Color.white.opacity(0.28))
                            Text("\(number)")
                                .font(.system(size: 66, weight: .bold, design: .rounded))
                                .foregroundStyle(number == 1 ? accent : Color.white)
                        }
                        .frame(width: 112, height: 112)
                        RoundedRectangle(cornerRadius: 28, style: .continuous)
                            .fill(Color.white.opacity(number == 1 ? 1 : 0.5))
                            .frame(width: number == 2 ? 290 : 360, height: 56)
                    }
                }
            }
        }
        .frame(width: 1024, height: 1024)
    }
}

let directory = URL(filePath: "App/Assets.xcassets/AppIcon.appiconset", directoryHint: .isDirectory)
try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)

let master: CGImage = MainActor.assumeIsolated {
    let renderer = ImageRenderer(content: Icon())
    renderer.scale = 1
    return renderer.cgImage!
}

func png(_ image: CGImage, size: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current?.imageInterpolation = .high
    NSImage(cgImage: image, size: NSSize(width: 1024, height: 1024)).draw(in: NSRect(x: 0, y: 0, width: size, height: size))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

var images: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try png(master, size: points * scale).write(to: directory.appending(path: name))
        images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
    }
}
let contents: [String: Any] = ["images": images, "info": ["version": 1, "author": "xcode"]]
try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
    .write(to: directory.appending(path: "Contents.json"))
try #"{"info":{"version":1,"author":"xcode"}}"#.write(to: directory.deletingLastPathComponent().appending(path: "Contents.json"), atomically: true, encoding: .utf8)
print("Icon written to \(directory.path)")
