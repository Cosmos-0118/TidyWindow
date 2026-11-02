using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>What Windows says about a file's Authenticode signature when the signing certificate is also checked for revocation.</summary>
public enum InstallerSignature { Valid, Unsigned, Invalid, Revoked, Unchecked, Unknown }

/// <summary>
/// Whether an installer file may be run or kept with administrator rights. Windows has to find its signature valid with the signing certificate not revoked
/// (a check that goes out for the certificate's revocation list, so a PC that is offline may not be able to answer, which counts as "not trusted"), the signer has to be
/// the program's publisher whenever the program's own record names one, and the file's own details have to name the program. A file in a place only administrators
/// can write to (C:\Windows\Installer, Program Files, the Windows-wide Package Cache) is trusted without a signature: nobody else could have put it there.
/// Every answer is a plain sentence for the repair's list of steps; null means the file may be used.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class InstallerTrust
{
    /// <summary>Whether the file is signed, with the signing certificate checked for revocation, and who signed it (empty when that cannot be read).</summary>
    public static (InstallerSignature Signature, string Signer) Verify(string path)
    {
        if (!File.Exists(path)) return (InstallerSignature.Unknown, "");
        var file = IntPtr.Zero; var data = IntPtr.Zero; var name = IntPtr.Zero;
        var opened = false;
        try
        {
            name = Marshal.StringToHGlobalUni(path);
            var info = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = name };
            file = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, file, false);
            // WTD_UI_NONE, WTD_REVOKE_WHOLECHAIN, WTD_CHOICE_FILE, WTD_STATEACTION_VERIFY (so the signer can be read afterwards), WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT.
            // Unlike the Startup page's check, this one may go online for revocation lists: what is checked here is about to run or be kept with administrator rights.
            var trust = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, RevocationChecks = 1, UnionChoice = 1, File = file, StateAction = StateVerify, ProviderFlags = 0x40 };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trust, data, false);
            var result = WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
            opened = true;
            return result switch
            {
                0 => (InstallerSignature.Valid, SignerOf(data)),
                unchecked((int)0x800B0100) or unchecked((int)0x800B0001) or unchecked((int)0x800B0003) => (InstallerSignature.Unsigned, ""),
                unchecked((int)0x800B010C) => (InstallerSignature.Revoked, ""),
                unchecked((int)0x800B010E) or unchecked((int)0x80092012) or unchecked((int)0x80092013) => (InstallerSignature.Unchecked, ""),
                _ => (InstallerSignature.Invalid, "")
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { return (InstallerSignature.Unknown, ""); }
        finally
        {
            if (opened) CloseState(data);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            if (file != IntPtr.Zero) Marshal.FreeHGlobal(file);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>Why the file's signature does not vouch for it as the program's publisher's, or null when it does. The publisher check is skipped when the program's record names none.</summary>
    public static string? SignatureProblem(string path, string publisher)
    {
        var (signature, signer) = Verify(path);
        var problem = signature switch
        {
            InstallerSignature.Valid => null,
            InstallerSignature.Unsigned => "is not signed",
            InstallerSignature.Revoked => "was signed with a certificate that has since been revoked",
            InstallerSignature.Unchecked => "has a signing certificate that could not be checked for revocation (this PC may be offline)",
            InstallerSignature.Invalid => "has a signature Windows does not accept",
            _ => "could not have its signature checked"
        };
        if (problem is not null) return problem;
        var wanted = PublisherNames.Tidy(publisher);
        if (wanted.Length > 0 && !SamePublisher(signer, wanted)) return $"is signed by {(signer.Length > 0 ? signer : "someone else")}, not by {wanted}, which the program's record names as its publisher";
        return null;
    }

    /// <summary>
    /// Why a setup program must not be used for this program, or null when it may be: its own details (product, description, company, file name) have to name the program,
    /// and its signature and signer have to pass <see cref="SignatureProblem"/>. With <paramref name="wholeName"/> every word of the program's name before its version is needed;
    /// without it the first is enough, for a setup program that installs several differently named parts.
    /// </summary>
    public static string? Problem(string path, string programName, string publisher, bool wholeName)
    {
        string details;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            details = string.Join(' ', info.ProductName, info.FileDescription, info.InternalName, info.OriginalFilename, info.CompanyName, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return "could not be read"; }
        if (!Names(details, programName, wholeName)) return "does not name the program in its own details";
        return SignatureProblem(path, publisher);
    }

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "corporation", "inc", "ltd", "llc", "the", "for", "and", "of", "app", "setup", "installer", "bit", "x64", "x86", "amd64", "arm64"
    };

    private static readonly HashSet<string> CompanySuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "corp", "corporation", "ltd", "limited", "llc", "gmbh", "ag", "sa", "srl", "sro", "bv", "co", "company", "plc", "pty", "pbc", "the", "of", "and"
    };

    /// <summary>
    /// The words that tell a program apart: those before its version, with the parts in brackets (architecture, language) and words every program shares ("Microsoft", "Setup") left out.
    /// "Microsoft Visual C++ 2015-2022 Redistributable (x64)" is told apart by "Visual C"; "7-Zip 24.08 (x64)" by "Zip"; "HP Support Assistant" by all three words.
    /// </summary>
    internal static IReadOnlyList<string> Brand(string programName)
    {
        var brand = new List<string>();
        foreach (var word in Words().Matches(Bracketed().Replace(programName, " ")).Select(x => x.Value))
        {
            if (word.All(char.IsAsciiDigit)) { if (brand.Count > 0) break; continue; }
            if (!Filler.Contains(word)) brand.Add(word);
        }
        return brand;
    }

    /// <summary>Whether a file's own details name the program. A short word (R, HP, LG) has to stand alone as a word, so it is not found inside any other.</summary>
    internal static bool Names(string details, string programName, bool wholeName)
    {
        var brand = Brand(programName);
        if (brand.Count == 0) return false;
        var words = Words().Matches(details).Select(x => x.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (wholeName ? brand : brand.Take(1)).All(word => word.Length < 3 ? words.Contains(word) : details.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether two company names are the same company, give or take the legal suffix and a word of the longer one ("Oracle America, Inc." is "Oracle Corporation"; "OpenJS Foundation" is not "Node.js Foundation").</summary>
    internal static bool SamePublisher(string signer, string publisher)
    {
        var a = Company(signer); var b = Company(publisher);
        if (a.Count == 0 || b.Count == 0) return false;
        return a.All(b.Contains) || b.All(a.Contains);
    }

    private static HashSet<string> Company(string name) => Words().Matches(name).Select(x => x.Value).Where(x => !CompanySuffix.Contains(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Bracketed();

    // ---- Places only administrators can write to ----

    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>The folders Windows and installers keep installer files in, which ordinary accounts cannot write to.</summary>
    public static IReadOnlyList<string> ProtectedRoots()
    {
        var roots = new List<string>();
        void Add(string? root) { if (!string.IsNullOrEmpty(root)) roots.Add(Path.TrimEndingDirectorySeparator(root)); }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"));
        return roots.Where(x => x.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Whether only administrators could have put the file where it is: it lies under one of <paramref name="roots"/> (the <see cref="ProtectedRoots"/> unless a test says otherwise),
    /// every folder from that root down and the file itself are owned by administrators or Windows, and nothing on the way is a link to somewhere else.
    /// </summary>
    public static bool InProtectedPlace(string path, IReadOnlyList<string>? roots = null)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var root = (roots ?? ProtectedRoots()).Select(x => Path.TrimEndingDirectorySeparator(x)).FirstOrDefault(x => full.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase));
        if (root is null) return false;
        try
        {
            for (var folder = Path.GetDirectoryName(full); folder is not null && folder.Length >= root.Length; folder = Path.GetDirectoryName(folder))
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || IsLink(info) || !OwnedByWindows(info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)))) return false;
                if (string.Equals(Path.TrimEndingDirectorySeparator(folder), root, StringComparison.OrdinalIgnoreCase)) break;
            }
            var file = new FileInfo(full);
            return file.Exists && !IsLink(file) && OwnedByWindows(file.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { return false; }
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool OwnedByWindows(IdentityReference? owner) =>
        owner is SecurityIdentifier sid && (sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.Equals(TrustedInstaller));

    // ---- WinVerifyTrust ----

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint StateVerify = 1, StateClose = 2;

    /// <summary>The company on the certificate that signed the file, read from the open verification state. Empty if it cannot be read.</summary>
    private static string SignerOf(IntPtr data)
    {
        try
        {
            var state = Marshal.PtrToStructure<WinTrustData>(data).StateData;
            if (state == IntPtr.Zero) return "";
            var provider = WTHelperProvDataFromStateData(state);
            if (provider == IntPtr.Zero) return "";
            var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return "";
            var link = WTHelperGetProvCertFromChain(signer, 0);
            if (link == IntPtr.Zero) return "";
            // CRYPT_PROVIDER_CERT is a size field followed by the certificate context pointer.
            var context = Marshal.ReadIntPtr(link, IntPtr.Size);
            if (context == IntPtr.Zero) return "";
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(context);
            return PublisherNames.FromSubject(certificate.Subject);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or AccessViolationException or SEHException or EntryPointNotFoundException) { return ""; }
    }

    private static void CloseState(IntPtr data)
    {
        try
        {
            var trust = Marshal.PtrToStructure<WinTrustData>(data);
            trust.StateAction = StateClose;
            Marshal.StructureToPtr(trust, data, false);
            WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo { public uint Size; public IntPtr FilePath; public IntPtr FileHandle; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks; public uint UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);
}
