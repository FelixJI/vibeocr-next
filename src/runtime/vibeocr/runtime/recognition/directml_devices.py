"""DXGI adapter ordinals for DirectML; CUDA ordinals are a separate namespace."""

from __future__ import annotations

import ctypes
import os
from uuid import UUID


def hardware_adapters() -> list[dict[str, int | str]]:
    if os.name != "nt":
        return []
    from ctypes import wintypes

    class Description(ctypes.Structure):
        _fields_ = [
            ("name", wintypes.WCHAR * 128),
            ("vendor", wintypes.UINT),
            ("device", wintypes.UINT),
            ("subsystem", wintypes.UINT),
            ("revision", wintypes.UINT),
            ("dedicated_video", ctypes.c_size_t),
            ("dedicated_system", ctypes.c_size_t),
            ("shared_system", ctypes.c_size_t),
            ("luid", ctypes.c_byte * 8),
            ("flags", wintypes.UINT),
        ]

    def method(pointer, index, result, *arguments):
        table = ctypes.cast(pointer, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p)))
        return ctypes.WINFUNCTYPE(result, ctypes.c_void_p, *arguments)(table[0][index])

    factory = ctypes.c_void_p()
    guid = (ctypes.c_byte * 16).from_buffer_copy(
        UUID("770aae78-f26f-4dba-a829-253c83d1b387").bytes_le
    )
    create = ctypes.WinDLL("dxgi").CreateDXGIFactory1
    create.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    create.restype = ctypes.c_long
    if create(ctypes.byref(guid), ctypes.byref(factory)) < 0:
        raise RuntimeError("DXGI factory unavailable")
    adapters = []
    try:
        enumerate_adapter = method(
            factory, 12, ctypes.c_long, wintypes.UINT, ctypes.POINTER(ctypes.c_void_p)
        )
        for ordinal in range(128):
            adapter = ctypes.c_void_p()
            status = enumerate_adapter(factory, ordinal, ctypes.byref(adapter))
            if status & 0xFFFFFFFF == 0x887A0002:
                break
            if status < 0:
                raise RuntimeError("DXGI adapter enumeration failed")
            try:
                description = Description()
                if (
                    method(adapter, 10, ctypes.c_long, ctypes.POINTER(Description))(
                        adapter, ctypes.byref(description)
                    )
                    < 0
                ):
                    raise RuntimeError("DXGI adapter description unavailable")
                if not description.flags & 2:
                    adapters.append(
                        {
                            "device_id": ordinal,
                            "name": description.name,
                            "vendor_id": description.vendor,
                            "dxgi_device_id": description.device,
                        }
                    )
            finally:
                method(adapter, 2, ctypes.c_ulong)(adapter)
    finally:
        method(factory, 2, ctypes.c_ulong)(factory)
    return adapters
