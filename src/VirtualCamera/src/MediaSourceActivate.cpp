#include "MediaSourceActivate.h"

#include "MediaSource.h"

using Microsoft::WRL::ComPtr;

namespace iPhoneMirror::virtual_camera {

HRESULT MediaSourceActivate::RuntimeClassInitialize() {
    return MFCreateAttributes(&attributes_, 8);
}

HRESULT MediaSourceActivate::ActivateObject(REFIID riid, void** object) {
    if (object == nullptr) return E_POINTER;
    *object = nullptr;

    ComPtr<MediaSource> source;
    HRESULT hr = Microsoft::WRL::MakeAndInitialize<MediaSource>(
        &source, attributes_.Get());
    if (FAILED(hr)) return hr;
    if (FAILED(hr = source->QueryInterface(riid, object))) return hr;
    source_ = source;
    return S_OK;
}

HRESULT MediaSourceActivate::ShutdownObject() {
    ComPtr<IMFMediaSource> source = source_;
    source_.Reset();
    return source == nullptr ? S_OK : source->Shutdown();
}

HRESULT MediaSourceActivate::DetachObject() {
    source_.Reset();
    return S_OK;
}

HRESULT MediaSourceActivate::GetItem(REFGUID key, PROPVARIANT* value) {
    return attributes_->GetItem(key, value);
}

HRESULT MediaSourceActivate::GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) {
    return attributes_->GetItemType(key, type);
}

HRESULT MediaSourceActivate::CompareItem(REFGUID key, REFPROPVARIANT value,
                                         BOOL* result) {
    return attributes_->CompareItem(key, value, result);
}

HRESULT MediaSourceActivate::Compare(IMFAttributes* theirs,
                                     MF_ATTRIBUTES_MATCH_TYPE match_type,
                                     BOOL* result) {
    return attributes_->Compare(theirs, match_type, result);
}

HRESULT MediaSourceActivate::GetUINT32(REFGUID key, UINT32* value) {
    return attributes_->GetUINT32(key, value);
}

HRESULT MediaSourceActivate::GetUINT64(REFGUID key, UINT64* value) {
    return attributes_->GetUINT64(key, value);
}

HRESULT MediaSourceActivate::GetDouble(REFGUID key, double* value) {
    return attributes_->GetDouble(key, value);
}

HRESULT MediaSourceActivate::GetGUID(REFGUID key, GUID* value) {
    return attributes_->GetGUID(key, value);
}

HRESULT MediaSourceActivate::GetStringLength(REFGUID key, UINT32* length) {
    return attributes_->GetStringLength(key, length);
}

HRESULT MediaSourceActivate::GetString(REFGUID key, LPWSTR value,
                                       UINT32 value_size, UINT32* length) {
    return attributes_->GetString(key, value, value_size, length);
}

HRESULT MediaSourceActivate::GetAllocatedString(REFGUID key, LPWSTR* value,
                                                UINT32* length) {
    return attributes_->GetAllocatedString(key, value, length);
}

HRESULT MediaSourceActivate::GetBlobSize(REFGUID key, UINT32* size) {
    return attributes_->GetBlobSize(key, size);
}

HRESULT MediaSourceActivate::GetBlob(REFGUID key, UINT8* buffer,
                                     UINT32 buffer_size, UINT32* size) {
    return attributes_->GetBlob(key, buffer, buffer_size, size);
}

HRESULT MediaSourceActivate::GetAllocatedBlob(REFGUID key, UINT8** buffer,
                                              UINT32* size) {
    return attributes_->GetAllocatedBlob(key, buffer, size);
}

HRESULT MediaSourceActivate::GetUnknown(REFGUID key, REFIID riid,
                                        void** object) {
    return attributes_->GetUnknown(key, riid, object);
}

HRESULT MediaSourceActivate::SetItem(REFGUID key, REFPROPVARIANT value) {
    return attributes_->SetItem(key, value);
}

HRESULT MediaSourceActivate::DeleteItem(REFGUID key) {
    return attributes_->DeleteItem(key);
}

HRESULT MediaSourceActivate::DeleteAllItems() {
    return attributes_->DeleteAllItems();
}

HRESULT MediaSourceActivate::SetUINT32(REFGUID key, UINT32 value) {
    return attributes_->SetUINT32(key, value);
}

HRESULT MediaSourceActivate::SetUINT64(REFGUID key, UINT64 value) {
    return attributes_->SetUINT64(key, value);
}

HRESULT MediaSourceActivate::SetDouble(REFGUID key, double value) {
    return attributes_->SetDouble(key, value);
}

HRESULT MediaSourceActivate::SetGUID(REFGUID key, REFGUID value) {
    return attributes_->SetGUID(key, value);
}

HRESULT MediaSourceActivate::SetString(REFGUID key, LPCWSTR value) {
    return attributes_->SetString(key, value);
}

HRESULT MediaSourceActivate::SetBlob(REFGUID key, const UINT8* buffer,
                                     UINT32 buffer_size) {
    return attributes_->SetBlob(key, buffer, buffer_size);
}

HRESULT MediaSourceActivate::SetUnknown(REFGUID key, IUnknown* object) {
    return attributes_->SetUnknown(key, object);
}

HRESULT MediaSourceActivate::LockStore() { return attributes_->LockStore(); }

HRESULT MediaSourceActivate::UnlockStore() { return attributes_->UnlockStore(); }

HRESULT MediaSourceActivate::GetCount(UINT32* count) {
    return attributes_->GetCount(count);
}

HRESULT MediaSourceActivate::GetItemByIndex(UINT32 index, GUID* key,
                                            PROPVARIANT* value) {
    return attributes_->GetItemByIndex(index, key, value);
}

HRESULT MediaSourceActivate::CopyAllItems(IMFAttributes* destination) {
    return attributes_->CopyAllItems(destination);
}

} // namespace iPhoneMirror::virtual_camera
