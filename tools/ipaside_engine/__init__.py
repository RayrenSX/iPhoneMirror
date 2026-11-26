"""iPASide Engine.

The Python core that performs all Apple-facing work for iPASide:
device I/O (via pymobiledevice3), Apple ID authentication, developer-services
provisioning, code signing, and installation. The Flutter desktop shell drives
this engine over a JSON protocol.

This package is intentionally usable stand-alone as a CLI:

    python -m ipaside_engine doctor
    python -m ipaside_engine devices
"""

__version__ = "1.2.17"
