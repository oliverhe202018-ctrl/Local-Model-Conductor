import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import sys
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen", required=True)
    parser.add_argument("--mode", choices=["fail-health", "fail-inference"], required=True)
    parser.add_argument("--model-id", default="fault-mock")
    args = parser.parse_args()

    host, port_s = args.listen.rsplit(":", 1)
    port = int(port_s)
    mode = args.mode
    model_id = args.model_id

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *margs):
            sys.stdout.write(f"[mock_fault_server mode={mode}] {fmt % margs}\n")
            sys.stdout.flush()

        def do_GET(self):
            if self.path == "/health":
                if mode == "fail-health":
                    body = json.dumps({"status": "error", "reason": "simulated_health_check_failure"}).encode("utf-8")
                    self.send_response(503)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:
                    body = json.dumps({"status": "ok"}).encode("utf-8")
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                return

            if self.path == "/v1/models":
                body = json.dumps({"object": "list", "data": [{"id": model_id, "object": "model"}]}).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return

            self.send_response(404)
            self.end_headers()

        def do_POST(self):
            length = int(self.headers.get("Content-Length", "0"))
            _ = self.rfile.read(length) if length > 0 else b""
            if mode == "fail-inference":
                body = json.dumps({
                    "error": {
                        "message": "Simulated CUDA illegal memory access during token decode",
                        "type": "inference_runtime_error",
                        "code": "simulated_decode_crash",
                    }
                }).encode("utf-8")
                self.send_response(500)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
                return
            self.send_response(503)
            self.end_headers()

    sys.stdout.write(f"[mock_fault_server] starting mode={mode} on {host}:{port}\n")
    sys.stdout.flush()
    server = ThreadingHTTPServer((host, port), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
