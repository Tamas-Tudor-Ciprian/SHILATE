#!/usr/bin/env python3
"""
SHILATE Debug Drive — Terminal-based WASD vehicle control from Leda.

Usage:
    python3 debug_drive.py [--mqtt-host localhost] [--mqtt-port 1883]

Controls:
    W/S     — Throttle / Brake
    A/D     — Steer left / right
    P/R/N/D — Gear selection
    ESC/Q   — Quit
"""

import argparse
import curses
import logging
import sys
import time

from controller import VehicleController

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
    filename="/tmp/debug_drive.log",
)
log = logging.getLogger("debug-drive")

CONTROL_HZ = 20
THROTTLE_STEP = 0.05
STEER_STEP = 0.08
BRAKE_STEP = 0.05
STEER_DECAY = 0.15


def main(stdscr, ctrl: VehicleController):
    curses.curs_set(0)
    stdscr.nodelay(True)
    stdscr.timeout(int(1000 / CONTROL_HZ))

    throttle = 0.0
    steer = 0.0
    brake = 0.0
    gear = "P"
    cur_gear = "P"

    # Shift to Drive: must hold brake first (Tesla safety rule)
    ctrl.set_brake(1.0)
    ctrl.send_action(0.0, 0.0, 1.0)
    time.sleep(0.15)          # give Unity time to receive brake
    ctrl.set_gear("D")
    gear = "D"
    time.sleep(0.05)          # let gear command arrive before releasing brake

    while True:
        key = stdscr.getch()

        # Quit
        if key in (27, ord('q'), ord('Q')):  # ESC or Q
            break

        # Throttle / Brake
        if key == ord('w') or key == ord('W'):
            throttle = min(1.0, throttle + THROTTLE_STEP)
            brake = 0.0
        elif key == ord('s') or key == ord('S'):
            brake = min(1.0, brake + BRAKE_STEP)
            throttle = 0.0
        else:
            # Gradual release
            throttle = max(0.0, throttle - THROTTLE_STEP * 0.5)
            brake = max(0.0, brake - BRAKE_STEP * 0.5)

        # Steering
        if key == ord('a') or key == ord('A'):
            steer = max(-1.0, steer - STEER_STEP)
        elif key == ord('d'):
            steer = min(1.0, steer + STEER_STEP)
        else:
            # Steer decay towards center
            if abs(steer) < STEER_DECAY:
                steer = 0.0
            elif steer > 0:
                steer -= STEER_DECAY
            else:
                steer += STEER_DECAY

        # Gear selection — require brake to be held for Drive/Reverse shifts
        if key == ord('p'):
            gear = "P"
            ctrl.set_gear("P")
        elif key == ord('r'):
            if brake >= 0.1:
                gear = "R"
                ctrl.set_gear("R")
        elif key == ord('n'):
            gear = "N"
            ctrl.set_gear("N")
        # 'D' for gear only when pressed as capital (Shift+D)
        elif key == ord('D'):
            if brake >= 0.1:
                gear = "D"
                ctrl.set_gear("D")

        # Send commands
        ctrl.send_action(steer, throttle, brake)

        # Fetch telemetry before drawing so cur_gear is fresh
        speed = ctrl.get_speed()
        rpm = ctrl.get_rpm()
        steering = ctrl.get_steering()
        cur_gear = ctrl.get_gear()

        # Draw HUD
        stdscr.erase()
        h, w = stdscr.getmaxyx()

        title = "═══ SHILATE Debug Drive ═══"
        stdscr.addstr(0, max(0, (w - len(title)) // 2), title, curses.A_BOLD)

        stdscr.addstr(2, 2, f"Controls: W/S=throttle/brake  A/D=steer  P/R/N/D=gear  ESC=quit")

        stdscr.addstr(4, 2, f"┌─────────────────────────────────────┐")
        stdscr.addstr(5, 2, f"│  Gear: cmd={gear:>2s} actual={cur_gear:>2s}           │")
        stdscr.addstr(6, 2, f"│  Throttle:  {throttle:>6.2f}    [{bar(throttle, 20)}]  │")
        stdscr.addstr(7, 2, f"│  Brake:     {brake:>6.2f}    [{bar(brake, 20)}]  │")
        stdscr.addstr(8, 2, f"│  Steer:     {steer:>+6.2f}    [{steer_bar(steer, 20)}]  │")
        stdscr.addstr(9, 2, f"└─────────────────────────────────────┘")

        stdscr.addstr(11, 2, "─── Telemetry from Unity ───", curses.A_BOLD)

        stdscr.addstr(12, 2, f"  Speed:    {speed:>7.1f} km/h")
        stdscr.addstr(13, 2, f"  RPM:      {rpm:>7.0f}")
        stdscr.addstr(14, 2, f"  Steering: {steering:>+7.1f}°")
        stdscr.addstr(15, 2, f"  Gear:     {cur_gear:>7s}")
        stdscr.addstr(16, 2, f"  Brake:    {ctrl.get_brake():>7.2f}")
        stdscr.addstr(17, 2, f"  Throttle: {ctrl.get_throttle():>7.2f}")

        stdscr.addstr(19, 2, f"  Env prefix: {ctrl.prefix}")

        stdscr.refresh()


def bar(value: float, width: int) -> str:
    filled = int(value * width)
    return "█" * filled + "░" * (width - filled)


def steer_bar(value: float, width: int) -> str:
    center = width // 2
    pos = int((value + 1.0) / 2.0 * width)
    pos = max(0, min(width - 1, pos))
    chars = list("░" * width)
    chars[center] = "│"
    chars[pos] = "█"
    return "".join(chars)


def parse_args():
    parser = argparse.ArgumentParser(description="SHILATE Debug Drive")
    parser.add_argument("--mqtt-host", default="localhost")
    parser.add_argument("--mqtt-port", type=int, default=1883)
    return parser.parse_args()


if __name__ == "__main__":
    args = parse_args()

    ctrl = VehicleController(
        mqtt_host=args.mqtt_host,
        mqtt_port=args.mqtt_port,
    )

    if not ctrl.connect():
        print("ERROR: Could not connect to MQTT broker", file=sys.stderr)
        sys.exit(1)

    try:
        curses.wrapper(main, ctrl)
    except KeyboardInterrupt:
        pass
    finally:
        # Stop the car before disconnecting
        ctrl.send_action(0.0, 0.0, 0.0)
        ctrl.set_gear("P")
        time.sleep(0.1)
        ctrl.disconnect()

    print("Debug drive session ended.")
