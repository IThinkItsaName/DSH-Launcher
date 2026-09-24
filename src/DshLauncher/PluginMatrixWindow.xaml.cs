using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DshLauncher.Models;
using DshLauncher.Services;
using Binding = System.Windows.Data.Binding;
using GridView = System.Windows.Controls.GridView;
using GridViewColumn = System.Windows.Controls.GridViewColumn;
using UserControl = System.Windows.Controls.UserControl;

namespace DshLauncher;

/// <summary>
/// 插件 × 实例矩阵（内嵌页）：行 = 插件，列 = 实例，单元格三态。
/// 列在代码里按实例动态生成；数据来自各实例 web profile 的插件列表。
/// </summary>
public partial class PluginMatrixWindow : UserControl
{
    private readonly PluginMatrixService _service;
    private readonly IReadOnlyList<ManagerInstance> _instances;
    private readonly Func<string, bool> _isRunning;
    private CancellationTokenSource? _cancellation;
    private PluginMatrix _matrix = PluginMatrix.Empty;

    public PluginMatrixWindow(
        ExtensionService extensionService,
        IEnumerable<ManagerInstance> instances,
        Func<string, bool> isRunning)
    {
        _service = new PluginMatrixService(extensionService);
        _instances = instances.ToArray();
        _isRunning = isRunning;
        InitializeComponent();
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        // 变更集 174（B3b）：矩阵列头点击排序（插件名 / 版本 / 各实例状态列）。
        MatrixList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(MatrixHeader_Click));
        await RefreshAsync();
    }

    private string? _sortKey;
    private bool _sortAscending = true;

    /// <summary>当前排序在状态行里的说法（未点过列头时为空，表示保持服务层默认顺序）。</summary>
    private string SortSummary => _sortKey switch
    {
        null => string.Empty,
        "name" => $"，按 插件 名称{(_sortAscending ? "升序" : "降序")}",
        "version" => $"，按 版本{(_sortAscending ? "升序" : "降序")}",
        { } id => $"，按 {_matrix.Columns.FirstOrDefault(column => column.InstanceId == id)?.InstanceName ?? "实例"} 状态{(_sortAscending ? "升序" : "降序")}"
    };

    /// <summary>
    /// 列头点击：切列或反向。键为 <c>name</c> / <c>version</c> / 实例 ID（状态列按中文状态文本排序）。
    /// 行的默认顺序仍由服务层决定，只有用户点过列头才重排。
    /// </summary>
    private void MatrixHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Column is null
            || MatrixList.View is not GridView view)
        {
            return;
        }

        var index = view.Columns.IndexOf(header.Column);
        if (index < 0)
        {
            return;
        }

        var key = index switch
        {
            0 => "name",
            1 => "version",
            _ => index - 2 < _matrix.Columns.Count ? _matrix.Columns[index - 2].InstanceId : null
        };
        if (key is null)
        {
            return;
        }

        _sortAscending = string.Equals(_sortKey, key, StringComparison.Ordinal) ? !_sortAscending : true;
        _sortKey = key;
        RenderMatrix();
    }

    /// <summary>应用当前排序（未点过列头时保持服务层的行序）。</summary>
    private IReadOnlyList<PluginMatrixRow> SortRows(IReadOnlyList<PluginMatrixRow> rows)
    {
        if (_sortKey is not { } key)
        {
            return rows;
        }

        Func<PluginMatrixRow, string> selector = key switch
        {
            "name" => row => row.Name,
            "version" => row => row.Version ?? string.Empty,
            _ => row => row[key]
        };
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return (_sortAscending
            ? rows.OrderBy(selector, comparer)
            : rows.OrderByDescending(selector, comparer)).ToArray();
    }

    private void Window_OnUnloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch
        {
            // 已取消/已释放都无所谓。
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_matrix.Rows.Count == 0)
        {
            StatusText.Text = "没有可复制的数据。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_matrix.ToText());
            StatusText.Text = "已复制到剪贴板。";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            StatusText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
        }
        catch
        {
            // 忽略取消竞态。
        }

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        StatusText.Text = "正在读取各实例插件…";
        try
        {
            var matrix = await _service.LoadAsync(_instances, _isRunning, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _matrix = matrix;
            RenderMatrix();
        }
        catch (OperationCanceledException)
        {
            // 页面已离开/再次刷新。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    private void RenderMatrix()
    {
        var view = new GridView();
        // 变更集 173：矩阵列的列头也走共享 keyed 样式（列是动态生成的，只能在代码里挂）。
        if (TryFindResource("TableColumnHeaderStyle") is Style headerStyle)
        {
            view.ColumnHeaderContainerStyle = headerStyle;
        }

        view.Columns.Add(new GridViewColumn
        {
            Header = "插件",
            Width = 220,
            DisplayMemberBinding = new Binding(nameof(PluginMatrixRow.Name))
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "版本",
            Width = 110,
            DisplayMemberBinding = new Binding(nameof(PluginMatrixRow.Version))
        });
        foreach (var column in _matrix.Columns)
        {
            var header = column.InstanceName.Length > 16
                ? column.InstanceName[..16] + "…"
                : column.InstanceName;
            view.Columns.Add(new GridViewColumn
            {
                Header = column.Running ? $"{header}（运行中）" : header,
                Width = 130,
                // 绑定到行的字符串索引器：{Binding [instanceId]}
                DisplayMemberBinding = new Binding($"[{column.InstanceId}]")
            });
        }

        MatrixList.View = view;
        MatrixList.ItemContainerStyle = TryFindResource("TableRowStyle") as Style;
        MatrixList.ItemsSource = SortRows(_matrix.Rows);
        EmptyText.Visibility = _matrix.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = _matrix.Columns.Count == 0
            ? "还没有实例。"
            : $"{_matrix.Columns.Count} 个实例 · {_matrix.Rows.Count} 个插件 · 已启用 {_matrix.EnabledCount} 处"
                + SortSummary + "（点列头排序）";
    }
}
