#!/usr/bin/env python3
"""PTY harness: emulate an XTerm answering .NET/Spectre console probes,
inject keystrokes by schedule, capture output. For kite verification."""
import os, pty, select, sys, time, signal, shlex, fcntl, struct, termios, tty

BIN = sys.argv[1]
OUT = sys.argv[2]
ARGS = shlex.split(BIN)
# schedule: list of (seconds, label, bytes)
SCHEDULE = [
    (1.5, "input1", b"hello kitty\r"),
    (7.0, "exit", b"/exit\r"),
]

if len(sys.argv) > 3:
    SCHEDULE = eval(sys.argv[3])  # allow custom schedule from CLI

pid, fd = pty.fork()
if pid == 0:
    fcntl.ioctl(0, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    tty.setraw(0)  # no echo / line buffering: capture holds app output only, no key echoes
    os.environ["TERM"] = "xterm-256color"
    os.execvp(ARGS[0], ARGS)

out = open(OUT, "wb")
start = time.time()
sent = {lbl: False for _, lbl, _ in SCHEDULE}
buf = b""
last_answer = {}  # pattern -> last response time (dedup, avoid stray bytes)

def answer(pat, resp, key, min_gap=2.0):
    now = time.time()
    if key in last_answer and now - last_answer[key] < min_gap:
        return
    if pat in data:
        last_answer[key] = now
        os.write(fd, resp)

while time.time() - start < 30:
    # Detect child exit (proves input was read and the app quits promptly)
    try:
        wpid, wstatus = os.waitpid(pid, os.WNOHANG)
        if wpid != 0:
            print(f"child exited at {time.time() - start:.2f}s status={os.waitstatus_to_exitcode(wstatus)}")
            break
    except ChildProcessError:
        break

    r, _, _ = select.select([fd], [], [], 0.05)
    if fd in r:
        try:
            data = os.read(fd, 65536)
        except OSError:
            break
        if not data:
            break
        buf += data
        out.write(data)
        answer(b"\x1b[6n", b"\x1b[1;1R", "cpr")
        answer(b"\x1b[c", b"\x1b[?1;2c", "da1")
        answer(b"\x1b[>c", b"\x1b[>0;276;0c", "da2")
        answer(b"\x1b[18t", b"\x1b[8;30;100t", "size")
    t = time.time() - start
    for sec, lbl, payload in SCHEDULE:
        if t >= sec and not sent[lbl]:
            os.write(fd, payload)
            sent[lbl] = True

try:
    os.kill(pid, signal.SIGKILL)
except ProcessLookupError:
    pass
# The EOF path can beat waitpid; reap once more and report the real exit
try:
    wpid, wstatus = os.waitpid(pid, os.WNOHANG)
    if wpid != 0:
        print(f"child exited at {time.time() - start:.2f}s status={os.waitstatus_to_exitcode(wstatus)}")
except ChildProcessError:
    pass
out.close()
print("done; captured", len(buf), "bytes ->", OUT)