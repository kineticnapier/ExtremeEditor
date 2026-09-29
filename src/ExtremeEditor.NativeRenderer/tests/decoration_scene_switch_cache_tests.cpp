#include "scene_local_cache.h"

#include <cstdio>
#include <cstdint>
#include <vector>

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

    return 0;
}
