#pragma once

#include "FrameExchange.h"
#include "ModuleState.h"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <wrl.h>
#include <ks.h>
#include <ksmedia.h>
#include <ksproxy.h>

#include <condition_variable>
#include <cstdint>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace iPhoneMirror::virtual_camera {

class MediaSource;

class MediaStream final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<
              IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>,
          Microsoft::WRL::FtmBase>,
      public ModuleObject {
public:
    HRESULT RuntimeClassInitialize(MediaSource* source,
                                   const wchar_t* channel_path,
                                   std::uint32_t output_width = 0,
                                   std::uint32_t output_height = 0,
                                   std::uint32_t frame_rate = 30);

    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback,
                                IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result,
                              IMFMediaEvent** event) override;
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extended_type,
                             HRESULT status,
                             const PROPVARIANT* value) override;

    IFACEMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    IFACEMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** descriptor) override;
    IFACEMETHODIMP RequestSample(IUnknown* token) override;
    IFACEMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
    IFACEMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

    HRESULT start(IMFMediaType* media_type, bool send_event);
    HRESULT stop(bool send_event);
    HRESULT shutdown();
    HRESULT set_sample_allocator(IMFVideoSampleAllocator* allocator);
    HRESULT copy_attributes(IMFAttributes** attributes);

private:
    HRESULT check_shutdown_locked() const noexcept;
    HRESULT start_locked(IMFMediaType* media_type, bool send_event);
    HRESULT stop_locked(bool send_event);
    HRESULT allocate_sample(IMFSample** sample, IMFMediaBuffer** buffer);
    HRESULT render_frame(IMFMediaBuffer* buffer, IMFMediaType* media_type);
    void process_sample_requests(std::stop_token stop_token) noexcept;

    std::mutex mutex_;
    std::condition_variable_any sample_condition_;
    Microsoft::WRL::ComPtr<IMFMediaSource> parent_;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> event_queue_;
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<IMFStreamDescriptor> descriptor_;
    Microsoft::WRL::ComPtr<IMFMediaType> media_type_;
    Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> allocator_;
    FrameReader reader_;
    FrameSnapshot last_frame_;
    FrameSnapshot pending_frame_;
    std::vector<BYTE> rendered_frame_;
    std::deque<Microsoft::WRL::ComPtr<IUnknown>> sample_requests_;
    std::jthread sample_worker_;
    std::wstring channel_path_;
    LONGLONG frame_duration_100ns_{10'000'000LL / 30};
    LONGLONG next_sample_time_{};
    MF_STREAM_STATE state_{MF_STREAM_STATE_STOPPED};
    bool selected_{};
    bool shutdown_{};
};

class MediaSource final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<
              IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
          IMFGetService, IKsControl, IMFSampleAllocatorControl,
          Microsoft::WRL::FtmBase>,
      public ModuleObject {
public:
    HRESULT RuntimeClassInitialize(IMFAttributes* activation_attributes);

    IFACEMETHODIMP BeginGetEvent(IMFAsyncCallback* callback,
                                IUnknown* state) override;
    IFACEMETHODIMP EndGetEvent(IMFAsyncResult* result,
                              IMFMediaEvent** event) override;
    IFACEMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
    IFACEMETHODIMP QueueEvent(MediaEventType type, REFGUID extended_type,
                             HRESULT status,
                             const PROPVARIANT* value) override;

    IFACEMETHODIMP GetCharacteristics(DWORD* characteristics) override;
    IFACEMETHODIMP CreatePresentationDescriptor(
        IMFPresentationDescriptor** descriptor) override;
    IFACEMETHODIMP Start(IMFPresentationDescriptor* descriptor,
                        const GUID* time_format,
                        const PROPVARIANT* start_position) override;
    IFACEMETHODIMP Stop() override;
    IFACEMETHODIMP Pause() override;
    IFACEMETHODIMP Shutdown() override;

    IFACEMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
    IFACEMETHODIMP GetStreamAttributes(DWORD stream_identifier,
                                      IMFAttributes** attributes) override;
    IFACEMETHODIMP SetD3DManager(IUnknown* manager) override;

    IFACEMETHODIMP GetService(REFGUID service, REFIID riid,
                             void** object) override;
    IFACEMETHODIMP KsProperty(PKSPROPERTY property, ULONG property_length,
                             void* data, ULONG data_length,
                             ULONG* bytes_returned) override;
    IFACEMETHODIMP KsMethod(PKSMETHOD method, ULONG method_length,
                           void* data, ULONG data_length,
                           ULONG* bytes_returned) override;
    IFACEMETHODIMP KsEvent(PKSEVENT event, ULONG event_length,
                          void* data, ULONG data_length,
                          ULONG* bytes_returned) override;

    IFACEMETHODIMP SetDefaultAllocator(DWORD output_stream_id,
                                      IUnknown* allocator) override;
    IFACEMETHODIMP GetAllocatorUsage(DWORD output_stream_id,
                                    DWORD* input_stream_id,
                                    MFSampleAllocatorUsage* usage) override;

private:
    enum class State { stopped, started, shutdown };

    HRESULT check_shutdown_locked() const noexcept;

    std::mutex mutex_;
    Microsoft::WRL::ComPtr<IMFMediaEventQueue> event_queue_;
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<IMFPresentationDescriptor> descriptor_;
    Microsoft::WRL::ComPtr<MediaStream> stream_;
    State state_{State::stopped};
    bool announced_stream_{};
};

} // namespace iPhoneMirror::virtual_camera
