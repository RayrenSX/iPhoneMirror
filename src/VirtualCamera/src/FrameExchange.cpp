#include "FrameExchange.h"

#include "VirtualCameraShared.h"

#include <sddl.h>
#include <shlobj.h>
#include <intrin.h>

#include <algorithm>
#include <cstring>
#include <filesystem>
#include <limits>
#include <memory>
#include <string>

namespace iPhoneMirror::virtual_camera {
namespace {

struct LocalFreeDeleter {
    void operator()(void* value) const noexcept {
        if (value != nullptr) LocalFree(value);
    }
};

using LocalMemory = std::unique_ptr<void, LocalFreeDeleter>;

HRESULT win32_error(DWORD error = GetLastError()) noexcept {
    return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
}

HRESULT current_user_sid(std::wstring& value) {
    HANDLE raw_token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token))
        return win32_error();
    const auto close_token = std::unique_ptr<void, decltype(&CloseHandle)>(
        raw_token, &CloseHandle);

    DWORD bytes{};
    GetTokenInformation(raw_token, TokenUser, nullptr, 0, &bytes);
    if (GetLastError() != ERROR_INSUFFICIENT_BUFFER) return win32_error();

    std::vector<std::uint8_t> buffer(bytes);
    if (!GetTokenInformation(raw_token, TokenUser, buffer.data(), bytes, &bytes))
        return win32_error();

    const auto* token_user = reinterpret_cast<const TOKEN_USER*>(buffer.data());
    LPWSTR raw_sid{};
    if (!ConvertSidToStringSidW(token_user->User.Sid, &raw_sid))
        return win32_error();
    LocalMemory sid(raw_sid);
    value.assign(raw_sid);
    return S_OK;
}

HRESULT create_channel_security(SECURITY_ATTRIBUTES& attributes,
                                LocalMemory& descriptor) {
    std::wstring sid;
    HRESULT result = current_user_sid(sid);
    if (FAILED(result)) return result;

    // Frame Server runs as a Windows service outside the publisher process.
    // Do not grant ALL APPLICATION PACKAGES: that would let an unrelated
    // AppContainer discover and map the live frame file without camera consent.
    const std::wstring sddl =
        L"D:P(A;;GA;;;SY)(A;;GR;;;LS)(A;;GA;;;" + sid + L")";
    PSECURITY_DESCRIPTOR raw_descriptor{};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.c_str(), SDDL_REVISION_1, &raw_descriptor, nullptr))
        return win32_error();
    descriptor.reset(raw_descriptor);
    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = raw_descriptor;
    attributes.bInheritHandle = FALSE;
    return S_OK;
}

HRESULT create_backing_file(HANDLE& file, std::wstring& path,
                            SECURITY_ATTRIBUTES* attributes) {
    PWSTR raw_public_documents{};
    HRESULT result = SHGetKnownFolderPath(
        FOLDERID_PublicDocuments, KF_FLAG_DEFAULT, nullptr,
        &raw_public_documents);
    if (FAILED(result)) return result;
    const auto release_path = std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)>(
        raw_public_documents, &CoTaskMemFree);

    std::filesystem::path directory =
        std::filesystem::path(raw_public_documents) / L"iPhoneMirror" /
        L"FrameChannels";
    std::error_code directory_error;
    std::filesystem::create_directories(directory, directory_error);
    if (directory_error)
        return HRESULT_FROM_WIN32(static_cast<DWORD>(directory_error.value()));

    GUID identifier{};
    if (FAILED(result = CoCreateGuid(&identifier))) return result;
    wchar_t identifier_text[40]{};
    if (StringFromGUID2(identifier, identifier_text,
                        static_cast<int>(std::size(identifier_text))) == 0)
        return E_UNEXPECTED;
    path = (directory / (std::wstring(L"imv-") + identifier_text +
                         L".frame")).native();

    file = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                       FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                       attributes, CREATE_NEW,
                       FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE,
                       nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        result = win32_error();
        path.clear();
        return result;
    }

    const auto mapping_bytes = static_cast<LONGLONG>(
        sizeof(SharedFrameHeader) + MaximumFrameBytes);
    LARGE_INTEGER size{};
    size.QuadPart = mapping_bytes;
    if (!SetFilePointerEx(file, size, nullptr, FILE_BEGIN) || !SetEndOfFile(file)) {
        result = win32_error();
        CloseHandle(file);
        file = INVALID_HANDLE_VALUE;
        path.clear();
        return result;
    }
    return S_OK;
}

bool valid_header(const SharedFrameHeader& header) noexcept {
    if (header.magic != FrameMagic || header.version != FrameVersion ||
        header.header_size != sizeof(SharedFrameHeader) ||
        header.pixel_format != FramePixelFormatBgra8 ||
        header.width == 0 || header.width > MaximumFrameWidth ||
        header.height == 0 || header.height > MaximumFrameHeight ||
        header.stride < header.width * 4U)
        return false;
    const auto bytes = static_cast<std::uint64_t>(header.stride) * header.height;
    return bytes == header.payload_size && bytes <= MaximumFrameBytes;
}

