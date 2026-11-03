#pragma once

#include <cmath>
#include <cstdint>

// Bloom as the stock VideoBloom camera effect draws it (ffxBloomPlus tweens
// VideoBloom.Threshold, MasterAmount and Tint):
//   bright = luma(src) < Threshold ? 0 : src, luma weights (0.30, 0.59, 0.11)
//   out    = src + Tint * 0.5 * MasterAmount * (MediumAmount * blur_m(bright)
//                                              + LargeAmount * blur_l(bright))
// The medium/large amounts and kernel settings below are VideoBloom's
// constructor defaults; values serialized on the game's camera prefab are
// not visible in the assemblies and may differ.
namespace ee
{
inline constexpr float BloomDefaultThreshold = 0.75f;
inline constexpr float BloomDefaultIntensity = 0.5f;
inline constexpr std::uint32_t BloomDefaultTint = 0xffffffu;
inline constexpr float BloomMediumAmount = 1.0f;
inline constexpr float BloomKernelSize = 50.0f;
inline constexpr float BloomMediumKernelScale = 1.0f;
inline constexpr float BloomLumaR = 0.30f;
inline constexpr float BloomLumaG = 0.59f;
inline constexpr float BloomLumaB = 0.11f;

// VideoBloom.videoBlurGetMaxScaleFor.
inline double VideoBloomMaxBlurScale(double kernel) noexcept
{
    if (kernel < 10.0)
        return 0.1 * kernel * 1.468417;
    if (kernel < 36.3287)
        return 0.127368 * kernel + 0.194737;
    return 0.8 * std::sqrt(kernel);
}

// Gaussian standard deviation, in frame pixels, equivalent to VideoBloom's
// medium blur. VideoBloom blurs at a height of 198 px with repeated 5-tap
// passes (centre 0.182, four taps 0.2045 at (4s/3, s/3) and (s/3, -4s/3))
// whose step s starts at 1 and grows by sqrt(2), followed by a fractional
// pass. Each pass adds 0.2045 * 34/9 * s^2 of variance per axis; the
// fractional pass adds that share of its variance. This matches the spread,
// not the exact kernel shape.
inline float VideoBloomBlurSigmaPixels(std::uint32_t frame_height) noexcept
{
    if (frame_height == 0u)
        return 0.0f;
    const double scale = 198.0 / static_cast<double>(frame_height);
    const double kernel = BloomKernelSize * BloomMediumKernelScale * scale;
    if (!(kernel > 0.0))
        return 0.0f;
    const double max_scale = VideoBloomMaxBlurScale(kernel);
    const int passes = static_cast<int>(max_scale);
    const double fraction = max_scale - passes;
    const double per_pass = 0.2045 * 34.0 / 9.0;
    const double steps = std::pow(2.0, passes);
    const double variance = per_pass * ((steps - 1.0) + fraction * steps);
    return static_cast<float>(std::sqrt(variance) / scale);
}
}
