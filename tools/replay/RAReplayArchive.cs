#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CnCNet.RAReplay
{
    /// <summary>
    /// RA1 replay container shared with the native-game launcher. A ZIP with a
    /// versioned manifest, UTF-8 spawn.ini, original map bytes and native events.
    /// Only explicitly named entries are read; archive paths are never extracted.
    /// </summary>
    public sealed class RAReplayArchive
    {
        public const string Format = "CnCNet-RA1-Replay";
        public const int Version = 1;
        public const int MaxSetupBytes = 32 * 1024 * 1024;
        public const int MaxEventsBytes = 256 * 1024 * 1024;
        public const int MaxTraceBytes = 64 * 1024 * 1024;
        public const int ClientHeaderSize = 1128;
        private const uint ClientMagic = 0x50525259; // Existing client's YRRP metadata envelope.
        private const uint NativeMagic = 0x52314152; // RA1R extension: never a YR event stream.

        private readonly string path;
        public Dictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> InputHashes { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> payloadHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        public RAReplayArchive(string path)
        {
            this.path = path;
            using (var zip = Open())
            {
                string manifest = new UTF8Encoding(false, true).GetString(ReadEntry(zip, "replay.ini", 128 * 1024));
                string section = string.Empty;
                foreach (string raw in manifest.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = line.Substring(1, line.Length - 2);
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;

                    string key = line.Substring(0, equals).Trim();
                    string value = line.Substring(equals + 1).Trim();
                    if (section == "Replay")
                        Metadata.Add(key, value);
                    else if (section == "PayloadHashes")
                        payloadHashes.Add(key, value);
                    else if (section == "GameFileHashes")
                    {
                        int separator = value.LastIndexOf('|');
                        if (separator <= 0)
                            throw new InvalidDataException("Invalid replay dependency hash");

                        string name = value.Substring(0, separator);
                        ValidateRelativePath(name);
                        InputHashes.Add(name, value.Substring(separator + 1));
                    }
                }
            }

            if (Get("Format") != Format)
                throw new InvalidDataException("Not an RA1 replay container");
        }

        public string Get(string key) => Metadata.TryGetValue(key, out string? value) ? value : string.Empty;

        public byte[] ReadPayload(string name)
        {
            int limit;
            switch (name)
            {
                case "spawn.ini":
                case "spawnmap.ini": limit = MaxSetupBytes; break;
                case "events.rarec": limit = MaxEventsBytes; break;
                case "checks.trace": limit = MaxTraceBytes; break;
                default: throw new InvalidDataException("Unknown replay payload");
            }

            using (var zip = Open())
            {
                byte[] data = ReadEntry(zip, name, limit);
                if (!payloadHashes.TryGetValue(name, out string? expected) || !Hash(data).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Replay payload hash mismatch: " + name);

                return data;
            }
        }

        public bool HasChecks => payloadHashes.ContainsKey("checks.trace");

        public List<string> FindInputMismatches(string gameDirectory)
        {
            var mismatches = new List<string>();
            if (!InputHashes.ContainsKey("ra95.dat"))
                mismatches.Add("Replay is missing its engine fingerprint");

            foreach (var input in InputHashes)
            {
                string file = Path.Combine(gameDirectory, input.Key);
                if (!File.Exists(file))
                {
                    mismatches.Add(input.Key + " is missing locally");
                    continue;
                }

                using (var hash = SHA256.Create())
                using (var stream = File.OpenRead(file))
                {
                    string actual = string.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
                    if (!actual.Equals(input.Value, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add(input.Key + " differs");
                }
            }

            foreach (string path in Directory.GetFiles(gameDirectory))
            {
                string name = Path.GetFileName(path);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if ((extension == ".mix" || extension == ".xdp" || extension == ".dll" || extension == ".ini") &&
                    !new[] { "spawn.ini", "spawnmap.ini", "redalert.ini", "ddraw.ini" }.Contains(name.ToLowerInvariant()) &&
                    !InputHashes.ContainsKey(name))
                    mismatches.Add(name + " was not recorded with this replay");
            }

            return mismatches;
        }

        private ZipArchive Open()
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                byte[] header = new byte[ClientHeaderSize];
                ReadExactly(stream, header);
                if (ReadUInt32(header, 0) != ClientMagic || ReadUInt32(header, 4) != 1 ||
                    ReadUInt32(header, 8) != ClientHeaderSize || ReadUInt32(header, 1096) != NativeMagic ||
                    ReadUInt32(header, 1100) != Version)
                    throw new InvalidDataException("Not a supported RA1 client-compatible replay");
                long zipOffset = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header, 1112, 8));
                long zipLength = BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(header, 1120, 8));
                if (zipOffset != ClientHeaderSize + (long)ReadUInt32(header, 1032) + ReadUInt32(header, 1036) ||
                    zipLength <= 0 || zipOffset + zipLength != stream.Length)
                    throw new InvalidDataException("Invalid RA1 payload offset or length");

                var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                if (zip.Entries.Count > 32 || zip.Entries.Sum(entry => entry.Length) > 512L * 1024 * 1024 ||
                    zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != zip.Entries.Count)
                {
                    zip.Dispose();
                    throw new InvalidDataException("Invalid or oversized replay archive");
                }

                return zip;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static byte[] ReadEntry(ZipArchive zip, string name, int limit)
        {
            ZipArchiveEntry? entry = zip.GetEntry(name);
            if (entry == null || entry.Length <= 0 || entry.Length > limit)
                throw new InvalidDataException("Missing or oversized replay entry: " + name);

            byte[] bytes = new byte[(int)entry.Length];
            using (Stream input = entry.Open())
            {
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0)
                        throw new InvalidDataException("Truncated replay entry: " + name);

                    offset += read;
                }

                if (input.ReadByte() != -1)
                    throw new InvalidDataException("Replay entry exceeds its declared length: " + name);
            }

            return bytes;
        }

        public static void Write(string path, IDictionary<string, string> metadata,
            IDictionary<string, string> inputs, IDictionary<string, byte[]> payloads)
        {
            foreach (string required in new[] { "spawn.ini", "spawnmap.ini", "events.rarec" })
            {
                if (!payloads.ContainsKey(required))
                    throw new InvalidDataException("Missing replay payload: " + required);
            }

            var manifest = new StringBuilder("[Replay]\nFormat=" + Format + "\nVersion=" + Version + "\n");
            foreach (var value in metadata)
            {
                if (value.Key == "Format" || value.Key == "Version" || value.Key.IndexOfAny(new[] { '\r', '\n', '=' }) >= 0 ||
                    value.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidDataException("Invalid replay metadata");

                manifest.Append(value.Key).Append('=').Append(value.Value).Append('\n');
            }

            manifest.Append("[PayloadHashes]\n");
            foreach (var payload in payloads)
                manifest.Append(payload.Key).Append('=').Append(Hash(payload.Value)).Append('\n');

            manifest.Append("[GameFileHashes]\n");
            int index = 0;
            foreach (var input in inputs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidateRelativePath(input.Key);
                manifest.Append(index++).Append('=').Append(input.Key).Append('|').Append(input.Value).Append('\n');
            }

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    byte[] compressedSpawn = Deflate(payloads["spawn.ini"]);
                    byte[] compressedMap = Deflate(payloads["spawnmap.ini"]);
                    byte[] header = BuildClientHeader(metadata, payloads, compressedSpawn, compressedMap);
                    stream.Write(header, 0, header.Length);
                    stream.Write(compressedSpawn, 0, compressedSpawn.Length);
                    stream.Write(compressedMap, 0, compressedMap.Length);
                    long zipOffset = stream.Position;
                    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    {
                        var entries = new Dictionary<string, byte[]>(payloads) { { "replay.ini", new UTF8Encoding(false).GetBytes(manifest.ToString()) } };
                        foreach (var payload in entries)
                        {
                            using (Stream output = zip.CreateEntry(payload.Key, CompressionLevel.Optimal).Open())
                                output.Write(payload.Value, 0, payload.Value.Length);
                        }
                    }

                    long zipLength = stream.Length - zipOffset;
                    stream.Position = 1120;
                    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                        writer.Write(zipLength);
                }

                File.Move(temporary, path); // Do not replace an existing recording.
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static byte[] BuildClientHeader(IDictionary<string, string> metadata, IDictionary<string, byte[]> payloads,
            byte[] spawn, byte[] map)
        {
            var header = new byte[ClientHeaderSize];
            using (var writer = new BinaryWriter(new MemoryStream(header)))
            {
                writer.Write(ClientMagic);
                writer.Write(1u);
                writer.Write((uint)ClientHeaderSize);
                writer.Write((uint)payloads["events.rarec"][0]);
                writer.Write(0u); // YR-only object counter; RA1 uses its native header instead.
                writer.Write(uint.Parse(metadata["Seed"], CultureInfo.InvariantCulture));
                writer.BaseStream.Position = 1032;
                writer.Write((uint)spawn.Length);
                writer.Write((uint)map.Length);
                int fps = int.Parse(metadata["FrameRate"], CultureInfo.InvariantCulture);
                int[] clientRates = { 60, 45, 30, 20, 15, 12, 10 };
                int speed = Array.IndexOf(clientRates, fps);
                writer.Write((uint)(speed < 0 ? 6 : speed));
                DateTime time = DateTime.Parse(metadata["RecordedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                writer.Write((ulong)new DateTimeOffset(time.ToUniversalTime()).ToUnixTimeSeconds());
                writer.Write(uint.Parse(metadata["TotalFrames"], CultureInfo.InvariantCulture));
                writer.Write(metadata["Complete"] == "1" ? 1u : 0u);
                writer.BaseStream.Position = 1084;
                writer.Write((uint)payloads["spawn.ini"].Length);
                writer.Write((uint)payloads["spawnmap.ini"].Length);
                writer.BaseStream.Position = 1096;
                writer.Write(NativeMagic);
                writer.Write((uint)Version);
                writer.Write(1u); // Repaired Westwood 3.03 native-stream ABI.
                writer.Write(0u);
                writer.Write((long)(ClientHeaderSize + spawn.Length + map.Length));
                writer.Write(0L); // Filled after the ZIP is finalized.
            }
            return header;
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var stream = new MemoryStream())
            {
                using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true))
                    deflate.Write(data, 0, data.Length);
                return stream.ToArray();
            }
        }

        private static uint ReadUInt32(byte[] data, int offset)
            => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, offset, sizeof(uint)));

        private static void ReadExactly(Stream stream, byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = stream.Read(data, offset, data.Length - offset);
                if (read <= 0)
                    throw new InvalidDataException("Truncated client replay header");
                offset += read;
            }
        }

        public static void ValidateRelativePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.IndexOfAny(new[] { ':', '|', '\r', '\n', '=' }) >= 0 ||
                name.Replace('\\', '/').Split('/').Any(part => part.Length == 0 || part == "." || part == ".." ||
                    part != part.TrimEnd(' ', '.') || part.Any(ch => ch < 32 || "<>\"?*".Contains(ch))))
                throw new InvalidDataException("Invalid replay dependency path");
        }

        public static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public sealed class NativeInfo
        {
            public uint Seed { get; internal set; }
            public int LastFrame { get; internal set; }
            public int Records { get; internal set; }
        }

        /// <summary>Validate the repaired Westwood 3.03 stream, including its two skirmish frame-zero entries.</summary>
        public static NativeInfo InspectNative(byte[] data)
        {
            if (data.Length < 53)
                throw new InvalidDataException("Truncated native replay header");

            int mode = data[0], protocol = data[1], rate = ReadInt32(data, 2), players = ReadInt32(data, 49);
            if ((mode != 3 && mode != 4 && mode != 5) || protocol > 2 || rate < 1 || rate > 60 || players < 1 || players > 8)
                throw new InvalidDataException("Invalid native replay session");

            int cursor = 754 + 29 * players;
            if (data.Length < cursor)
                throw new InvalidDataException("Truncated native replay header");

            var result = new NativeInfo { Seed = unchecked((uint)ReadInt32(data, 53 + 29 * players + 8)) };
            int step = mode != 5 && protocol == 2 ? rate : 1;
            int frame = mode == 5 ? 0 : step;
            while (cursor < data.Length)
            {
                if (data.Length - cursor < 4)
                    throw new InvalidDataException("Truncated native event count");

                int count = ReadInt32(data, cursor);
                cursor += 4;
                if (count < 0 || count > 4096 || (long)cursor + count * 20 > data.Length)
                    throw new InvalidDataException("Invalid native event count");

                for (int end = cursor + count * 20; cursor < end; cursor += 20)
                {
                    uint bits = unchecked((uint)ReadInt32(data, cursor + 1));
                    if (data[cursor] > 32 || ((bits >> 26) & 31) >= 20 || (bits & 0x03FFFFFF) != frame)
                        throw new InvalidDataException("Invalid native event sequence");
                }

                result.LastFrame = frame;
                result.Records++;
                if (mode != 5 || result.Records != 1)
                    frame += step;
            }

            if (result.Records == 0)
                throw new InvalidDataException("Empty native replay stream");

            return result;
        }

        private static int ReadInt32(byte[] data, int offset)
            => BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(data, offset, sizeof(int)));
    }
}