constexpr std::uint64_t FrameMappingBytes =
    sizeof(SharedFrameHeader) + MaximumFrameBytes;

class FileRangeLock {
public:
    FileRangeLock(HANDLE file, bool exclusive) noexcept : file_(file) {
        const DWORD flags = exclusive ? LOCKFILE_EXCLUSIVE_LOCK : 0;
        if (LockFileEx(file_, flags, 0,
                       static_cast<DWORD>(FrameMappingBytes & 0xffffffffU),
                       static_cast<DWORD>(FrameMappingBytes >> 32U),
                       &overlapped_)) {
            locked_ = true;
        } else {
            error_ = GetLastError();
        }
    }

    FileRangeLock(const FileRangeLock&) = delete;
    FileRangeLock& operator=(const FileRangeLock&) = delete;

    ~FileRangeLock() {
        if (locked_) {
            UnlockFileEx(file_, 0,
                         static_cast<DWORD>(FrameMappingBytes & 0xffffffffU),
                         static_cast<DWORD>(FrameMappingBytes >> 32U),
                         &overlapped_);
        }
    }

    [[nodiscard]] bool locked() const noexcept { return locked_; }
    [[nodiscard]] DWORD error() const noexcept { return error_; }

private:
    HANDLE file_{INVALID_HANDLE_VALUE};
    OVERLAPPED overlapped_{};
    DWORD error_{ERROR_SUCCESS};
    bool locked_{};
};

} // namespace

FramePublisher::~FramePublisher() { close(); }

HRESULT FramePublisher::open_for_current_user() {
    close();

    SECURITY_ATTRIBUTES attributes{};
    LocalMemory descriptor;
    HRESULT result = create_channel_security(attributes, descriptor);
    if (FAILED(result)) {
        close();
        return result;
    }

    result = create_backing_file(backing_file_, backing_path_, &attributes);
    if (FAILED(result)) {
        close();
        return result;
    }

    mapping_ = CreateFileMappingW(
        backing_file_, nullptr, PAGE_READWRITE,
        static_cast<DWORD>(FrameMappingBytes >> 32U),
        static_cast<DWORD>(FrameMappingBytes & 0xffffffffU), nullptr);
    if (mapping_ == nullptr) {
        result = win32_error();
        close();
        return result;
    }
    view_ = static_cast<SharedFrameHeader*>(MapViewOfFile(
        mapping_, FILE_MAP_ALL_ACCESS, 0, 0,
        static_cast<SIZE_T>(FrameMappingBytes)));
    if (view_ == nullptr) {
        result = win32_error();
        close();
        return result;
    }

    std::memset(view_, 0, sizeof(*view_));
    view_->magic = FrameMagic;
    view_->version = FrameVersion;
    view_->header_size = static_cast<std::uint16_t>(sizeof(SharedFrameHeader));
    view_->pixel_format = FramePixelFormatBgra8;
    channel_worker_ = std::jthread(
        [this](std::stop_token token) { serve_channel_path(token); });
    return S_OK;
}

void FramePublisher::serve_channel_path(std::stop_token stop_token) const noexcept {
    while (!stop_token.stop_requested()) {
        SECURITY_ATTRIBUTES attributes{};
        LocalMemory descriptor;
        if (FAILED(create_channel_security(attributes, descriptor))) return;
        HANDLE pipe = CreateNamedPipeW(
            FrameChannelPipeName,
            PIPE_ACCESS_OUTBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 1024, 1024, 0, &attributes);
        if (pipe == INVALID_HANDLE_VALUE) return;
        const BOOL connected = ConnectNamedPipe(pipe, nullptr) ||
            GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected && !stop_token.stop_requested()) {
            const auto bytes = static_cast<DWORD>(
                (backing_path_.size() + 1U) * sizeof(wchar_t));
            DWORD written{};
            WriteFile(pipe, backing_path_.c_str(), bytes, &written, nullptr);
            FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe);
        }
        CloseHandle(pipe);
    }
}

HRESULT FramePublisher::publish(const std::uint8_t* pixels,
                                std::uint32_t width,
                                std::uint32_t height,
                                std::uint32_t stride,
                                std::int64_t timestamp_100ns) {
    if (view_ == nullptr) return CO_E_NOTINITIALIZED;
    if (pixels == nullptr || width == 0 || width > MaximumFrameWidth ||
        height == 0 || height > MaximumFrameHeight ||
        width > std::numeric_limits<std::uint32_t>::max() / 4U ||
        stride < width * 4U)
        return E_INVALIDARG;

    const auto payload_size = static_cast<std::uint64_t>(stride) * height;
    if (payload_size > MaximumFrameBytes ||
        payload_size > std::numeric_limits<std::uint32_t>::max())
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);

    FileRangeLock file_lock(backing_file_, true);
    if (!file_lock.locked()) return win32_error(file_lock.error());

    auto* sequence = reinterpret_cast<volatile LONG64*>(&view_->sequence);
    InterlockedIncrement64(sequence); // odd: writer owns the payload
    view_->width = width;
    view_->height = height;
    view_->stride = stride;
    view_->pixel_format = FramePixelFormatBgra8;
    view_->timestamp_100ns = timestamp_100ns;
    view_->payload_size = static_cast<std::uint32_t>(payload_size);
    ++view_->published_frames;
    auto* destination = reinterpret_cast<std::uint8_t*>(view_ + 1);
    std::memcpy(destination, pixels, static_cast<std::size_t>(payload_size));
    MemoryBarrier();
    InterlockedIncrement64(sequence); // even: stable snapshot
    return S_OK;
}

