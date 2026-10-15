## 1. CPU and RAM Performance Benchmark

### 1.1 Test Configuration

The benchmark was conducted in the `release-core-debug-ui-idle` scenario, with the agent core and lock-screen UI running in an idle state.

Two processes were monitored:
- `BaronDeskAgent.ServiceCore`
- `BaronDesk.LockUI`

The test collected **23 samples per process**, approximately every 5 seconds, to measure CPU consumption and memory usage.

### 1.2 CPU and Memory Results

| Metric | Agent Core | LockUI |
|---|---:|---:|
| Average CPU usage | 0.029% | 0% |
| 95th-percentile CPU usage (P95) | 0.078% | 0% |
| Maximum CPU usage | 0.272% | 0% |
| Average private memory | 34.3 MB | 54.6 MB |
| Maximum private memory | 36.6 MB | 55.1 MB |
| Private memory growth | 0.5 MB | 0.2 MB |
| Average working set | Approximately 86.7 MB | Approximately 112.4 MB |

*Note: CPU and private-memory summary values come from the benchmark summary. Working-set averages are approximate values calculated from the recorded samples.*

### 1.3 CPU Consumption Analysis

The agent core demonstrated very low CPU consumption during idle operation:

- Average CPU usage was **0.029%**, with a P95 of **0.078%**.
- The maximum observed CPU usage was **0.272%**.
- The LockUI process recorded 0% CPU usage across the collected samples, at the measurement precision of the monitoring tool.

These results indicate that the agent's background processes impose minimal CPU overhead when idle. This is a positive result for a gaming environment, where CPU resources should remain available to the running game.

### 1.4 RAM Consumption Analysis

The agent core used an average of **34.3 MB of private memory**, reaching a maximum of 36.6 MB. Its measured private-memory growth was only 0.5 MB over the sampling period.

The LockUI process used an average of **54.6 MB of private memory**, with a maximum of 55.1 MB and measured growth of 0.2 MB.

The combined average private memory consumption of both processes was approximately **88.9 MB**.

The recorded working-set values were higher because working set and private memory measure different aspects of process memory. Working set represents memory currently resident in physical RAM, whereas private memory represents memory private to the process.

The small changes in private memory during the test suggest stable memory usage during idle operation, with no obvious short-term memory-growth issue.

### 1.5 Conclusion

The benchmark results show that the BaronDesk Agent Core and LockUI have **low CPU overhead and relatively stable memory consumption during idle operation**. The agent core's average CPU usage was 0.029%, and the two processes together consumed approximately 88.9 MB of private memory on average.

These findings support the suitability of the current implementation for low-overhead background operation on gaming PCs.

However, this benchmark covers only the idle scenario. Additional measurements under active sessions, network interruptions, reconnection, and lock/unlock operations would be required to establish performance under realistic operating conditions.
