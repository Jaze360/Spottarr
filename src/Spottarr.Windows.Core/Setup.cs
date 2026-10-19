using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Quartz;

namespace Spottarr.Windows;

public static class Setup
{
    public static string Home =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Spottarr"
        );
    public static string ConfigFile => Path.Combine(Home, "windows.json");
    public static readonly string[] SecretPaths =
    [
        "Usenet:Username",
        "Usenet:Password",
        "Newznab:ApiKey",
        "Admin:ApiKey",
    ];

    public static JsonObject Defaults(string serverDirectory)
    {
        var root = JsonNode
            .Parse(File.ReadAllText(Path.Combine(serverDirectory, "appsettings.json")))!
            .AsObject();
        root["Urls"] = "http://127.0.0.1:8383";
        root["Database"] = new JsonObject
        {
            ["Provider"] = "Sqlite",
            ["ConnectionString"] = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Home, "spottarr.db"),
            }.ToString(),
        };
        root["Spotnet"]!["RetrieveAfter"] = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        root["Newznab"]!["ApiKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return root;
    }

    public static string Get(JsonObject root, string path) =>
        path.Split(':').Aggregate((JsonNode?)root, (n, key) => n?[key])?.ToString() ?? "";

    public static void Set(JsonObject root, string path, string value)
    {
        var parts = path.Split(':');
        JsonObject node = root;
        foreach (var part in parts[..^1])
        {
            node[part] ??= new JsonObject();
            node = node[part]!.AsObject();
        }
        node[parts[^1]] = value;
    }

    public static void Validate(JsonObject root)
    {
        void Required(string key)
        {
            if (string.IsNullOrWhiteSpace(Get(root, key)))
                throw new ArgumentException($"Vul {key} in.");
        }
        void Number(string key, int min, int max)
        {
            if (!int.TryParse(Get(root, key), out var n) || n < min || n > max)
                throw new ArgumentException($"{key}: verwacht {min}–{max}.");
        }
        Required("Usenet:Hostname");
        Required("Usenet:Username");
        Required("Usenet:Password");
        if (Uri.CheckHostName(Get(root, "Usenet:Hostname")) == UriHostNameType.Unknown)
            throw new ArgumentException("Ongeldige Usenet-hostnaam.");
        Number("Usenet:Port", 1, 65535);
        Number("Usenet:MaxConnections", 1, 100);
        Number("Spotnet:ImportBatchSize", 1, 100000);
        Number("Spotnet:RetentionDays", 0, 36500);
        foreach (var key in new[] { "Usenet:UseTls", "Spotnet:ImportAdultContent" })
            if (!bool.TryParse(Get(root, key), out _))
                throw new ArgumentException($"{key}: verwacht true of false.");
        foreach (var key in new[] { "SpotGroup", "CommentGroup", "ReportGroup", "NzbGroup" })
            if (
                !System.Text.RegularExpressions.Regex.IsMatch(
                    Get(root, "Spotnet:" + key),
                    @"^[A-Za-z0-9][A-Za-z0-9.+_-]*$"
                )
            )
                throw new ArgumentException("Ongeldige Spotnet-groep: " + key);
        foreach (var key in new[] { "ImportSpotsSchedule", "CleanUpSpotsSchedule" })
        {
            try
            {
                _ = new CronExpression(Get(root, "Spotnet:" + key));
            }
            catch (FormatException)
            {
                throw new ArgumentException("Ongeldige Quartz-cron: " + key);
            }
        }
        if (!DateTimeOffset.TryParse(Get(root, "Spotnet:RetrieveAfter"), out _))
            throw new ArgumentException("Ongeldige ophaaldatum.");
        if (
            !Uri.TryCreate(Get(root, "Urls"), UriKind.Absolute, out var url)
            || url.Scheme != "http"
            || url.Host != "127.0.0.1"
            || url.Port < 1
            || url.AbsolutePath != "/"
            || url.Query != ""
            || url.Fragment != ""
            || url.UserInfo != ""
        )
            throw new ArgumentException("Gebruik http://127.0.0.1:poort voor de lokale server.");
        if (Get(root, "Database:Provider") != "Sqlite")
            throw new ArgumentException("Deze wizard ondersteunt SQLite.");
        var db = new SqliteConnectionStringBuilder(Get(root, "Database:ConnectionString"));
        if (!Path.IsPathFullyQualified(db.DataSource) || db.Mode == SqliteOpenMode.Memory)
            throw new ArgumentException("SQLite vereist een absoluut bestandspad.");
        if (
            !Path.GetFullPath(db.DataSource)
                .StartsWith(
                    Path.GetFullPath(Home) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                )
        )
            throw new ArgumentException(
                "Bewaar SQLite in de beveiligde Spottarr-map onder %LOCALAPPDATA%\\Spottarr."
            );
        if (
            !new[] { "None", "Deflate", "OperationExtensions" }.Contains(
                Get(root, "Usenet:Compression")
            )
        )
            throw new ArgumentException("Ongeldige compressiemethode.");
        Required("Newznab:ApiKey");
    }

    public static void RestrictDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (
            var sid in new[]
            {
                WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            }
        )
            acl.AddAccessRule(
                new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                )
            );
        new DirectoryInfo(directory).SetAccessControl(acl);
    }

    public static void Save(JsonObject settings, string file)
    {
        Validate(settings);
        var safe = (JsonObject)settings.DeepClone();
        var secrets = new JsonObject();
        foreach (var path in SecretPaths)
        {
            secrets[path] = Get(safe, path);
            Set(safe, path, "");
        }
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(secrets);
        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
        var envelope = new JsonObject
        {
            ["Version"] = 1,
            ["Settings"] = safe,
            ["ProtectedSecrets"] = Convert.ToBase64String(cipher),
        };
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        RestrictDirectory(directory);
        var temp = Path.Combine(directory, Guid.NewGuid() + ".tmp");
        try
        {
            using (
                var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough
                )
            )
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    envelope,
                    new JsonSerializerOptions { WriteIndented = true }
                );
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(file))
                File.Replace(temp, file, null);
            else
                File.Move(temp, file);
            var fileAcl = new FileSecurity();
            fileAcl.SetAccessRuleProtection(true, false);
            foreach (
                var sid in new[]
                {
                    WindowsIdentity.GetCurrent().User!,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                }
            )
                fileAcl.AddAccessRule(
                    new FileSystemAccessRule(
                        sid,
                        FileSystemRights.FullControl,
                        AccessControlType.Allow
                    )
                );
            new FileInfo(file).SetAccessControl(fileAcl);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static JsonObject Load(string file)
    {
        var envelope = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        if (envelope["Version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Onbekende configuratieversie.");
        var root = envelope["Settings"]!.AsObject();
        var clear = ProtectedData.Unprotect(
            Convert.FromBase64String(envelope["ProtectedSecrets"]!.GetValue<string>()),
            null,
            DataProtectionScope.CurrentUser
        );
        try
        {
            var secrets = JsonNode.Parse(clear)!.AsObject();
            foreach (var path in SecretPaths)
                Set(root, path, secrets[path]!.GetValue<string>());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
        Validate(root);
        return root;
    }

    public static ProcessStartInfo ServerStart(JsonObject settings, string serverDirectory)
    {
        Validate(settings);
        var exe = Path.Combine(Path.GetFullPath(serverDirectory), "Spottarr.Web.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Spottarr.Web.exe ontbreekt.");
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        void Flatten(JsonObject node, string prefix)
        {
            foreach (var pair in node)
            {
                var key = prefix + pair.Key;
                if (pair.Value is JsonObject child)
                    Flatten(child, key + "__");
                else if (pair.Value != null)
                    start.Environment[key] = pair.Value.ToString();
            }
        }
        Flatten(settings, "");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = Get(settings, "Urls");
        start.Environment["Logging__LogLevel__Usenet"] = "None";
        return start;
    }

    public static string TaskXml(string launcher)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] content) => new(ns + name, content);
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        return new XDocument(
            E(
                "Task",
                new XAttribute("version", "1.2"),
                E("Triggers", E("LogonTrigger", E("Enabled", "true"), E("UserId", sid))),
                E(
                    "Principals",
                    E(
                        "Principal",
                        new XAttribute("id", "Author"),
                        E("UserId", sid),
                        E("LogonType", "InteractiveToken"),
                        E("RunLevel", "LeastPrivilege")
                    )
                ),
                E(
                    "Settings",
                    E("MultipleInstancesPolicy", "IgnoreNew"),
                    E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"),
                    E("ExecutionTimeLimit", "PT0S"),
                    E("RestartOnFailure", E("Interval", "PT1M"), E("Count", "3"))
                ),
                E(
                    "Actions",
                    new XAttribute("Context", "Author"),
                    E(
                        "Exec",
                        E("Command", Path.GetFullPath(launcher)),
                        E("Arguments", "--run"),
                        E("WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(launcher))!)
                    )
                )
            )
        ).ToString();
    }
}
