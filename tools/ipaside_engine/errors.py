"""The engine's expected, user-actionable failures.

Every exception here carries a message meant to be read verbatim by a person: the
app renders it in an error banner, and the CLI prints it instead of a traceback.

The distinction from an ordinary exception is deliberate. Anything *not* descended
from :class:`EngineError` is a bug rather than a situation, so it keeps its
traceback, because a stack is what you need to diagnose it and a tidy one-liner is
what you need when you simply forgot to plug the phone in.

This module imports nothing from the package so any module can raise from it.
"""

from __future__ import annotations


class EngineError(Exception):
    """A failure the user can understand and do something about.

    Subclasses exist per area (device, signing, Apple services) so callers can
    still catch narrowly; the shared base is what lets the CLI and the persistent
    ``serve`` loop treat all of them as "report the message, not the stack".
    """
