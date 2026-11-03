namespace TidyWindow.Next.Core;

/// <summary>What the codes in a crash record mean, in words a person can act on. Unknown codes stay as the code; nothing is guessed.</summary>
public static class CrashKnowledge
{
    private static readonly Dictionary<uint, (string Name, string Meaning)> Exceptions = new()
    {
        [0xC0000005] = ("Access violation", "The program used memory it was not allowed to. Usually a bug in the program or in a plug-in or overlay it loaded. When many different programs do this, suspect a driver or failing memory."),
        [0xC0000006] = ("In-page I/O error", "Windows could not read part of a file the program needed, often from a failing disk or a network or removable drive that went away."),
        [0xC0000008] = ("Invalid handle", "The program used something Windows had already closed. A bug in the program."),
        [0xC000001D] = ("Illegal instruction", "The program ran code this processor cannot run, usually from a damaged file or a build for different hardware."),
        [0xC0000017] = ("Out of memory", "The program asked for memory and Windows had none to give. Close other programs, or the program itself may be leaking memory."),
        [0xC0000094] = ("Divide by zero", "The program divided a number by zero. A bug in the program."),
        [0xC0000096] = ("Privileged instruction", "The program tried an operation only Windows itself may do."),
        [0xC00000FD] = ("Stack overflow", "The program called itself too deeply and ran out of working space. A bug in the program."),
        [0xC0000135] = ("DLL not found", "The program needs a shared file that is missing. Reinstalling the program, or the Visual C++ or .NET runtime it relies on, usually fixes this."),
        [0xC0000139] = ("Entry point not found", "A shared file is the wrong version for this program. Reinstalling or updating the program usually fixes this."),
        [0xC0000142] = ("DLL initialization failed", "A shared file refused to start. Often a damaged install or a security tool blocking it."),
        [0xC0000374] = ("Heap corruption", "The program damaged its own memory. A bug in the program or one of its add-ins."),
        [0xC0000409] = ("Stack buffer overrun", "The program (or Windows) stopped it on purpose after detecting corrupted memory. This is a safety stop, and the program or one of its add-ins is the usual cause."),
        [0xC000041D] = ("Unhandled exception in callback", "The program failed while Windows was calling back into it."),
        [0xC0000417] = ("Invalid parameter", "The program passed a value its own runtime rejected. A bug in the program."),
        [0xC06D007E] = ("Module not found", "The program needs a shared file that is missing. Reinstalling it usually fixes this."),
        [0xE0434352] = (".NET exception", "A .NET program hit an error it did not handle. The exception below names the cause."),
        [0xE06D7363] = ("C++ exception", "The program threw an error it did not handle."),
        [0x80000003] = ("Breakpoint", "The program stopped itself on purpose, as a debugger or a built-in check would.")
    };

