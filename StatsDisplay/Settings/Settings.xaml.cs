using System;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using StatsDisplay.Settings.Messages;

namespace StatsDisplay.Settings
{
    /// <summary>
    /// Interaction logic for Settings.xaml
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public Properties.Settings Settings => App.Settings;
        private readonly SettingsVm _viewModel;

        public SettingsWindow()
        {
            _viewModel = new SettingsVm();
            DataContext = _viewModel;

            WeakReferenceMessenger.Default.Register<ShowSettingsWindow>(this, (r, m) => Show());
            WeakReferenceMessenger.Default.Register<HideSettingsWindow>(this, (r, m) => Hide());

            Loaded += (_, __) => _viewModel.OnActivated();

            if (App.Settings.SettingsWindowTop <= 0) {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            _viewModel.OnWindowStateChanged();
    }
    }
}
