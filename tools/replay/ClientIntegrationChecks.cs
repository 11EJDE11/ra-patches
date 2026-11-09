// Standalone checks against the actual built client and shared archive code.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using ClientCore;
using CnCNet.RAReplay;
using DTAClient.Domain;
using Rampastring.Tools;

internal static class ClientIntegrationChecks
{
    private static string assemblies;
    private static int passed;
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: ClientIntegrationChecks <client resources or build directory> <replay filename>");
            return 2;
        }
        assemblies = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            string name = new AssemblyName(request.Name).Name;
            foreach (string folder in new[] { assemblies, Path.Combine(assemblies, "Binaries", "Windows"), Path.Combine(assemblies, "Binaries") })
                foreach (string extension in new[] { ".dll", ".exe" })
                {
                    string path = Path.Combine(folder, name + extension);
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
            return null;
        };
        try { Run(args[1]); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("Failed: " + name);
        Console.WriteLine("PASS " + name);
        passed++;
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Check(true, name); return; }
        throw new Exception("Invalid data accepted: " + name);
    }

    private static void Run(string replayName)
    {
        Logger.Initialize(ProgramConstants.GamePath, "ReplayIntegrationChecks.log");
        var source = new RAReplayArchive(ReplayManager.GetReplayFile(replayName).FullName);
        byte[] events = source.ReadPayload("events.rarec");
        var native = RAReplayArchive.InspectNative(events);
        byte[] recordedSpawn = source.ReadPayload("spawn.ini");
        var embedded = new IniFile(new MemoryStream(recordedSpawn), new UTF8Encoding(false), applyBaseIni: false);
        var replay = new YRReplayGame(replayName);
        Check(replay.ParseInfo() && replay.IsPlayable, "unmodified client reader accepts RA1 metadata envelope");
        Check(replay.GUIName == embedded.GetStringValue("Settings", "UIMapName", Path.GetFileNameWithoutExtension(replayName)) &&
              replay.Players[0].Name == embedded.GetStringValue("Settings", "Name", ""), "map and recording player metadata");
        Check(replay.IsComplete && replay.TotalFrames == native.LastFrame, "frame count and completion status");
        string spawn;
        byte[] map;
        Check(replay.TryReadSpawnFiles(out spawn, out map) && Encoding.UTF8.GetBytes(spawn).SequenceEqual(recordedSpawn) &&
              map.SequenceEqual(source.ReadPayload("spawnmap.ini")), "client setup matches the verified RA1 payload byte for byte");
        Check(source.FindInputMismatches(ProgramConstants.GamePath).Count == 0,
              "RA1 launcher input fingerprints match");
        string engineHash = source.InputHashes["ra95.dat"];
        source.InputHashes["ra95.dat"] = new string('0', 64);
        Check(source.FindInputMismatches(ProgramConstants.GamePath).Contains("ra95.dat differs"), "changed engine detected before launch");
        source.InputHashes["ra95.dat"] = engineHash;
        Check(native.Records > 0, "native stream inspection");
        Check(ReplayManager.List().Any(item => item is YRReplayGame && item.FileName == replayName), "existing replay list accepts RA1 recording");

        var definitions = (IniFile)typeof(ClientConfiguration).GetField("clientDefinitionsIni", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ClientConfiguration.Instance);
        string originalDirectory = ReplayManager.DirectoryName;
        string originalExtension = ReplayManager.FileExtension;
        string temporaryRelative = "ReplayIntegrationChecks-" + Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(ProgramConstants.GamePath, temporaryRelative);
        Directory.CreateDirectory(temporary);
        string settings = temporaryRelative + "/settings.ini";
        File.WriteAllText(Path.Combine(temporary, "settings.ini"), "[Replays]\nRecordReplays=no\nMaxKeptReplays=1\nMaxReplayFolderSizeMB=0\n");
        definitions.SetStringValue("Settings", "ReplaysDirectory", temporaryRelative);
        definitions.SetStringValue("Settings", "ReplayFileExtension", "custom-ra");
        // The source file is deliberately outside the temporary replay directory.
        string original = Path.Combine(ProgramConstants.GamePath, originalDirectory, replayName);
        try
        {
            string imported = ReplayManager.Import(original);
            // Import requires the configured extension, so explicitly give this fixture that extension.
            Check(imported == null, "import respects configured extension");
            string custom = Path.Combine(temporary, "fixture.custom-ra");
            File.Copy(original, custom);
            Check(ReplayManager.HasReplayExtension(custom) && ReplayManager.List().Count == 1, "custom extension lists correctly");

            UserINISettings.Initialize(settings);
            var launch = new IniFile();
            ReplayManager.PrepareRecording(launch, "toggle check");
            Check(!launch.GetBooleanValue("Settings", "EnableReplayRecording", true) &&
                  launch.GetStringValue("Settings", "ReplayFileOut", "") == "", "recording off is honored");
            UserINISettings.Instance.RecordReplays.Value = true;
            ReplayManager.PrepareRecording(launch, "toggle check");
            Check(launch.GetBooleanValue("Settings", "EnableReplayRecording", false) &&
                  launch.GetStringValue("Settings", "ReplayFileOut", "").EndsWith(".custom-ra"), "recording on uses configured directory and extension");

            File.Copy(custom, Path.Combine(temporary, "newer.custom-ra"));
            File.SetLastWriteTimeUtc(custom, DateTime.UtcNow.AddMinutes(-5));
            ReplayManager.Prune();
            Check(Directory.GetFiles(temporary, "*.custom-ra").Length == 1, "existing storage pruning works for RA1");

            var metadata = source.Metadata.Where(pair => pair.Key != "Format" && pair.Key != "Version").ToDictionary(pair => pair.Key, pair => pair.Value);
            var payloads = new Dictionary<string, byte[]> { { "spawn.ini", source.ReadPayload("spawn.ini") }, { "spawnmap.ini", map }, { "events.rarec", events } };
            string roundTrip = Path.Combine(temporary, "round-trip.custom-ra");
            RAReplayArchive.Write(roundTrip, metadata, source.InputHashes, payloads);
            Check(new RAReplayArchive(roundTrip).ReadPayload("events.rarec").SequenceEqual(events), "container round trip preserves every native event byte");
            using (var zip = new ZipArchive(File.Open(roundTrip, FileMode.Open, FileAccess.ReadWrite), ZipArchiveMode.Update))
            {
                zip.GetEntry("events.rarec").Delete();
                using (Stream output = zip.CreateEntry("events.rarec").Open()) output.WriteByte(1);
            }
            Reject(() => new RAReplayArchive(roundTrip).ReadPayload("events.rarec"), "tampered payload rejected");
            Reject(() => RAReplayArchive.ValidateRelativePath("../ra95.dat"), "path traversal rejected");
            Reject(() => RAReplayArchive.ValidateRelativePath("folder/.. /ra95.dat"), "Windows normalized traversal rejected");
            byte[] malformed = (byte[])events.Clone();
            int header = 754 + 29 * BitConverter.ToInt32(events, 49);
            Array.Copy(BitConverter.GetBytes(-1), 0, malformed, header, 4);
            Reject(() => RAReplayArchive.InspectNative(malformed), "negative native event count rejected");

            string unsupported = Path.Combine(temporary, "future.custom-ra");
            using (var writer = new BinaryWriter(File.Create(unsupported)))
            {
                writer.Write(0x50525259u); writer.Write(2u); writer.Write(1128u);
            }
            var future = new YRReplayGame(Path.GetFileName(unsupported));
            Check(future.ParseInfo() && !future.IsPlayable && future.Status == ReplayStatus.UnsupportedVersion, "unsupported RA1 versions remain visible");

            string yrPath = Path.Combine(temporary, "yr-regression.custom-ra");
            WriteYRFixture(yrPath);
            var yr = new YRReplayGame(Path.GetFileName(yrPath));
            Check(yr.ParseInfo() && yr.IsPlayable && yr.Players[0].Name == "YR Player" && yr.TotalFrames == 900 && yr.FramesPerSecond == 45,
                  "YR reader regression");
            Check(ReplayManager.List().Any(item => item is YRReplayGame && item.FileName == Path.GetFileName(yrPath)), "YR remains the default format backend");
        }
        finally
        {
            definitions.SetStringValue("Settings", "ReplaysDirectory", originalDirectory);
            definitions.SetStringValue("Settings", "ReplayFileExtension", originalExtension);
            Directory.Delete(temporary, true); // Only this test's newly created GUID directory.
        }
        Console.WriteLine("All " + passed + " client/container checks passed");
    }

    private static byte[] Deflate(byte[] data)
    {
        using (var stream = new MemoryStream())
        {
            using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true)) deflate.Write(data, 0, data.Length);
            return stream.ToArray();
        }
    }

    private static void WriteYRFixture(string path)
    {
        byte[] spawn = Encoding.UTF8.GetBytes("[Settings]\nName=YR Player\nUIMapName=YR Fixture\nGamePackageVersion=YR Test\nSide=0\n");
        byte[] map = Encoding.ASCII.GetBytes("[Map]\nSize=0,0,80,80\n");
        byte[] compressedSpawn = Deflate(spawn), compressedMap = Deflate(map);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(new byte[1096]);
            writer.BaseStream.Position = 0;
            writer.Write(0x50525259u); writer.Write(1u); writer.Write(1096u);
            writer.BaseStream.Position = 1032;
            writer.Write(compressedSpawn.Length); writer.Write(compressedMap.Length); writer.Write(1);
            writer.Write((ulong)1700000000); writer.Write(900u); writer.Write(1u);
            writer.BaseStream.Position = 1084;
            writer.Write(spawn.Length); writer.Write(map.Length);
            writer.BaseStream.Position = 1096;
            writer.Write(compressedSpawn); writer.Write(compressedMap);
        }
    }
}
