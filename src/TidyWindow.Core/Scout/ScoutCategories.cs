using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// A kind of file people look for ("documents", "installers"), as the extensions that belong to it. A few extensions are only counted when the
/// name also fits: a <c>.exe</c> is an installer when it is called setup, install or the like, so programs do not drown the installers.
/// </summary>
public sealed class ScoutCategory
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string[] Aliases { get; init; }
    public required HashSet<string> Extensions { get; init; }
    /// <summary>Extensions that count only when the file name matches <see cref="Hint"/>.</summary>
    public HashSet<string> HintedExtensions { get; init; } = [];
    public Regex? Hint { get; init; }

    public bool Contains(string extension, string stem) =>
        extension.Length > 0 && (Extensions.Contains(extension) || Hint is not null && HintedExtensions.Contains(extension) && Hint.IsMatch(stem));

    /// <summary>Every extension the kind can have, for a search that may return a little too much and is narrowed afterwards.</summary>
    public IEnumerable<string> PossibleExtensions => Extensions.Concat(HintedExtensions);
}

public static class ScoutCategories
{
    public static IReadOnlyList<ScoutCategory> All { get; } =
    [
        Make("document", "Documents", "documents doc docs text word", "pdf doc docx docm dot dotx dotm odt rtf txt md markdown tex wpd wps pages"),
        Make("pdf", "PDFs", "pdfs", "pdf"),
        Make("spreadsheet", "Spreadsheets", "spreadsheets sheet sheets excel", "xls xlsx xlsm xlsb xlt xltx xltm ods csv tsv numbers"),
        Make("presentation", "Presentations", "presentations slides slide powerpoint deck decks", "ppt pptx pptm pps ppsx potx potm odp key"),
        Make("ebook", "E-books", "ebooks book books", "epub mobi azw azw3 fb2 djvu cbz cbr"),
        Make("image", "Images", "images photo photos picture pictures pic pics img", "jpg jpeg jpe jfif png gif bmp webp tif tiff heic heif avif ico svg raw cr2 cr3 nef arw dng orf rw2"),
        Make("design", "Design files", "designs graphics", "psd psb ai eps indd xd fig sketch xcf kra"),
        Make("video", "Videos", "videos movie movies film films clip clips", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg mpe 3gp m2ts vob ogv"),
        Make("audio", "Audio", "music song songs sound sounds", "mp3 wav flac aac ogg oga opus m4a wma aiff aif mid midi amr ape"),
        Make("subtitle", "Subtitles", "subtitles subs caption captions", "srt ass ssa vtt sub idx"),
        Make("archive", "Archives", "archives zip zips compressed", "zip rar 7z tar gz tgz bz2 tbz xz txz zst cab lz lzma z"),
        Make("installer", "Installers", "installers setup setups install installation", "msi msix msixbundle appx appxbundle msp msu dmg pkg deb rpm apk appimage",
            hinted: "exe", hint: "^(?!.*unins).*(setup|install|redist|bootstrap)"),
        Make("app", "Programs", "apps program programs executable executables exe application applications", "exe com scr"),
        Make("script", "Scripts", "scripts", "ps1 psm1 psd1 bat cmd sh bash zsh vbs vbe wsf au3 ahk"),
        Make("code", "Source code", "source sourcecode dev developer", "cs csx vb fs py pyw ipynb js jsx mjs cjs ts tsx java kt kts c h cpp cc cxx hpp hh go rs rb php swift dart lua pl r scala sql html htm css scss sass less vue svelte sln csproj vbproj fsproj xaml cshtml razor"),
        Make("config", "Config files", "configs configuration settings setting", "json xml yaml yml toml ini cfg conf config env properties plist reg"),
        Make("database", "Databases", "databases db", "db sqlite sqlite3 db3 mdb accdb sdf mdf ldf dbf frm ibd"),
        Make("log", "Logs", "logs", "log etl evtx wer"),
        Make("dump", "Crash dumps", "dumps crash crashes", "dmp mdmp hdmp"),
        Make("diskimage", "Disk images", "diskimages disk iso isos vm vhd", "iso img vhd vhdx vmdk vdi wim esd dmg"),
        Make("backup", "Backups", "backups bak", "bak bkp backup old orig"),
        Make("temp", "Temporary files", "temporary tmp junk", "tmp temp cache chk gid"),
        Make("partial", "Unfinished downloads", "incomplete unfinished partials", "crdownload part partial download opdownload"),
        Make("font", "Fonts", "fonts typeface", "ttf otf woff woff2 ttc fon pfb"),
        Make("model", "3D and CAD", "models cad 3d", "stl obj fbx blend dwg dxf step stp skp 3mf gltf glb ifc"),
        Make("email", "Email", "emails mail", "eml msg pst ost mbox emlx"),
        Make("certificate", "Certificates and keys", "certificates cert certs keys", "pem cer crt der pfx p12 key csr ppk"),
        Make("shortcut", "Shortcuts", "shortcuts link links", "lnk url"),
        Make("torrent", "Torrents", "torrents", "torrent"),
    ];

    private static ScoutCategory Make(string key, string label, string aliases, string extensions, string? hinted = null, string? hint = null) => new()
    {
        Key = key, Label = label,
        Aliases = aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        Extensions = [.. extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
        HintedExtensions = hinted is null ? [] : [.. hinted.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
        Hint = hint is null ? null : new Regex(hint, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
    };

    /// <summary>The kind a word names: its key or any alias, in singular or plural.</summary>
    public static ScoutCategory? Find(string word)
    {
        word = word.Trim().ToLowerInvariant();
        foreach (var candidate in word.EndsWith('s') && word.Length > 3 ? new[] { word, word[..^1] } : [word])
            foreach (var category in All)
                if (category.Key == candidate || Array.IndexOf(category.Aliases, candidate) >= 0) return category;
        return null;
    }
}
