using System.Drawing;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: IconGenerator <input.png> <output.ico>");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };

var iconEntries = new List<(int Size, byte[] Data)>();

using (var source = Image.FromFile(inputPath))
{
    foreach (var size in sizes)
    {
        using var bitmap = new Bitmap(source, new Size(size, size));
        using var ms = new MemoryStream();
        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        iconEntries.Add((size, ms.ToArray()));
    }
}

using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
using var writer = new BinaryWriter(fs);

ushort reserved = 0;
ushort type = 1; // icon
ushort count = (ushort)iconEntries.Count;

writer.Write(reserved);
writer.Write(type);
writer.Write(count);

var offset = 6 + (16 * iconEntries.Count);

foreach (var entry in iconEntries)
{
    var size = entry.Size;
    var data = entry.Data;
    writer.Write((byte)(size >= 256 ? 0 : size)); // width
    writer.Write((byte)(size >= 256 ? 0 : size)); // height
    writer.Write((byte)0); // colors
    writer.Write((byte)0); // reserved
    writer.Write((ushort)0); // planes
    writer.Write((ushort)32); // bit count
    writer.Write((uint)data.Length); // bytes in resource
    writer.Write((uint)offset); // image offset
    offset += data.Length;
}

foreach (var entry in iconEntries)
{
    writer.Write(entry.Data);
}

Console.WriteLine($"Icon written to {outputPath}");
return 0;