void FramePublisher::close() noexcept {
    if (channel_worker_.joinable()) {
        channel_worker_.request_stop();
        const HANDLE worker = channel_worker_.native_handle();
        while (WaitForSingleObject(worker, 10) == WAIT_TIMEOUT) {
            // Cover both sides of the race: cancel an already-blocked
            // ConnectNamedPipe, then wake a pipe created just after cancellation.
            CancelSynchronousIo(worker);
            HANDLE wake = CreateFileW(FrameChannelPipeName, GENERIC_READ, 0,
                nullptr, OPEN_EXISTING, 0, nullptr);
            if (wake != INVALID_HANDLE_VALUE) CloseHandle(wake);
        }
        channel_worker_.join();
    }
    if (view_ != nullptr) {
        UnmapViewOfFile(view_);
        view_ = nullptr;
    }
    if (mapping_ != nullptr) {
        CloseHandle(mapping_);
        mapping_ = nullptr;
    }
    if (backing_file_ != INVALID_HANDLE_VALUE) {
        CloseHandle(backing_file_);
        backing_file_ = INVALID_HANDLE_VALUE;
    }
    if (!backing_path_.empty()) {
        DeleteFileW(backing_path_.c_str());
        backing_path_.clear();
    }
}

std::uint64_t FramePublisher::published_frames() const noexcept {
    return view_ == nullptr ? 0 : view_->published_frames;
}

std::uint32_t FramePublisher::published_width() const noexcept {
    return view_ == nullptr ? 0 : view_->width;
}

std::uint32_t FramePublisher::published_height() const noexcept {
    return view_ == nullptr ? 0 : view_->height;
}

FrameReader::~FrameReader() { close(); }

HRESULT FrameReader::open(const wchar_t* channel_path) {
    close();
    if (channel_path == nullptr || *channel_path == L'\0') return E_INVALIDARG;
    backing_file_ = CreateFileW(
        channel_path, GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (backing_file_ == INVALID_HANDLE_VALUE) return win32_error();

    mapping_ = CreateFileMappingW(backing_file_, nullptr, PAGE_READONLY,
                                  0, 0, nullptr);
    if (mapping_ == nullptr) {
        const HRESULT result = win32_error();
        close();
        return result;
    }

    view_ = static_cast<const SharedFrameHeader*>(
        MapViewOfFile(mapping_, FILE_MAP_READ, 0, 0,
                      static_cast<SIZE_T>(FrameMappingBytes)));
    if (view_ == nullptr) {
        const HRESULT result = win32_error();
        close();
        return result;
    }
    return S_OK;
}

bool FrameReader::read(FrameSnapshot& snapshot) const {
    if (view_ == nullptr) return false;
    FileRangeLock file_lock(backing_file_, false);
    if (!file_lock.locked()) return false;

    const auto* sequence =
        reinterpret_cast<const volatile __int64*>(&view_->sequence);
    for (int attempt = 0; attempt < 4; ++attempt) {
        const LONG64 before = __iso_volatile_load64(sequence);
        if ((before & 1) != 0) {
            YieldProcessor();
            continue;
        }

        SharedFrameHeader header{};
        std::memcpy(&header, view_, sizeof(header));
        if (!valid_header(header)) return false;

        try {
            snapshot.pixels.resize(header.payload_size);
        } catch (...) {
            return false;
        }
        std::memcpy(snapshot.pixels.data(), view_ + 1, header.payload_size);
        MemoryBarrier();
        const LONG64 after = __iso_volatile_load64(sequence);
        if (before == after && (after & 1) == 0) {
            snapshot.width = header.width;
            snapshot.height = header.height;
            snapshot.stride = header.stride;
            snapshot.timestamp_100ns = header.timestamp_100ns;
            snapshot.published_frames = header.published_frames;
            return true;
        }
    }
    return false;
}

void FrameReader::close() noexcept {
    if (view_ != nullptr) {
        UnmapViewOfFile(view_);
        view_ = nullptr;
    }
    if (mapping_ != nullptr) {
        CloseHandle(mapping_);
        mapping_ = nullptr;
    }
    if (backing_file_ != INVALID_HANDLE_VALUE) {
        CloseHandle(backing_file_);
        backing_file_ = INVALID_HANDLE_VALUE;
    }
}

} // namespace iPhoneMirror::virtual_camera
