using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Spottarr.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.SequenceEqual(new[] { "--remove-startup" }))
            {
                var start = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("/Delete");
                start.ArgumentList.Add("/TN");
                start.ArgumentList.Add(
                    "Spottarr.Windows."
                        + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
                );
                start.ArgumentList.Add("/F");
                using var task = Process.Start(start)!;
                var output = task.StandardOutput.ReadToEndAsync();
                var error = task.StandardError.ReadToEndAsync();
                task.WaitForExit();
                Task.WaitAll(output, error);
                return 0; // Deleting an already absent task is harmless during uninstall.
            }
            if (args.SequenceEqual(new[] { "--run" }))
            {
                using var mutex = new Mutex(true, @"Local\Spottarr.Windows.Server", out var owned);
                if (!owned)
                    return 0;
                var settings = Setup.Load(Setup.ConfigFile);
                var database = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(
                    Setup.Get(settings, "Database:ConnectionString")
                ).DataSource;
                Setup.RestrictDirectory(Path.GetDirectoryName(database)!);
                using var job = new ServerJob();
                using var process = Process.Start(
                    Setup.ServerStart(settings, Path.Combine(AppContext.BaseDirectory, "server"))
                )!;
                try
                {
                    job.Attach(process);
                }
                catch
                {
                    process.Kill(true);
                    throw;
                }
                process.WaitForExit();
                return process.ExitCode;
            }
            Application.Run(new Wizard());
            return 0;
        }
        catch
        {
            if (
                args.SequenceEqual(new[] { "--run" })
                || args.SequenceEqual(new[] { "--remove-startup" })
            )
                return 1;
            MessageBox.Show(
                "Spottarr kan niet starten. Controleer de installatie en configuratie voor deze Windows-gebruiker.",
                "Spottarr",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return 1;
        }
    }
}

