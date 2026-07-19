using System;
using System.Linq;
using System.Collections.Generic;
using Heroes.ReplayParser;
using System.ComponentModel;

namespace StatsFetcher
{
    public class PlayerProfile : INotifyPropertyChanged
    {
        public Game Game { get; private set; }
        public PlayerProfile(Game game, string battleTag, Region region)
        {
            BattleTag = battleTag;
            Region = region;
            Game = game;
            Ranks = new Dictionary<GameMode, MmrValue> {
                { GameMode.QuickMatch, null },
                { GameMode.UnrankedDraft, null },
                { GameMode.HeroLeague, null },
                { GameMode.TeamLeague, null },
                { GameMode.StormLeague, null },
            };
            HeroWinRates = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            MapWinRates = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }

        public string BattleTag { get; private set; }
        public Region Region { get; private set; }
        public long? ProfileId { get; set; }
        public int Team { get; set; }
        public string Hero { get; set; }

        public Dictionary<GameMode, MmrValue> Ranks { get; private set; }

        public float? MapWinRate { get; set; }
        public float? HeroWinRate { get; set; }
        public int? HeroLevel { get; set; }
        public int? GamesCount { get; set; }

        public string Name { get { return BattleTag.Split('#')[0]; } }
        public string Link { get { return ProfileId == null ? null : $"http://www.hotslogs.com/Player/Profile?PlayerID={ProfileId}&utm_source=HotsStats&utm_medium=link"; } }

        // using this properties greatly simplifies style bingings in gui
        public bool IsMe { get { return Game.Me == this; } }
        public bool IsMyTeam { get { return (Game.Me?.Team ?? 0) == Team; } }

        public ScoreResult Stats { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void TriggerPropertyChanged()
        {
            // I'm too lazy to integrate this stuff for each property so we just trigger them all at once
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        // Per-hero / per-map win rates (0..100), keyed by display name as returned by HeroesProfile.
        // Populated once ProfileId is known; a miss on lookup (e.g. a localized hero/map name) is
        // logged rather than throwing - see FileProcessor.ExtractFullData.
        public Dictionary<string, float> HeroWinRates { get; private set; }
        public Dictionary<string, float> MapWinRates { get; private set; }

        public class MmrValue
        {
            public int Mmr { get; set; }
            public League? League { get; set; }
            public GameMode GameMode { get; set; }
            public int? LeagueRank { get; set; }

            public MmrValue()
            {
            }

            public MmrValue(GameMode gameMode, int mmr, League? league, int? leagueRank)
            {
                Mmr = mmr;
                League = league;
                GameMode = gameMode;
                LeagueRank = leagueRank;
            }            
        }

        public enum League
        {
            Master = 1,
            Diamond= 2,
            Platinum = 3,
            Gold = 4,
            Silver = 5,
            Bronze = 6
        }
    }
}
