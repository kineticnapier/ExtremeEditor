#include "extreme_editor_renderer.h"

#include <iostream>

int main()
{
    if (ee_renderer_get_api_version() != EE_RENDERER_API_VERSION)
    {
        std::cerr << "API version mismatch.\n";
        return 1;
    }

    EeAbiInfo info{};
    info.struct_size = sizeof(EeAbiInfo);
    if (ee_renderer_get_abi_info(&info) != EE_OK)
    {
        std::cerr << "ABI info query failed.\n";
        return 1;
    }

    if (info.api_version != EE_RENDERER_API_VERSION)
    {
        std::cerr << "ABI info reported wrong API version.\n";
        return 1;
    }

    EeAbiInfo bad{};
    if (ee_renderer_get_abi_info(&bad) != EE_ERROR_ABI_MISMATCH)
    {
        std::cerr << "ABI size mismatch was not rejected.\n";
        return 1;
    }

    std::cout << "PASS: native renderer ABI smoke test.\n";
    return 0;
}
