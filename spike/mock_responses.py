#!/usr/bin/env python3
"""Mock OpenAI Responses API (DeepSeek-compatible) for kite E2E testing.

Usage: python3 spike/mock_responses.py [port] [request_log]
  - Serves POST /responses with a scripted SSE stream.
  - Authorization key selects scenario:
      Bearer fail   -> response.failed event
      Bearer denied -> HTTP 401 with JSON error body
      otherwise     -> normal stream with usage (12 in / 34 out)
  - Tool scenario: latest user message "run ls" triggers a function_call
    round; the follow-up request (carrying function_call_output) gets the
    normal text stream.
  - Request body is appended to request_log (default spike/mock_requests.txt).
"""
import json, sys, time
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
LOGPATH = sys.argv[2] if len(sys.argv) > 2 else "spike/mock_requests.txt"

DELTAS = [
    "这是",
    "来自 mock 服务器",
    "的第一句回复。",
    "\n\n第二段：",
    "模拟真实模型的分段输出。",
    "\n\n第三段：用于验证 meta 行显示真实 usage。",
]


def sse(event: str, data: dict) -> bytes:
    return f"event: {event}\ndata: {json.dumps(data, ensure_ascii=False)}\n\n".encode()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode()
        with open(LOGPATH, "a", encoding="utf-8") as f:
            f.write(f"--- {time.strftime('%H:%M:%S')} {self.path} ---\nHEADERS: {dict(self.headers)}\nBODY ({length}): {body}\n")
        auth = self.headers.get("Authorization", "")

        try:
            event = json.loads(body)
        except json.JSONDecodeError as ex:
            payload = json.dumps({"error": {"message": f"400 模拟：请求体不是合法 JSON ({ex})"}}).encode()
            self.send_response(400)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            print(f"json error on body: {body!r}", flush=True)
            return

        if auth == "Bearer denied":
            payload = json.dumps({"error": {"message": "401 模拟：Invalid API key"}}).encode()
            self.send_response(401)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            return

        rid = "mock_resp_1"
        model = event.get("model", "unknown")

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.close_connection = True
        self.end_headers()

        def emit(ev, data):
            self.wfile.write(sse(ev, data))
            self.wfile.flush()
            time.sleep(0.15)

        if auth == "Bearer fail":
            emit("response.created", {"type": "response.created", "sequence_number": 0,
                                      "response": {"id": rid, "object": "response", "status": "in_progress"}})
            emit("response.output_text.delta", {"type": "response.output_text.delta", "sequence_number": 1,
                                                "item_id": "msg_1", "output_index": 0, "content_index": 0,
                                                "delta": "半途而"})
            emit("response.failed", {"type": "response.failed", "sequence_number": 2, "response": {
                "id": rid, "object": "response", "status": "failed",
                "error": {"code": "mock_boom", "message": "模拟：模型内部错误"}}})
            return

        # Tool call scenario: if the request already carries a function_call /
        # function_call_output, answer with plain text (round 2+); if the latest
        # user message is "run ls", emit a function_call round instead of text.
        inputs = event.get("input") or []
        has_call_output = any(i.get("type") == "function_call_output" for i in inputs)
        latest_user = ""
        for i in reversed(inputs):
            if i.get("type") == "message" and i.get("role") == "user":
                latest_user = i.get("content", "")
                break

        if not has_call_output and latest_user == "run ls":
            fc = {"type": "function_call", "id": "fc_1", "call_id": "fc_1",
                  "name": "run", "arguments": '{"command":"ls"}'}
            emit("response.created", {"type": "response.created", "sequence_number": 0,
                                      "response": {"id": rid, "object": "response", "status": "in_progress",
                                                   "model": model}})
            emit("response.output_item.added", {"type": "response.output_item.added", "sequence_number": 1,
                                                "output_index": 0,
                                                "item": {"type": "function_call", "id": "fc_1", "name": "run",
                                                         "arguments": ""}})
            emit("response.function_call_arguments.delta",
                 {"type": "response.function_call_arguments.delta", "sequence_number": 2,
                  "item_id": "fc_1", "output_index": 0, "delta": '{"command":"ls"}'})
            emit("response.output_item.done", {"type": "response.output_item.done", "sequence_number": 3,
                                               "output_index": 0, "item": fc})
            emit("response.completed", {"type": "response.completed", "sequence_number": 4, "response": {
                "id": rid, "object": "response", "status": "completed", "model": model,
                "output": [dict(fc)],
                "usage": {"input_tokens": 20, "input_tokens_details": {"cached_tokens": 0},
                          "output_tokens": 30, "output_tokens_details": {"reasoning_tokens": 0},
                          "total_tokens": 50}}})
            return

        emit("response.created", {"type": "response.created", "sequence_number": 0,
                                  "response": {"id": rid, "object": "response", "status": "in_progress",
                                               "model": model}})
        emit("response.output_item.added", {"type": "response.output_item.added", "sequence_number": 1,
                                            "output_index": 0, "item": {"type": "message", "id": "msg_1",
                                                                         "role": "assistant", "status": "in_progress"}})
        n = 2
        for delta in DELTAS:
            emit("response.output_text.delta", {"type": "response.output_text.delta",
                                                "sequence_number": n, "item_id": "msg_1",
                                                "output_index": 0, "content_index": 0, "delta": delta})
            n += 1
        emit("response.completed", {"type": "response.completed", "sequence_number": n, "response": {
            "id": rid, "object": "response", "status": "completed", "model": model,
            "output": [{"type": "message", "id": "msg_1", "status": "completed", "role": "assistant",
                        "content": [{"type": "output_text", "text": "".join(DELTAS)}]}],
            "usage": {"input_tokens": 12, "input_tokens_details": {"cached_tokens": 0},
                      "output_tokens": 34, "output_tokens_details": {"reasoning_tokens": 0},
                      "total_tokens": 46}}})


if __name__ == "__main__":
    print(f"mock responses listening on 127.0.0.1:{PORT}, log -> {LOGPATH}", flush=True)
    HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()