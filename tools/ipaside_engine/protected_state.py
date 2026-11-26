"""Protect cached Apple sessions and private-key JSON with Windows user DPAPI."""
import ctypes
from ctypes import wintypes
from pathlib import Path


class _Blob(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]


def _crypt(data: bytes, protect: bool) -> bytes:
    source = ctypes.create_string_buffer(data)
    blob = _Blob(len(data), ctypes.cast(source, ctypes.POINTER(ctypes.c_ubyte)))
    result = _Blob()
    crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
    function = crypt32.CryptProtectData if protect else crypt32.CryptUnprotectData
    function.restype = wintypes.BOOL
    function.argtypes = [ctypes.POINTER(_Blob), ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                         ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(_Blob)]
    if not function(ctypes.byref(blob), None, None, None, None, 1, ctypes.byref(result)):
        raise OSError(ctypes.get_last_error(), "Windows could not protect signing state.")
    try:
        return ctypes.string_at(result.data, result.size)
    finally:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.LocalFree.argtypes = [ctypes.c_void_p]
        kernel32.LocalFree.restype = ctypes.c_void_p
        kernel32.LocalFree(result.data)


class ProtectedStatePath(type(Path())):
    def write_text(self, data, encoding=None, errors=None, newline=None):
        if self.suffix != ".json":
            return super().write_text(data, encoding=encoding, errors=errors, newline=newline)
        payload = b"IMDPAPI1\0" + _crypt(data.encode(encoding or "utf-8", errors or "strict"), True)
        temporary = Path(str(self) + ".new")
        temporary.write_bytes(payload)
        temporary.replace(self)
        return len(data)

    def read_text(self, encoding=None, errors=None):
        if self.suffix != ".json":
            return super().read_text(encoding=encoding, errors=errors)
        data = self.read_bytes()
        if not data.startswith(b"IMDPAPI1\0"):
            raise ValueError("Invalid protected signing state.")
        return _crypt(data[9:], False).decode(encoding or "utf-8", errors or "strict")
