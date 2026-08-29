using System.ComponentModel;

namespace VirtualMonitorsUniverse.Server;

internal sealed class LogForm : Form
{
    private static readonly (string Label, string Key)[] Sources =
    [
        ("VMU", "VMU"),
        ("VMU Server", "VMU_SERVER"),
        ("Web Server", "WEB"),
        ("Socket Server", "SOCKET"),
    ];

    private readonly LogStore _store;
    private readonly Icon _icon;
    private readonly Func<IReadOnlyDictionary<string, bool>> _statusProvider;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoGenerateColumns = false,
        ColumnHeadersHeight = 24,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EnableHeadersVisualStyles = false,
    };
    private readonly TextBox _search = new() { Width = 230, PlaceholderText = "Search..." };
    private readonly Button _clearSearch = new() { Text = "×", Width = 22, Height = 23, Enabled = false, TabStop = false };
    private readonly CheckBox _tail = new() { Text = "Always at end", Checked = true, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 750 };
    private readonly Dictionary<string, CheckBox> _filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PictureBox> _statusLights = new(StringComparer.OrdinalIgnoreCase);
    private SplitContainer? _split;
    private FlowLayoutPanel? _tailHost;
    private LogDetailForm? _detailForm;
    private long _lastId = -1;
    private string _sortColumnName = "Time";
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public LogForm(LogStore store, Icon icon, Func<IReadOnlyDictionary<string, bool>> statusProvider)
    {
        _store = store;
        _icon = icon;
        _statusProvider = statusProvider;
        Text = "Log";
        Icon = icon;
        Width = 1250;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);

        BuildUi();
        ConfigureGrid();
        _search.TextChanged += (_, _) => { _clearSearch.Enabled = _search.TextLength > 0; Render(true); };
        _clearSearch.Click += (_, _) => _search.Clear();
        _tail.CheckedChanged += (_, _) =>
        {
            _grid.SortModeForAll(_tail.Checked ? DataGridViewColumnSortMode.NotSortable : DataGridViewColumnSortMode.Programmatic);
            if (_tail.Checked)
            {
                ClearSortGlyphs();
                SelectEnd();
            }
            else
            {
                _sortColumnName = "Time";
                _sortDirection = ListSortDirection.Ascending;
                ApplySort();
            }
        };
        _grid.ColumnHeaderMouseClick += (_, e) => SortByColumn(e.ColumnIndex);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenDetail(); };
        _grid.SelectionChanged += (_, _) => UpdateOpenDetail();
        _timer.Tick += (_, _) => { RefreshStatusLights(); Render(false); };
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _detailForm?.Close(); };
        Shown += (_, _) => BeginInvoke((Action)(() => { ApplyInitialSplitterDistance(); RefreshStatusLights(); Render(true); }));
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var searchPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
        searchPanel.Controls.Add(_search);
        searchPanel.Controls.Add(_clearSearch);
        top.Controls.Add(searchPanel, 1, 0);
        root.Controls.Add(top, 0, 0);

        _split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        _split.SplitterMoved += (_, _) => AlignBottomControls();
        var left = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(new Label { Text = "Filters", AutoSize = true, Padding = new Padding(0, 5, 0, 5), Margin = Padding.Empty, BackColor = Color.Gainsboro, Dock = DockStyle.Fill }, 0, 0);
        var filterList = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(8, 6, 8, 0) };
        filterList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        foreach (var (label, key) in Sources)
        {
            var row = filterList.RowCount++;
            var check = new CheckBox { Text = label, Checked = true, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            check.CheckedChanged += (_, _) => Render(true);
            var light = new PictureBox { Size = new Size(14, 14), SizeMode = PictureBoxSizeMode.StretchImage, Anchor = AnchorStyles.Right, Margin = new Padding(0, 3, 2, 2) };
            _filters[key] = check;
            _statusLights[key] = light;
            filterList.Controls.Add(check, 0, row);
            filterList.Controls.Add(light, 1, row);
        }
        left.Controls.Add(filterList, 0, 1);
        _split.Panel1.Controls.Add(left);
        _split.Panel2.Controls.Add(_grid);
        root.Controls.Add(_split, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tailHost = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _tailHost.Controls.Add(_tail);
        bottom.Controls.Add(_tailHost, 0, 0);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
        var export = new Button { Text = "Export", AutoSize = true, Image = UiIcons.Create(UiIconKind.Export, 16), TextImageRelation = TextImageRelation.ImageBeforeText };
        var clear = new Button { Text = "Clear", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true };
        export.Click += (_, _) => ExportLog();
        clear.Click += (_, _) => ClearLog();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(export);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(close);
        bottom.Controls.Add(buttons, 1, 0);
        root.Controls.Add(bottom, 0, 2);
        Controls.Add(root);
    }

    private void ConfigureGrid()
    {
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(225, 230, 236);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 230, 236);
        var timeStyle = new DataGridViewCellStyle { Format = "dd.MM.yyyy HH:mm:ss.fff", Alignment = DataGridViewContentAlignment.MiddleRight };
        _grid.DefaultCellStyle.Font = new Font("Consolas", 9f);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Timecode", Width = 185, ValueType = typeof(DateTime), DefaultCellStyle = timeStyle });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "Level", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Service", HeaderText = "Service", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Monitor", HeaderText = "Monitor", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = "Event", Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Message", HeaderText = "Message", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.SortModeForAll(DataGridViewColumnSortMode.NotSortable);
    }

    private void SortByColumn(int columnIndex)
    {
        if (_tail.Checked || columnIndex < 0 || columnIndex >= _grid.Columns.Count) return;
        var column = _grid.Columns[columnIndex];
        if (_sortColumnName == column.Name)
            _sortDirection = _sortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        else
        {
            _sortColumnName = column.Name;
            _sortDirection = ListSortDirection.Ascending;
        }
        ApplySort();
    }

    private void ApplySort()
    {
        if (_tail.Checked || _grid.Rows.Count == 0) return;
        var column = _grid.Columns[_sortColumnName];
        if (column is null) return;
        _grid.Sort(column, _sortDirection);
        ClearSortGlyphs();
        column.HeaderCell.SortGlyphDirection = _sortDirection == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
    }

    private void ClearSortGlyphs()
    {
        foreach (DataGridViewColumn column in _grid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
    }

    private IReadOnlyCollection<string> SelectedServices() => _filters.Where(x => x.Value.Checked).Select(x => x.Key).ToArray();

    private void Render(bool force)
    {
        try
        {
            var selectedId = SelectedEntry()?.Id;
            var entries = _store.Read(_search.Text, SelectedServices());
            var newest = entries.Count == 0 ? 0 : entries[^1].Id;
            if (!force && newest == _lastId) return;
            _lastId = newest;
            _grid.Rows.Clear();
            foreach (var entry in entries)
            {
                var index = _grid.Rows.Add(entry.Timestamp, entry.Level, entry.Service, entry.MonitorId ?? string.Empty, entry.Event, entry.Message);
                _grid.Rows[index].Tag = entry;
                if (!_tail.Checked && selectedId == entry.Id) _grid.Rows[index].Selected = true;
            }
            if (_tail.Checked) SelectEnd();
            else ApplySort();
            UpdateOpenDetail();
        }
        catch
        {
            // A temporary SQLite read problem must not terminate the tray application.
        }
    }

    private void RefreshStatusLights()
    {
        IReadOnlyDictionary<string, bool> states;
        try { states = _statusProvider(); } catch { return; }
        foreach (var (key, picture) in _statusLights)
        {
            picture.Image?.Dispose();
            picture.Image = UiIcons.Create(states.TryGetValue(key, out var running) && running ? UiIconKind.Running : UiIconKind.Stopped, 14);
        }
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export VMU log",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv|Text (*.txt)|*.txt",
            DefaultExt = "xlsx",
            AddExtension = true,
            FileName = $"vmu-log-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LogExportService.Export(dialog.FileName, _store.Read(_search.Text, SelectedServices())); }
        catch (Exception ex) { MessageBox.Show($"Could not export the log.\r\n\r\n{ex.Message}", "VMU Log", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ApplyInitialSplitterDistance()
    {
        if (_split is null) return;
        var width = _split.ClientSize.Width;
        if (width < 425) return;
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 300;
        _split.SplitterDistance = Math.Clamp((int)(width * 0.18), 120, width - 305);
        AlignBottomControls();
    }

    private void AlignBottomControls()
    {
        if (_split is not null && _tailHost is not null) _tailHost.Padding = new Padding(_split.SplitterDistance + _split.SplitterWidth, 0, 0, 0);
    }

    private void SelectEnd()
    {
        if (_grid.Rows.Count == 0) return;
        var row = _grid.Rows[^1];
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells[0];
        _grid.FirstDisplayedScrollingRowIndex = row.Index;
    }

    private LogEntry? SelectedEntry() => _grid.SelectedRows.Count == 0 ? null : _grid.SelectedRows[0].Tag as LogEntry;

    private void OpenDetail()
    {
        var entry = SelectedEntry();
        if (entry is null) return;
        if (_detailForm is null || _detailForm.IsDisposed)
        {
            _detailForm = new LogDetailForm(_icon);
            _detailForm.FormClosed += (_, _) => _detailForm = null;
            _detailForm.Show(this);
        }
        _detailForm.ShowEntry(entry);
        _detailForm.Activate();
    }

    private void UpdateOpenDetail()
    {
        if (_detailForm is not null && !_detailForm.IsDisposed && SelectedEntry() is { } entry) _detailForm.ShowEntry(entry);
    }

    private void ClearLog()
    {
        if (MessageBox.Show("Clear the log?", "Clear Log", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _store.Clear();
        _lastId = -1;
        Render(true);
    }
}

internal static class DataGridViewExtensions
{
    public static void SortModeForAll(this DataGridView grid, DataGridViewColumnSortMode mode)
    {
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = mode;
    }
}
