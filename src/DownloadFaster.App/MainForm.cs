using System.Diagnostics;
using DownloadFaster.Core;

namespace DownloadFaster.App;

internal sealed class MainForm : Form
{
    private const string AppName = "Download Faster";
    private const int ProgressColumn = 2;

    private static readonly int[] ChunkSizesMB = [1, 2, 4, 8, 16, 32];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DownloadOptions _options = new();
    private readonly Dictionary<string, RouteSettings> _routeSettings = [];
    private readonly Dictionary<string, double> _testResults = [];
    private readonly List<AdapterRow> _adapterRows = [];
    private readonly List<DownloadItem> _downloads = [];
    private IReadOnlyList<NetworkRoute> _routes = [];
    private bool _initialized;
    private bool _closingAfterPause;

    private readonly ToolTip _tips = new() { AutoPopDelay = 20000, InitialDelay = 400 };
    private readonly TextBox _urlBox = new();
    private readonly Button _downloadButton = new();
    private readonly TextBox _folderBox = new();
    private readonly Button _browseButton = new();
    private readonly ComboBox _modeBox = new();
    private readonly ComboBox _chunkBox = new();
    private readonly Button _testButton = new();
    private readonly Button _refreshButton = new();
    private readonly Label _testSummary = new();
    private readonly TableLayoutPanel _adapterTable = new();
    private readonly BufferedListView _list = new();
    private readonly Button _pauseButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _openFolderButton = new();
    private readonly Button _removeButton = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _activeLabel = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppName;
        Icon = LoadIcon();
        ClientSize = new Size(1000, 700);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;

