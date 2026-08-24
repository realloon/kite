#!/usr/bin/env python3
"""Minimal VT100/xterm emulator: replay a raw terminal byte stream into a grid,
print the final screen. Supports: DECSTBM, CUP/CUU/CUD/CUF/CUB, ED/EL, CSI
S/T region scroll, CHA, CR/LF with scroll-region scrolling, SGR(ignored),
hide/show cursor (ignored), auto-wrap."""
import sys, re


class Term:
    def __init__(self, rows=24, cols=80):
        self.rows, self.cols = rows, cols
        self.grid = [[" "] * cols for _ in range(rows)]
        self.r, self.c = 1, 1              # cursor, 1-based
        self.top, self.bottom = 1, rows    # scroll region

    def scroll_up(self, n=1):
        for _ in range(n):
            for row in range(self.top, self.bottom):
                self.grid[row - 1] = self.grid[row][:]
            self.grid[self.bottom - 1] = [" "] * self.cols

    def scroll_down(self, n=1):
        for _ in range(n):
            for row in range(self.bottom, self.top, -1):
                self.grid[row - 1] = self.grid[row - 2][:]
            self.grid[self.top - 1] = [" "] * self.cols

    def lf(self):
        if self.r == self.bottom:
            self.scroll_up()
        elif self.r < self.rows:
            self.r += 1

    def put(self, ch):
        if ch == "\r":
            self.c = 1
            return
        if ch == "\n":
            self.lf()
            return
        if ch == "\b":
            if self.c > 1:
                self.c -= 1
            return
        if ord(ch) < 32:
            return
        # write + advance with wrap
        self.grid[self.r - 1][self.c - 1] = ch
        self.c += 1
        if self.c > self.cols:
            self.c = 1
            self.lf()

    def csi(self, params: str, final: str):
        # Strip private marker prefixes (? > < =)
        params = params.lstrip("?><=")
        p = params or "1"
        nums = [int(x) if x else 1 for x in p.split(";")]

        def n(i=0, default=1):
            return nums[i] if len(nums) > i and nums[i] != 0 else default

        if final in "ABCD":
            d = n()
            if final == "A":
                self.r = max(self.top, self.r - d)
            elif final == "B":
                self.r = min(self.bottom, self.r + d)
            elif final == "C":
                self.c = min(self.cols, self.c + d)
            elif final == "D":
                self.c = max(1, self.c - d)
        elif final in "Hf":
            self.r = min(self.rows, max(1, n(0)))
            self.c = min(self.cols, max(1, n(1, 1)))
        elif final == "G":
            self.c = min(self.cols, max(1, n()))
        elif final == "J":
            mode = nums[0] if nums else 0
            if mode < 2:  # 0: to end, 1: from start
                rng = range(self.r - 1, self.rows) if mode == 0 else range(0, self.r)
                for row in rng:
                    self.grid[row] = [" "] * self.cols
            else:  # 2: whole screen
                self.grid = [[" "] * self.cols for _ in range(self.rows)]
        elif final == "K":
            mode = nums[0] if nums else 0
            if mode == 0:
                for i in range(self.c - 1, self.cols):
                    self.grid[self.r - 1][i] = " "
            elif mode == 1:
                for i in range(0, self.c - 1):
                    self.grid[self.r - 1][i] = " "
            else:
                self.grid[self.r - 1] = [" "] * self.cols
        elif final == "S":
            # No params = 1 (scroll one line); explicit params respected
            self.scroll_up(n())
        elif final == "T":
            self.scroll_down(n())
        elif final == "r":
            # DECSTBM: no params -> reset to full screen
            if not params:
                self.top, self.bottom = 1, self.rows
            else:
                self.top = max(1, nums[0])
                self.bottom = min(self.rows, nums[1] if len(nums) > 1 else self.rows)
            self.r, self.c = 1, 1
        # s / u (save/restore cursor) are ignored

    def feed(self, data: str):
        i = 0
        while i < len(data):
            ch = data[i]
            if ch == "\x1b":
                if i + 1 >= len(data):
                    break
                nxt = data[i + 1]
                if nxt == "[":
                    m = re.match(r"([0-9;?><=]*)([A-Za-z@`])", data[i + 2:])
                    if not m:
                        break
                    self.csi(m.group(1), m.group(2))
                    i += 2 + m.end()
                    continue
                elif nxt == "=":
                    i += 2  # DECPAM: ignore
                    continue
                elif nxt == "(" or nxt == ")":
                    i += 3  # charset selection: ignore
                    continue
                else:
                    i += 2  # other single-ESC sequences: ignore
                    continue
            self.put(ch)
            i += 1


def main():
    cols, rows = 80, 24
    path = sys.argv[1]
    if len(sys.argv) > 3:
        cols, rows = int(sys.argv[2]), int(sys.argv[3])
    raw = open(path, "rb").read().decode("utf-8", "replace")
    term = Term(rows, cols)
    term.feed(raw)
    print(f"=== final screen ({cols}x{rows}) ===")
    for i, row in enumerate(term.grid, 1):
        suffix = "   <- last row (footer row)" if i == rows else ""
        print(f"{i:2}|{''.join(row)}{suffix}")


if __name__ == "__main__":
    main()