using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NLog;
using StatsDisplay.Helpers;
using StatsDisplay.Stats;
using StatsFetcher;

namespace StatsDisplay
{
	/// <summary>
	/// Interaction logic for App.xaml
	/// </summary>
	public partial class App : Application
	{
#if DEBUG
		public const bool Debug = true;
#else
		public const bool Debug = false;
#endif

		// introduce some spaghetti with static globals
		public static Game Game { get; set; }
		public static Properties.Settings Settings { get { return StatsDisplay.Properties.Settings.Default; } }

		private static Logger _logger = LogManager.GetCurrentClassLogger();
		private static HotKey _hotKey;
		private SynchronizationContext _currentSyncContext;
		private Window _currentWindow;

		private void Application_Startup(object sender, StartupEventArgs e)
		{
			SetExceptionHandlers();
			_logger.Info("App started");
			_currentSyncContext = SynchronizationContext.Current;

			if (Settings.UpgradeRequired) {
				Settings.Upgrade();
				Settings.UpgradeRequired = false;
				Settings.Save();
			}

			new DispatcherTimer() {
				Interval = TimeSpan.FromHours(1),
				IsEnabled = true
			}.Tick += (_, __) => CheckForUpdates();
			CheckForUpdates();

			SetupFileMonitor();
			SetupHotkeys();
		}

		private void Application_Exit(object sender, ExitEventArgs e)
		{
			Settings.Save();
		}

		private void SetupHotkeys()
		{
			_hotKey = new HotKey(Key.Tab, KeyModifier.Shift | KeyModifier.NoRepeat, register: false);
			if (!_hotKey.Register()) {
				_logger.Warn("Failed to register the Shift+Tab overlay hotkey; toggling the overlay via hotkey will not work.");
			}
			_hotKey.Pressed += (o, e) => {
				if (_currentWindow == null) {
					return;
				}
					
				if (_currentWindow.IsVisible) {
					_currentWindow.Hide();
				} else {
					_currentWindow.Show();
				}

			};
		}

		private void SetupFileMonitor()
		{
			var mon = new FileMonitor();
			//TODO Temporary solution to run on main thread. Refactor filemon to use TPL in order to achieve this
			mon.BattleLobbyCreated += (_, e) => RunOnMainThread(() => ProcessLobbyFile(e.Data));
			mon.RejoinFileCreated += (_, e) => RunOnMainThread(() => ProcessRejoinFile(e.Data));
			mon.ReplayFileCreated += (_, e) => RunOnMainThread(() => ProcessReplayFile(e.Data));
			mon.StartMonitoring();
		}


		internal async void ProcessLobbyFile(string path)
		{
			try {
				if (!Settings.Enabled)
					return;

				//TODO: remove global state
				Game = await FileProcessor.ProcessLobbyFile(path);
				Game.Me = Game.Players.FirstOrDefault(p => p.BattleTag == Settings.BattleTag || p.Name == Settings.BattleTag);

				_currentWindow?.Close();
				_currentWindow = new ShortStatsWindow();
				if (Settings.AutoShow)
					_currentWindow.Show();
			}
			catch (Exception ex) {
				_logger.Error(ex, "Failed to process lobby file");
			}
		}

		internal async void ProcessRejoinFile(string path)
		{
			try {
				if (Game == null) {
					return;
				}
				await FileProcessor.ProcessRejoinAsync(path, Game);
				_currentWindow?.Close();
				_currentWindow = new FullStatsWindow();

				if (Debug) {
					try {
						var path1 = Path.Combine(@"saves", Path.GetRandomFileName());
						Directory.CreateDirectory(path1);
						File.Copy(path, path1 + "\\save.StormSave");
						File.Copy(Path.Combine(Path.GetTempPath(), @"Heroes of the Storm\TempWriteReplayP1\replay.server.battlelobby"), path1 + "\\replay.server.battlelobby");
					}
					catch { }
				}
			}
			catch (Exception ex) {
				MessageBox.Show(ex.ToString());
			}

		}

		internal async void ProcessReplayFile(string path)
		{
			try {
				if (Settings.ShowRecap) {
					if (Game == null) {
						return;
					}
					await FileProcessor.ProcessReplayFile(path, Game);
					_currentWindow?.Close();
					_currentWindow = new RecapStatsWindow();
					if (Settings.AutoShow)
						_currentWindow.Show();
				}
			}
			catch (Exception ex) {
				_logger.Error(ex, "Failed to process replay file");
			}
		}

		private void CheckForUpdates()
		{
			// Auto-update is disabled for this MVP: the default UpdateRepository used to point at
			// the archived poma/HotsStats releases, and there's no vetted hotsapi release to
			// auto-update onto yet either. Short-circuit before contacting Squirrel/GitHub at all.
			_logger.Info("auto-update disabled for MVP");
		}

		/// <summary>
		/// Curry function to allow execution of an action on the main thread using the synchronization context
		/// </summary>
		/// <param name="action"></param>
		private void RunOnMainThread(Action action)
		{
			_currentSyncContext.Post(_ => action(), null);
		}

		private void SetExceptionHandlers()
		{
			DispatcherUnhandledException += (o, e) => {
				_logger.Error(e.Exception, "Dispatcher unhandled exception");
				try {
					MessageBox.Show(e.Exception.ToString(), "Unhandled exception");
				}
				catch { /* probably not gui thread */ }
			};

			AppDomain.CurrentDomain.UnhandledException += (o, e) => {
				_logger.Fatal(e.ExceptionObject as Exception, "Domain unhandled exception");
				try {
					MessageBox.Show(e.ExceptionObject.ToString(), "Critical exception");
				}
				catch { /* probably not gui thread */ }
			};
		}
	}
}
