# Deep Scan Benchmarks

The deep scan automation takes a conservative approach to traversing file systems so the UI remains responsive. Measurements were captured on a Surface Laptop Studio (Intel Core i7, 32 GB RAM, NVMe SSD) running Windows 11 24H2.

| Scenario           | Root            | Threshold | Top results | Duration | Notes                                              |
| ------------------ | --------------- | --------- | ----------- | -------- | -------------------------------------------------- |
| Home profile sweep | `C:\Users\user` | 200 MB    | 25          | 8.4 s    | Includes OneDrive cache; `IncludeHidden` disabled  |
| Project workspace  | `D:\Repos`      | 100 MB    | 30          | 5.1 s    | Highlights multi-gigabyte Docker layers            |
| Video archive      | `E:\Media`      | 500 MB    | 20          | 11.7 s   | Majority of time spent on thumbnail directory ACLs |

**How to reproduce**

1. Launch TidyWindow and open the **Deep scan analyzer** module.
2. Enter the target path and adjust the minimum size threshold/top count to match the desired scenario.
3. Click **Run scan** and observe the status bar for the completion summary (total candidates and cumulative size).
4. Optionally enable **Include hidden** to inspect caches or system folders (this may require elevation depending on ACLs).

**Performance notes**

-   Filters run client-side so the PowerShell script only emits a compact JSON payload; the view model handles formatting.
-   Very deep trees (millions of files) can still be slow if they are all above the threshold. Reduce the result count or narrow the path to mitigate.
-   Hidden/system folders are skipped by default to avoid incidental ACL prompts. Toggle **Include hidden** when investigating disk usage spikes caused by caches or temp stores.
