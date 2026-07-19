using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.Messaging;
using Heroes.ReplayParser;
using StatsDisplay.Stats.Messages;
using StatsFetcher;

namespace StatsDisplay.Stats
{
    public class ShortStatsVm : ViewModelBase
    {
        public Game Game => App.Game;
        public Properties.Settings Settings => App.Settings;
        private int? _teamOneAverageMmr;
        private int? _teamTwoAverageMmr;
        private TeamVm _teamOne;
        private TeamVm _teamTwo;
        private PropertyChangedEventHandler _settingsPropertyChangedHandler;
        private PropertyChangedEventHandler _gamePropertyChangedHandler;
        private Game _subscribedGame;

        public int? TeamTwoAverageMmr
        {
            get { return _teamTwoAverageMmr; }
            set
            {
                if (_teamTwoAverageMmr != value) {
                    _teamTwoAverageMmr = value;
                    RaisePropertyChanged();
                }
            }
        }

        public int? TeamOneAverageMmr
        {
            get { return _teamOneAverageMmr; }
            set
            {
                if (_teamOneAverageMmr != value) {
                    _teamOneAverageMmr = value;
                    RaisePropertyChanged();
                }
            }
        }

        public TeamVm TeamOne
        {
            get { return _teamOne; }
            set
            {
                if (_teamOne != value) {
                    _teamOne = value;
                    RaisePropertyChanged();
                }
            }
        }

        public TeamVm TeamTwo
        {
            get { return _teamTwo; }
            set
            {
                if (_teamTwo != value) {
                    _teamTwo = value;
                    RaisePropertyChanged();
                }
            }
        }

        public ShortStatsVm()
        {

        }

        public async void OnActivated()
        {
            var me = Game.Me;
            var myTeam = me?.Team ?? 0;

            // time for some quick ugly hacks
            var teamOneMmebers = Game.Players.Where(p => p.Team == 0).ToList();
            var teamTwoMembers = Game.Players.Where(p => p.Team == 1).ToList();
            if (teamOneMmebers.Contains(me)) {
                teamOneMmebers.Remove(me);
                teamOneMmebers.Insert(0, me);
            }
            if (teamTwoMembers.Contains(me)) {
                teamTwoMembers.Remove(me);
                teamTwoMembers.Insert(0, me);
            }
            TeamOne = new TeamVm(teamOneMmebers);
            TeamTwo = new TeamVm(teamTwoMembers);

            TeamOne.TeamType = myTeam == 0 ? TeamTypes.Friendly : TeamTypes.Enemy;
            TeamTwo.TeamType = myTeam == 1 ? TeamTypes.Friendly : TeamTypes.Enemy;

            TeamOneAverageMmr = TeamOne.AverageMmr(Settings.MmrDisplayMode);
            TeamTwoAverageMmr = TeamTwo.AverageMmr(Settings.MmrDisplayMode);

            // Capture the specific Game instance this VM is bound to rather than re-reading the
            // Game property later: App.Game (and therefore what the Game property returns) is
            // reassigned to a new Game on the next match before this window's Closed handler
            // fires, so unsubscribing via the live Game property in OnDeactivated could detach
            // from the wrong (new) instance and leave this subscription on the old one forever.
            _subscribedGame = Game;
            _gamePropertyChangedHandler = (o, e) => {
                TeamOneAverageMmr = TeamOne.AverageMmr(Settings.MmrDisplayMode);
                TeamTwoAverageMmr = TeamTwo.AverageMmr(Settings.MmrDisplayMode);
            };
            _subscribedGame.PropertyChanged += _gamePropertyChangedHandler;
            // Settings.Default is an application-lifetime static, so this subscription must be
            // removed explicitly (see OnDeactivated) or every game would leak this view model
            // (and the window holding it) for the lifetime of the app.
            _settingsPropertyChangedHandler = (o, e) => {
                if (e.PropertyName == nameof(Settings.MmrDisplayMode)) {
                    TeamOneAverageMmr = TeamOne.AverageMmr(Settings.MmrDisplayMode);
                    TeamTwoAverageMmr = TeamTwo.AverageMmr(Settings.MmrDisplayMode);
                }
            };
            Settings.PropertyChanged += _settingsPropertyChangedHandler;

            if (Settings.AutoClose) {
                await Task.Delay(10000);
                Messenger.Default.Send(new HideShortStats());
            }
        }

        /// <summary>
        /// Detaches from the static Settings.PropertyChanged event and the subscribed Game's
        /// PropertyChanged event. Must be called when the owning window is closed, otherwise
        /// this view model (and the window) is kept alive - by Settings.Default forever, and by
        /// the Game instance until it is replaced by the next match.
        /// </summary>
        public void OnDeactivated()
        {
            if (_gamePropertyChangedHandler != null && _subscribedGame != null) {
                _subscribedGame.PropertyChanged -= _gamePropertyChangedHandler;
                _gamePropertyChangedHandler = null;
                _subscribedGame = null;
            }
            if (_settingsPropertyChangedHandler != null) {
                Settings.PropertyChanged -= _settingsPropertyChangedHandler;
                _settingsPropertyChangedHandler = null;
            }
        }
    }

    public class TeamVm : ViewModelBase
    {
        private TeamTypes _teamType;

        public TeamTypes TeamType
        {
            get
            {
                return _teamType; 
                
            }
            set
            {
                _teamType = value;
                RaisePropertyChanged();
            }
        }

        public TeamVm(List<PlayerProfile> members)
        {
            Members = members;
        }

        public string Style { get; set; }
        public List<PlayerProfile> Members { get; set; }

        public int? AverageMmr(GameMode mmrMode)
        {
            return (int?)Members.Average(p => p.Ranks[mmrMode]?.Mmr);
        }
    }

    public enum TeamTypes
    {
        Friendly,
        Enemy
    }
}

