namespace VirtualMonitorsUniverse.Server;

/// <summary>
/// Displays the SQLite operational log using the visual concept established by
/// Socket Universe Bridge. Monitor filters will occupy the left pane once monitor
/// persistence is introduced.
/// </summary>
internal sealed class LogForm : Form
{
    private readonly LogStore _store;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoGenerateColumns = false };
    private readonly TextBox _search = new() { Width = 230, PlaceholderText = "Search..." };
    private readonly Button _clearSearch = new() { Text = "×", Width = 22, Height = 23, Margin = new Padding(2, 0, 0, 0), TabStop = false, Enabled = false };
    private readonly CheckBox _tail = new() { Text = "Always at end", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private long _lastId = -1;

    public LogForm(LogStore store, Icon icon)
    {
        _store = store;
        Text = "Log";
        Icon = icon;
        Width = 1250;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
        BuildUi();
        ConfigureGrid();

        _search.TextChanged += (_, _) => { _clearSearch.Enabled = _search.TextLength > 0; Render(force: true); };
        _clearSearch.Click += (_, _) => _search.Clear();
        _tail.CheckedChanged += (_, _) => { if (_tail.Checked) ScrollToEnd(); };
        _timer.Tick += (_, _) => Render(force: false);
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
        Shown += (_, _) => Render(force: true);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "●  Server", AutoSize = true, ForeColor = Color.ForestGreen, Anchor = AnchorStyles.Left }, 0, 0);
        var searchPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        searchPanel.Controls.Add(_search);
        searchPanel.Controls.Add(_clearSearch);
        top.Controls.Add(searchPanel, 1, 0);
        root.Controls.Add(top, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterDistance = 200, Panel1MinSize = 120, Panel2MinSize = 300 };
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = new Padding(0) };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label { Text = "Monitor filters", AutoSize = true, Padding = new Padding(0, 5, 0, 5), Margin = new Padding(0), BackColor = Color.Gainsboro, Dock = DockStyle.Fill }, 0, 0);
        left.Controls.Add(new Label { Text = "No monitors", AutoSize = true, Padding = new Padding(8), ForeColor = Color.DimGray }, 0, 1);
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(_grid);
        root.Controls.Add(split, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_tail, 0, 0);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
        var clear = new Button { Text = "Clear", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        clear.Click += (_, _) => ClearLog();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(clear);
        buttons.Controls.Add(close);
        bottom.Controls.Add(buttons, 1, 0);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        var timeStyle = new DataGridViewCellStyle { Format = "dd.MM.yyyy HH:mm:ss.fff", Alignment = DataGridViewContentAlignment.MiddleRight };
        _grid.DefaultCellStyle.Font = new Font("Consolas", 9f);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Timecode", Width = 185, ValueType = typeof(DateTime), DefaultCellStyle = timeStyle });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Level", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Service", HeaderText = "Service", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Monitor", HeaderText = "Monitor", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = "Event", Width = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            var level = Convert.ToString(_grid.Rows[e.RowIndex].Cells["Level"].Value) ?? string.Empty;
            _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ? Color.Firebrick : level.Equals("WARNING", StringComparison.OrdinalIgnoreCase) ? Color.DarkOrange : SystemColors.ControlText;
        };
    }

    private void Render(bool force)
    {
        try
        {
            var entries = _store.ReadAll(_search.Text);
            var newest = entries.Count == 0 ? 0 : entries[^1].Id;
            if (!force && newest == _lastId) return;
            _lastId = newest;
            _grid.Rows.Clear();
            foreach (var entry in entries)
            {
                _grid.Rows.Add(entry.Timestamp, entry.Level, entry.Service, entry.MonitorId ?? string.Empty, entry.Event, entry.Message);
            }
            if (_tail.Checked) ScrollToEnd();
        }
        catch
        {
            // The viewer must not terminate the tray application if a read temporarily fails.
        }
    }

    private void ScrollToEnd()
    {
        if (_grid.Rows.Count > 0) _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
    }

    private void ClearLog()
    {
        if (MessageBox.Show("Are you sure you want to clear the log?", "Clear log", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _store.Clear();
        _lastId = -1;
        Render(force: true);
    }
}
