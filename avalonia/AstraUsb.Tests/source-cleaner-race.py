#!/usr/bin/env python3
"""Подменяет тестовый файл точно перед Linux syscall удаления или карантина."""

import ctypes
import os
from pathlib import Path
import resource
import signal
import sys
import tempfile


class Registers(ctypes.Structure):
    _fields_ = [(name, ctypes.c_ulonglong) for name in (
        "r15 r14 r13 r12 rbp rbx r11 r10 r9 r8 rax rcx rdx rsi rdi orig_rax "
        "rip cs eflags rsp ss fs_base gs_base ds es fs gs".split())]


libc = ctypes.CDLL(None, use_errno=True)
libc.ptrace.restype = ctypes.c_long
libc.ptrace.argtypes = [ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]


def ptrace(request, child, address=0, data=0):
    ctypes.set_errno(0)
    value = libc.ptrace(request, child, address, data)
    if value == -1 and ctypes.get_errno():
        raise OSError(ctypes.get_errno(), os.strerror(ctypes.get_errno()))
    return value


def read_string(child, address):
    value = bytearray()
    while len(value) < 4096:
        chunk = ptrace(2, child, address + len(value)).to_bytes(8, "little", signed=True)
        value.extend(chunk)
        if 0 in chunk:
            return os.fsdecode(bytes(value[:value.index(0)]))
    raise RuntimeError("Слишком длинное имя syscall")


def run(runtime, assembly, scenario):
    with tempfile.TemporaryDirectory(prefix="astra-source-race-", dir="/tmp") as root:
        child = os.fork()
        if child == 0:
            resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
            ptrace(0, 0)
            os.kill(os.getpid(), signal.SIGSTOP)
            os.execv(runtime, [runtime, assembly, "source-cleaner-race", scenario, root])
        _, status = os.waitpid(child, 0)
        if not os.WIFSTOPPED(status):
            raise RuntimeError("Tracee не остановился")
        ptrace(0x4200, child, 0, 1 | 0x100000)
        entering = True
        replaced = False
        pending_recreate = False
        deliver = 0
        try:
            while True:
                ptrace(24, child, 0, deliver)
                deliver = 0
                _, status = os.waitpid(child, 0)
                if os.WIFEXITED(status):
                    if os.WEXITSTATUS(status) != 0:
                        raise RuntimeError(f"source-cleaner-{scenario}: exit {os.WEXITSTATUS(status)}")
                    break
                if os.WIFSIGNALED(status):
                    raise RuntimeError(f"Tracee завершён сигналом {os.WTERMSIG(status)}")
                if os.WSTOPSIG(status) != (signal.SIGTRAP | 0x80):
                    if os.WSTOPSIG(status) not in (signal.SIGTRAP, signal.SIGSTOP):
                        deliver = os.WSTOPSIG(status)
                    continue
                regs = Registers()
                ptrace(12, child, 0, ctypes.addressof(regs))
                if entering and not replaced and scenario != "normal" and regs.orig_rax in (263, 316):
                    if read_string(child, regs.rsi) == "clip.mp4":
                        directory = os.readlink(f"/proc/{child}/fd/{regs.rdi}")
                        if directory == root + "/source":
                            video = Path(directory) / "clip.mp4"
                            video.rename(str(video) + ".previous")
                            video.write_text("NEW")
                            replaced = True
                            pending_recreate = scenario == "recreate" and regs.orig_rax == 316
                elif not entering and pending_recreate:
                    (Path(root) / "source" / "clip.mp4").write_text("LATEST")
                    pending_recreate = False
                entering = not entering
            if scenario != "normal" and not replaced:
                raise RuntimeError("Граница syscall не достигнута")
        except BaseException:
            try:
                os.kill(child, signal.SIGKILL)
                os.waitpid(child, 0)
            except ProcessLookupError:
                pass
            raise


if __name__ == "__main__":
    if os.uname().machine != "x86_64":
        raise RuntimeError("Проверка ptrace рассчитана на Linux x86_64")
    for scenario in ("normal", "replace", "recreate"):
        run(sys.argv[1], sys.argv[2], scenario)
