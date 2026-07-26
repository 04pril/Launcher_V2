using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PixelmonLauncher;

internal sealed class MinecraftLaunchRequest
{
    public required string LauncherRoot { get; init; }
    public required string GameDirectory { get; init; }
    public required string VersionId { get; init; }
    public required string Nickname { get; init; }
    public int MaxMemoryMb { get; init; } = 4096;
}

internal sealed class MinecraftLaunchResult
{
    public required int ProcessId { get; init; }
    public required string JavaExecutable { get; init; }
    public required string ArgumentFile { get; init; }
    public required string DiagnosticLog { get; init; }
}

internal static class MinecraftLaunchService
{
    private const string LauncherName = "2004pril-pixelmon-launcher";
    private const string LauncherVersion = "1.0.0";
    private static readonly string OsName = OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static Task<MinecraftLaunchResult> LaunchAsync(MinecraftLaunchRequest request)
    {
        return Task.Run(() => Launch(request));
    }

    private static MinecraftLaunchResult Launch(MinecraftLaunchRequest request)
    {
        if (!Directory.Exists(request.GameDirectory))
        {
            throw new DirectoryNotFoundException("Game folder not found: " + request.GameDirectory);
        }

        var versionDir = Path.Combine(request.GameDirectory, "versions", request.VersionId);
        var versionJsonPath = Path.Combine(versionDir, request.VersionId + ".json");
        if (!File.Exists(versionJsonPath))
        {
            throw new FileNotFoundException("NeoForge version json not found.", versionJsonPath);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
        var root = document.RootElement;
        var versionId = ReadString(root, "id") ?? request.VersionId;
        var mainClass = ReadString(root, "mainClass")
            ?? throw new InvalidOperationException("Version json does not contain mainClass.");
        var assetsIndex = ReadString(root, "assets")
                          ?? (root.TryGetProperty("assetIndex", out var assetIndexElement)
                              ? ReadString(assetIndexElement, "id")
                              : null)
                          ?? "17";
        var versionType = ReadString(root, "type") ?? "release";

        var librariesDir = Path.Combine(request.GameDirectory, "libraries");
        var assetsDir = Path.Combine(request.GameDirectory, "assets");
        var nativesDir = Path.Combine(versionDir, "natives");
        var logsDir = Path.Combine(request.GameDirectory, "logs");
        Directory.CreateDirectory(nativesDir);
        Directory.CreateDirectory(logsDir);

        var libraryJars = CollectLibraryJars(root, librariesDir, out var nativeJars, out var missingLibraries);
        if (missingLibraries.Count > 0)
        {
            throw new FileNotFoundException(
                "Some Minecraft libraries are missing:" + Environment.NewLine +
                string.Join(Environment.NewLine, missingLibraries.Take(12)));
        }

        ExtractNatives(nativeJars, nativesDir);

        var clientJar = Path.Combine(versionDir, versionId + ".jar");
        if (!File.Exists(clientJar))
        {
            var jarId = ReadString(root, "jar") ?? versionId;
            clientJar = Path.Combine(request.GameDirectory, "versions", jarId, jarId + ".jar");
        }

        if (!File.Exists(clientJar))
        {
            throw new FileNotFoundException("Minecraft client jar not found.", clientJar);
        }

        var classpath = string.Join(
            Path.PathSeparator,
            libraryJars.Append(clientJar).Distinct(StringComparer.OrdinalIgnoreCase));

        var replacements = BuildReplacements(
            request,
            versionId,
            versionType,
            assetsIndex,
            librariesDir,
            assetsDir,
            nativesDir,
            classpath);

        var args = new List<string>
        {
            $"-Xmx{Math.Max(1024, request.MaxMemoryMb)}M",
            "-Xms512M",
            "-Dfile.encoding=UTF-8"
        };

        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var clientLogging) &&
            clientLogging.TryGetProperty("argument", out var loggingArgument) &&
            loggingArgument.ValueKind == JsonValueKind.String &&
            clientLogging.TryGetProperty("file", out var loggingFile))
        {
            var logConfigPath = Path.Combine(
                assetsDir,
                "log_configs",
                ReadString(loggingFile, "id") ?? "");

            if (File.Exists(logConfigPath))
            {
                args.Add(Expand(
                    loggingArgument.GetString()!,
                    WithReplacement(replacements, "path", logConfigPath)));
            }
        }

