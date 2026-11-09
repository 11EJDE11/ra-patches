// Adapt the existing client's replay requests to the repaired Westwood engine.
// Capture launch inputs before the engine scrubs spawn.ini and package them
// with the native event stream; preserve scratch directories only when requested.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using CnCNet.RAReplay;

internal static class ClientReplayLauncher
{
    private static readonly Encoding IniEncoding = Encoding.GetEncoding(28591);
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string ClientRoot;

    [STAThread]
    private static int Main(string[] args)
    {
        ClientRoot = AppDomain.CurrentDomain.BaseDirectory;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, request) =>
        {
            string file = Path.Combine(ClientRoot, "Resources", "Binaries", new System.Reflection.AssemblyName(request.Name).Name + ".dll");
            return File.Exists(file) ? System.Reflection.Assembly.LoadFrom(file) : null;
        };
        FileStream matchLock = null;
        try
        {
            matchLock = AcquireMatchLock();
            if (args.Length >= 2 && args[0] == "--export-bundle")
            {
                ExportBundle(Path.GetFullPath(args[1]), null, true);
                return 0;
            }
            var launchSettings = File.ReadAllLines(Path.Combine(ClientRoot, "spawn.ini"), IniEncoding).ToList();
            string replayFile = Get(launchSettings, "Settings", "ReplayFile");
            if (args.Length >= 2 && args[0] == "--play-file")
                replayFile = args[1];
            if (!string.IsNullOrEmpty(replayFile))
                return PlayReplay(replayFile, args.Contains("--test-hidden"));

            bool record = IsTrue(Get(launchSettings, "Settings", "EnableReplayRecording"));
            if (IsTrue(Get(launchSettings, "Settings", "LoadSaveGame")) || IsTrue(Get(launchSettings, "Settings", "IsSinglePlayer")))
            {
                record = false;
                Log("Replay recording skipped for a campaign or saved-game start.");
            }
            if (!record)
            {
                Set(launchSettings, "Settings", "recording_mode", "0");
                File.WriteAllLines(Path.Combine(ClientRoot, "spawn.ini"), launchSettings, IniEncoding);
                if (args.Contains("--prepare-only"))
                    return 0;
                int liveExit = RunGame(ClientRoot, "ra95.dat", args);
                return liveExit == 1 ? 0 : liveExit;
            }

            string bundle = Capture();
            if (args.Contains("--prepare-only"))
                return 0;
            var forwarded = args.Where(a => a != "--test-hidden").ToList();
            if (forwarded.Count > 0 && string.Equals(Path.GetFileName(forwarded[0]), "ra95.dat", StringComparison.OrdinalIgnoreCase))
                forwarded.RemoveAt(0); // XNA client supplies the engine name to its launcher.
            if (!forwarded.Any(a => a.Equals("-SPAWN", StringComparison.OrdinalIgnoreCase)))
                forwarded.Add("-SPAWN");
            var start = new ProcessStartInfo(Path.Combine(bundle, "ra95.exe"), string.Join(" ", forwarded.Select(Quote)))
            {
                WorkingDirectory = bundle,
                UseShellExecute = false,
                WindowStyle = args.Contains("--test-hidden") ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
            string statistics = Path.Combine(ClientRoot, "stats.dmp");
            if (File.Exists(statistics))
                File.Delete(statistics);
            int gameExit;
            using (Process game = Process.Start(start))
            {
                Log("Game PID " + game.Id + "; bundle " + bundle);
                File.WriteAllText(Path.Combine(bundle, "game.pid"), game.Id.ToString(CultureInfo.InvariantCulture));
                game.WaitForExit();
                gameExit = game.ExitCode;
                Log("Game exit " + game.ExitCode + "; bundle " + bundle);
                File.WriteAllText(Path.Combine(bundle, "game-exit.txt"), game.ExitCode.ToString(CultureInfo.InvariantCulture));
            }
            string recordedStats = Path.Combine(bundle, "stats.dmp");
            if (File.Exists(recordedStats))
                File.Copy(recordedStats, statistics, true);
            string recording = Path.Combine(bundle, "ratest.rarec");
            if (!File.Exists(recording) || new FileInfo(recording).Length == 0)
                throw new InvalidOperationException("The game ended without producing a recording. See ReplayLauncher.log.");
            Log("Recording completed: " + recording);
            ExportBundle(bundle, Get(launchSettings, "Settings", "ReplayFileOut"), gameExit == 0 || gameExit == 1);
            File.WriteAllText(Path.Combine(ClientRoot, "ReplayRecordings", "latest-recording.txt"), bundle + Environment.NewLine);
            if (!KeepSnapshots())
            {
                File.Delete(Path.Combine(ClientRoot, "ReplayRecordings", "latest-recording.txt"));
                File.Delete(Path.Combine(ClientRoot, "ReplayRecordings", "latest-launch.txt"));
                RemoveScratch(bundle, "ReplayRecordings");
            }
            return 0; // The native game's status 1 also occurs after completed matches.
        }
        catch (Exception error)
        {
            Log(error.ToString());
            if (!args.Contains("--test-hidden") && !args.Contains("--prepare-only"))
                MessageBox.Show(error.Message + "\n\nDetails: " + Path.Combine(ClientRoot, "ReplayLauncher.log"),
                                "RA1 replay test", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            if (matchLock != null)
                matchLock.Dispose();
        }
    }

    private static FileStream AcquireMatchLock()
    {
        try
        {
            return new FileStream(Path.Combine(ClientRoot, "replay-launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("A match or replay is already running from this test client. Finish it before launching another.", error);
        }
    }

    private static string Capture()
    {
        string spawnPath = Path.Combine(ClientRoot, "spawn.ini");
        var spawn = File.ReadAllLines(spawnPath, IniEncoding).ToList();
        if (IsTrue(Get(spawn, "Settings", "LoadSaveGame")) || IsTrue(Get(spawn, "Settings", "IsSinglePlayer")))
            throw new InvalidOperationException("Use a fresh skirmish for this replay test; campaign and saved-game starts are not supported yet.");
        string scenario = Get(spawn, "Settings", "Scenario");
        string map = Path.GetFullPath(Path.Combine(ClientRoot, scenario));
        if (!map.StartsWith(ClientRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(map))
            throw new InvalidOperationException("The client-generated map could not be found: " + scenario);
        long seedValue;
        if (!long.TryParse(Get(spawn, "Settings", "Seed"), out seedValue))
            throw new InvalidOperationException("The launch settings do not contain a valid Seed.");
        if (seedValue == 0 && string.IsNullOrEmpty(Get(spawn, "Other1", "Name")))
        {
            byte[] bytes = new byte[4];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            seedValue = (BitConverter.ToUInt32(bytes, 0) & 0x7FFFFFFE) + 1;
            Set(spawn, "Settings", "Seed", seedValue.ToString(CultureInfo.InvariantCulture));
        }
        var build = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(ClientRoot, "ra95.dat.json")));
        if (Hash(Path.Combine(ClientRoot, "ra95.dat")) != (string)build["output_sha256"])
            throw new InvalidOperationException("The replay engine no longer matches its build manifest.");
        string recordings = Path.Combine(ClientRoot, "ReplayRecordings");
        string bundle = Path.Combine(recordings, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(bundle);
        var extensions = new HashSet<string>(new[] { ".mix", ".xdp", ".dll", ".ini" }, StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(ClientRoot))
            if (extensions.Contains(Path.GetExtension(file)))
                File.Copy(file, Path.Combine(bundle, Path.GetFileName(file)));
        CopyTree(Path.Combine(ClientRoot, "Shaders"), Path.Combine(bundle, "Shaders"));
        File.Copy(map, Path.Combine(bundle, "spawnmap.ini"), true);
        File.Copy(Path.Combine(ClientRoot, "ra95.dat"), Path.Combine(bundle, "ra95.exe"));
        File.Copy(Path.Combine(ClientRoot, "ra95.dat.json"), Path.Combine(bundle, "replay-build.json"));
        Set(spawn, "Settings", "Scenario", "spawnmap.ini");
        Set(spawn, "Settings", "recording_mode", "1");
        File.WriteAllLines(Path.Combine(bundle, "spawn.ini"), spawn, IniEncoding);
        File.WriteAllLines(Path.Combine(bundle, "record-spawn.ini"), spawn, IniEncoding);
        var hashes = new Dictionary<string, string>();
        foreach (string file in Directory.GetFiles(bundle, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file).ToLowerInvariant();
            if (name != "spawn.ini" && name != "redalert.ini" && name != "ddraw.ini")
                hashes.Add(file.Substring(bundle.Length + 1), Hash(file));
        }
        var manifest = new Dictionary<string, object>
        {
            { "format", "ra-native-seed-v1" },
            { "seed", unchecked((uint)seedValue) },
            { "hashes", hashes }
        };
        File.WriteAllText(Path.Combine(bundle, "replay-match.json"), Json.Serialize(manifest));
        File.WriteAllText(Path.Combine(recordings, "latest-launch.txt"), bundle + Environment.NewLine);
        Log("Captured launch inputs: " + bundle);
        return bundle;
    }

    private static string SafeClientPath(string relative)
    {
        RAReplayArchive.ValidateRelativePath(relative);
        string path = Path.GetFullPath(Path.Combine(ClientRoot, relative));
        if (!path.StartsWith(ClientRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Replay path escapes the client folder.");
        return path;
    }

    private static string ExportBundle(string bundle, string output, bool complete)
    {
        var spawn = File.ReadAllLines(Path.Combine(bundle, "record-spawn.ini"), IniEncoding).ToList();
        var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(bundle, "replay-match.json")));
        var hashes = (Dictionary<string, object>)manifest["hashes"];
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in hashes)
        {
            string name = pair.Key.Replace('\\', '/');
            if (name.Equals("record-spawn.ini", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("spawnmap.ini", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("replay-build.json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Hash(Path.Combine(bundle, pair.Key)) != (string)pair.Value)
                throw new InvalidDataException("Preserved replay input changed: " + pair.Key);
            inputs.Add(name.Equals("ra95.exe", StringComparison.OrdinalIgnoreCase) ? "ra95.dat" : name, (string)pair.Value);
        }
        byte[] events = File.ReadAllBytes(Path.Combine(bundle, "ratest.rarec"));
        var native = RAReplayArchive.InspectNative(events);
        int speed;
        if (!int.TryParse(Get(spawn, "Settings", "GameSpeed"), out speed))
            speed = 3;
        var metadata = new Dictionary<string, string>
        {
            { "RecordedAt", Directory.GetCreationTimeUtc(bundle).ToString("O", CultureInfo.InvariantCulture) },
            { "TotalFrames", native.LastFrame.ToString(CultureInfo.InvariantCulture) },
            { "FrameRate", (60 / Math.Max(1, Math.Min(60, speed))).ToString(CultureInfo.InvariantCulture) },
            { "Complete", complete ? "1" : "0" },
            { "Seed", native.Seed.ToString(CultureInfo.InvariantCulture) }
        };
        var payloads = new Dictionary<string, byte[]>
        {
            { "spawn.ini", new UTF8Encoding(false).GetBytes(string.Join("\n", spawn) + "\n") },
            { "spawnmap.ini", File.ReadAllBytes(Path.Combine(bundle, "spawnmap.ini")) },
            { "events.rarec", events }
        };
        string trace = Path.Combine(bundle, "replay-record.trace");
        if (File.Exists(trace))
            payloads.Add("checks.trace", File.ReadAllBytes(trace));
        if (string.IsNullOrEmpty(output))
        {
            var definitions = File.ReadAllLines(Path.Combine(ClientRoot, "Resources", "ClientDefinitions.ini")).ToList();
            string directory = Get(definitions, "Settings", "ReplaysDirectory");
            if (string.IsNullOrEmpty(directory)) directory = "Replays";
            string extension = Get(definitions, "Settings", "ReplayFileExtension");
            if (string.IsNullOrEmpty(extension)) extension = "rarp";
            string mapName = Get(spawn, "Settings", "UIMapName");
            string safeName = new string(mapName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            if (safeName.Length > 80) safeName = safeName.Substring(0, 80);
            string name = Path.GetFileName(bundle) + " " + safeName;
            output = Path.Combine(directory, name + "." + extension);
            for (int counter = 1; File.Exists(SafeClientPath(output)); counter++)
                output = Path.Combine(directory, name + " (" + counter + ")." + extension);
        }
        string destination = SafeClientPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        RAReplayArchive.Write(destination, metadata, inputs, payloads);
        File.WriteAllText(Path.Combine(bundle, "portable-replay.txt"), destination);
        File.WriteAllText(Path.Combine(ClientRoot, "latest-replay.txt"), output);
        Log("Portable replay saved: " + destination);
        return destination;
    }

    private static int RunGame(string directory, string executable, string[] args)
    {
        var forwarded = args.Where(a => a != "--test-hidden").ToList();
        if (forwarded.Count > 0 && Path.GetFileName(forwarded[0]).Equals("ra95.dat", StringComparison.OrdinalIgnoreCase))
            forwarded.RemoveAt(0);
        if (!forwarded.Any(a => a.Equals("-SPAWN", StringComparison.OrdinalIgnoreCase))) forwarded.Add("-SPAWN");
        if (!forwarded.Any(a => a.Equals("-CD", StringComparison.OrdinalIgnoreCase))) forwarded.Add("-CD");
        var start = new ProcessStartInfo(Path.Combine(directory, executable), string.Join(" ", forwarded.Select(Quote)))
        {
            WorkingDirectory = directory, UseShellExecute = false,
            WindowStyle = args.Contains("--test-hidden") ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        using (Process game = Process.Start(start))
        {
            Log("Game PID " + game.Id + "; directory " + directory);
            File.WriteAllText(Path.Combine(directory, "game.pid"), game.Id.ToString(CultureInfo.InvariantCulture));
            game.WaitForExit();
            Log("Game exit " + game.ExitCode + "; directory " + directory);
            return game.ExitCode;
        }
    }

    private static int PlayReplay(string relative, bool hidden)
    {
        var archive = new RAReplayArchive(SafeClientPath(relative));
        if (archive.Get("Version") != RAReplayArchive.Version.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("This RA1 replay format needs a different launcher version.");
        var mismatches = archive.FindInputMismatches(ClientRoot);
        if (mismatches.Count > 0)
            throw new InvalidDataException("Replay game files differ: " + mismatches[0]);
        byte[] events = archive.ReadPayload("events.rarec");
        var native = RAReplayArchive.InspectNative(events);
        if (native.Seed.ToString(CultureInfo.InvariantCulture) != archive.Get("Seed") ||
            native.LastFrame.ToString(CultureInfo.InvariantCulture) != archive.Get("TotalFrames"))
            throw new InvalidDataException("Replay metadata disagrees with its native event stream.");
        var spawn = new UTF8Encoding(false, true).GetString(archive.ReadPayload("spawn.ini")).Split('\n').ToList();
        if (IsTrue(Get(spawn, "Settings", "LoadSaveGame")) || IsTrue(Get(spawn, "Settings", "IsSinglePlayer")))
            throw new InvalidDataException("RA1 replay playback supports fresh skirmish and multiplayer games.");
        string directory = Path.Combine(ClientRoot, "ReplayPlayback", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(directory);
        var extensions = new HashSet<string>(new[] { ".mix", ".xdp", ".dll", ".ini" }, StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(ClientRoot))
        {
            string name = Path.GetFileName(file);
            if (extensions.Contains(Path.GetExtension(file)))
            {
                if (!new[] { "spawn.ini", "spawnmap.ini", "redalert.ini", "ddraw.ini" }.Contains(name.ToLowerInvariant()) &&
                    !archive.InputHashes.ContainsKey(name))
                    throw new InvalidDataException("An extra game input was not present in this replay: " + name);
                File.Copy(file, Path.Combine(directory, name));
            }
        }
        CopyTree(Path.Combine(ClientRoot, "Shaders"), Path.Combine(directory, "Shaders"));
        string optionsPath = Path.Combine(directory, "redalert.ini");
        var options = File.ReadAllLines(optionsPath, IniEncoding).ToList();
        Set(options, "Options", "SkipScoreScreen", "Yes");
        File.WriteAllLines(optionsPath, options, IniEncoding);
        File.Copy(Path.Combine(ClientRoot, "ra95.dat"), Path.Combine(directory, "ra95.exe"));
        File.WriteAllBytes(Path.Combine(directory, "spawnmap.ini"), archive.ReadPayload("spawnmap.ini"));
        File.WriteAllBytes(Path.Combine(directory, "ratest.rarec"), events);
        Set(spawn, "Settings", "Scenario", "spawnmap.ini");
        Set(spawn, "Settings", "recording_mode", "2");
        Set(spawn, "Settings", "EnableReplayRecording", "No");
        File.WriteAllLines(Path.Combine(directory, "spawn.ini"), spawn, IniEncoding);
        if (archive.HasChecks)
            File.WriteAllBytes(Path.Combine(directory, "replay-record.trace"), archive.ReadPayload("checks.trace"));
        File.WriteAllText(Path.Combine(ClientRoot, "ReplayPlayback", "latest-playback.txt"), directory);
        int exit = RunGame(directory, "ra95.exe", hidden ? new[] { "--test-hidden" } : new string[0]);
        if (exit != 0 && exit != 1)
            throw new InvalidOperationException("Replay game exited unexpectedly: " + exit);
        if (archive.HasChecks)
        {
            byte[] recorded = File.ReadAllBytes(Path.Combine(directory, "replay-record.trace"));
            byte[] played = File.ReadAllBytes(Path.Combine(directory, "replay-play.trace"));
            if (recorded.Length == 0 || recorded.Length % 12 != 0 || played.Length % 12 != 0)
                throw new InvalidDataException("Replay check trace is empty or truncated.");
            int shared = Math.Min(recorded.Length, played.Length);
            for (int offset = 0; offset < shared; offset++)
                if (recorded[offset] != played[offset])
                    throw new InvalidDataException("Replay diverged at sample " + (offset / 12));
            string result = played.Length >= recorded.Length
                ? "All " + (recorded.Length / 12) + " recorded frame/RNG/CRC samples match; playback has " + (played.Length / 12) + " samples"
                : "Playback stopped early; all " + (played.Length / 12) + " played samples match the recording";
            File.WriteAllText(Path.Combine(directory, "playback-validation.txt"), result);
            File.WriteAllText(Path.Combine(ClientRoot, "ReplayPlayback", "latest-validation.txt"), result);
            Log(result);
        }
        if (!KeepSnapshots())
        {
            File.Delete(Path.Combine(ClientRoot, "ReplayPlayback", "latest-playback.txt"));
            RemoveScratch(directory, "ReplayPlayback");
        }
        return 0;
    }

    private static bool KeepSnapshots()
    {
        var definitions = File.ReadAllLines(Path.Combine(ClientRoot, "Resources", "ClientDefinitions.ini")).ToList();
        return IsTrue(Get(definitions, "Settings", "ReplayKeepTestSnapshots"));
    }

    private static void RemoveScratch(string directory, string parentName)
    {
        string expectedParent = Path.GetFullPath(Path.Combine(ClientRoot, parentName)).TrimEnd(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(full).Length < 25)
            throw new InvalidOperationException("Refusing to remove a directory outside the replay scratch area.");
        try
        {
            // The client marks its renderer DLL read-only. File.Copy preserves
            // that attribute, so clear it only on our disposable copies.
            foreach (string file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(full, true); // Only this launch's newly created scratch directory.
        }
        catch (IOException error)
        {
            Log("Replay saved; scratch cleanup deferred for " + full + ": " + error.Message);
        }
        catch (UnauthorizedAccessException error)
        {
            Log("Replay saved; scratch cleanup deferred for " + full + ": " + error.Message);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        if (!Directory.Exists(source))
            return;
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source))
            CopyTree(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string Get(List<string> lines, string section, string key)
    {
        bool inside = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith("["))
                inside = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
            int equals = line.IndexOf('=');
            if (inside && equals > 0 && line.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Substring(equals + 1).Split(';')[0].Trim();
        }
        return "";
    }

    private static void Set(List<string> lines, string section, string key, string value)
    {
        bool inside = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("["))
            {
                if (inside)
                {
                    lines.Insert(i, key + "=" + value);
                    return;
                }
                inside = line.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
            }
            int equals = line.IndexOf('=');
            if (inside && equals > 0 && line.Substring(0, equals).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                return;
            }
        }
        if (!inside)
            lines.Add("[" + section + "]");
        lines.Add(key + "=" + value);
    }

    private static bool IsTrue(string value)
    {
        return value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string file)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(file))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    private static void Log(string message)
    {
        File.AppendAllText(Path.Combine(ClientRoot, "ReplayLauncher.log"), DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
    }
}
