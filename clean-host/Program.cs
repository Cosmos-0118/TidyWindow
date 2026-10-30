// The elevated half of Cleanup. All behavior lives in TidyWindow.Next.Core so it can be tested without a permission prompt.
return await TidyWindow.Next.Core.SystemCleanHost.RunAsync(args);