        if (root.TryGetProperty("arguments", out var arguments))
        {
            if (arguments.TryGetProperty("jvm", out var jvmArguments))
            {
                AppendArguments(jvmArguments, args, replacements);
            }

            args.Add(mainClass);

            if (arguments.TryGetProperty("game", out var gameArguments))
            {
                AppendArguments(gameArguments, args, replacements);
            }
        }
        else
        {
            args.Add("-cp");
            args.Add(classpath);
            args.Add(mainClass);
            AppendLegacyMinecraftArguments(args, replacements);
        }

        var javaExecutable = FindJavaExecutable(request.LauncherRoot);
        var argumentFile = Path.Combine(logsDir, "pixelmon-launcher-args.txt");
        File.WriteAllLines(
            argumentFile,
            args.Select(ToJavaArgFileToken),
            new UTF8Encoding(false));

        var diagnosticLog = Path.Combine(logsDir, "pixelmon-launcher-last-command.txt");
        File.WriteAllText(
            diagnosticLog,
            javaExecutable + Environment.NewLine +
            "@" + argumentFile + Environment.NewLine +
            Environment.NewLine +
            string.Join(Environment.NewLine, args.Select(ToJavaArgFileToken)),
            new UTF8Encoding(false));

        var startInfo = new ProcessStartInfo
        {
            FileName = javaExecutable,
            WorkingDirectory = request.GameDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("@" + argumentFile);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Minecraft process did not start.");

        return new MinecraftLaunchResult
        {
            ProcessId = process.Id,
            JavaExecutable = javaExecutable,
            ArgumentFile = argumentFile,
            DiagnosticLog = diagnosticLog
        };
    }

