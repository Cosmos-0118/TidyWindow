# Crashes: finding out what crashed, and how often

Research and implementation decision, 3 November 2026. The Crashes page answers three questions a person cannot easily answer on Windows: did an app crash or freeze, which one does it most, and what happened each time. It reads Windows' own records and adds nothing that runs in the background.

## Where the records are

Windows already keeps the evidence. It is spread over two event logs, in a format only Event Viewer shows, and it rolls over on its own. Reliability Monitor, the closest built-in view, groups the same records by day but offers no search, no ranking of apps and no copyable text. Open-source event viewers such as EventLook are faster than Event Viewer, but they show raw events, not crashes.

| Log | Component and event | What it is |
| --- | --- | --- |
| Application | Application Error 1000 | An app crashed. Names the program, version, failing file, exception code, offset and process. |
| Application | Application Hang 1002 | An app stopped responding. |
| Application | .NET Runtime 1026 | A .NET app crashed. Carries the exception and stack, which Windows' own record does not. Merged into the 1000 record for the same crash. |
| Application | Windows Error Reporting 1001, for crash, hang and kernel reports only | Holds the report folder Windows keeps. Attached to the crash with the same report number. A kernel report with a known code is a problem of its own. |
| System | WER-SystemErrorReporting 1001, Kernel-Power 41, EventLog 6008 | Blue screen, or Windows restarting without shutting down. One incident, written up to three times; kept once. |
| System | Service Control Manager 7000, 7009, 7031, 7034 | A service crashed, failed to start or timed out. |
| System | Display 4101 | The graphics driver stopped responding and recovered. |
| System | WHEA-Logger 1, 17, 18, 19, 20 | Hardware errors. |
| System | disk 7, 11, 51, 153 · Ntfs 55 | Disk and file system errors. |
| System | Resource-Exhaustion-Detector 2004 | Windows ran low on memory. |

Every query names the component and the event number. Matching on the number alone is wrong: on the PC this was built on, a vendor service writes its routine "service is running" messages as events 1000 and 1002.

## What it deliberately leaves out

- DistributedCOM 10016 and similar. 249 of them in 30 days on a healthy PC; nothing for a person to do.
- Most kernel reports. A single minute of USB activity produced 1,134 `LiveKernelEvent` reports. Only the four codes with a documented meaning are kept, and repeats of one problem within ten minutes are one entry with a count.
- Anything that needs administrator rights. The Application and System logs are readable by a standard account.
- Reading memory dumps. The page shows where Windows saved one and what its stop code means, but does not open it.

## Why it uses almost no system resources

- Nothing watches the log. There is no service, no subscription and no timer. A scan runs when the page is opened, on Refresh, and once about ten seconds after launch so the sidebar can show whether anything is new.
- Windows does the filtering. The query is an XPath naming the components, the event numbers and the start time, so only matching events are copied out of the log. The reader is the system's `wevtapi` through three P/Invoke calls, so the app takes no new dependency.
- A scan is incremental. The first reads 90 days; every later one reads only what is newer than the previous scan (minus a five-minute overlap, and a record already held is ignored). Measured on a real PC: the first scan read 1,218 events in about 210 ms, and a later scan took about 100 ms and 54 MB at its peak.
- It runs on a thread of its own at the lowest CPU priority, in Windows' background mode, so disk and memory yield to whatever the person is doing.
- History is a small JSON-lines file (`%LOCALAPPDATA%\TidyWindow.Next\Crashes`), bounded to 6,000 records and 120 days. It opens in the background, and the page shows it at once while a scan catches up.
- Searching works on strings built once per refresh: about 2.5 ms for a search over 1,200 events. Typing waits 140 ms before it filters.
- The lists build a row only for what is open, a list shows at most 500 rows, and an app's events are created when it is expanded.

## What it shows

- **By app.** One row per program: how many crashes and freezes, a 14-day chart, the last time. Open it for what failed (the failing file and error, counted), the versions affected, and every event. Sorted by most trouble, most recent or name.
- **Windows.** Blue screens, unexpected shutdowns, failing services, graphics driver resets, hardware and disk errors.
- **Timeline.** Everything, newest first.
- **Observations.** Several apps failing in one file, an app crashing the same way repeatedly or in a burst, repeated blue screens, shutdowns or driver resets. Generic files such as `ntdll.dll` are never blamed.
- **Plain language.** Exception codes and stop codes are named and explained. A code with no entry stays a code.

## Search

Any words must all match, anywhere in an event; small typos are forgiven. Beyond that:

| Syntax | Meaning |
| --- | --- |
| `"access violation"` | An exact phrase. |
| `-edge` | Leave out anything matching edge. |
| `app:` `module:` `code:` `source:` `version:` `path:` `pid:` | Narrow one field. Several values for one field mean either. |
| `kind:hang` `kind:crash` `kind:bsod` `kind:windows` `kind:service` `kind:driver` `kind:disk` | Narrow by kind. |
| `since:7d` `since:yesterday` `since:2026-10-01` `before:2d` | Narrow by time. |

Plain words also find kinds: `freeze` finds hangs and `bsod` finds blue screens. Codes match with or without `0x`. The page shows how the search was read.

## Privacy

Nothing leaves the PC. Copy, and Copy summary, replace the Windows account name in paths with `%USERPROFILE%`, as the activity log does; this does not remove every identifying detail, so review a report before sending it. "Search the web" opens the default browser with the program, file and error words. Clearing history removes only what TidyWindow saved, never Windows' own log.

## Limits

- Windows keeps its own log for a limited time and size, so history before the first scan is only what Windows still had. After that, TidyWindow's copy outlives Windows'.
- A crash that kills the logging service, or a power cut, leaves no crash record. Those appear only as an unexpected shutdown.
- Programs are grouped by file name. Two different programs both named `setup.exe` are one row.
- Event layouts for blue screens, services, the display driver, hardware, disk and memory events were written from documentation and tested with sample data; only application crashes, hangs, .NET crashes, reports, Kernel-Power 41, EventLog 6008 and service stops were checked against events on a real PC.
