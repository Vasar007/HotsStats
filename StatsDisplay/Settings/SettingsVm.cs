using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Heroes.ReplayParser;
using NLog;
using StatsDisplay.Settings.Messages;
using Application = System.Windows.Application;

namespace StatsDisplay.Settings
{
    /// <summary>
    /// Viewmodel for the settings window
    /// </summary>
    public class SettingsVm : ObservableObject
    {
        public Properties.Settings Settings => App.Settings;
        private static Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly Assembly _currentAssembly;
        private WindowState _windowState;
        private NotifyIcon _trayIcon;

        #region Bindable Properties
        public ICommand NavigateToDataSource { get; set; }
        public ICommand Test1 { get; set; }
        public ICommand Test2 { get; set; }
        public ICommand Test3 { get; set; }

        public string Title { get; private set; }
        public string DataSourceUri => "https://www.heroesprofile.com/";
        public bool IsTestButtonVisible => App.Debug;

        public WindowState WindowState
        {
            get { return _windowState; }
            set
            {
                if (_windowState != value) {
                    _windowState = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets the available game modes
        /// </summary>
        public IEnumerable<GameMode> GameModes { private set; get; }
        #endregion

        public SettingsVm()
        {
            GameModes = new List<GameMode> {
                GameMode.QuickMatch,
                GameMode.UnrankedDraft,
                GameMode.HeroLeague,
                GameMode.TeamLeague,
                GameMode.StormLeague,
            };

            _currentAssembly = Assembly.GetExecutingAssembly();
            var currentVersion = _currentAssembly.GetName().Version;
            Title = $"HotsStats v{currentVersion.Major}.{currentVersion.Minor}" + (currentVersion.Build == 0 ? "" : $".{currentVersion.Build}");
            NavigateToDataSource = new RelayCommand(() => OnNavigate(DataSourceUri));
            Test1 = new RelayCommand(() => OnTest1());
            Test2 = new RelayCommand(() => OnTest2());
            Test3 = new RelayCommand(() => OnTest3());
        }

        private void OnNavigate(string uri)
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }

        private void OnTest1()
        {
            (Application.Current as App).ProcessLobbyFile(@"replay.server.battlelobby");
        }

        private void OnTest2()
        {
            (Application.Current as App).ProcessRejoinFile(@"save.StormSave");
        }

        private void OnTest3()
        {
            (Application.Current as App).ProcessReplayFile(@"replay.StormReplay");
        }

        public void OnActivated()
        {
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            // Environment.ProcessPath is the robust choice on modern .NET: Assembly.Location can
            // be an empty string under single-file publish, which throws inside
            // Icon.ExtractAssociatedIcon. For a normal build both paths point at the same exe.
            var iconSourcePath = Environment.ProcessPath ?? _currentAssembly.Location;
            _trayIcon = new NotifyIcon {
                Icon = Icon.ExtractAssociatedIcon(iconSourcePath),
                Visible = false
            };
            _trayIcon.Click += (o, e) => {
                SendMessage<ShowSettingsWindow>();
                WindowState = WindowState.Normal;
                _trayIcon.Visible = false;
            };
        }

        public void OnWindowStateChanged()
        {
            if (Settings.MinimizeToTray && WindowState == WindowState.Minimized) {
                SendMessage<HideSettingsWindow>();
                _trayIcon.Visible = true;
            }
        }

        /// <summary>
        /// Sends a  simple message using the default messenger
        /// </summary>
        /// <typeparam name="T">the message type</typeparam>
        private void SendMessage<T>()
            where T : class, new()
        {
            WeakReferenceMessenger.Default.Send<T>(new T());
        }
    }
}
