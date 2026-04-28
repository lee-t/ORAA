#!/usr/bin/env python3
"""
generate_stubs.py - Generate Python gRPC stubs from the ORAA proto file.

Run this script from the client/ directory:
    python generate_stubs.py

It reads ../OpenRA.API/Protos/oraa.proto and outputs the generated
Python modules into the client/ directory.
"""

import subprocess
import sys
import os

PROTO_PATH = os.path.join(os.path.dirname(__file__), "..", "OpenRA.API", "Protos")
PROTO_FILE = "oraa.proto"
OUTPUT_DIR = os.path.dirname(__file__) or "."


def main():
    cmd = [
        sys.executable, "-m", "grpc_tools.protoc",
        f"--proto_path={PROTO_PATH}",
        f"--python_out={OUTPUT_DIR}",
        f"--grpc_python_out={OUTPUT_DIR}",
        os.path.join(PROTO_PATH, PROTO_FILE),
    ]

    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print(f"Error generating stubs:\n{result.stderr}", file=sys.stderr)
        sys.exit(1)

    print("Successfully generated Python gRPC stubs:")
    print(f"  - {OUTPUT_DIR}/oraa_pb2.py")
    print(f"  - {OUTPUT_DIR}/oraa_pb2_grpc.py")


if __name__ == "__main__":
    main()
