#include "scene_local_cache.h"
#include "decoration_assets.h"

#include <cstdio>
#include <chrono>
#include <cstdint>
#include <vector>
#include <memory>

namespace
{
void Sync(
    std::vector<int>& backend_storage,
    std::uint64_t& cached_scene_generation,
    std::uint64_t& cached_version,
    std::uint64_t scene_generation,
    std::uint64_t version,
    const std::vector<int>& incoming)
{
    if (!ee::ShouldRefreshSceneLocalCache(
            cached_scene_generation,
            cached_version,
            scene_generation,
            version))
    {
        return;
    }

    backend_storage = incoming;
    cached_scene_generation = scene_generation;
    cached_version = version;
}
}

int main()
{
    std::vector<int> backend_storage;
    std::uint64_t cached_scene_generation = 0u;
    std::uint64_t cached_version = 0u;

    Sync(backend_storage, cached_scene_generation, cached_version, 1u, 1u, {});
    Sync(backend_storage, cached_scene_generation, cached_version, 2u, 1u, {42});

    if (backend_storage.size() != 1u || backend_storage[0] != 42)
    {
        std::fprintf(
            stderr,
            "FAIL: decoration cache did not refresh for a new scene with the same local version.\n");
        return 1;
    }

    // Current Encore diagnostics resolve 71 unique decoration images. Model that
    // load shape so an accidental return to O(assetCount^2 * pixelBytes) copying
    // remains structurally visible in this native regression.
    constexpr std::uint32_t asset_count = 71u;
    constexpr std::size_t bytes_per_asset = 2u * 1024u * 1024u;
    const auto asset_started = std::chrono::steady_clock::now();
    ee::DecorationAssetTable assets;
    std::vector<const std::vector<std::uint8_t>*> pixel_identities;
    pixel_identities.reserve(asset_count);
    for (std::uint32_t id = 0; id < asset_count; ++id)
    {
        auto pixels = std::make_shared<std::vector<std::uint8_t>>(bytes_per_asset, 0x7fu);
        ee::DecorationAsset asset;
        asset.width = 1024u;
        asset.height = 512u;
        asset.stride = 4096u;
        asset.pixels = pixels;

        ee::DecorationAssetTable next = assets;
        next[id] = std::move(asset);
        assets = std::move(next);
        pixel_identities.push_back(pixels.get());
    }
    const auto asset_finished = std::chrono::steady_clock::now();
    for (std::uint32_t id = 0; id < asset_count; ++id)
    {
        if (assets.at(id).pixels.get() != pixel_identities[id])
        {
            std::fprintf(
                stderr,
                "FAIL: decoration asset table snapshot recopied the pixel buffer.\n");
            return 1;
        }
    }
    std::fprintf(
        stdout,
        "[native-upload-test] decorationAssets=%u payloadMiB=%zu snapshotBuild=%.1fms sharedPixels=1\n",
        asset_count,
        asset_count * bytes_per_asset / (1024u * 1024u),
        std::chrono::duration<double, std::milli>(asset_finished - asset_started).count());

    return 0;
}
