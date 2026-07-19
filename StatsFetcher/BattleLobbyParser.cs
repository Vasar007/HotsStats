using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using NLog;

namespace StatsFetcher
{
    // Below you will see a bunch of ugly and unreliable code because structure of 'replay.server.battlelobby' is not documented
    public class BattleLobbyParser
    {
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        // Fallback region used when the 's2mh' marker can't be located, e.g. because the
        // battlelobby layout changed. There is no way to detect the "true" region once the
        // marker is gone, so this is just a reasonable default rather than a real guess.
        private static readonly Region DefaultRegion = Region.EU;

        private readonly int MaxTagByteLength = 32; // Longest player name is 12 letters. Unicode is allowed so 25 bytes + 7 for digits seems reasonable (but technically could be much more)
        private readonly string TagRegex = @"^\w{2,12}#\d{4,8}$";
        private readonly byte[] data;

        public BattleLobbyParser(string file) : this(File.ReadAllBytes(file))
        {

        }

        public BattleLobbyParser(byte[] data)
        {
            this.data = data;
        }

        public Game Parse()
        {
            var game = new Game();
            game.Region = ExtractRegion();
            var tags = ExtractBattleTagsNoboundaries();
            game.Players = tags.Select(tag => new PlayerProfile(game, tag, game.Region)).ToList();
            // Tag scanning can return fewer than 10 tags if the layout has drifted; iterate over
            // the actual player count instead of assuming a full 10-player match so a partial
            // parse still produces a usable (if incomplete) Game rather than throwing.
            for (int i = 0; i < game.Players.Count; i++) {
                game.Players[i].Team = i >= 5 ? 1 : 0;
            }
            return game;
        }

        // Since we don't know structure of this file we will search for anything that looks like BattleTag
        // We know that BattleTags reside at file end after large '0' padding
        public List<string> ExtractBattleTagsRegex()
        {
            var result = new List<string>();

            var initialOffset = Find(Enumerable.Repeat<byte>(0, 32).ToArray());

            var strings = GetStrings(initialOffset, 8, MaxTagByteLength);

            foreach (var str in strings) {
                string s;
                try {
                    s = Encoding.UTF8.GetString(data, str.Item1, str.Item2);
                }
                catch(ArgumentException) {
                    continue; // not a valid string
                }

                if (!Regex.IsMatch(s, TagRegex))
                    continue;

                if (s.StartsWith("T:"))
                    continue;

                result.Add(s);

                if (result.Count == 10)
                    break; // do not parse observers
            }

            return result;
        }

        // Look for '#' symbol with digits on the right and letters to the left, prefixed with string length
        public List<string> ExtractBattleTagsNoboundaries()
        {
            var result = new List<string>();

            var offset = Find(Enumerable.Repeat<byte>(0, 32).ToArray());

            while (true) {
                offset = Find(new byte[] { (byte)'#' }, offset + 1);
                if (offset == -1)
                    break;
                var tag = TryExtractBattleTag(offset);
                if (tag != null)
                    result.Add(tag);
                if (result.Count == 10)
                    break; // do not parse observers
            }

            return result;
        }

        /// <summary>
        /// Extract region. Non-fatal: if the 's2mh' marker can't be found (or the byte offset
        /// past it doesn't decode to a known region), this is logged and <see cref="DefaultRegion"/>
        /// is returned instead of throwing, so a single layout drift doesn't abort the whole parse.
        /// NOTE: the "6/7 bytes past 's2mh\0\0'" offset is an old heuristic that has not been
        /// re-derived from a fresh 'replay.server.battlelobby' capture on a current game build
        /// (layout is known to have changed around build 85027); it may simply no longer match.
        /// </summary>
        public Region ExtractRegion()
        {
            // looks like region is always follows this pattern
            var i = Find(new byte[] { (byte)'s', (byte)'2', (byte)'m', (byte)'h', 0, 0 });
            if (i == -1 || i + 7 >= data.Length) {
                _logger.Warn("Can't find 's2mh' region marker in battlelobby; falling back to {0}", DefaultRegion);
                return DefaultRegion;
            }

            var region = new string(new char[] { (char)data[i + 6], (char)data[i + 7] });
            Region result;
            if (!Enum.TryParse<Region>(region, out result)) {
                _logger.Warn("Region marker did not decode to a known region ('{0}'); falling back to {1}", region, DefaultRegion);
                return DefaultRegion;
            }
            return result;
        }

        /// <summary>
        /// Try to extract BattleTag given position of '#' symbol
        /// </summary>
        private string TryExtractBattleTag(int offset)
        {
            string tag = "#";

            // look for digits to the right
            for (int i = 1; i < 10; i++) {
                var c = (char)data[offset + i];
                if (char.IsDigit(c))
                    tag += c;
                else
                    break;
            }

            // 3 digits for tag is too short and 9 is too much
            if (tag.Length < 5 || tag.Length > 9)
                return null;

            string name = null;
            for (int i = MaxTagByteLength - 1; i >= 3; i--) {
                try {
                    name = Encoding.UTF8.GetString(data, offset - i, i);
                    break;
                }
                catch (ArgumentException) {
                    // continue;
                }
            }
            if (name == null)
                return null;
            var m = Regex.Match(name, @"\w{2,12}$");
            if (m.Success) {
                return m.Value + tag;
            } else {
                return null;
            }
        }

        /// <summary>
        /// Search for pattern in data array
        /// </summary>
        private int Find(byte[] pattern, int offset = 0)
        {
            for (int i = offset; i < data.Length - pattern.Length; i++)
                if (Match(pattern, i))
                    return i;

            return -1;
        }

        /// <summary>
        /// Try to match pattern at certain offset
        /// </summary>
        private bool Match(byte[] pattern, int offset = 0)
        {
            for (int i = 0; i < pattern.Length; i++)
                if (data[offset + i] != pattern[i])
                    return false;

            return true;
        }

        /// <summary>
        /// Look for all possible strings in data array
        /// </summary>
        /// <param name="offset">Search offset</param>
        /// <param name="minLength">Minimum string length</param>
        /// <param name="maxLength">Maximum string length</param>
        /// <returns>Returns offset-length pairs</returns>
        private List<Tuple<int, int>> GetStrings(int offset = 0, int minLength = 0, int maxLength = 255)
        {
            var result = new List<Tuple<int, int>>();
            for (int i = offset; i < data.Length; i++) {
                if (data[i] >= minLength && data[i] <= maxLength && i + data[i] + 1 < data.Length) {
                    result.Add(new Tuple<int, int>(i + 1, data[i]));
                }
            }
            return result;
        }
    }
}