    private static readonly Dictionary<uint, (string Name, string Meaning)> Bugchecks = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "A driver or Windows touched memory at the wrong time. Usually a driver."),
        [0x19] = ("BAD_POOL_HEADER", "Windows memory was damaged. A driver or faulty memory."),
        [0x1A] = ("MEMORY_MANAGEMENT", "Windows found a serious problem in how memory is used. Faulty memory or a driver."),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "A driver or Windows component failed. Usually a driver."),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "Windows code failed while handling a request from a program. Usually a driver."),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "Windows used memory that was not there. Faulty memory, a driver or antivirus software."),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "Windows could not read something it needed from the disk. A failing disk, cable or controller."),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "Windows lost the drive it starts from. A storage driver, cable or disk problem."),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "A system thread failed. Usually a driver."),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "A driver did not handle sleep, wake or shutdown. Update the drivers for the devices that were just used."),
        [0xC2] = ("BAD_POOL_CALLER", "A driver misused memory."),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "Driver Verifier caught a driver misbehaving."),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "A driver damaged Windows memory."),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "A driver touched memory at the wrong time. Update or remove the driver named in the dump."),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "A process Windows cannot run without ended. Damaged system files, a failing disk or a driver."),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "A process Windows cannot run without was stopped. Often a failing disk."),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "A driver tried to run code from memory marked as data."),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "A processor stopped responding. Overclocking, firmware or a failing processor."),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "Windows found its own protected data changed. A driver or faulty memory."),
        [0x116] = ("VIDEO_TDR_FAILURE", "The graphics driver stopped responding and could not recover. Update or reinstall the graphics driver and check temperatures."),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "The graphics driver stopped responding. Update or reinstall the graphics driver."),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "The graphics driver or hardware failed."),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "The hardware reported an error it could not correct. A processor, memory, disk or overclocking problem."),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "A driver took too long to respond. Update storage and other drivers, and firmware for SSDs."),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "Windows found corrupted data. A driver or faulty memory."),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "A driver damaged Windows memory."),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "The memory store failed. Often a failing SSD or its driver."),
        [0x1CC] = ("EXFAT_FILE_SYSTEM", "An exFAT drive caused a failure."),
        [0x1000007E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "A system thread failed. Usually a driver.")
    };

    private static readonly Dictionary<uint, string> LiveKernel = new()
    {
        // Only codes with a documented meaning. Windows writes many more of these reports, and an unexplained one is noise, not information.
        [0x117] = "Display driver stopped responding (timeout)",
        [0x141] = "Display driver stopped responding (GPU hang)",
        [0x144] = "USB 3 controller driver error",
        [0x193] = "Graphics driver error"
    };

    public static string ExceptionName(string code) => TryParse(code, out var value) && Exceptions.TryGetValue(value, out var known) ? known.Name : "";
    public static string BugcheckName(string code) => TryParse(code, out var value) && Bugchecks.TryGetValue(value, out var known) ? known.Name : "";
    public static string LiveKernelName(string code) => TryParse(code, out var value) && LiveKernel.TryGetValue(value, out var known) ? known : "";

    /// <summary>"c0000005", "0xC0000005" and "0x0000009f" all become one canonical spelling; text that is not a code is returned as it came.</summary>
    public static string NormalizeCode(string code)
    {
        code = code.Trim();
        if (!TryParse(code, out var value)) return code;
        return value > 0xFFFF || code.Length > 6 ? $"0x{value:X8}" : $"0x{value:X}";
    }

    public static bool TryParse(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, null, out value);
    }

    public static string Headline(CrashEvent e)
    {
        var who = e.AppName;
        return e.Kind switch
        {
            CrashKind.AppCrash => e.Managed.Length > 0 ? $"{who} crashed: {e.Managed}" : e.Module.Length > 0 ? $"{who} crashed in {e.Module}" : $"{who} crashed",
            CrashKind.AppHang => $"{who} stopped responding",
            CrashKind.BlueScreen => e.CodeName.Length > 0 ? $"Windows showed a blue screen: {e.CodeName}" : "Windows showed a blue screen",
            CrashKind.UnexpectedShutdown => "Windows stopped without shutting down properly",
            CrashKind.ServiceFailure => e.Detail.StartsWith("failed", StringComparison.OrdinalIgnoreCase) || e.Detail.StartsWith("timed", StringComparison.OrdinalIgnoreCase) ? $"{who} {e.Detail.Split('\n')[0].TrimEnd('.')}" : $"{who} stopped unexpectedly",
            CrashKind.DriverReset => e.CodeName.Length > 0 ? e.CodeName : e.App.Length > 0 ? $"The {who} display driver stopped responding and recovered" : e.Detail.Length > 0 ? e.Detail.Split('\n')[0] : "A driver stopped responding and recovered",
            CrashKind.HardwareError => e.Detail.Length > 0 ? e.Detail.Split('\n')[0].TrimEnd('.') : "Windows reported a hardware error",
            CrashKind.StorageError => e.Detail.Length > 0 ? e.Detail.Split('\n')[0].TrimEnd('.') : "A disk reported an error",
            CrashKind.LowMemory => "Windows ran low on memory",
            _ => "Report"
        };
    }

    public static string Explain(CrashEvent e)
    {
        if (e.Kind == CrashKind.BlueScreen && TryParse(e.Code, out var stop) && Bugchecks.TryGetValue(stop, out var bug)) return bug.Meaning;
        if (e.Kind == CrashKind.AppHang) return "The program stopped answering Windows, so Windows offered to close it. Often it was busy, waiting on a slow disk or network, or stuck on a bug.";
        if (e.Kind == CrashKind.AppCrash && TryParse(e.Code, out var code) && Exceptions.TryGetValue(code, out var known)) return known.Meaning;
        return e.Kind switch
        {
            CrashKind.UnexpectedShutdown => "Windows lost power or was reset before it could shut down. Look for a failing power supply or battery, overheating, or a freeze that ended in holding the power button.",
            CrashKind.ServiceFailure => "A Windows background service ended without being asked to. Windows may have restarted it.",
            CrashKind.DriverReset => "Windows reset the graphics driver after it stopped responding. Repeated resets point to the graphics driver, overheating or a failing graphics card.",
            CrashKind.HardwareError => "The processor, memory or a device reported a hardware fault to Windows. Repeated ones are worth taking seriously.",
            CrashKind.StorageError => "Windows hit a problem reading or writing a disk. Check the disk's health and back up what matters.",
            CrashKind.LowMemory => "Windows ran short of memory and may have closed programs. Close programs you are not using, or add memory.",
            _ => ""
        };
    }
}
