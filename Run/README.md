# Running the benchmarks

The `Run` folder must be placed in the project's root directory, alongside `AlgorithmComparison.CLI` and `Data`. Run the CMD files from this folder and keep the rest of the project structure unchanged.

- **Benchmark.cmd** - compares Dijkstra, CSA and RAPTOR. Enter the number of queries, the walking radius and the maximum number of transfers, then select the random seed, algorithms and datasets. Use the arrow keys to move between items, Space to select or deselect an item, and Enter to confirm. Results are saved in `Reports`.
- **RAPTOR-Realtime.cmd** - runs RAPTOR benchmarks with GZM updates. Online mode requires an internet connection and automatically downloads the timetable for the current day. Offline mode replays a saved update and requires a `.pb` file and a matching GTFS timetable. Reports and downloaded data are saved in `DataRealTime/Slask`.

The realtime menu allows either a fixed number of measurements or continuous operation until stopped manually with Ctrl+C.
