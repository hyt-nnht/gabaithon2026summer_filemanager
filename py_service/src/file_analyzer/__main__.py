from __future__ import annotations

import argparse
import socket

import uvicorn


def main() -> None:
    parser = argparse.ArgumentParser(description="Start the local file analysis service")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost"}:
        parser.error("--host must be 127.0.0.1 or localhost")

    config = uvicorn.Config("file_analyzer.api.app:app", host=args.host, port=args.port, log_level="info")
    server = uvicorn.Server(config)
    if args.port != 0:
        server.run()
        return

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(2048)
    port = listener.getsockname()[1]
    print(f"PORT={port}", flush=True)
    server.run(sockets=[listener])


if __name__ == "__main__":
    main()

