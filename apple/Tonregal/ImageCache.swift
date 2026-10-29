// SPDX-License-Identifier: AGPL-3.0-or-later
import SwiftUI
import UIKit

/// Cover im Speicher, verkleinert; Alben ohne Cover (404) werden gemerkt, damit das Raster sie nicht immer wieder
/// anfragt.
@MainActor
final class ImageCache {
    static let shared = ImageCache()

    private let cache = NSCache<NSURL, UIImage>()
    private var missing: Set<URL> = []
    private var loading: [URL: Task<UIImage?, Never>] = [:]

    func cached(_ url: URL) -> UIImage? {
        cache.object(forKey: url as NSURL)
    }

    func image(for url: URL) async -> UIImage? {
        if let image = cached(url) { return image }
        if missing.contains(url) { return nil }
        if let task = loading[url] { return await task.value }
        let task = Task<UIImage?, Never> {
            guard let (data, response) = try? await URLSession.shared.data(from: url),
                  (response as? HTTPURLResponse)?.statusCode == 200
            else { return nil }
            return await Task.detached(priority: .utility) {
                UIImage(data: data)?.preparingThumbnail(of: CGSize(width: 800, height: 800))
            }.value
        }
        loading[url] = task
        let image = await task.value
        loading[url] = nil
        if let image {
            cache.setObject(image, forKey: url as NSURL)
        } else {
            missing.insert(url)
        }
        return image
    }

    func forgetMissing() {
        missing.removeAll()
    }
}

struct CoverImage: View {
    let url: URL?
    var cornerRadius: CGFloat = 8

    @State private var image: UIImage?

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFill()
                } else {
                    ZStack {
                        Rectangle().fill(.quaternary)
                        Image(systemName: "opticaldisc")
                            .font(.system(size: 28, weight: .light))
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .task(id: url) {
                image = url.flatMap { ImageCache.shared.cached($0) }
                if image == nil, let url {
                    image = await ImageCache.shared.image(for: url)
                }
            }
    }
}
