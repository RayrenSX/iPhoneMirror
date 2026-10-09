"""Use the pinned USB binaries from the spec, never the build host's backend.

The upstream hook discovers libraries via PyUSB and PATH. In particular an
extra usb-1.0.dll can shadow libusb0.dll in pyi_rth_usb's broad `usb*` lookup.
Keep the runtime hook's glob dependency, but perform no native discovery.
"""

hiddenimports = ['glob']
binaries = []
