using System.Windows.Controls;
using AIGeekTuner.ViewModels;

namespace AIGeekTuner.Views
{
    public partial class Dashboard : Page
    {
        public Dashboard()
        {
            InitializeComponent();

            // M5.2C：进入仪表盘立即启动运行时间逐秒时钟（初次打开即显示，
            // 不等待其它硬件来源）；离开页面即停止，不产生后台常驻计时。
            Loaded += (_, _) => (DataContext as DashboardViewModel)?.Hardware.StartUptimeClock();
            Unloaded += (_, _) => (DataContext as DashboardViewModel)?.Hardware.StopUptimeClock();
        }
    }
}
