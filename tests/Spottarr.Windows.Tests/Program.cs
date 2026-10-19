using System.Security.AccessControl;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Spottarr.Windows;

if (args.Length == 1 && args[0] == "--sleep")
{
    Thread.Sleep(60000);
    return;
}
var repository = Path.GetFullPath(args[0]);
var scratch = Path.Combine(Path.GetTempPath(), "Spottarr.Tests." + Guid.NewGuid());
Directory.CreateDirectory(scratch);
var checks = 0;
void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Reject(Action action, string name)
{
    try
    {
        action();
    }
    catch
    {
        Check(true, name);
        return;
    }
    throw new Exception("Expected rejection: " + name);
}
try
{
    var root = Setup.Defaults(repository);
    Setup.Set(root, "Usenet:Hostname", "news.example.org");
    Setup.Set(root, "Usenet:Username", "private-user");
    Setup.Set(root, "Usenet:Password", "SECRET-$`\"<&-test");
    Setup.Validate(root);
    using (var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToJsonString())))
    {
        var config = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = config
            .GetSection("Usenet")
            .Get<Spottarr.Configuration.Options.UsenetOptions>()!;
        Check(
            options.UseTls
                && options.Port == 563
                && options.Password == Setup.Get(root, "Usenet:Password"),
            "settings bind to actual upstream configuration class"
        );
    }
    Check(true, "upstream defaults validate after credentials");
    foreach (
        var (path, value) in new[]
        {
            ("Usenet:Port", "0"),
            ("Usenet:Port", "65536"),
            ("Usenet:UseTls", "yes"),
            ("Spotnet:ImportSpotsSchedule", "* * * * *"),
            ("Database:ConnectionString", "Data Source=:memory:"),
            ("Urls", "http://0.0.0.0:8383"),
            ("Urls", "http://127.0.0.1:8383/?secret=1"),
            ("Spotnet:SpotGroup", "bad\r\ngroup"),
        }
    )
    {
        var invalid = (JsonObject)root.DeepClone();
        Setup.Set(invalid, path, value);
        Reject(
            () => Setup.Validate(invalid),
            "reject " + path + " / " + value.Replace('\r', '_').Replace('\n', '_')
        );
    }
    var file = Path.Combine(scratch, "settings.json");
    root["UnknownExtension"] = new JsonObject { ["Enabled"] = true };
    Setup.Save(root, file);
    var stored = File.ReadAllText(file);
    Check(
        new FileInfo(file).GetAccessControl().AreAccessRulesProtected,
        "configuration file has explicit private ACL"
    );
    Check(
        !stored.Contains("private-user", StringComparison.Ordinal)
            && !stored.Contains("SECRET", StringComparison.Ordinal)
            && !stored.Contains(Setup.Get(root, "Newznab:ApiKey"), StringComparison.Ordinal),
        "no plaintext credentials on disk"
    );
    Check(
        new DirectoryInfo(scratch).GetAccessControl().AreAccessRulesProtected,
        "directory inheritance disabled"
    );
    var loaded = Setup.Load(file);
    Check(
        Setup.Get(loaded, "Usenet:Password") == Setup.Get(root, "Usenet:Password"),
        "DPAPI roundtrip preserves special characters"
    );
    Check(loaded["UnknownExtension"]!["Enabled"]!.GetValue<bool>(), "unknown settings preserved");
    Setup.Set(loaded, "Usenet:Port", "564");
    Setup.Save(loaded, file);
    Check(Setup.Get(Setup.Load(file), "Usenet:Port") == "564", "atomic replacement readable");
    var corrupt = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    corrupt["ProtectedSecrets"] = "AAAA";
    File.WriteAllText(file, corrupt.ToJsonString());
    Reject(() => Setup.Load(file), "tampered ciphertext rejected");
    File.WriteAllText(file, "{broken");
    Reject(() => Setup.Load(file), "invalid JSON rejected");
    File.WriteAllText(Path.Combine(scratch, "Spottarr.Web.exe"), "test placeholder");
    var process = Setup.ServerStart(root, scratch);
    Check(
        process.ArgumentList.Count == 0 && process.Arguments == "" && !process.UseShellExecute,
        "no credentials in command line"
    );
    Check(
        process.Environment["Usenet__Password"] == Setup.Get(root, "Usenet:Password"),
        "credentials reach child environment"
    );
    var task = XDocument.Parse(Setup.TaskXml(Path.Combine(scratch, "A & B", "wizard.exe")));
    XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    Check(
        task.Descendants(ns + "LogonType").Single().Value == "InteractiveToken"
            && task.Descendants(ns + "RunLevel").Single().Value == "LeastPrivilege",
        "task uses current user without password or elevation"
    );
    Check(
        task.Descendants(ns + "Command").Single().Value.Contains("A & B", StringComparison.Ordinal),
        "task XML escapes special paths"
    );
    Check(
        task.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S",
        "task has no default 72-hour timeout"
    );
    var taskFile = Path.Combine(scratch, "task.xml");
    File.WriteAllText(taskFile, task.ToString());
    var taskName = "Spottarr.Configurator.Test." + Guid.NewGuid();
    int Scheduler(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var command = System.Diagnostics.Process.Start(start)!;
        var output = command.StandardOutput.ReadToEndAsync();
        var error = command.StandardError.ReadToEndAsync();
        command.WaitForExit();
        Task.WaitAll(output, error);
        return command.ExitCode;
    }
    try
    {
        Check(
            Scheduler("/Create", "/TN", taskName, "/XML", taskFile) == 0,
            "Windows Task Scheduler accepts generated task XML"
        );
    }
    finally
    {
        Scheduler("/Delete", "/TN", taskName, "/F");
    }
    var childStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    if (
        Path.GetFileNameWithoutExtension(Environment.ProcessPath!)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    )
        childStart.ArgumentList.Add(
            typeof(Setup).Assembly.Location.Replace(
                "Spottarr.Windows.Core.dll",
                "Spottarr.Windows.Tests.dll",
                StringComparison.Ordinal
            )
        );
    childStart.ArgumentList.Add("--sleep");
    using (var child = System.Diagnostics.Process.Start(childStart)!)
    {
        using (var job = new ServerJob())
        {
            job.Attach(child);
            Check(!child.HasExited, "job accepts live process");
        }
        Check(child.WaitForExit(5000), "closing launcher job terminates child");
    }
    Exception? renderError = null;
    var uiThread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            using var wizard = new Wizard(repository, Path.Combine(scratch, "not-created.json"));
            wizard.Show();
            var tabs = wizard.Controls.OfType<TabControl>().Single();
            var destination = Path.Combine(repository, "artifacts", "wizard-preview");
            Directory.CreateDirectory(destination);
            for (var index = 0; index < tabs.TabCount; index++)
            {
                tabs.SelectedIndex = index;
                Application.DoEvents();
                using var bitmap = new Bitmap(wizard.Width, wizard.Height);
                wizard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, wizard.Size));
                bitmap.Save(Path.Combine(destination, $"step-{index + 1}.png"));
            }
            Check(tabs.TabCount == 4, "four wizard pages render");
            IEnumerable<Control> Descendants(Control c) =>
                c
                    .Controls.Cast<Control>()
                    .SelectMany(child => new[] { child }.Concat(Descendants(child)));
            Check(
                Descendants(wizard).OfType<TextBox>().Count(box => box.UseSystemPasswordChar) == 5,
                "all five secret inputs are masked"
            );
            wizard.Close();
        }
        catch (Exception error)
        {
            renderError = error;
        }
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    if (renderError != null)
        throw renderError;
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    Directory.Delete(scratch, true);
}
