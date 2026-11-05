// The elevated half of TidyWindow. All behavior lives in TidyWindow.Next.Core so it can be tested without a permission prompt.
// The one exception to "elevated" is --driver-scan: it only reads, and the app starts it as the ordinary account so the search can be ended on cancel.
return args.Length > 0 && args[0] == TidyWindow.Next.Core.RepairHost.Command
    ? await TidyWindow.Next.Core.RepairHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.StartupHost.Command
    ? await TidyWindow.Next.Core.StartupHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.BackgroundHost.Command
    ? await TidyWindow.Next.Core.BackgroundHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.FixHost.Command
    ? await TidyWindow.Next.Core.FixHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.TuneHost.Command
    ? await TidyWindow.Next.Core.TuneHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.DebloatHost.Command
    ? await TidyWindow.Next.Core.DebloatHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.DriverHost.Command
    ? await TidyWindow.Next.Core.DriverHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.DriverScanHost.Command
    ? await TidyWindow.Next.Core.DriverScanHost.RunAsync(args)
    : args.Length > 0 && args[0] == TidyWindow.Next.Core.MachinePathHost.Command
    ? await TidyWindow.Next.Core.MachinePathHost.RunAsync(args)
    : await TidyWindow.Next.Core.SystemCleanHost.RunAsync(args);
