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

    IFACEMETHODIMP GetItem(REFGUID key, PROPVARIANT* value) override;
    IFACEMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override;
    IFACEMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT value,
                              BOOL* result) override;
    IFACEMETHODIMP Compare(IMFAttributes* theirs,
                          MF_ATTRIBUTES_MATCH_TYPE match_type,
                          BOOL* result) override;
    IFACEMETHODIMP GetUINT32(REFGUID key, UINT32* value) override;
    IFACEMETHODIMP GetUINT64(REFGUID key, UINT64* value) override;
    IFACEMETHODIMP GetDouble(REFGUID key, double* value) override;
    IFACEMETHODIMP GetGUID(REFGUID key, GUID* value) override;
    IFACEMETHODIMP GetStringLength(REFGUID key, UINT32* length) override;
    IFACEMETHODIMP GetString(REFGUID key, LPWSTR value, UINT32 value_size,
                            UINT32* length) override;
    IFACEMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* value,
                                     UINT32* length) override;
    IFACEMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override;
    IFACEMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 buffer_size,
                          UINT32* size) override;
    IFACEMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer,
                                   UINT32* size) override;
    IFACEMETHODIMP GetUnknown(REFGUID key, REFIID riid, void** object) override;
    IFACEMETHODIMP SetItem(REFGUID key, REFPROPVARIANT value) override;
    IFACEMETHODIMP DeleteItem(REFGUID key) override;
    IFACEMETHODIMP DeleteAllItems() override;
    IFACEMETHODIMP SetUINT32(REFGUID key, UINT32 value) override;
    IFACEMETHODIMP SetUINT64(REFGUID key, UINT64 value) override;
    IFACEMETHODIMP SetDouble(REFGUID key, double value) override;
    IFACEMETHODIMP SetGUID(REFGUID key, REFGUID value) override;
    IFACEMETHODIMP SetString(REFGUID key, LPCWSTR value) override;
    IFACEMETHODIMP SetBlob(REFGUID key, const UINT8* buffer,
                          UINT32 buffer_size) override;
    IFACEMETHODIMP SetUnknown(REFGUID key, IUnknown* object) override;
    IFACEMETHODIMP LockStore() override;
    IFACEMETHODIMP UnlockStore() override;
    IFACEMETHODIMP GetCount(UINT32* count) override;
    IFACEMETHODIMP GetItemByIndex(UINT32 index, GUID* key,
                                 PROPVARIANT* value) override;
    IFACEMETHODIMP CopyAllItems(IMFAttributes* destination) override;

private:
    Microsoft::WRL::ComPtr<IMFAttributes> attributes_;
    Microsoft::WRL::ComPtr<IMFMediaSource> source_;
};

} // namespace iPhoneMirror::virtual_camera
