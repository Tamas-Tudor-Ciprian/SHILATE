# Pierburg CWA50 Pump — Raspberry Pi 4 Control Spec

Handoff document for writing the Pi 4 control code. The hardware is already wired and verified. This doc describes the hardware, the **inverted** signal relationship, and what the code needs to do.

## Goal

Drive a Pierburg CWA50 brushless coolant pump (GM p/n 13346941, Opel Astra J auxiliary pump) at variable speed from a Raspberry Pi 4, using PWM. The Pi commands speed; the pump's internal controller does the actual motor drive.

## Hardware

| Component | Detail |
|---|---|
| Pump | Pierburg CWA50, 3-pin. Brushless DC, internal controller. 8–16 V (nominal 12.5 V), draws 5–7 A. |
| Pump power | Separate 12 V supply rated ≥ 8 A. **Not** powered from the Pi. |
| Level shifter / driver | HW-042 (IRF520 N-channel MOSFET module), used as a low-side switch on the signal line only. |
| Controller | Raspberry Pi 4 (3.3 V GPIO logic). |

## Wiring (already built and verified)

Pump connector (3 pins), confirmed by multimeter (the ~2 kΩ pair = +12V↔PWM, internal pull-up; the high-impedance pin = GND):

- Pump **+12 V** pin → 12 V supply positive
- Pump **GND** pin → 12 V supply negative
- Pump **PWM** pin → IRF520 module **V−** (switched/drain terminal)

IRF520 (HW-042) module:

- Header **GND** → Pi GND
- Header **VCC** → not connected
- Header **SIG** → Pi GPIO (PWM output pin — see code notes)
- Screw **GND** → common ground
- Screw **V−** → pump PWM pin
- Screw **Vin / V+** → unused

**Common ground:** Pi GND, IRF520 GND (header + screw), 12 V supply negative, and pump GND are all tied to one node. This is essential for the signal reference.

## CRITICAL: the signal is INVERTED

The IRF520 pulls the pump's PWM pin **LOW** when the Pi's SIG output is **HIGH**. So the duty cycle the pump sees is the complement of what the Pi outputs:

```
duty_pump = 1 - duty_pi
```

Pump behavior (from CWA50 datasheet, referenced to the duty cycle on the pump's PWM pin):

- The PWM pin has an internal **2 kΩ pull-up**, so it idles HIGH.
- Duty cycle on the pin maps roughly **13% → 85% = minimum → maximum flow**. Below ~13% it does not run reliably.
- **Pin left HIGH / floating (≈100% pump duty) → pump runs FULL SPEED** after a ~3–10 s timeout. This is the fail-safe state.

Therefore, in terms of the **Pi's** output:

- Pi SIG = 0% duty (always low) → pump pin floats high → **full speed** (safe default).
- Pi SIG = high duty → pump pin held low more → **lower speed / stop**.
- Higher Pi duty = slower pump. **Invert in software.**

Because the exact mapping is approximate and direction should be confirmed on the bench, the code must expose a calibratable mapping rather than hard-coding percentages.

## PWM signal requirements

- Frequency: ~**70–100 Hz** works well. The pump is not fussy about exact frequency; 100 Hz is a fine default. Avoid very high frequencies.
- Use a **stable, hardware-timed PWM** source. On the Pi 4, prefer `pigpio` (hardware-timed PWM on any GPIO) over software/`RPi.GPIO` PWM, which jitters. Hardware PWM channels are on GPIO12/13 and GPIO18/19 if you want the on-chip PWM peripheral.
- 3.3 V logic level is fine for the IRF520 in this role — it only sinks a few mA through the pump's 2 kΩ pull-up, so partial gate turn-on is not a problem.

## What the code should do

1. **Set up PWM** on the chosen GPIO at ~100 Hz using `pigpio` (recommended) or equivalent.
2. **Expose a `set_speed(percent)` API** where `percent` is intuitive (0 = off, 100 = full speed) and the function internally:
   - Maps user percent → pump-pin duty within the usable window (~13–85%), clamping outside it.
   - Applies the **inversion** (`duty_pi = 1 - duty_pump`) before writing to the GPIO.
   - Keep the min/max window and the inversion as named constants / config so they can be tuned on the bench.
3. **Safe default / startup state:** initialize to a defined speed (e.g. full speed or a chosen idle) explicitly. Do not leave the GPIO floating; note that SIG low = pump full speed = safe for cooling.
4. **Clean shutdown:** on exit / Ctrl-C, set the pump to the chosen safe state (recommend full speed for a cooling loop, or stop if appropriate) and release the GPIO / stop pigpio cleanly.
5. **Optional niceties:** a small CLI or function to ramp speed, a calibration mode that sweeps duty so the user can record the real min/max flow points, and logging of commanded speed.

## Suggested skeleton (illustrative, not final)

```python
import pigpio, atexit

PWM_GPIO   = 18          # any GPIO; 18 is a hardware-PWM pin
PWM_FREQ   = 100         # Hz
PUMP_MIN_D = 0.13        # min usable pump-pin duty (calibrate)
PUMP_MAX_D = 0.85        # max usable pump-pin duty (calibrate)
INVERT     = True        # IRF520 inverts: duty_pi = 1 - duty_pump

pi = pigpio.pi()

def set_speed(percent):
    percent = max(0.0, min(100.0, percent))
    if percent == 0:
        duty_pump = 1.0          # pin high = full speed fail-safe; or 0 to stop — decide per app
    else:
        duty_pump = PUMP_MIN_D + (PUMP_MAX_D - PUMP_MIN_D) * (percent / 100.0)
    duty_pi = (1.0 - duty_pump) if INVERT else duty_pump
    pi.hardware_PWM(PWM_GPIO, PWM_FREQ, int(duty_pi * 1_000_000))  # duty in 0..1e6

def shutdown():
    set_speed(100)               # safe state for a cooling loop; change if needed
    pi.stop()

atexit.register(shutdown)
```

> Note: confirm the inversion direction and the 13/85 endpoints empirically. Start at low commanded speed, watch the response, and adjust `INVERT`, `PUMP_MIN_D`, `PUMP_MAX_D`.

## Operational / safety notes

- Never connect the pump PWM pin directly to a GPIO — it can idle near 12 V. It must stay behind the IRF520 V− terminal.
- Do not run the pump dry; bench-test with the pump in liquid or with hoses looped.
- The motor current (5–7 A) flows only through the 12 V supply and the pump's own pins — never through the IRF520 module (which switches the signal only).
```
