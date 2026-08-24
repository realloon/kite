using System.Text;

namespace Kite.Ui;

/// <summary>
/// fx-style streaming pacer: adapts chars/sec to the backlog (400–5000 cps),
/// draining the backlog in ~1.5s; after Finish() it drains at a fast 200ms target.
/// </summary>
public sealed class StreamingPacer {
    private const int MinCps = 400;
    private const int MaxCps = 5000;
    private const int DrainTargetMs = 1500;
    private const int FinishTargetMs = 200;
    private const int TickMs = 16;

    private readonly StringBuilder _buffer = new();
    private readonly Lock _lock = new();
    private bool _finished;
    private int _emitted;

    public int EmittedChars => _emitted;

    public void Enqueue(string chunk) {
        lock (_lock) {
            _buffer.Append(chunk);
        }
    }

    /// <summary>Mark the end of the stream: drain the remaining backlog fast.</summary>
    public void Finish() {
        lock (_lock) {
            _finished = true;
        }
    }

    public async Task DrainAsync(Func<string, Task> emit, CancellationToken cancellationToken) {
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();

            int backlog;
            bool finished;
            lock (_lock) {
                backlog = _buffer.Length;
                finished = _finished;
            }

            if (backlog == 0 && finished) {
                return;
            }

            var targetMs = finished && backlog > 0 ? FinishTargetMs : DrainTargetMs;
            var cps = Math.Clamp(backlog * 1000 / Math.Max(targetMs, 1), MinCps, MaxCps);
            var charsThisTick = Math.Max(1, cps * TickMs / 1000);

            var sent = DequeueUpTo(charsThisTick);
            if (sent.Length > 0) {
                _emitted += sent.Length;
                await emit(sent);
            } else {
                await Task.Delay(TickMs, cancellationToken);
            }
        }
    }

    private string DequeueUpTo(int maxChars) {
        lock (_lock) {
            if (_buffer.Length == 0) {
                return string.Empty;
            }

            var take = Math.Min(maxChars, _buffer.Length);

            // Tool action lines are wrapped in \x01...\x01: take them atomically
            // so the char budget never splits a marker chunk. A complete chunk is
            // enqueued in one call, so a missing closing marker cannot stall.
            if (_buffer[0] == '\x01') {
                var end = _buffer.ToString().IndexOf('\x01', 1);
                if (end < 0) {
                    return string.Empty;
                }

                take = end + 1;
            }

            var sent = _buffer.ToString(0, take);
            _buffer.Remove(0, take);
            return sent;
        }
    }
}