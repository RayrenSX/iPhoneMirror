#pragma once

#include <cstddef>
#include <cstdint>
#include <guiddef.h>

namespace iPhoneMirror::virtual_camera {

// {4C0D85FD-695A-491D-945B-21DDF7EEC1E2}
inline constexpr GUID MediaSourceClsid{
    0x4c0d85fd, 0x695a, 0x491d,
    {0x94, 0x5b, 0x21, 0xdd, 0xf7, 0xee, 0xc1, 0xe2}};

// {C40F3947-6E27-4D08-AFD2-226E7182A20B}
inline constexpr GUID FrameChannelPathAttribute{
    0xc40f3947, 0x6e27, 0x4d08,
    {0xaf, 0xd2, 0x22, 0x6e, 0x71, 0x82, 0xa2, 0x0b}};

// {BA1EA32F-E95E-45D4-A310-0D3E75626EED}
inline constexpr GUID OutputWidthAttribute{
    0xba1ea32f, 0xe95e, 0x45d4,
    {0xa3, 0x10, 0x0d, 0x3e, 0x75, 0x62, 0x6e, 0xed}};

// {4295EF84-572A-4AC7-8941-86EC4B884167}
inline constexpr GUID OutputHeightAttribute{
    0x4295ef84, 0x572a, 0x4ac7,
    {0x89, 0x41, 0x86, 0xec, 0x4b, 0x88, 0x41, 0x67}};

// {0E7DE723-4234-4D96-AB6C-D5C499636826}
inline constexpr GUID OutputFrameRateAttribute{
    0x0e7de723, 0x4234, 0x4d96,
    {0xab, 0x6c, 0xd5, 0xc4, 0x99, 0x63, 0x68, 0x26}};

inline constexpr wchar_t MediaSourceClsidString[] =
    L"{4C0D85FD-695A-491D-945B-21DDF7EEC1E2}";
inline constexpr wchar_t DefaultFriendlyName[] = L"iPhoneMirror Virtual Camera";
inline constexpr wchar_t FrameChannelPipeName[] =
    L"\\\\.\\pipe\\iPhoneMirror.VirtualCamera.FrameChannel.v1";

constexpr std::uint32_t FrameMagic = 0x4D564349; // ICVM
constexpr std::uint16_t FrameVersion = 1;
constexpr std::uint32_t FramePixelFormatBgra8 = 1;
constexpr std::uint32_t MaximumFrameWidth = 3840;
constexpr std::uint32_t MaximumFrameHeight = 2160;
constexpr std::size_t MaximumFrameBytes =
    static_cast<std::size_t>(MaximumFrameWidth) * MaximumFrameHeight * 4;

struct alignas(8) SharedFrameHeader {
    std::uint32_t magic;
    std::uint16_t version;
    std::uint16_t header_size;
    volatile std::int64_t sequence;
    std::uint32_t width;
    std::uint32_t height;
    std::uint32_t stride;
    std::uint32_t pixel_format;
    std::int64_t timestamp_100ns;
    std::uint32_t payload_size;
    std::uint32_t reserved;
    std::uint64_t published_frames;
};

static_assert(offsetof(SharedFrameHeader, sequence) % alignof(std::int64_t) == 0);

} // namespace iPhoneMirror::virtual_camera
