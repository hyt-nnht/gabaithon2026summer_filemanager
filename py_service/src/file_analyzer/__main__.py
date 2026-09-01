from __future__ import annotations

import argparse
import socket

import uvicorn


class IpcServer(uvicorn.Server):
    """Uvicorn server that announces a dynamically assigned IPC port when ready."""

    def __init__(self, config: uvicorn.Config, announced_port: int | None = None) -> None:
        super().__init__(config)
        self.announced_port = announced_port

    async def startup(self, sockets: list[socket.socket] | None = None) -> None:
        await super().startup(sockets=sockets)
        if self.started and self.announced_port is not None:
            print(f"PORT: {self.announced_port}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Start the local file analysis service")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    if args.host not in {"127.0.0.1", "localhost"}:
        parser.error("--host must be 127.0.0.1 or localhost")

    config = uvicorn.Config("file_analyzer.api.app:app", host=args.host, port=args.port, log_level="info")
    server = IpcServer(config)
    if args.port != 0:
        server.run()
        return

    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(2048)
    port = listener.getsockname()[1]
    server.announced_port = port
    server.run(sockets=[listener])


if __name__ == "__main__":
    main()
