using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AntigravityChinese.Core;

public static class ElectronIntegrity
{
    private sealed record Resource(long Offset, byte[] Bytes);

    private static Resource? ReadResource(string executable)
    {
        var module = LoadLibraryEx(executable, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Antigravity 的可执行文件资源。");
        try
        {
            var resource = FindResource(module, "ElectronAsar", "Integrity");
            if (resource == IntPtr.Zero) return null;
            var size = SizeofResource(module, resource);
            var pointer = LockResource(LoadResource(module, resource));
            if (pointer == IntPtr.Zero || size == 0) throw new InvalidDataException("Electron 完整性资源为空。");
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var offset = pointer.ToInt64() - (module.ToInt64() & ~3L);
            using var file = File.OpenRead(executable);
            if (offset < 0 || offset + bytes.Length > file.Length) throw new InvalidDataException("Electron 资源文件范围无效。");
            file.Position = offset;
            var original = new byte[bytes.Length];
            file.ReadExactly(original);
            if (!original.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("无法确认 Electron 资源的文件位置。");
            return new(offset, bytes);
        }
        finally { FreeLibrary(module); }
    }

    public static string? ReadHeaderHash(string executable)
    {
        var resource = ReadResource(executable);
        if (resource is null) return null;
        using var json = JsonDocument.Parse(resource.Bytes.AsSpan().TrimEnd((byte)0).ToArray());
        foreach (var entry in json.RootElement.EnumerateArray())
            if (entry.GetProperty("file").GetString()?.Replace('\\', '/') == "resources/app.asar")
                return entry.GetProperty("value").GetString();
        throw new InvalidDataException("Electron 完整性资源中缺少 app.asar。");
    }

    public static void CreatePatchedExecutable(string source, string output, string originalHeaderHash, string newHeaderHash)
    {
        var resource = ReadResource(source);
        var oldHash = ReadHeaderHash(source);
        if (oldHash is not null && !string.Equals(oldHash, originalHeaderHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Antigravity 的原始资源与可执行文件校验不一致，请先恢复或修复 Antigravity App。");
        File.Copy(source, output, false);
        if (resource is null) return;
        var needle = Encoding.ASCII.GetBytes(oldHash!);
        var relativeOffset = resource.Bytes.AsSpan().IndexOf(needle);
        if (relativeOffset < 0 || resource.Bytes.AsSpan(relativeOffset + needle.Length).IndexOf(needle) >= 0 || newHeaderHash.Length != needle.Length)
            throw new InvalidDataException("无法唯一定位 Electron 的 ASAR 校验值。");
        using (var outputFile = new FileStream(output, FileMode.Open, FileAccess.Write))
        {
            outputFile.Position = resource.Offset + relativeOffset;
            outputFile.Write(Encoding.ASCII.GetBytes(newHeaderHash));
            outputFile.Flush(true);
        }
        if (ReadHeaderHash(output) != newHeaderHash) throw new InvalidDataException("写入后的 Electron 校验值验证失败。");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, string name, string type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
