#!/usr/bin/env python3
"""
client.py - Basic ORAA Python client for reinforcement learning.

This client connects to the ORAA gRPC server running inside the
OpenRA engine, resets the environment, and runs a simple loop
that moves a tank to random positions on the map.

Usage:
    1. First generate the gRPC stubs:
         python generate_stubs.py

    2. Start OpenRA in headless mode with ORAA_HEADLESS=1

    3. Run this client:
         python client.py
"""

import random
import sys
import time

import grpc

# Import generated protobuf stubs.
# Run generate_stubs.py first if these don't exist.
try:
    import oraa_pb2
    import oraa_pb2_grpc
except ImportError:
    print("ERROR: gRPC stubs not found. Run 'python generate_stubs.py' first.")
    sys.exit(1)


SERVER_ADDRESS = "localhost:50051"
NUM_STEPS = 100
MAP_SIZE = 64  # Approximate map size for random target generation


def main():
    print(f"[ORAA Client] Connecting to {SERVER_ADDRESS}...")
    channel = grpc.insecure_channel(SERVER_ADDRESS)
    stub = oraa_pb2_grpc.RlEnvironmentStub(channel)

    # --- Reset ---
    print("[ORAA Client] Sending Reset...")
    reset_response = stub.Reset(oraa_pb2.ResetRequest())
    state = reset_response.state
    print(f"[ORAA Client] Game reset. Tick: {state.tick}, Units: {len(state.units)}, Resources: {state.resources}")
    print_units(state)

    # Find a unit we own (first mobile unit belonging to any non-neutral player).
    my_unit = None
    for unit in state.units:
        # Skip neutral/creep actors
        if unit.owner in ("Neutral", "Creeps", "Everyone"):
            continue
        my_unit = unit
        break

    if my_unit is None:
        print("[ORAA Client] No controllable units found. Exiting.")
        channel.close()
        return

    print(f"[ORAA Client] Controlling unit: {my_unit.type} (ID: {my_unit.actor_id}, Owner: {my_unit.owner})")

    # --- Step loop ---
    for step in range(NUM_STEPS):
        # Generate a random move target.
        target_x = random.randint(0, MAP_SIZE)
        target_y = random.randint(0, MAP_SIZE)

        action = oraa_pb2.Action(
            type=oraa_pb2.ACTION_MOVE,
            actor_id=my_unit.actor_id,
            target_x=target_x,
            target_y=target_y,
        )

        step_response = stub.Step(oraa_pb2.StepRequest(action=action))
        state = step_response.state
        done = step_response.done
        reward = step_response.reward

        # Find updated position of our unit.
        updated_unit = next((u for u in state.units if u.actor_id == my_unit.actor_id), None)
        pos_str = f"({updated_unit.pos_x}, {updated_unit.pos_y})" if updated_unit else "(dead/gone)"

        print(
            f"  Step {step + 1:3d}/{NUM_STEPS}: "
            f"Move -> ({target_x}, {target_y}) | "
            f"Unit pos: {pos_str} | "
            f"Tick: {state.tick} | "
            f"Reward: {reward:.2f} | "
            f"Done: {done}"
        )

        if done:
            print("[ORAA Client] Episode finished (game over).")
            break

    # --- Cleanup ---
    print(f"[ORAA Client] Completed {NUM_STEPS} steps. Closing connection.")
    channel.close()


def print_units(state):
    """Print a summary of all units in the current state."""
    if not state.units:
        print("  (no units)")
        return

    print(f"  {'ID':>6} | {'Type':<12} | {'Owner':<12} | {'HP':>4}/{'Max':>4} | {'Pos'}")
    print(f"  {'─' * 6} | {'─' * 12} | {'─' * 12} | {'─' * 4}/{'─' * 4} | {'─' * 10}")
    for u in state.units[:20]:  # Limit to first 20 units
        print(f"  {u.actor_id:>6} | {u.type:<12} | {u.owner:<12} | {u.hp:>4}/{u.max_hp:>4} | ({u.pos_x}, {u.pos_y})")
    if len(state.units) > 20:
        print(f"  ... and {len(state.units) - 20} more units")


if __name__ == "__main__":
    main()
