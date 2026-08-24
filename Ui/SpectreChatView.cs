using System.Diagnostics;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kite.Ui;

/// <summary>
/// Spectre-based chat view (append-only stream + absolute bands).
/// Invariants:
/// 1) committed transcript is never repainted — only appended with explicit
///    CRLF; terminal scrollback holds history, so Cmd+K cannot resurrect it;
/// 2) only the draft/band rows are ever repainted, using absolute coordinates
///    plus a row model (_cursorRow); no cross-frame relative accumulation
///    (CUU/CUD get clamped at the bottom and drift);
/// 3) all cursor-sensitive output uses explicit \r\n, never ONLCR.
/// </summary>
public sealed class SpectreChatView(string? modelLabel = null) : IChatView {
    private readonly InputLine _input = new(newlineOnEnter: false);
    private readonly StringBuilder _paragraph = new();
    private string _footerText = modelLabel ?? "fake-agent";

    private StreamingPacer? _pacer;
    private Task? _drainTask;
    private CancellationTokenSource? _turnCts;
    private Stopwatch? _turnElapsed;
    private bool _lastWasBlank;
    private bool _streaming; // streaming: DECSTBM active, band (input+footer) floats
    private int _cursorRow = 1; // content row model (writer thread only)
    private int _bandInput = 2; // streaming band's placeholder row (floats, pins to bottom)

    private const char ToolMarker = '\x01';

    /// <inheritdoc/>
    public CancellationToken TurnCancellationToken => _turnCts?.Token ?? CancellationToken.None;

    /// <inheritdoc/>
    public void ShowWelcome() {
        Console.Out.Write("\e[H\e[2J");
        _lastWasBlank = false;
        _cursorRow = 1;

        WriteLine("  kite", dim: true);
        WriteBlank();
    }

    /// <inheritdoc/>
    public void StartAssistantTurn() {
        _pacer = new StreamingPacer();
        _paragraph.Clear();
        _lastWasBlank = false;
        _turnCts = new CancellationTokenSource();
        _turnElapsed = Stopwatch.StartNew();

        // Single writer: the drain loop writes from a background thread; the main
        // flow only enqueues; the Esc watcher only reads keys, never writes
        _drainTask = Task.Run(() => _pacer.DrainAsync(EmitAsync, _turnCts.Token), TurnCancellationToken);
        _ = WatchInterruptAsync();
    }

    /// <inheritdoc/>
    public void AppendAssistantChunk(string chunk) => _pacer!.Enqueue(chunk);

    /// <inheritdoc/>
    public void AppendToolLine(string line) => _pacer!.Enqueue($"{ToolMarker}{line}{ToolMarker}");

    /// <inheritdoc/>
    public async Task<TurnMeta> EndAssistantTurnAsync(bool interrupted, int promptTokens, int completionTokens) {
        if (interrupted) {
            await _turnCts!.CancelAsync();
        } else {
            _pacer!.Finish();
        }

        if (_drainTask is not null) {
            try {
                await _drainTask;
            } catch (OperationCanceledException) {
                // Interrupt path: expected
            }
        }

        CommitParagraph(withBlank: true);

        // Leave streaming layout: clear the band (reply gap + placeholder + gap +
        // footer), reset the scroll region (cursor to home is standard behavior),
        // and return to the content end per the row model — no stale band fragments
        var rows = Math.Max(12, Console.WindowHeight);
        await Console.Out.WriteAsync(
            $"\e[{Math.Max(1, _bandInput - 1)};1H\e[2K\e[{_bandInput};1H\e[2K\e[{_bandInput + 1};1H\e[2K\e[{Math.Min(_bandInput + 2, rows)};1H\e[2K");
        await Console.Out.WriteAsync($"\e[r\e[{_cursorRow};1H");
        _streaming = false;

        await _turnCts?.CancelAsync()!;
        _turnCts = null;

        var meta = new TurnMeta(
            _turnElapsed!.Elapsed,
            promptTokens,
            completionTokens,
            interrupted);

        var line = interrupted ? $"interrupted — {meta}" : meta.ToString();
        WriteLine($"{ReplyIndent}{line}", dim: true);
        WriteBlank();

        return meta;
    }

    /// <inheritdoc/>
    public void WriteError(string message) {
        WriteLine($"错误：{message}", dim: true);
        WriteBlank();
    }

    /// <inheritdoc/>
    public void WriteInfo(string message) {
        WriteLine(message, dim: true);
        WriteBlank();
    }

    /// <inheritdoc/>
    public async Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
        await ReadPromptAsync(prompt, cancellationToken, masked: true);

    /// <inheritdoc/>
    public async Task<string?> ReadTextAsync(string prompt, CancellationToken cancellationToken) =>
        await ReadPromptAsync(prompt, cancellationToken, masked: false);

    private async Task<string?> ReadPromptAsync(string prompt, CancellationToken cancellationToken, bool masked) {
        WriteInfo(prompt);
        PrepareIdleBand();
        return await _input.ReadAsync(cancellationToken, masked);
    }