    private static Dictionary<string, string> BuildReplacements(
        MinecraftLaunchRequest request,
        string versionId,
        string versionType,
        string assetsIndex,
        string librariesDir,
        string assetsDir,
        string nativesDir,
        string classpath)
    {
        var offlineUuid = CreateOfflineUuid(request.Nickname);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = request.Nickname,
            ["version_name"] = versionId,
            ["game_directory"] = request.GameDirectory,
            ["assets_root"] = assetsDir,
            ["assets_index_name"] = assetsIndex,
            ["auth_uuid"] = offlineUuid,
            ["auth_access_token"] = "0",
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = "legacy",
            ["version_type"] = versionType,
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = LauncherName,
            ["launcher_version"] = LauncherVersion,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = librariesDir,
            ["resolution_width"] = "925",
            ["resolution_height"] = "530",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = ""
        };
    }

    private static void AppendLegacyMinecraftArguments(
        List<string> args,
        IReadOnlyDictionary<string, string> replacements)
    {
        args.AddRange(new[]
        {
            "--username", replacements["auth_player_name"],
            "--version", replacements["version_name"],
            "--gameDir", replacements["game_directory"],
            "--assetsDir", replacements["assets_root"],
            "--assetIndex", replacements["assets_index_name"],
            "--uuid", replacements["auth_uuid"],
            "--accessToken", replacements["auth_access_token"],
            "--userType", replacements["user_type"],
            "--versionType", replacements["version_type"]
        });
    }

    private static void AppendArguments(
        JsonElement arguments,
        List<string> target,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (arguments.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var argument in arguments.EnumerateArray())
        {
            AppendArgument(argument, target, replacements);
        }
    }

    private static void AppendArgument(
        JsonElement argument,
        List<string> target,
        IReadOnlyDictionary<string, string> replacements)
    {
        if (argument.ValueKind == JsonValueKind.String)
        {
            target.Add(Expand(argument.GetString()!, replacements));
            return;
        }

        if (argument.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (argument.TryGetProperty("rules", out var rules) && !Allows(rules))
        {
            return;
        }

        if (!argument.TryGetProperty("value", out var value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            target.Add(Expand(value.GetString()!, replacements));
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    target.Add(Expand(item.GetString()!, replacements));
                }
            }
        }
    }

    private static List<string> CollectLibraryJars(
        JsonElement root,
        string librariesDir,
        out List<string> nativeJars,
        out List<string> missingLibraries)
    {
        var jars = new List<string>();
        nativeJars = new List<string>();
        missingLibraries = new List<string>();

        if (!root.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
        {
            return jars;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            if (library.TryGetProperty("rules", out var rules) && !Allows(rules))
            {
                continue;
            }

            var artifactPath = ReadArtifactPath(library);
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                continue;
            }

            var jarPath = Path.Combine(
                librariesDir,
                artifactPath.Replace('/', Path.DirectorySeparatorChar));

            if (IsNativeLibrary(jarPath) && !NativeLibraryMatchesCurrentMachine(jarPath))
            {
                continue;
            }

            if (!File.Exists(jarPath))
            {
                missingLibraries.Add(jarPath);
                continue;
            }

            jars.Add(jarPath);
            if (IsNativeLibrary(jarPath))
            {
                nativeJars.Add(jarPath);
            }
        }

        return jars;
    }

    private static string? ReadArtifactPath(JsonElement library)
    {
        if (library.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("artifact", out var artifact) &&
            artifact.TryGetProperty("path", out var path) &&
            path.ValueKind == JsonValueKind.String)
        {
            return path.GetString();
        }

        var name = ReadString(library, "name");
        return !string.IsNullOrWhiteSpace(name) ? MavenNameToPath(name) : null;
    }

    private static string MavenNameToPath(string name)
    {
        var parts = name.Split(':', StringSplitOptions.None);
        if (parts.Length < 3)
        {
            return name.Replace('.', '/') + ".jar";
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    private static bool IsNativeLibrary(string jarPath)
    {
        return Path.GetFileName(jarPath).Contains("-natives-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NativeLibraryMatchesCurrentMachine(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath).ToLowerInvariant();

        if (OperatingSystem.IsWindows())
        {
            if (!fileName.Contains("-natives-windows", StringComparison.Ordinal))
            {
                return false;
            }

            if (fileName.Contains("-natives-windows-arm64", StringComparison.Ordinal))
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            }

            if (fileName.Contains("-natives-windows-x86", StringComparison.Ordinal))
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.X86;
            }

            return RuntimeInformation.ProcessArchitecture == Architecture.X64;
        }

        if (OperatingSystem.IsMacOS())
        {
            if (fileName.Contains("-natives-macos-arm64", StringComparison.Ordinal))
            {
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            }

            return fileName.Contains("-natives-macos", StringComparison.Ordinal);
        }

        return fileName.Contains("-natives-linux", StringComparison.Ordinal);
    }

    private static void ExtractNatives(IEnumerable<string> nativeJars, string nativesDir)
    {
        foreach (var nativeJar in nativeJars)
        {
            using var archive = ZipFile.OpenRead(nativeJar);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) ||
                    entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var destination = Path.Combine(nativesDir, entry.Name);
                if (File.Exists(destination))
                {
                    continue;
                }

                try
                {
                    entry.ExtractToFile(destination, false);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another extraction won the race.
                }
            }
        }
    }

    private static bool Allows(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind == JsonValueKind.Object && RuleMatches(rule))
            {
                allowed = string.Equals(
                    ReadString(rule, "action"),
                    "allow",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static bool RuleMatches(JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
        {
            var name = ReadString(os, "name");
            if (!string.IsNullOrEmpty(name) &&
                !string.Equals(name, OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var arch = ReadString(os, "arch");
            if (!string.IsNullOrEmpty(arch) && !ArchMatches(arch))
            {
                return false;
            }
        }

        if (rule.TryGetProperty("features", out var features) &&
            features.ValueKind == JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static bool ArchMatches(string arch)
    {
        return arch.ToLowerInvariant() switch
        {
            "x86" => RuntimeInformation.ProcessArchitecture == Architecture.X86,
            "x64" or "amd64" => RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "arm64" or "aarch64" => RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            _ => true
        };
    }

    private static string Expand(
        string value,
        IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var replacement in replacements)
        {
            value = value.Replace(
                "${" + replacement.Key + "}",
                replacement.Value,
                StringComparison.Ordinal);
        }

        return value;
    }

    private static string FindJavaExecutable(string launcherRoot)
    {
        var candidates = new[]
        {
            Path.Combine(launcherRoot, "jre", "x64", "bin", "javaw.exe"),
            Path.Combine(launcherRoot, "jre", "java-runtime-delta", "windows-x64", "java-runtime-delta", "bin", "javaw.exe"),
            Path.Combine(launcherRoot, "jre", "arm64", "bin", "javaw.exe"),
            "javaw.exe"
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) || File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Bundled Java was not found.");
    }

    private static string CreateOfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ToJavaArgFileToken(string value)
    {
        return "\"" +
               value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal) +
               "\"";
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static Dictionary<string, string> WithReplacement(
        IReadOnlyDictionary<string, string> source,
        string key,
        string value)
    {
        var copy = new Dictionary<string, string>(source, StringComparer.Ordinal)
        {
            [key] = value
        };
        return copy;
    }
}
