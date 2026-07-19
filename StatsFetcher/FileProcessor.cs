using System;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Heroes.ReplayParser;
using Foole.Mpq;
using NLog;

namespace StatsFetcher
{
	public static class FileProcessor
	{
		private static Logger _logger = LogManager.GetCurrentClassLogger();

		public static async Task<Game> ProcessLobbyFile(string path)
		{
			var tmpPath = Path.GetTempFileName();
			try {
				await SafeCopy(path, tmpPath, true);
				var game = new BattleLobbyParser(tmpPath).Parse();
				await FetchProfiles(game);
				return game;
			}
			finally {
				try { File.Delete(tmpPath); } catch { }
			}
		}

		public static async Task ProcessReplayFile(string path, Game game)
		{
			var tmpPath = Path.GetTempFileName();
			try {
				// todo: we need a way to detect when HotS finished writing this file
				// For now we will just wait 1 sec and hope it is enough
				await Task.Delay(1000);
				await SafeCopy(path, tmpPath, true);
				var replayData = DataParser.ParseReplay(tmpPath, true, true, skipUnitParsing: true, skipMouseMoveEvents: true);
				var replay = replayData.Item2;

				if (replayData.Item2 == null) {
					throw new Exception($"Unable to parse replay: {replayData.Item1}");
				}

				foreach (var profile in game.Players) {
					var player = replay.Players.FirstOrDefault(p => p.Name == profile.Name);
					if (player == null)
						continue;
					profile.Stats = player.ScoreResult;
				}
			}
			finally {
				try { File.Delete(tmpPath); } catch { }
			}
		}

		public static async Task FetchProfiles(Game game)
		{
			var f = new ProfileFetcher(game);
			await f.FetchBasicProfiles();
			game.TriggerPropertyChanged();
			await f.FetchFullProfiles();
			ExtractBasicData(game);
			game.TriggerPropertyChanged();
		}

		public static async Task ProcessRejoinAsync(string path, Game game)
		{
			var tmpPath = Path.GetTempFileName();
			try {
				await SafeCopy(path, tmpPath, true);

				var replay = ParseRejoin(tmpPath);
				foreach (var profile in game.Players){
					var player = replay.Players.FirstOrDefault(p => p.Name == profile.Name);
					if (player == null)
						continue;
					profile.Hero = player.Character;
					profile.HeroLevel = player.CharacterLevel;
					//profile.Team = player.Team; // this should fix possible mistakes made by battlelobby analyzer
				}
				game.Map = replay.Map;
				game.GameMode = replay.GameMode;
				ExtractFullData(game);
			}
			finally {
				try { File.Delete(tmpPath); } catch { }
			}
		}


		private static async Task SafeCopy(string source, string dest, bool overwrite)
		{
			var watchdog = 10;
			while (true)
			{
				try
				{
					File.Copy(source, dest, overwrite);
					return;
				}
				catch (Exception ex)
				{
					_logger.Warn(ex, $"Failed to copy {source} to {dest}. Retries left: {watchdog}");
					if (watchdog <= 0)
					{
						throw;
					}
				}
				watchdog--;
				await Task.Delay(1000);
			}
		}

		public static Replay ParseRejoin(string fileName)
		{
			try {
				using (var archive = new MpqArchive(fileName)) {
					archive.AddListfileFilenames();
					var replay = new Replay();

					// Replay Details
					ReplayDetails.Parse(replay, DataParser.GetMpqFile(archive, "save.details"), true);

					// Player level is stored there
					// does not exist in brawl
					if (archive.FileExists("replay.attributes.events")) {
						ReplayAttributeEvents.Parse(replay, DataParser.GetMpqFile(archive, "replay.attributes.events"));
					}

					return replay;
				}
			}
			catch (Exception ex) {
				_logger.Warn(ex, $"Failed to parse rejoin file '{fileName}'");
				throw;
			}
		}

		// Games-count is now populated directly from the HeroesProfile battletag/search response
		// (see ProfileFetcher.FetchBasicProfile), so there's nothing left to extract here. Kept as
		// a no-op so the existing FetchProfiles call site doesn't need touching.
		public static void ExtractBasicData(Game game)
		{
		}

		// Extract hero/map win rates from the dictionaries ProfileFetcher.FetchFullProfile filled in
		// once we know which Hero/Map were actually played. A lookup miss (e.g. a localized game
		// client reporting a hero/map name HeroesProfile doesn't recognize) is logged and left unset
		// rather than crashing.
		public static void ExtractFullData(Game game)
		{
			foreach (var p in game.Players) {
				float mapWinRate;
				if (!string.IsNullOrEmpty(game.Map) && p.MapWinRates.TryGetValue(game.Map, out mapWinRate)) {
					p.MapWinRate = mapWinRate;
				}
				else {
					_logger.Warn($"No HeroesProfile win rate for map '{game.Map}' ({p.BattleTag})");
				}

				float heroWinRate;
				if (!string.IsNullOrEmpty(p.Hero) && p.HeroWinRates.TryGetValue(p.Hero, out heroWinRate)) {
					p.HeroWinRate = heroWinRate;
				}
				else {
					_logger.Warn($"No HeroesProfile win rate for hero '{p.Hero}' ({p.BattleTag})");
				}
			}
		}
	}
}
