/* SPDX-License-Identifier: LGPL-3.0-or-later */
#ifndef IPHONE_MIRROR_CONTROL_H
#define IPHONE_MIRROR_CONTROL_H
#include <windows.h>
#include <stdint.h>
#include <wchar.h>
#define IM_CONTROL_MAX 262144
#define IM_CONTROL_OUTPUT (IM_CONTROL_MAX + 4096)
typedef uint64_t (__cdecl *im_control_create_fn)(void);
typedef void (__cdecl *im_control_destroy_fn)(uint64_t);
typedef int (__cdecl *im_control_run_fn)(uint64_t,int,const void*,int,void*,int,const void*,int);
typedef int (__cdecl *im_media_run_fn)(uint64_t,int,const void*,int,void*,int);
static im_media_run_fn im_media_run;
static im_control_create_fn im_control_create;
static im_control_destroy_fn im_control_destroy;
static im_control_run_fn im_control_run;
static INIT_ONCE im_control_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK im_control_load(PINIT_ONCE once, PVOID arg, PVOID *context)
{
    HMODULE owner = NULL, module;
    wchar_t path[32768], *slash;
    DWORD length;
    (void)once; (void)arg; (void)context;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (LPCWSTR)&im_control_load, &owner)) return TRUE;
    length = GetModuleFileNameW(owner,path,32768);
    if (!length || length >= 32700) return TRUE;
    slash = wcsrchr(path,L'\\'); if (!slash) return TRUE;
    wcscpy(slash+1,L"iPhoneMirror.AirPlayControl.dll");
    module = LoadLibraryExW(path,NULL,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) return TRUE;
    im_control_create = (im_control_create_fn)GetProcAddress(module,"IMControlCreate");
    im_control_destroy = (im_control_destroy_fn)GetProcAddress(module,"IMControlDestroy");
    im_control_run = (im_control_run_fn)GetProcAddress(module,"IMControlRun");
    im_media_run = (im_media_run_fn)GetProcAddress(module,"IMMediaRun");
    if (!im_control_create || !im_control_destroy || !im_control_run || !im_media_run) {
        im_control_create = NULL; im_control_destroy = NULL; im_control_run = NULL;
        FreeLibrary(module);
    }
    return TRUE;
}
static int im_control_available(void) {
    InitOnceExecuteOnce(&im_control_once,im_control_load,NULL,NULL);
    return im_control_run != NULL;
}
#endif