        _options.Mode = _settings.Mode;
        _options.ChunkSizeBytes = Math.Clamp(_settings.ChunkSizeMB, 1, 64) * DownloadOptions.MiB;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10, 8, 10, 4) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildLinkPanel(), 0, 0);
        root.Controls.Add(BuildAdapterGroup(), 0, 1);
        root.Controls.Add(BuildDownloadsGroup(), 0, 2);
        Controls.Add(root);
        Controls.Add(BuildStatusStrip());

        LoadAdapters();
        RestoreDownloads();

        ResumeLayout(false);
        PerformLayout();
        _initialized = true;

        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        RefreshView();
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLinkPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 6) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _urlBox.Dock = DockStyle.Fill;
        _urlBox.PlaceholderText = "Paste a link to a file, e.g. https://example.com/big-file.zip";
        _urlBox.Margin = new Padding(3, 5, 3, 3);
        _urlBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                StartNewDownload();
            }
        };

        _downloadButton.Text = "Download";
        _downloadButton.AutoSize = true;
        _downloadButton.MinimumSize = new Size(110, 0);
        _downloadButton.Click += (_, _) => StartNewDownload();

        _folderBox.Dock = DockStyle.Fill;
        _folderBox.Text = _settings.DownloadFolder;
        _folderBox.Margin = new Padding(3, 5, 3, 3);
        _folderBox.Leave += (_, _) => SaveSettings();

        _browseButton.Text = "Browse...";
        _browseButton.AutoSize = true;
        _browseButton.MinimumSize = new Size(110, 0);
        _browseButton.Click += (_, _) => BrowseForFolder();

        panel.Controls.Add(FieldLabel("Download link:"), 0, 0);
        panel.Controls.Add(_urlBox, 1, 0);
        panel.Controls.Add(_downloadButton, 2, 0);
        panel.Controls.Add(FieldLabel("Save to:"), 0, 1);
        panel.Controls.Add(_folderBox, 1, 1);
        panel.Controls.Add(_browseButton, 2, 1);
        return panel;
    }

    private Control BuildAdapterGroup()
    {
        var group = new GroupBox
        {
            Text = "Network adapters (changes apply instantly, even during a download)",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 6),
            Margin = new Padding(0, 0, 0, 8),
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };

        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.Width = 270;
        _modeBox.Items.AddRange(["Automatic: fastest finish (recommended)", "Fixed split: follow the Share sliders"]);
        _modeBox.SelectedIndex = _options.Mode == BalanceMode.FixedSplit ? 1 : 0;
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            _options.Mode = _modeBox.SelectedIndex == 1 ? BalanceMode.FixedSplit : BalanceMode.Auto;
            UpdateShareControls();
            SaveSettings();
        };
        _tips.SetToolTip(_modeBox,
            "Automatic: every adapter grabs the next piece as soon as it is free, so faster adapters do more and the download ends soonest." +
            Environment.NewLine +
            "Fixed split: each adapter downloads the share you set with its slider (useful to spare a metered connection).");

        _chunkBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _chunkBox.Width = 80;
        _chunkBox.Items.AddRange(ChunkSizesMB.Select(mb => (object)$"{mb} MB").ToArray());
        int chunkIndex = Array.IndexOf(ChunkSizesMB, (int)(_options.ChunkSizeBytes / DownloadOptions.MiB));
        _chunkBox.SelectedIndex = chunkIndex >= 0 ? chunkIndex : 2;
        _chunkBox.SelectedIndexChanged += (_, _) =>
        {
            _options.ChunkSizeBytes = ChunkSizesMB[_chunkBox.SelectedIndex] * DownloadOptions.MiB;
            SaveSettings();
        };
        _tips.SetToolTip(_chunkBox, "Size of the pieces the file is cut into. Smaller balances better between adapters; bigger means fewer requests. Applies to new or resumed downloads.");

        _testButton.Text = "Test speed";
        _testButton.AutoSize = true;
        _testButton.Click += (_, _) => RunSpeedTest();
        _tips.SetToolTip(_testButton, "Measures each ticked adapter alone, then all of them together, to show what combining them gains.");

        _refreshButton.Text = "Refresh adapters";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => LoadAdapters();

        _testSummary.AutoSize = true;
        _testSummary.ForeColor = SystemColors.GrayText;
        _testSummary.Anchor = AnchorStyles.Left;
        _testSummary.Margin = new Padding(8, 8, 3, 3);

        bar.Controls.Add(FieldLabel("Balancing:"));
        bar.Controls.Add(_modeBox);
        bar.Controls.Add(FieldLabel("Chunk size:", leftMargin: 12));
        bar.Controls.Add(_chunkBox);
        bar.Controls.Add(Spacer(12));
        bar.Controls.Add(_testButton);
        bar.Controls.Add(_refreshButton);
        bar.Controls.Add(_testSummary);

        _adapterTable.Dock = DockStyle.Fill;
        _adapterTable.AutoSize = true;
        _adapterTable.ColumnCount = 6;
        _adapterTable.Margin = new Padding(0, 8, 0, 0);
        _adapterTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _adapterTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++)
        {
            _adapterTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        var hint = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 6, 3, 0),
            Text = "Speeds only add up when the adapters reach the internet through different lines, " +
                "e.g. home broadband on Ethernet plus a phone hotspot on Wi-Fi.",
        };

        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(_adapterTable, 0, 1);
        layout.Controls.Add(hint, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildDownloadsGroup()
    {
        var group = new GroupBox { Text = "Downloads", Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.OwnerDraw = true;
        _list.Columns.Add("File", 220);
        _list.Columns.Add("Size", 75);
        _list.Columns.Add("Progress", 130);
        _list.Columns.Add("Speed", 85);
        _list.Columns.Add("Adapters", 230);
        _list.Columns.Add("Time left", 70);
        _list.Columns.Add("Status", 140);
        _list.Resize += (_, _) => FitStatusColumn();
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += DrawListSubItem;
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => OpenSelectedFile();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && Selected is { } item)
            {
                if (item.Snapshot.State == DownloadState.Completed)
                {
                    RemoveItem(item);
                }
                else
                {
                    CancelDownload(item);
                }
            }
        };
        _list.ContextMenuStrip = BuildListMenu();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0), WrapContents = false };
        SetupButton(_pauseButton, "Pause", (_, _) => TogglePause());
        SetupButton(_cancelButton, "Cancel", (_, _) =>
        {
            if (Selected is { } item)
            {
                CancelDownload(item);
            }
        });
        SetupButton(_openFolderButton, "Show in folder", (_, _) => ShowSelectedInFolder());
        SetupButton(_removeButton, "Remove from list", (_, _) =>
        {
            if (Selected is { } item)
            {
                RemoveItem(item);
            }
        });
        buttons.Controls.AddRange([_pauseButton, _cancelButton, _openFolderButton, _removeButton]);

        layout.Controls.Add(_list, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private ContextMenuStrip BuildListMenu()
    {
        var menu = new ContextMenuStrip();
        var pause = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var openFile = new ToolStripMenuItem("Open file", null, (_, _) => OpenSelectedFile());
        var showInFolder = new ToolStripMenuItem("Show in folder", null, (_, _) => ShowSelectedInFolder());
        var copyLink = new ToolStripMenuItem("Copy link", null, (_, _) =>
        {
            if (Selected is { } item)
            {
                Clipboard.SetText(item.Download.Url.AbsoluteUri);
            }
        });
        var cancel = new ToolStripMenuItem("Cancel and delete partial file", null, (_, _) =>
        {
            if (Selected is { } item)
            {
                CancelDownload(item);
            }
        });
        var remove = new ToolStripMenuItem("Remove from list", null, (_, _) =>
        {
            if (Selected is { } item)
            {
                RemoveItem(item);
            }
        });
        menu.Items.AddRange([pause, openFile, showInFolder, copyLink, new ToolStripSeparator(), cancel, remove]);
        menu.Opening += (_, e) =>
        {
            var item = Selected;
            if (item is null)
            {
                e.Cancel = true;
                return;
            }

            bool done = item.Snapshot.State == DownloadState.Completed;
            pause.Text = PauseButtonText(item);
            pause.Enabled = item.IsRunning || item.CanResume;
            openFile.Enabled = done;
            cancel.Enabled = !done;
            remove.Enabled = done;
        };
        return menu;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip { SizingGrip = true };
        _statusLabel.Spring = true;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        strip.Items.AddRange([_statusLabel, _activeLabel]);
        return strip;
    }

    private static Label FieldLabel(string text, int leftMargin = 3) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(leftMargin, 8, 3, 3),
    };

    private static Control Spacer(int width) => new Panel { Width = width, Height = 1, Margin = new Padding(0) };

    private static void SetupButton(Button button, string text, EventHandler onClick)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(100, 0);
        button.Click += onClick;
    }

    private int Px(int logicalPixels) => _initialized ? LogicalToDeviceUnits(logicalPixels) : logicalPixels;

    private static Icon? LoadIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? null : new Icon(stream);
    }

    // ---------------------------------------------------------------- adapters

    private void LoadAdapters()
    {
        _routes = AdapterDiscovery.Discover(SettingsFor);
        RebuildAdapterRows();
        foreach (var item in _downloads.Where(d => !d.IsRunning))
        {
            item.Download.Routes = _routes;
        }

        SaveSettings();
    }

    private RouteSettings SettingsFor(NetworkRoute route)
    {
        if (!_routeSettings.TryGetValue(route.Id, out var settings))
        {
            settings = _settings.Adapters.TryGetValue(route.Id, out var prefs)
                ? prefs.ToSettings()
                : AdapterDiscovery.DefaultSettingsFor(route);
            _routeSettings[route.Id] = settings;
        }

        return settings;
    }

    private void RebuildAdapterRows()
    {
        _adapterTable.SuspendLayout();
        foreach (var control in _adapterTable.Controls.Cast<Control>().ToList())
        {
            control.Dispose();
        }

        _adapterTable.Controls.Clear();
        _adapterTable.RowStyles.Clear();
        _adapterRows.Clear();
        _adapterTable.RowCount = Math.Max(2, _routes.Count + 1);

        string[] headers = ["Use", "Adapter", "Connections", "Speed limit", "Share", "Now"];
        for (int i = 0; i < headers.Length; i++)
        {
            _adapterTable.Controls.Add(
                new Label { Text = headers[i], AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 2) }, i, 0);
        }

        if (_routes.Count == 0)
        {
            var none = new Label
            {
                AutoSize = true,
                ForeColor = Color.Firebrick,
                Margin = new Padding(3, 6, 3, 6),
                Text = "No connected network adapter found. Connect Wi-Fi and/or Ethernet, then click Refresh adapters.",
            };
            _adapterTable.Controls.Add(none, 0, 1);
            _adapterTable.SetColumnSpan(none, 6);
        }

        for (int i = 0; i < _routes.Count; i++)
        {
            var row = new AdapterRow(_routes[i], Px, _tips, OnAdapterSettingsChanged);
            row.AddTo(_adapterTable, i + 1);
            _adapterRows.Add(row);
        }

        _adapterTable.ResumeLayout();
        UpdateShareControls();
    }

    private void OnAdapterSettingsChanged()
    {
        UpdateShareControls();
        SaveSettings();
    }

    private void UpdateShareControls()
    {
        bool fixedSplit = _options.Mode == BalanceMode.FixedSplit;
        int total = _adapterRows.Where(r => r.Use.Checked).Sum(r => r.Route.Settings.Share);
        foreach (var row in _adapterRows)
        {
            row.UpdateShare(fixedSplit, total);
        }
    }

    private async void RunSpeedTest()
    {
        var rows = _adapterRows.Where(r => r.Use.Checked).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one adapter to test.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_downloads.Any(d => d.IsRunning)
            && MessageBox.Show(this, "Downloads are running, so the test will be slower than your real speed. Test anyway?",
                AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var url = Uri.TryCreate(_settings.SpeedTestUrl, UriKind.Absolute, out var configured) ? configured : SpeedTest.DefaultUrl;
        var duration = TimeSpan.FromSeconds(6);
        _testButton.Enabled = false;
        _refreshButton.Enabled = false;
        try
        {
            var alone = new Dictionary<AdapterRow, double>();
            foreach (var row in rows)
            {
                _testSummary.Text = $"Testing {row.Route.Name}...";
                alone[row] = await TestAsync(row, url, duration);
                _testResults[row.Route.Id] = alone[row];
            }

            string summary = "Alone: " + string.Join(", ", rows.Select(r => $"{r.Route.Name} {Units.Mbps(alone[r])}"));
            if (rows.Count > 1)
            {
                _testSummary.Text = "Testing all adapters together...";
                double together = (await Task.WhenAll(rows.Select(r => TestAsync(r, url, duration)))).Sum();
                double best = alone.Values.Max();
                summary += $".  Together: {Units.Mbps(together)}";
                if (best > 0)
                {
                    summary += together > best * 1.15
                        ? $" ({together / best - 1:P0} faster than the best adapter alone)"
                        : " (little gain: these adapters probably share one internet line)";
                }
            }

            _testSummary.Text = summary;
        }
        finally
        {
            foreach (var row in rows)
            {
                row.TestText = null;
            }

            _testButton.Enabled = true;
            _refreshButton.Enabled = true;
            RefreshView();
        }
    }

    private async Task<double> TestAsync(AdapterRow row, Uri url, TimeSpan duration)
    {
        row.TestText = "Testing...";
        var progress = new Progress<double>(speed => row.TestText = $"Testing: {Units.Mbps(speed)}");
        try
        {
            return await SpeedTest.RunAsync(row.Route, url, row.Route.Settings.Connections, duration, progress);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _tips.SetToolTip(_testSummary, $"{row.Route.Name}: {ex.Message}");
            return 0;
        }
        finally
        {
            row.TestText = null;
        }
    }

    // ---------------------------------------------------------------- downloads

    private DownloadItem? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as DownloadItem : null;

    private void StartNewDownload()
    {
        string text = _urlBox.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "Paste a full link that starts with http:// or https://.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _urlBox.Focus();
            return;
        }

        string folder = _folderBox.Text.Trim();
        try
        {
            if (folder.Length == 0)
            {
                throw new IOException("No folder chosen.");
            }

            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, $"Can't save to that folder: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!_routes.Any(r => r.Settings.Enabled))
        {
            MessageBox.Show(this, "Tick at least one network adapter first.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_downloads.FirstOrDefault(d => d.Download.Url == url && d.Snapshot.State != DownloadState.Completed) is { } existing)
        {
            existing.Row!.Selected = true;
            existing.Row.EnsureVisible();
            if (existing.CanResume)
            {
                Start(existing);
            }

            _urlBox.Clear();
            return;
        }

        var item = new DownloadItem(new SegmentedDownload(url, folder, _routes, _options));
        AddRow(item);
        Start(item);
        item.Row!.Selected = true;
        _urlBox.Clear();
        SaveSettings();
    }

    private void RestoreDownloads()
    {
        foreach (var saved in _settings.Downloads)
        {
            if (Uri.TryCreate(saved.Url, UriKind.Absolute, out var url) && saved.Folder.Length > 0)
            {
                AddRow(new DownloadItem(new SegmentedDownload(url, saved.Folder, _routes, _options, saved.FileName)));
            }
        }
    }

    private void AddRow(DownloadItem item)
    {
        var url = item.Download.Url;
        var row = new ListViewItem([item.Download.FileName ?? FileNames.FromUrl(url) ?? url.Host, "", "", "", "", "", ""]) { Tag = item };
        item.Row = row;
        _downloads.Add(item);
        _list.Items.Add(row);
        UpdateRow(item);
    }

    private void Start(DownloadItem item)
    {
        if (item.IsRunning)
        {
            return;
        }

        item.Download.Routes = _routes;
        item.Cts?.Dispose();
        item.Cts = new CancellationTokenSource();
        var token = item.Cts.Token;
        item.Run = Task.Run(() => item.Download.RunAsync(token));
        item.Run.ContinueWith(_ => OnUiThread(() => OnDownloadFinished(item)), TaskScheduler.Default);
        RefreshView();
    }

    private void OnDownloadFinished(DownloadItem item)
    {
        RefreshView();
        SaveSettings();
        if (item.Snapshot.State == DownloadState.Completed && !ContainsFocus)
        {
            FlashTaskbar();
        }
    }

    private void TogglePause()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (item.IsRunning)
        {
            item.Cts?.Cancel();
        }
        else if (item.CanResume)
        {
            Start(item);
        }
    }

    private async void CancelDownload(DownloadItem item)
    {
        if (item.Snapshot.State == DownloadState.Completed)
        {
            return;
        }

        string name = item.Row?.Text ?? "this download";
        if (MessageBox.Show(this, $"Stop \"{name}\" and delete what has been downloaded so far?", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        item.Cts?.Cancel();
        await item.Run;
        item.Download.DeletePartialFiles();
        RemoveItem(item);
    }

    private void RemoveItem(DownloadItem item)
    {
        if (item.IsRunning)
        {
            return;
        }

        if (item.Row is not null)
        {
            _list.Items.Remove(item.Row);
        }

        _downloads.Remove(item);
        item.Cts?.Dispose();
        SaveSettings();
        UpdateButtons();
    }

    private void OpenSelectedFile()
    {
        if (Selected is { Snapshot.State: DownloadState.Completed } item && item.Download.FilePath is { } path && File.Exists(path))
        {
            Launch(path);
        }
        else
        {
            TogglePause();
        }
    }

    private void ShowSelectedInFolder()
    {
        if (Selected is not { } item)
        {
            return;
        }

        string? path = item.Download.FilePath;
        if (path is not null && !File.Exists(path) && File.Exists(path + SegmentedDownload.PartSuffix))
        {
            path += SegmentedDownload.PartSuffix;
        }

        if (path is not null && File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(item.Download.OutputDirectory))
        {
            Launch(item.Download.OutputDirectory);
        }
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where downloads are saved",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderBox.Text) ? _folderBox.Text : _settings.DownloadFolder,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folderBox.Text = dialog.SelectedPath;
            SaveSettings();
        }
    }

    // ---------------------------------------------------------------- refresh

    private void RefreshView()
    {
        var speedByRoute = new Dictionary<string, double>();
        var problemByRoute = new Dictionary<string, string>();
        double totalSpeed = 0;
        int active = 0;

        foreach (var item in _downloads)
        {
            var snapshot = item.Download.GetSnapshot();
            item.Snapshot = snapshot;
            if (item.IsRunning)
            {
                active++;
                totalSpeed += snapshot.BytesPerSecond;
                foreach (var route in snapshot.Routes)
                {
                    speedByRoute[route.RouteId] = speedByRoute.GetValueOrDefault(route.RouteId) + route.BytesPerSecond;
                    if (route.Problem is not null)
                    {
                        problemByRoute[route.RouteId] = route.Problem;
                    }
                }
            }

            UpdateRow(item);
        }

        foreach (var row in _adapterRows)
        {
            string id = row.Route.Id;
            row.ShowStatus(
                speedByRoute.GetValueOrDefault(id),
                active > 0,
                problemByRoute.GetValueOrDefault(id),
                _testResults.TryGetValue(id, out double tested) ? tested : null);
        }

        if (active > 0)
        {
            var parts = _adapterRows
                .Where(r => speedByRoute.GetValueOrDefault(r.Route.Id) > 0)
                .Select(r => $"{r.Route.Name} {Units.Speed(speedByRoute[r.Route.Id])}");
            _statusLabel.Text = $"Total {Units.Speed(totalSpeed)} ({Units.Mbps(totalSpeed)})    " + string.Join("  +  ", parts);
            _activeLabel.Text = active == 1 ? "1 download running" : $"{active} downloads running";
            Text = $"{Units.Speed(totalSpeed)} - {AppName}";
        }
        else
        {
            int ticked = _routes.Count(r => r.Settings.Enabled);
            _statusLabel.Text = ticked switch
            {
                0 when _routes.Count == 0 => "No network adapter found.",
                0 => "No adapter ticked.",
                1 => "Ready. Only one adapter is ticked, so downloads use a single connection path.",
                _ => $"Ready. {ticked} adapters will share each download.",
            };
            _activeLabel.Text = "";
            Text = AppName;
        }

        UpdateButtons();
    }

    private static void UpdateRow(DownloadItem item)
    {
        if (item.Row is not { } row)
        {
            return;
        }

        var s = item.Snapshot;
        var url = item.Download.Url;
        bool downloading = s.State == DownloadState.Downloading;

        SetCell(row, 0, s.FileName ?? FileNames.FromUrl(url) ?? url.Host);
        SetCell(row, 1, s.TotalBytes is long total ? Units.Bytes(total) : s.State == DownloadState.Completed ? Units.Bytes(s.DownloadedBytes) : "");
        SetCell(row, ProgressColumn, s.Fraction is double f ? $"{f:P1}" : s.DownloadedBytes > 0 ? Units.Bytes(s.DownloadedBytes) : "");
        SetCell(row, 3, downloading ? Units.Speed(s.BytesPerSecond) : "");
        SetCell(row, 4, AdaptersText(s));
        SetCell(row, 5, s.Eta is TimeSpan eta ? Units.Duration(eta) : "");
        SetCell(row, 6, StatusText(item));
        row.ToolTipText = s.Message is { } message ? $"{message}{Environment.NewLine}{url}" : url.AbsoluteUri;
    }

    private static string AdaptersText(DownloadSnapshot s)
    {
        if (s.State == DownloadState.Downloading)
        {
            return string.Join("  +  ", s.Routes
                .Where(r => r.Connections > 0 || r.BytesPerSecond > 0)
                .Select(r => $"{r.Name} {Units.Speed(r.BytesPerSecond)}"));
        }

        long total = s.Routes.Sum(r => r.Bytes);
        if (total <= 0)
        {
            return "";
        }

        // Once stopped, show how the work was split between adapters.
        return string.Join("  /  ", s.Routes.Where(r => r.Bytes > 0).Select(r => $"{r.Name} {(double)r.Bytes / total:P0}"));
    }

    private static string StatusText(DownloadItem item)
    {
        var s = item.Snapshot;
        return s.State switch
        {
            DownloadState.Queued when !item.IsRunning => "Paused",
            DownloadState.Queued or DownloadState.Connecting => "Connecting...",
            DownloadState.Downloading when s.Message is not null && s.IsSplit => s.Message,
            DownloadState.Downloading => s.IsSplit ? "Downloading" : "Downloading (1 connection)",
            DownloadState.Paused => "Paused",
            DownloadState.Completed => "Done",
            DownloadState.Failed => $"Failed: {s.Message}",
            _ => s.State.ToString(),
        };
    }

    private static void SetCell(ListViewItem row, int column, string text)
    {
        if (row.SubItems[column].Text != text)
        {
            row.SubItems[column].Text = text;
        }
    }

    private void UpdateButtons()
    {
        var item = Selected;
        _pauseButton.Enabled = item is not null && (item.IsRunning || item.CanResume);
        _pauseButton.Text = item is null ? "Pause" : PauseButtonText(item);
        _cancelButton.Enabled = item is not null && item.Snapshot.State != DownloadState.Completed;
        _openFolderButton.Enabled = item is not null;
        _removeButton.Enabled = item is not null && !item.IsRunning && item.Snapshot.State == DownloadState.Completed;
    }

    private static string PauseButtonText(DownloadItem item) => item.Snapshot.State switch
    {
        _ when item.IsRunning => "Pause",
        DownloadState.Failed => "Retry",
        DownloadState.Completed => "Pause",
        _ => "Resume",
    };

    private void DrawListSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != ProgressColumn || e.Item?.Tag is not DownloadItem item || e.SubItem is null)
        {
            e.DrawDefault = true;
            return;
        }

        var g = e.Graphics;
        bool selected = e.Item.Selected;
        using (var background = new SolidBrush(selected ? SystemColors.Highlight : _list.BackColor))
        {
            g.FillRectangle(background, e.Bounds);
        }

        int padX = Math.Max(3, e.Bounds.Height / 6);
        int padY = Math.Max(2, e.Bounds.Height / 8);
        var bar = Rectangle.Inflate(e.Bounds, -padX, -padY);
        double fraction = item.Snapshot.Fraction ?? 0;

        if (ProgressBarRenderer.IsSupported)
        {
            ProgressBarRenderer.DrawHorizontalBar(g, bar);
            var fill = Rectangle.Inflate(bar, -1, -1);
            fill.Width = (int)(fill.Width * fraction);
            if (fill.Width > 0)
            {
                ProgressBarRenderer.DrawHorizontalChunks(g, fill);
            }
        }
        else
        {
            g.FillRectangle(SystemBrushes.Window, bar);
            var fill = bar with { Width = (int)(bar.Width * fraction) };
            g.FillRectangle(Brushes.MediumSeaGreen, fill);
            g.DrawRectangle(SystemPens.ControlDark, bar);
        }

        TextRenderer.DrawText(g, e.SubItem.Text, _list.Font, bar, Color.Black,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    // ---------------------------------------------------------------- lifecycle

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Offer a copied link so the user can just press Enter.
        if (_urlBox.TextLength > 0)
        {
            return;
        }

        try
        {
            string clip = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
            if (clip.Length < 2048 && Uri.TryCreate(clip, UriKind.Absolute, out var url)
                && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                && !_downloads.Any(d => d.Download.Url == url))
            {
                _urlBox.Text = clip;
                _urlBox.SelectAll();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another app is holding the clipboard.
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int[] widths = [220, 75, 130, 85, 230, 70];
        for (int i = 0; i < widths.Length; i++)
        {
            _list.Columns[i].Width = LogicalToDeviceUnits(widths[i]);
        }

        FitStatusColumn();
    }

    /// <summary>Lets the Status column take whatever width is left so there is no horizontal scrollbar.</summary>
    private void FitStatusColumn()
    {
        var columns = _list.Columns;
        if (columns.Count == 0)
        {
            return;
        }

        int used = columns.Cast<ColumnHeader>().Take(columns.Count - 1).Sum(c => c.Width);
        int available = _list.ClientSize.Width - used - 4;
        columns[^1].Width = Math.Max(LogicalToDeviceUnits(120), available);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingAfterPause && _downloads.Any(d => d.IsRunning))
        {
            // Pause everything first so each download saves where it got to.
            e.Cancel = true;
            _closingAfterPause = true;
            Enabled = false;
            Text = $"Saving progress - {AppName}";
            foreach (var item in _downloads)
            {
                item.Cts?.Cancel();
            }

            await Task.WhenAny(Task.WhenAll(_downloads.Select(d => d.Run)), Task.Delay(5000));
            SaveSettings();
            Close();
            return;
        }

        _timer.Stop();
        SaveSettings();
        base.OnFormClosing(e);
    }

    private void SaveSettings()
    {
        if (!_initialized)
        {
            return;
        }

        _settings.DownloadFolder = _folderBox.Text.Trim();
        _settings.Mode = _options.Mode;
        _settings.ChunkSizeMB = (int)(_options.ChunkSizeBytes / DownloadOptions.MiB);
        foreach (var route in _routes)
        {
            _settings.Adapters[route.Id] = AdapterPrefs.From(route.Settings);
        }

        _settings.Downloads = _downloads
            .Where(d => d.Download.State != DownloadState.Completed)
            .Select(d => new SavedDownload
            {
                Url = d.Download.Url.AbsoluteUri,
                Folder = d.Download.OutputDirectory,
                FileName = d.Download.FileName,
            })
            .ToList();
        _settings.Save();
    }

    private void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The window is closing.
        }
    }

    private void FlashTaskbar()
    {
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    private static void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No app is associated with this file type.
        }
    }
}
