using System.Windows;
using System.Windows.Controls;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    /// <summary>
    /// 数据源详情（§25）：极轻量调试入口，展示最近一次快照的
    /// Raw → canonical 对应关系。不做任何花哨样式，不属于正式功能页。
    /// </summary>
    public partial class DataSourcesDetailWindow : Window
    {
        public DataSourcesDetailWindow(
            IReadOnlyList<TelemetryDebugRow> rows,
            string versions,
            string capturedAt)
        {
            InitializeComponent();
            Title = $"数据源详情 · {capturedAt}";
            VersionsText.Text = string.IsNullOrWhiteSpace(versions)
                ? "来源版本：不可用"
                : "来源版本：" + versions;
            RowsGrid.ItemsSource = rows;
        }
    }
}
