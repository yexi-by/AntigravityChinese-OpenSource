using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AntigravityChinese.Core;

public sealed class AsarArchive : IDisposable
{
    private readonly FileStream stream;
    private readonly long payloadOffset;
    private readonly Dictionary<string, JsonObject> files = new(StringComparer.Ordinal);
    public JsonObject Header { get; }
    public string HeaderHash { get; }
    public IEnumerable<string> PackedFiles => files.Where(p => p.Value["offset"] is not null).Select(p => p.Key);

    public AsarArchive(string path)
    {
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> prefix = stackalloc byte[16];
            stream.ReadExactly(prefix);
            var pickleSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
            var jsonSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != 4 || pickleSize < 8 || jsonSize > pickleSize - 8 ||
                pickleSize > stream.Length - 8 || jsonSize > int.MaxValue)
                throw new InvalidDataException("Antigravity 的 ASAR 资源头无效。");
            var headerBytes = new byte[jsonSize];
            stream.ReadExactly(headerBytes);
            HeaderHash = Convert.ToHexStringLower(SHA256.HashData(headerBytes));
            Header = JsonNode.Parse(headerBytes) as JsonObject ?? throw new InvalidDataException("ASAR 资源头不是对象。");
            payloadOffset = 8L + pickleSize;
            Collect(Header["files"] as JsonObject ?? throw new InvalidDataException("ASAR 缺少文件目录。"), "");
        }
        catch { stream.Dispose(); throw; }
    }

    private void Collect(JsonObject directory, string prefix)
    {
        foreach (var entry in directory)
        {
            if (entry.Value is not JsonObject metadata) throw new InvalidDataException("ASAR 文件目录无效。");
            var name = prefix + entry.Key;
            if (metadata["files"] is JsonObject nested) Collect(nested, name + "/");
            else files.Add(name, metadata);
        }
    }

    public byte[] Read(string path)
    {
        var metadata = files[path];
        if (metadata["offset"] is null) throw new InvalidDataException($"资源不在 ASAR 内：{path}");
        var offset = long.Parse(metadata["offset"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        var size = metadata["size"]!.GetValue<long>();
        if (size > int.MaxValue || offset < 0 || payloadOffset + offset + size > stream.Length)
            throw new InvalidDataException($"ASAR 文件范围无效：{path}");
        var bytes = new byte[size];
        stream.Position = payloadOffset + offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    public string ReadText(string path) => new UTF8Encoding(false, true).GetString(Read(path));

    public void WritePatched(string output, IReadOnlyDictionary<string, byte[]> replacements,
        CancellationToken cancellationToken = default)
    {
        var header = (JsonObject)Header.DeepClone();
        var ordered = files.Where(p => p.Value["offset"] is not null)
            .OrderBy(p => long.Parse(p.Value["offset"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        foreach (var (name, data) in replacements.Where(p => !files.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var parts = name.Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\')))
                throw new InvalidDataException("新增资源路径无效。");
            var directory = header["files"]!.AsObject();
            foreach (var part in parts[..^1])
            {
                directory[part] ??= new JsonObject { ["files"] = new JsonObject() };
                directory = directory[part]!["files"]!.AsObject();
            }
            var metadata = new JsonObject { ["offset"] = "0", ["size"] = (long)data.Length };
            directory.Add(parts[^1], metadata);
            ordered.Add(new(name, metadata));
        }
        long offset = 0;
        foreach (var (name, metadata) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Find(header, name);
            target["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var size = metadata["size"]!.GetValue<long>();
            if (replacements.TryGetValue(name, out var data))
            {
                size = data.Length;
                target["size"] = size;
                var blockSize = metadata["integrity"]?["blockSize"]?.GetValue<int>() ?? 4 * 1024 * 1024;
                var blocks = new JsonArray();
                for (var blockOffset = 0; blockOffset < data.Length; blockOffset += blockSize)
                    blocks.Add(Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(blockOffset,
                        Math.Min(blockSize, data.Length - blockOffset)))));
                target["integrity"] = new JsonObject
                {
                    ["algorithm"] = "SHA256", ["hash"] = Convert.ToHexStringLower(SHA256.HashData(data)),
                    ["blockSize"] = blockSize, ["blocks"] = blocks
                };
            }
            offset = checked(offset + size);
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(header,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var alignedLength = checked((json.Length + 3) / 4 * 4);
        using var outputStream = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(outputStream, Encoding.UTF8, true);
        writer.Write(4u);
        writer.Write(checked((uint)(8 + alignedLength)));
        writer.Write(checked((uint)(4 + alignedLength)));
        writer.Write(checked((uint)json.Length));
        writer.Write(json);
        writer.Write(new byte[alignedLength - json.Length]);
        foreach (var (name, _) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outputStream.Write(replacements.TryGetValue(name, out var data) ? data : Read(name));
        }
        outputStream.Flush(true);
    }

    private static JsonObject Find(JsonObject header, string name)
    {
        JsonObject current = header;
        foreach (var part in name.Split('/')) current = (JsonObject)current["files"]![part]!;
        return current;
    }

    public static string FileHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    public void Dispose() => stream.Dispose();
}