internal sealed class Wizard : Form
{
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly TabControl pages = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(720, 0) };
    private readonly string server;
    private JsonObject settings;

    public Wizard(string? serverDirectory = null, string? configFile = null)
    {
        server = serverDirectory ?? Path.Combine(AppContext.BaseDirectory, "server");
        Text = "Spottarr • Windows-installatie";
        Width = 850;
        Height = 740;
        MinimumSize = new Size(760, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var source = configFile ?? Setup.ConfigFile;
        settings = File.Exists(source) ? Setup.Load(source) : Setup.Defaults(server);
        var usenet = Page(
            "1 · Usenet",
            "Vul de gegevens van je Usenet-provider in. Geheimen blijven verborgen en worden met Windows DPAPI opgeslagen."
        );
        Field(usenet, "Server", "Usenet:Hostname");
        Field(usenet, "Gebruikersnaam", "Usenet:Username", true);
        Field(usenet, "Wachtwoord", "Usenet:Password", true);
        Field(usenet, "Poort", "Usenet:Port");
        Field(usenet, "TLS (true/false)", "Usenet:UseTls");
        Field(usenet, "Verbindingen (1–100)", "Usenet:MaxConnections");
        Field(usenet, "Compressie (None/Deflate/OperationExtensions)", "Usenet:Compression");
        var spot = Page(
            "2 · Spotnet",
            "Quartz-cron gebruikt seconden en een ? voor dag/weekdag. De bestaande Spottarr-standaarden zijn vooraf ingevuld."
        );
        foreach (var group in new[] { "SpotGroup", "CommentGroup", "ReportGroup", "NzbGroup" })
            Field(spot, group, "Spotnet:" + group);
        Field(spot, "Ophalen vanaf (ISO-datum met tijdzone)", "Spotnet:RetrieveAfter");
        Field(spot, "Batchgrootte", "Spotnet:ImportBatchSize");
        Field(spot, "Bewaartermijn in dagen (0 = onbeperkt)", "Spotnet:RetentionDays");
        Field(spot, "Volwassen inhoud (true/false)", "Spotnet:ImportAdultContent");
        Field(spot, "Import-cron", "Spotnet:ImportSpotsSchedule");
        Field(spot, "Onderhoud-cron", "Spotnet:CleanUpSpotsSchedule");
        var storage = Page(
            "3 · Opslag en poort",
            "De server is alleen bereikbaar op deze pc. Kies voor SQLite een lokale schijf. Stop Spottarr vóór wijzigingen; een bestaande database wordt niet verplaatst of verwijderd."
        );
        Field(storage, "Lokale URL (http://127.0.0.1:8383)", "Urls");
        Field(
            storage,
            "SQLite: absoluut pad binnen %LOCALAPPDATA%\\Spottarr",
            "Database:ConnectionString"
        );
        Field(storage, "Newznab API-sleutel (willekeurig aangemaakt)", "Newznab:ApiKey", true);
        Field(storage, "Admin API-sleutel (leeg = uitgeschakeld)", "Admin:ApiKey", true);
        var finish = Page(
            "4 · Opslaan en verbinden",
            "Opslaan verandert de lokale configuratie. Start en automatisch starten zijn afzonderlijke acties. Automatisch starten gebeurt na aanmelden als deze gebruiker, zonder beheerdersrechten."
        );
        Button(
            finish,
            "Valideren en veilig opslaan",
            () =>
            {
                var current = Read();
                Setup.Save(current, Setup.ConfigFile);
                settings = current;
                status.Text =
                    "Configuratie veilig opgeslagen. Herstart een actieve Spottarr om wijzigingen te gebruiken.";
            }
        );
        Button(
            finish,
            "Spottarr starten",
            () =>
            {
                Setup.Load(Setup.ConfigFile);
                Process.Start(
                    new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false,
                        ArgumentList = { "--run" },
                    }
                );
                status.Text =
                    "Start aangevraagd. Gebruik de verbindingstest om te controleren of de server beschikbaar is.";
            }
        );
        Button(finish, "Automatisch starten bij aanmelden instellen", () => RegisterTask(false));
        Button(finish, "Automatisch starten verwijderen", () => RegisterTask(true));
        var prowlarrUrl = new TextBox
        {
            Width = 660,
            PlaceholderText = "Prowlarr URL, bijvoorbeeld http://127.0.0.1:9696",
        };
        var prowlarrKey = new TextBox
        {
            Width = 660,
            UseSystemPasswordChar = true,
            PlaceholderText = "Prowlarr API-sleutel (wordt niet opgeslagen)",
        };
        finish.Controls.Add(
            new Label
            {
                Text =
                    "Prowlarr: voeg handmatig Generic Newznab toe met de lokale Spottarr-URL en je Newznab-sleutel. Kopieer de sleutel hieronder alleen indien nodig.",
                AutoSize = true,
                MaximumSize = new Size(690, 0),
            }
        );
        Button(
            finish,
            "Newznab-sleutel kopiëren naar klembord",
            () =>
            {
                Clipboard.SetText(Setup.Get(Setup.Load(Setup.ConfigFile), "Newznab:ApiKey"));
                status.Text =
                    "Sleutel gekopieerd. Wis het klembord na gebruik; klembordgeschiedenis kan een kopie bewaren.";
            }
        );
        finish.Controls.Add(prowlarrUrl);
        finish.Controls.Add(prowlarrKey);
        var test = new System.Windows.Forms.Button
        {
            Text = "Spottarr en Prowlarr testen",
            AutoSize = true,
        };
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            try
            {
                var saved = Setup.Load(Setup.ConfigFile);
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var baseUrl = Setup.Get(saved, "Urls");
                using var health = await client.GetAsync(baseUrl + "/healthz");
                health.EnsureSuccessStatusCode();
                var caps = await client.GetStringAsync(
                    baseUrl
                        + "/newznab/api?t=caps&apikey="
                        + Uri.EscapeDataString(Setup.Get(saved, "Newznab:ApiKey"))
                );
                using var xmlReader = System.Xml.XmlReader.Create(
                    new StringReader(caps),
                    new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        XmlResolver = null,
                    }
                );
                if (XDocument.Load(xmlReader).Root?.Name.LocalName != "caps")
                    throw new InvalidDataException();
                var search = await client.GetStringAsync(
                    baseUrl
                        + "/newznab/api?t=search&limit=0&apikey="
                        + Uri.EscapeDataString(Setup.Get(saved, "Newznab:ApiKey"))
                );
                using var searchReader = System.Xml.XmlReader.Create(
                    new StringReader(search),
                    new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        XmlResolver = null,
                    }
                );
                if (XDocument.Load(searchReader).Root?.Name.LocalName != "rss")
                    throw new InvalidDataException();
                if (!string.IsNullOrWhiteSpace(prowlarrUrl.Text))
                {
                    if (
                        !Uri.TryCreate(prowlarrUrl.Text, UriKind.Absolute, out var uri)
                        || uri.UserInfo != ""
                        || uri.Query != ""
                        || uri.Fragment != ""
                        || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                    )
                        throw new ArgumentException();
                    using var request = new HttpRequestMessage(
                        HttpMethod.Get,
                        prowlarrUrl.Text.TrimEnd('/') + "/api/v1/system/status"
                    );
                    request.Headers.Add("X-Api-Key", prowlarrKey.Text);
                    using var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                }
                status.Text =
                    "Spottarr health, caps en geautoriseerde zoekopdracht geslaagd."
                    + (
                        prowlarrUrl.Text.Length > 0
                            ? " Prowlarr API bereikbaar; indexer moet handmatig worden toegevoegd."
                            : " Geen Prowlarr-test aangevraagd."
                    );
            }
            catch
            {
                status.Text =
                    "Verbindingstest mislukt. Controleer serverstatus, URL en API-sleutel. Er zijn geen geheimen gelogd.";
            }
            finally
            {
                prowlarrKey.Clear();
                test.Enabled = true;
            }
        };
        finish.Controls.Add(test);
        finish.Controls.Add(status);
        var navigation = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
        };
        Button(
            navigation,
            "Volgende →",
            () => pages.SelectedIndex = Math.Min(pages.SelectedIndex + 1, pages.TabCount - 1)
        );
        Button(
            navigation,
            "← Vorige",
            () => pages.SelectedIndex = Math.Max(pages.SelectedIndex - 1, 0)
        );
        Controls.Add(pages);
        Controls.Add(navigation);
    }

    private FlowLayoutPanel Page(string title, string info)
    {
        var tab = new TabPage(title);
        pages.TabPages.Add(tab);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };
        panel.Controls.Add(
            new Label
            {
                Text = info,
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                Margin = new Padding(0, 0, 0, 18),
            }
        );
        tab.Controls.Add(panel);
        return panel;
    }

    private void Field(Control panel, string label, string path, bool secret = false)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true });
        var input = new TextBox
        {
            Text = Setup.Get(settings, path),
            Width = 680,
            UseSystemPasswordChar = secret,
        };
        fields[path] = input;
        panel.Controls.Add(input);
    }

    private static void Button(Control panel, string text, Action action)
    {
        var button = new System.Windows.Forms.Button
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 6, 3, 6),
        };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (ArgumentException e)
            {
                MessageBox.Show(e.Message, "Controleer de invoer");
            }
            catch
            {
                MessageBox.Show(
                    "Actie mislukt. Controleer of de configuratie bestaat, de installatie compleet is en je toegang hebt.",
                    "Spottarr"
                );
            }
        };
        panel.Controls.Add(button);
    }

    private JsonObject Read()
    {
        var root = (JsonObject)settings.DeepClone();
        foreach (var field in fields)
            Setup.Set(root, field.Key, field.Value.Text);
        Setup.Validate(root);
        return root;
    }

    private void RegisterTask(bool remove)
    {
        if (!remove)
            Setup.Load(Setup.ConfigFile);
        Setup.RestrictDirectory(Setup.Home);
        var xml = Path.Combine(Setup.Home, "task.xml");
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(remove ? "/Delete" : "/Create");
        start.ArgumentList.Add("/TN");
        start.ArgumentList.Add(
            "Spottarr.Windows." + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
        );
        if (!remove)
        {
            File.WriteAllText(xml, Setup.TaskXml(Environment.ProcessPath!));
            start.ArgumentList.Add("/XML");
            start.ArgumentList.Add(xml);
        }
        start.ArgumentList.Add("/F");
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException();
        }
        finally
        {
            if (File.Exists(xml))
                File.Delete(xml);
        }
        status.Text = remove
            ? "Automatisch starten verwijderd."
            : "Automatisch starten ingesteld voor deze gebruiker. Een bestaande taak met de naam Spottarr blijft behouden.";
    }
}
