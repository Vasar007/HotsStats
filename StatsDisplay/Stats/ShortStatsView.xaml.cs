using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using StatsDisplay.Stats.Messages;

namespace StatsDisplay.Stats
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class ShortStatsWindow : HeroesWindow
    {
        private ShortStatsVm _viewModel;
        public  ShortStatsWindow()
        {
            InitializeComponent();
            _viewModel = new ShortStatsVm();
            if (!System.ComponentModel.DesignerProperties.GetIsInDesignMode(this))
            {
                this.DataContext = _viewModel;
            }

            if (App.Settings.ShortStatsWindowTop <= 0)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            WeakReferenceMessenger.Default.Register<HideShortStats>(this, (r, m) => Hide());
            Loaded += (_, __) => _viewModel.OnActivated();
            Closed += (_, __) => _viewModel.OnDeactivated();
        }
    }
}
