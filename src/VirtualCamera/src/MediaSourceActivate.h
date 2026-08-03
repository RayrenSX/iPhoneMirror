#pragma once

#include "ModuleState.h"

#include <mfapi.h>
#include <mfidl.h>
#include <wrl.h>

namespace iPhoneMirror::virtual_camera {

class MediaSourceActivate final
    : public Microsoft::WRL::RuntimeClass<
          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
          Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>,
          Microsoft::WRL::FtmBase>,
      public ModuleObject {
public:
    HRESULT RuntimeClassInitialize();

    IFACEMETHODIMP ActivateObject(REFIID riid, void** object) override;
    IFACEMETHODIMP ShutdownObject() override;
    IFACEMETHODIMP DetachObject() override;

    IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* result) override;
    IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override;
    IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT item,
                              BOOL* result) override;
    IFACEMETHODIMP Compare(IMFAttributes* theirs,
                          MF_ATTRIBUTES_MATCH_TYPE match_type,
                          BOOL* result) override;
    IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* result) override;
    IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* result) override;
    IFACEMETHODIMP GetDouble(REFGUID key, double* result) override;
    IFACEMETHODIMP GetGUID(REFGUID key, GUID* result) override;
    IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override;
    IFACEMETHODIMP GetString(REFGUID key, LPWSTR buffer, UINT32 buffer_size,
                            UINT32* length) override;
    IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* result,
                                     UINT32* length) override;
    IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override;
    IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 buffer_size,
                          UINT32* size) override;
    IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer,
                                   UINT32* size) override;
    IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, void** object) override;
    IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT item) override;
    IFACEMETHODIMP DeleteItem(REFGUID key) override;
    IFACEMETHODIMP DeleteAllItems() override;
    IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 item) override;
    IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 item) override;
    IFACEMETHODIMP SetDouble(REFGUID key, double item) override;
    IFACEMETHODIMP SetGUID(REFGUID key, REFGUID item) override;
    IFACEMETHODIMP SetString(REFGUID key, LPCWSTR item) override;
    IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer,
                          UINT32 buffer_size) override;
    IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* object) override;
    IFACEMETHODIMP LockStore() override;
    IFACEMETHODIMP UnlockStore() override;
    IFACEMETHODIMP GetCount(UINT32* count) override;
    IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key,
                                 PROPVARIANT* result) override;
    IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override;

private:
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<IMFMediaSource> source_;
};

} // namespace iPhoneMirror::virtual_camera