    /// <inheritdoc/>
    public void SetModelName(string modelName) =>
        _footerText = modelName;

    /// <inheritdoc/>
    public async Task<string?> ReadUserInputAsync(CancellationToken cancellationToken) {
        var inputRow = PrepareIdleBand();
        var text = await _input.ReadAsync(cancellationToken);

        if (text is not null && !string.IsNullOrWhiteSpace(text) && text != "/exit" &&
            !text.StartsWith('/')) {
            // Submit: enter streaming layout. The draft starts below the card row (+2,
            // +1 is the gap). The band (reply gap + placeholder + gap + footer) follows
            // the content while floating, then pins to the bottom;
            // region = 1..(bandInput-2) with top=1, so scroll exits into scrollback
            var rows = Math.Max(12, Console.WindowHeight);
            _cursorRow = Math.Min(inputRow + 2, rows - 4);
            _bandInput = Math.Min(_cursorRow + 2, rows - 2);

            if (_bandInput == rows - 2 && inputRow > _cursorRow - 2) {
                // Pinned submit: the card row collides with the band. Commit card + gap
                // into the content area by scrolling the screen up, then paint the band
                var scroll = inputRow - (_cursorRow - 2);
                await Console.Out.WriteAsync($"\e[{rows};1H{new string('\n', scroll)}");
                await Console.Out.WriteAsync($"\e[{_cursorRow};1H\e[2K"); // clear old band remnants on the draft row
                await Console.Out.WriteAsync(
                    $"\e[{_bandInput - 1};1H\e[2K"); // clear old band remnants on the reply gap
            }

            // Clear the idle footer row (2 lines below the card): with the floating
            // band it would linger on the draft row as a phantom footer mid-stream
            await Console.Out.WriteAsync($"\e[{Math.Min(inputRow + 2, rows)};1H\e[2K");

            ApplyStreamBand();
            _streaming = true;

            // Thinking hint on the draft row until the first chunk replaces it;
            // keep the cursor parked in the input box
            Console.Out.Write($"\e[{_cursorRow};1H\e[2m{ReplyIndent}Thinking…\e[0m");
            ParkStreamCursor();
        }

        return text;
    }

    /// <summary>
    /// Enter idle: make sure three band rows exist below the content (if not,
    /// scroll the whole screen from the bottom; scrolled-out history goes into
    /// terminal scrollback), repaint only input/gap/footer, and park the cursor
    /// on the input row. Returns the input row number.
    /// </summary>
    private int PrepareIdleBand() {
        var rows = Math.Max(12, Console.WindowHeight);
        var inputRow = Math.Min(_cursorRow, Math.Max(1, rows - 2));

        if (_cursorRow > rows - 2) {
            // Not enough room at the bottom: scroll the whole screen up (LF-driven,
            // content enters scrollback)
            Console.Out.Write($"\e[{rows};1H");
            for (var i = 0; i < _cursorRow - (rows - 2); i++) {
                Console.Out.Write("\n");
            }

            _cursorRow = rows;
            inputRow = rows - 2;
        }

        // Repaint only the three band rows (the writable region)
        Console.Out.Write($"\e[{inputRow};1H\e[0J");
        Console.Out.Write($"\e[{inputRow + 2};1H\e[0J{ReplyIndent}{_footerText}");
        Console.Out.Write($"\e[{inputRow};1H");
        _cursorRow = inputRow;

        return inputRow;
    }

