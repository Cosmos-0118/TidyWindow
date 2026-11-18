# Health: how worn a battery is, and where the numbers come from

Research and implementation decision, 16 November 2026. The Health tab in Overview lists the hardware in this PC and says how each part is doing. Battery is the first device. The tab reads when the person asks, changes nothing and runs nothing in the background.

## What battery health means

Health is how much charge the battery holds now compared with when it was new:

**health % = full charge capacity ÷ design capacity × 100**

Both numbers are energy, in milliwatt-hours (mWh). Design capacity is what the cells were built to hold. Full charge capacity is what the battery's own gauge says a full charge holds today. The percentage in the taskbar is something else: it is the charge left divided by today's full capacity, so a worn battery can still read 100% when it is full.

| Grade | Health |
| --- | --- |
| Good | 80% or more |
| Fair | 60% to 79% |
| Worn | below 60% |

About 80% is the point where makers usually call a battery worn. Windows has no threshold of its own; the percentage is the measurement and the words are a reading aid. A battery that reports slightly more than it was built for (a new one often does) is capped at 100%.

## Where the numbers come from

A battery driver answers questions about its own battery. TidyWindow asks the same ones `powercfg /batteryreport` does:

1. `SetupDiGetClassDevs` for the battery device class lists every battery Windows has now. A desktop has none.
2. Each is opened for reading, and `IOCTL_BATTERY_QUERY_TAG` gives the tag that says which battery is in the slot. No tag means an empty slot, which is skipped without an error.
3. `IOCTL_BATTERY_QUERY_INFORMATION` with `BatteryInformation` returns design capacity, full charge capacity, cycle count, chemistry and a flag for batteries that count in their own units. The same call with `BatteryDeviceName` and `BatteryManufactureName` returns the name and the maker.

Other ways were considered:

| Way | Why not |
| --- | --- |
| `powercfg /batteryreport` | Writes an HTML file the app would have to find and parse, and its layout is localised. |
| WMI `root\wmi` (`BatteryStaticData`, `BatteryFullChargedCapacity`) | Gives the same numbers and works, but loads the WMI stack for a few values, and the classes are missing on some PCs that still have a battery. |
| `Windows.Devices.Power.Battery` (WinRT) | Right numbers, but `TidyWindow.Core` targets plain `net10.0`, so it is not available there, and the project avoids packages to add it. Its aggregate report also adds batteries together, which hides which one is worn. |
| `Win32_Battery` | Its charge is the taskbar level, and design and full capacity are often missing. |

Asking the driver needs no administrator permission, starts no program and writes no file. On the PC this was built on it returned the same full charge capacity as `BatteryFullChargedCapacity` in WMI.

## What the page shows

- **The card** has the button, so the answer appears where the question was asked: the percent, a bar, a line such as "Holds 42,100 of 50,000 mWh · checked 12:31 AM", and a word for the grade beside the title. State is always a word as well as a colour.
- **View details**, along the bottom of the card once there is a reading, opens the details: the headline and what it means, full charge, design capacity, capacity lost to wear, cycle count, chemistry, maker and name, and notes on reading the number.
- **Two batteries** are shown as one summed figure on the card (total full charge over total design capacity, which is not the same as the average of the two) and one block each in the details.
- **No battery** shows "No battery in this PC", with no button. `PowerSource.HasBattery()` decides, the same check Lab uses to warn laptop users about performance plans.

## What can go wrong

- **No design capacity.** Some batteries do not report it, or report 0. The health cannot be worked out, and the page says so instead of showing 0% or 100%. The capacities that were reported are still shown.
- **Capacity in the battery's own units.** A battery with the relative-capacity flag counts in units that are not mWh. The ratio is still right, so the percent is shown, but the numbers are not labelled mWh and are not added to another battery's.
- **An unreadable battery.** One that will not open is skipped and the others are still shown. Only when there is a battery and none could be read does the card say so, with the reason, and the same text goes to Logs.
- **The full charge figure is an estimate.** The gauge works it out from how the battery has been used. It moves a little with temperature and with how recently the battery was fully charged and run down, so two readings an hour apart can differ by a point or two. A full discharge and recharge can make the estimate more accurate. It does not bring lost capacity back.
- **Cycle count** is reported by only some batteries. When it is missing or 0 the page says "Not reported".

## Tests

`tests/TidyWindow.Tests/Health/BatteryHealthTests.cs` covers the percentage and the cap, missing capacities, the grade boundaries at 60 and 80, wear, summed against averaged batteries, batteries in their own units and the chemistry names. A read-only live test reads this PC and passes on a desktop with an empty list; when Windows says there is a battery it must find one.

## Not done yet

- Other devices. The page builds its cards from a list, so another device is another row, with its own reader.
- Keeping readings over time to show a trend.
