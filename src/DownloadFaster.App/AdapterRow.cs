using System.Globalization;
using System.Text.RegularExpressions;
using DownloadFaster.Core;

namespace DownloadFaster.App;

/// <summary>The controls for one network adapter in the adapters table.</summary>
internal sealed partial class AdapterRow
{
    private static readonly string[] LimitChoices =
        ["No limit", "0.5 MB/s", "1 MB/s", "2 MB/s", "5 MB/s", "10 MB/s", "20 MB/s", "50 MB/s", "100 MB/s"];

    private readonly Action _changed;
    private readonly ToolTip _tips;
    private readonly Label _title;
    private readonly FlowLayoutPanel _info;
    private readonly NumericUpDown _connections;
    private readonly ComboBox _limit;
    private readonly TrackBar _share;
    private readonly Label _sharePercent;
    private readonly FlowLayoutPanel _sharePanel;
    private readonly Label _status;

    public AdapterRow(NetworkRoute route, Func<int, int> px, ToolTip tips, Action changed)
    {
        Route = route;
        _tips = tips;
        _changed = changed;
        var settings = route.Settings;

        Use = new CheckBox { AutoSize = true, Checked = settings.Enabled, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 8, 3) };
        tips.SetToolTip(Use, "Use this adapter for downloads");

        _title = new Label { AutoSize = true, Text = route.Name, Margin = new Padding(0, 4, 0, 0) };
        var details = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = string.Join("  |  ", new[] { route.KindLabel, route.LocalAddresses[0].ToString(), Units.LinkSpeed(route.LinkSpeedBitsPerSecond) }
                .Where(s => s.Length > 0)),
            Margin = new Padding(0, 0, 0, 4),
        };
        _info = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0),
        };
        _info.Controls.AddRange([_title, details]);
        string addressList = string.Join(Environment.NewLine, route.LocalAddresses);
        tips.SetToolTip(_title, $"{route.Description}{Environment.NewLine}{addressList}");
        tips.SetToolTip(details, $"{route.Description}{Environment.NewLine}{addressList}");

        _connections = new NumericUpDown
        {
            Minimum = 1,
            Maximum = RouteSettings.MaxConnections,
            Value = settings.Connections,
            Width = px(64),
            Anchor = AnchorStyles.Left,
            TextAlign = HorizontalAlignment.Center,
            Margin = new Padding(3, 3, 12, 3),
        };
        tips.SetToolTip(_connections, "Parallel connections through this adapter for downloads started here (the link box). 4-8 is usually plenty. Other apps decide their own number of connections.");

        _limit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = px(96), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };
        _limit.Items.AddRange(LimitChoices);
        _limit.Text = FormatLimit(settings.SpeedLimitBytesPerSecond);
        tips.SetToolTip(_limit, "Cap this adapter's speed, e.g. to save a metered hotspot. Type any number of MB/s (1 MB/s = 8 Mbps).");

        _share = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = px(110),
            Height = px(26),
            Value = settings.Share,
            SmallChange = 5,
            LargeChange = 10,
            Margin = new Padding(0),
        };
        _sharePercent = new Label { AutoSize = false, Width = px(44), Height = px(26), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
        _sharePanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 12, 3) };
        _sharePanel.Controls.AddRange([_share, _sharePercent]);
        tips.SetToolTip(_share, "Share of the file this adapter downloads. Used when Balancing is set to Fixed split.");

        _status = new Label { AutoSize = false, Width = px(170), Height = px(26), TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left };

        Use.CheckedChanged += (_, _) =>
        {
            settings.Enabled = Use.Checked;
            UpdateEnabled();
            _changed();
        };
        _connections.ValueChanged += (_, _) =>
        {
            settings.Connections = (int)_connections.Value;
            _changed();
        };
        _limit.SelectedIndexChanged += (_, _) => ApplyLimit();
        _limit.Leave += (_, _) => ApplyLimit();
        _limit.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                ApplyLimit();
                e.SuppressKeyPress = true;
            }
        };
        _share.ValueChanged += (_, _) =>
        {
            settings.Share = _share.Value;
            _changed();
        };

        UpdateEnabled();
    }

    public NetworkRoute Route { get; }

    public CheckBox Use { get; }

    /// <summary>Shown instead of the live speed while a speed test runs.</summary>
    public string? TestText { get; set; }

    public void AddTo(TableLayoutPanel table, int row)
    {
        _title.Font = new Font(table.Font, FontStyle.Bold);
        table.Controls.Add(Use, 0, row);
        table.Controls.Add(_info, 1, row);
        table.Controls.Add(_connections, 2, row);
        table.Controls.Add(_limit, 3, row);
        table.Controls.Add(_sharePanel, 4, row);
        table.Controls.Add(_status, 5, row);
    }

    /// <param name="fixedSplit">Whether the share sliders are in use.</param>
    /// <param name="totalShare">Sum of the shares of all ticked adapters.</param>
    public void UpdateShare(bool fixedSplit, int totalShare)
    {
        bool active = fixedSplit && Use.Checked;
        _share.Enabled = active;
        _sharePercent.ForeColor = active ? SystemColors.ControlText : SystemColors.GrayText;
        _sharePercent.Text = !active ? "auto" : totalShare > 0 ? $"{100.0 * Route.Settings.Share / totalShare:0}%" : "0%";
    }

    public void ShowStatus(double liveSpeed, bool downloading, string? problem, double? testResult)
    {
        string text;
        Color color = SystemColors.ControlText;
        string? tip = null;

        if (TestText is not null)
        {
            text = TestText;
            color = Color.RoyalBlue;
        }
        else if (!Use.Checked)
        {
            text = "Off";
            color = SystemColors.GrayText;
        }
        else if (downloading && problem is not null)
        {
            text = "Problem, retrying";
            color = Color.Firebrick;
            tip = problem;
        }
        else if (downloading)
        {
            text = $"{Units.Speed(liveSpeed)}";
            tip = Units.Mbps(liveSpeed);
            color = liveSpeed > 0 ? Color.SeaGreen : SystemColors.GrayText;
        }
        else if (testResult is double tested)
        {
            text = tested > 0 ? $"Tested {Units.Mbps(tested)}" : "Test failed";
            color = tested > 0 ? SystemColors.ControlText : Color.Firebrick;
        }
        else
        {
            text = "Ready";
            color = SystemColors.GrayText;
        }

        if (_status.Text != text)
        {
            _status.Text = text;
        }

        _status.ForeColor = color;
        if (_tips.GetToolTip(_status) != (tip ?? ""))
        {
            _tips.SetToolTip(_status, tip);
        }
    }

    private void UpdateEnabled()
    {
        _connections.Enabled = Use.Checked;
        _limit.Enabled = Use.Checked;
    }

    private void ApplyLimit()
    {
        var match = NumberPattern().Match(_limit.Text);
        double mbps = match.Success
            ? double.Parse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture)
            : 0;
        long bytes = (long)(Math.Max(0, mbps) * DownloadOptions.MiB);
        Route.Settings.SpeedLimitBytesPerSecond = bytes;

        string normalized = FormatLimit(bytes);
        if (_limit.Text != normalized)
        {
            _limit.Text = normalized;
        }

        _changed();
    }

    private static string FormatLimit(long bytesPerSecond) => bytesPerSecond <= 0
        ? "No limit"
        : string.Create(CultureInfo.CurrentCulture, $"{(double)bytesPerSecond / DownloadOptions.MiB:0.#} MB/s");

    [GeneratedRegex(@"\d+([.,]\d+)?")]
    private static partial Regex NumberPattern();
}
