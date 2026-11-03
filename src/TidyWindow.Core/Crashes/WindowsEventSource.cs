using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace TidyWindow.Next.Core;

/// <summary>Where the events come from. Newest first, so a caller that only wants the latest few can stop early.</summary>
public interface ICrashEventSource
{
    /// <exception cref="EventSourceException">The log could not be read (missing, or access refused).</exception>
    IReadOnlyList<RawEvent> Read(string log, string query, int limit, CancellationToken token);
}

public sealed class EventSourceException(string message, int code) : IOException(message)
{
    public int Code { get; } = code;
    /// <summary>The log does not exist on this PC, which is normal for a component Windows has not installed.</summary>
    public bool Missing => Code is 15007 or 2;
}

/// <summary>
/// Reads the Windows event log with the system's own query engine (wevtapi). Windows does the filtering, so only the matching events are ever copied
/// into this program, and nothing runs until a scan is asked for: no service, no watcher, no polling.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventSource : ICrashEventSource
{
    private const uint ChannelPath = 0x1, ReverseDirection = 0x200, TolerateErrors = 0x1000, RenderXml = 1;
    private const int InsufficientBuffer = 122, NoMoreItems = 259, Timeout = 1460;

    private sealed class EvtHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EvtHandle() : base(true) { }
        protected override bool ReleaseHandle() => EvtClose(handle);
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern EvtHandle EvtQuery(IntPtr session, string path, string query, uint flags);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtNext(EvtHandle results, uint size, [Out] IntPtr[] events, uint timeout, uint flags, out uint returned);
    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, uint flags, uint size, IntPtr buffer, out uint used, out uint properties);
    [DllImport("wevtapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EvtClose(IntPtr handle);

    public IReadOnlyList<RawEvent> Read(string log, string query, int limit, CancellationToken token)
    {
        var found = new List<RawEvent>();
        using var results = EvtQuery(IntPtr.Zero, log, query, ChannelPath | ReverseDirection | TolerateErrors);
        if (results.IsInvalid) { var code = Marshal.GetLastWin32Error(); throw new EventSourceException($"The {log} log could not be read (error {code}).", code); }
        var batch = new IntPtr[64];
        var size = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            while (found.Count < limit)
            {
                token.ThrowIfCancellationRequested();
                if (!EvtNext(results, (uint)batch.Length, batch, 2000, 0, out var returned))
                {
                    var code = Marshal.GetLastWin32Error();
                    if (code == NoMoreItems) break;
                    if (code == Timeout) continue;
                    throw new EventSourceException($"The {log} log could not be read (error {code}).", code);
                }
                try
                {
                    for (var i = 0; i < returned && found.Count < limit; i++)
                    {
                        var xml = Render(batch[i], ref buffer, ref size);
                        if (EventXml.Parse(xml, log) is { } raw) found.Add(raw);
                    }
                }
                finally { for (var i = 0; i < returned; i++) EvtClose(batch[i]); }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return found;
    }

    private static string Render(IntPtr handle, ref IntPtr buffer, ref int size)
    {
        if (!EvtRender(IntPtr.Zero, handle, RenderXml, (uint)size, buffer, out var used, out _))
        {
            var code = Marshal.GetLastWin32Error();
            if (code != InsufficientBuffer) throw new EventSourceException($"An event could not be read (error {code}).", code);
            buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)used); size = (int)used;
            if (!EvtRender(IntPtr.Zero, handle, RenderXml, (uint)size, buffer, out used, out _))
                throw new EventSourceException($"An event could not be read (error {Marshal.GetLastWin32Error()}).", Marshal.GetLastWin32Error());
        }
        return Marshal.PtrToStringUni(buffer) ?? "";
    }
}

/// <summary>Reads the fields this feature needs out of one event's XML, in a single pass and without building a document.</summary>
public static class EventXml
{
    public static RawEvent? Parse(string xml, string log)
    {
        if (xml.Length == 0) return null;
        string provider = "";
        int id = 0;
        long record = 0;
        var time = DateTimeOffset.MinValue;
        var data = new List<KeyValuePair<string, string>>();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                switch (reader.LocalName)
                {
                    case "Provider": provider = reader.GetAttribute("Name") ?? ""; reader.Read(); break;
                    case "TimeCreated":
                        DateTimeOffset.TryParse(reader.GetAttribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);
                        reader.Read(); break;
                    case "EventID": _ = int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id); break;
                    case "EventRecordID": _ = long.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out record); break;
                    case "Data":
                        var name = reader.GetAttribute("Name") ?? "";
                        var empty = reader.IsEmptyElement;
                        data.Add(new(name, empty ? "" : reader.ReadElementContentAsString()));
                        if (empty) reader.Read();
                        break;
                    default: reader.Read(); break;
                }
            }
        }
        catch (XmlException) { return null; }
        return provider.Length == 0 || id == 0 || time == DateTimeOffset.MinValue ? null : new RawEvent(log, record, id, provider, time, data);
    }
}

/// <summary>Runs work so it never competes with what the person is doing: lowest CPU priority, and background mode for disk and memory.</summary>
public static class LowImpact
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);
    private const int BackgroundBegin = 0x10000, BackgroundEnd = 0x20000;

    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A thread of its own, because a pool thread's priority is not ours to change.
        var thread = new Thread(() =>
        {
            var background = OperatingSystem.IsWindows() && SetThreadPriority(GetCurrentThread(), BackgroundBegin);
            try { done.SetResult(work()); }
            catch (OperationCanceledException ex) { done.SetCanceled(ex.CancellationToken); }
            catch (Exception ex) { done.SetException(ex); }
            finally { if (background) SetThreadPriority(GetCurrentThread(), BackgroundEnd); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Crash scan" };
        thread.Start();
        return done.Task;
    }
}
