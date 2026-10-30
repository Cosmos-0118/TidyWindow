// The elevated half of TidyWindow. All behavior lives in TidyWindow.Next.Core so it can be tested without a permission prompt.
return args.Length > 0 && args[0] == TidyWindow.Next.Core.MachinePathHost.Command
    ? await TidyWindow.Next.Core.MachinePathHost.RunAsync(args)
    : await TidyWindow.Next.Core.SystemCleanHost.RunAsync(args);
