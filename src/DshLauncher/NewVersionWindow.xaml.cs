using System.Windows;
using DshLauncher.Models;

namespace DshLauncher;

public partial class NewVersionWindow : Window
{
    private readonly string _defaultVersion;

    public NewVersionWindow(
        Window? owner,
        IReadOnlyList<string> versions,
        string defaultVersion,
        DshDownloadSource defaultDownloadSource = DshDownloadSource.Official)
    {
        InitializeComponent();
        Owner = owner;
        _defaultVersion = defaultVersion?.Trim() ?? string.Empty;
        DownloadSourceBox.Items.Add("npm 官方源（registry.npmjs.org）");
        DownloadSourceBox.Items.Add("npmmirror 国内镜像（registry.npmmirror.com）");
        DownloadSourceBox.SelectedIndex = defaultDownloadSource == DshDownloadSource.ChinaMirror ? 1 : 0;
        UpdateVersions(versions);
        NameBox.SelectAll();
        NameBox.Focus();
        VersionBox.SelectionChanged += (_, _) =>
        {
            if (NameBox.Text.StartsWith("DSh ", StringComparison.Ordinal))
            {
                NameBox.Text = VersionBox.SelectedItem is { } selected ? $"DSh {selected}" : string.Empty;
            }
        };
    }

    /// <summary>
    /// 异步补全官方版本列表：保留用户已选版本；名称仍是默认值时同步更新。
    /// 由 <see cref="VersionControlWindow"/> 在弹窗打开后调用（npmjs 可能很慢）。
    /// </summary>
    public void UpdateVersions(IReadOnlyList<string> versions)
    {
        var previous = VersionBox.SelectedItem as string;
        VersionBox.ItemsSource = versions;
        var selected = versions.FirstOrDefault(version =>
                string.Equals(version, previous, StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault(version =>
                string.Equals(version, _defaultVersion, StringComparison.OrdinalIgnoreCase))
            ?? versions.FirstOrDefault();
        VersionBox.SelectedItem = selected;
        if (string.IsNullOrWhiteSpace(NameBox.Text)
            || string.Equals(NameBox.Text, "DSh", StringComparison.Ordinal)
            || NameBox.Text.StartsWith("DSh ", StringComparison.Ordinal))
        {
            NameBox.Text = selected is null ? string.Empty : $"DSh {selected}";
        }
    }

    public string VersionName => NameBox.Text.Trim();

    public string DshVersion => VersionBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;

    /// <summary>弹窗内临时选择的下载源；只影响本次创建，不回写设置（work-log/161）。</summary>
    public DshDownloadSource SelectedDownloadSource =>
        DownloadSourceBox.SelectedIndex == 1 ? DshDownloadSource.ChinaMirror : DshDownloadSource.Official;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VersionName) || string.IsNullOrWhiteSpace(DshVersion))
        {
            AppDialog.Show(this, "请输入版本名称并选择 DSh 版本。", "新建版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