    /// <summary>Esc during streaming cancels the turn; stops together with _turnCts after the turn.</summary>
    private async Task WatchInterruptAsync() {
        try {
            while (_turnCts is { IsCancellationRequested: false }) {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape) {
                    await _turnCts.CancelAsync();
                    return;
                }

                await Task.Delay(40, TurnCancellationToken);
            }
        } catch {
            // Ignore watcher thread exceptions
        }
    }

    /// <summary>
    /// Pacer callback: true inline streaming. Each chunk paints the trailing
    /// partial line in place on the draft row (the one rewritable row); the
    /// moment that line wraps, it is committed and the draft moves down one
    /// row. Committed rows are never repainted.
    /// </summary>
    private async Task EmitAsync(string sent) {
        if (sent.Length >= 2 && sent.StartsWith(ToolMarker)) {
            await EmitToolLineAsync(sent[1..^1]);
            return;
        }

        var hasText = false;
        foreach (var c in sent) {
            if (c == '\n') {
                CommitParagraph(withBlank: true);
                hasText = false;
            } else {
                _paragraph.Append(c);
                hasText = true;
            }
        }

        if (hasText) {
            CompactDraft();
            RenderDraft();
        }

        await Task.CompletedTask;
    }

    /// <summary>Tool action line: commit the pending draft, then a dim indented line + blank.</summary>
    private async Task EmitToolLineAsync(string line) {
        if (_paragraph.Length > 0) {
            CommitParagraph(withBlank: true);
        } else if (!_lastWasBlank) {
            WriteBlank();
            _lastWasBlank = true;
        }

        WriteLine($"{ReplyIndent}{line}", dim: true);
        WriteBlank();
        _lastWasBlank = true;
        await Task.CompletedTask;
    }

    /// <summary>Commit every wrapped line that is already full; keep the trailing partial as the draft.</summary>
    private void CompactDraft() {
        if (_paragraph.Length == 0) {
            return;
        }

        var lines = WrapAssistant.Wrap(_paragraph.ToString(), ReplyCols, ReplyIndent);
        if (lines.Count <= 1) {
            return; // still a single partial line — keep it as the live draft
        }

        for (var i = 0; i < lines.Count - 1; i++) {
            WriteLine(lines[i]);
        }

        _paragraph.Clear();
        _paragraph.Append(lines[^1]);
    }

    /// <summary>Paint the live draft line on the draft row; keep the cursor in the input box.</summary>
    private void RenderDraft() {
        if (_paragraph.Length == 0) {
            ParkStreamCursor();
            return;
        }

        EnsureDraftRow();
        var text = _paragraph.ToString();
        // Paint with the same indent as committed lines, so the draft does not
        // jump right when it wraps and becomes part of the transcript
        Console.Out.Write($"\e[{_cursorRow};1H{ReplyIndent}{text}\e[K");
        ParkStreamCursor();
    }

    /// <summary>Commit the whole paragraph (or a blank line when empty) and, optionally, a trailing blank.</summary>
    private void CommitParagraph(bool withBlank) {
        var text = _paragraph.ToString();
        _paragraph.Clear();

        if (text.Length == 0) {
            if (withBlank && !_lastWasBlank) {
                WriteBlank();
                _lastWasBlank = true;
            }

            return;
        }

        foreach (var line in WrapAssistant.Wrap(text, ReplyCols, ReplyIndent)) {
            WriteLine(line);
        }

        if (withBlank) {
            WriteBlank();
            _lastWasBlank = true;
        }
    }

    private static int ContentCols => Math.Max(20, Console.WindowWidth - 5);

    /// <summary>Reply/meta/tool/footer indent — the same width as the "┃ " input prefix.</summary>
    private const string ReplyIndent = "  ";

    private static int ReplyCols => Math.Max(18, ContentCols - ReplyIndent.Length);

    // ---------- output primitives (single output path: explicit CRLF + row model) ----------

    private void WriteLine(string text, bool dim = false) {
        if (_streaming) {
            EnsureDraftRow();

            Console.Out.Write($"{(dim ? "\e[2m" : string.Empty)}\e[{_cursorRow};1H{text}\e[K\e[0m");
            _cursorRow++;
            ParkStreamCursor();
            return;
        }

        Console.Out.Write($"{(dim ? "\e[2m" : string.Empty)}{text}\e[0m\r\n");

        // Row model cap: when overflow auto-scrolls, the real cursor stays on the
        // bottom row, so the model must match terminal semantics
        var bottom = Math.Max(12, Console.WindowHeight);
        _cursorRow = Math.Min(_cursorRow + 1, bottom);
    }

    /// <summary>
    /// Guarantee the draft row (next write position) sits above the band: float the
    /// band down one row while it still can, otherwise scroll the pinned region up
    /// (top row into scrollback). Called before both commits and draft repaints, so
    /// the reply gap / placeholder / gap / footer rows are never overwritten.
    /// </summary>
    private void EnsureDraftRow() {
        var rows = Math.Max(12, Console.WindowHeight);
        if (_cursorRow <= _bandInput - 2) {
            return;
        }

        if (_bandInput < rows - 2) {
            _bandInput++;
            ApplyStreamBand(); // region + band move down one row
        } else {
            Console.Out.Write("\e[1S"); // pinned: region scrolls up (top row into scrollback)
            _cursorRow = _bandInput - 2; // draft goes to the region bottom (above the reply gap)
        }
    }

    /// <summary>
    /// Streaming band: set region 1..bandInput-2, paint "reply gap (blank) +
    /// input placeholder + gap + footer", park the cursor at the placeholder.
    /// </summary>
    private void ApplyStreamBand() {
        var rows = Math.Max(12, Console.WindowHeight);
        Console.Out.Write($"\e[1;{Math.Max(1, _bandInput - 2)}r");
        Console.Out.Write($"\e[{Math.Max(1, _bandInput - 1)};1H\e[2K");
        Console.Out.Write($"\e[{_bandInput};1H\e[2K┃ ");
        Console.Out.Write($"\e[{_bandInput + 1};1H\e[2K");
        Console.Out.Write($"\e[{Math.Min(_bandInput + 2, rows)};1H\e[2K{ReplyIndent}{_footerText}");
        ParkStreamCursor();
    }

    private void ParkStreamCursor() {
        Console.Out.Write($"\e[{_bandInput};3H");
    }

    private void WriteBlank() => WriteLine(string.Empty);
}