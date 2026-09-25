# Main-thread timer demand fixture

This standalone Godot 4.5.1 Mono project compiles the actual pump and dispatcher sources into a scratch project. It checks a repeating 10 ms timer at 60 FPS: `IsStopped()` can be true while internal processing remains active. Releasing the final tick lease must stop that processing; reacquiring must restart it; reconciling redundant demand while overdue must not reset the countdown.

Run with a Godot 4.5.1 Mono executable:

```bash
SPI_GODOT_BIN=/path/to/godot-4.5.1-mono experiments/main-thread-timer-demand/run-probe.sh
```

The script creates `/tmp/spirectl-timer-demand-*`, builds under the shared .NET build lock, and retains `source-sha256.txt`, `build.log`, `import.log`, and `run.log`. `SPI_TIMER_PROBE_SCRATCH_DIR` selects a particular scratch directory. `SPI_TIMER_PUMP_SOURCE` and `SPI_TIMER_DISPATCHER_SOURCE` can select an exact historical source file for a negative control; they never change the repository sources.

The result proves the timer/lease behavior in a tiny headless Godot project. It does not measure game performance, identify current-game timing, or exercise the installed bridge.
