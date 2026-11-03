# Lab: measure, then change one thing

Research and implementation decision, 30 October 2026. This replaces the old Performance Lab. The old page had eight sections of tuning scripts (about 1,300 lines of script, 1,700 lines of view model, 1,300 lines of layout) and no way to tell whether any of them helped. Lab keeps the few that have evidence behind them, drops the rest with a reason, and adds the missing piece: a check that measures this PC, so a change can be judged by numbers instead of hope.

## Decision

- **Check this PC** (new). Four short tests, about 15 seconds, no download and no administrator rights. Each runs in three rounds; the middle result is kept together with how much the rounds differed. Results are saved with the power plan and memory integrity setting that were in force, and a new check is compared with the one before it. A difference only counts when it is bigger than 5% and bigger than twice the wobble of either check; otherwise the page says "no clear difference".
- **Power plan** (kept). Switch to an Ultimate Performance plan and back. Needs no administrator rights. The plan that was active is remembered on disk, so going back works after the app was closed.
- **Memory integrity** (kept, as an item in Tune). It is the one setting with a measured effect on speed, and also a security feature, so it lives next to Tune's other reversible, restart-aware settings. The Lab shows its state and links to it.
- **Give an app priority** (kept, reduced). Ease, Normal or Boost for one running app, until it closes. Never real time. Windows and security software are refused.

## What the old Lab had, and what happened to it

| Old section | Result | Why |
| --- | --- | --- |
| Power plan (Ultimate Performance) | **Kept** | Reversible, documented, no administrator rights. The gain is small. |
| Core isolation (VBS/HVCI) | **Kept**, memory integrity only, in Tune | Measured at about 4 to 8% in games. The old script also turned the hypervisor off (`hypervisorlaunchtype`), which breaks WSL 2, Hyper-V, Docker and the Android subsystem; that part was dropped. |
| Scheduler and affinity | **Kept** as app priority; pinning to cores dropped | Priority only matters when the processor is busy, and is easy to undo. Windows' scheduler places threads better than a fixed core list, and no source shows a gain from pinning. |
| Service templates | Dropped | Quiet already turns telemetry and optional services off from a reviewed catalog, with locks for services other things need. An idle service uses a few megabytes; disabling more of them gains nothing measurable and risks breakage. |
| Hardware reserved fix | Dropped | It cleared `truncatememory` and `removememory` and turned memory compression off. Memory compression has no downside worth the change. The two bcdedit values are only set by a person or a tool, so clearing them is a repair for a rare case, not a speed-up. |
| ETW tracing cleanup | Dropped | Stops Windows tracing sessions. The saving is negligible, they start again at boot, and it removes data that diagnostics rely on. |
| Kernel and boot tweaks | Dropped | Microsoft's `bcdedit /set` page says `useplatformclock`, `tscsyncpolicy` and `disabledynamictick` "should only be used for debugging". The one benchmark found showed no gain, and `useplatformclock` scored lower. Forum reports describe stutter that lasts until Windows is reinstalled. |
| Auto-tune monitor | Dropped | Needs a watcher that stays running, to apply presets Windows' own foreground boost and Game Mode mostly make unnecessary. |
| Boot automation | Dropped | Nothing left to automate. |
| Pagefile presets, working-set sweep (old Essentials) | Dropped | Already covered in [fixes-research.md](fixes-research.md): a system-managed pagefile is Microsoft's recommendation, and emptying working sets makes Windows read the same data from disk again. |

## What the evidence says

| Claim | Evidence | Verdict |
| --- | --- | --- |
| Memory integrity (HVCI) costs frame rate | Tom's Hardware measured a geometric mean of about 4 to 6% across Intel and AMD tests; ComputerBase measured about 8% on a Ryzen 7 5800X3D with Windows 11 24H2; PCWorld found no drastic loss and did not confirm the early 28% headline. Microsoft says older processors that emulate the needed feature "will have a bigger impact on performance". | Real but modest, and it depends on the CPU and game. The summary of the two benchmarks comes from search results, not from reading both articles in full. |
| Ultimate Performance helps | Guides call the gain small; one forum thread reports lower latency from plans that never lower the minimum processor state. No controlled Windows 11 benchmark was found. | Probably small on a desktop. |
| bcdedit timer options help | Microsoft: debugging only. One blog benchmark: no gain in PCMark 8 or 3DMark. | No. |
| Hardware-accelerated GPU scheduling and Game Mode | Results are mixed and anecdotal; Microsoft leaves both to the driver and the user. | Not included. A check cannot measure frame rate, so there is no way to tell here. |

## Why the margin is 5%

Two checks of the very same settings differed by 3.4% in memory access, while the rounds inside one check differed by less than 2%. The first margin (3%) called that "worse", so the margin is 5%.

## How the check works

Each test is plain managed code, run in-process:

1. **Wake-up speed**: 30 times, sleep 40 ms (so the processor idles), then time a fixed burst of arithmetic (about 2 ms). The median is kept. This is what a power plan changes: how quickly the clock speed comes back.
2. **All-core work**: one thread per logical processor runs the same arithmetic for 2 seconds; the result is steps per second.
3. **Memory copy**: a 128 MB block is copied six times after one untimed pass that touches the memory.
4. **Random memory access**: 32 million entries (128 MB, well past the processor cache) form one random loop built with Sattolo's shuffle; following it for 10 million steps times a memory read with nothing to guess ahead. Memory integrity runs Windows on top of the hypervisor, which makes page lookups cost more, so this is where it would show.

None of them measure frame rate, input lag or boot time. A check can show that a setting moved memory or processor speed; it cannot show that a game feels better.

## Safety rules

- Only these `powercfg` commands can run: list, get the active plan, activate a plan that exists, copy the Ultimate Performance or High performance template, and name that copy. There is no delete, import or setting change. A plan is picked by its id, never by a typed name.
- Priority: never real time; Windows components and security software are refused by name; an app is identified by process number and start time, so a number reused by another program is not touched; every change is read back, and the original priority is kept so it can be put back.
- Memory integrity goes through Tune's engine: one named registry value, the original saved first, read back after writing, and put back exactly.
- Nothing on the Lab page writes to the registry or needs administrator permission. Memory integrity does, through Tune's single permission prompt.

## Sources

| Source | Used for |
| --- | --- |
| [Microsoft Learn: BCDEdit /set](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set) | `useplatformclock`, `tscsyncpolicy` and `disabledynamictick` are for debugging only; what `truncatememory` and `removememory` do |
| [Microsoft Learn: Enable memory integrity](https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity) | The registry value, that a restart and drivers matter, the effect on older processors, the recovery steps |
| [Microsoft Learn: Powercfg command-line options](https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options) | The commands used to list, copy and activate plans |
| [Tom's Hardware: Windows 11 security features and gaming performance](https://www.tomshardware.com/news/windows-11-gaming-benchmarks-performance-vbs-hvci-security) | Size of the memory integrity cost (taken from a search summary) |
| [ComputerBase: VBS and HVCI in Windows 11, game benchmarks](https://www.computerbase.de/2023-03/windows-11-vbs-hvci-spiele-benchmarks/) | A larger cost on one CPU (taken from a search summary) |
| [PCWorld: does Windows 11 nuke gaming performance](https://www.pcworld.com/article/541137/tested-does-windows-11-nuke-gaming-performance.html) | The early large figure was not confirmed |

Nothing is copied from these. The tests, text and code are written for this app.
