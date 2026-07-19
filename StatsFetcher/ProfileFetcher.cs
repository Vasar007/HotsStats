using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Heroes.ReplayParser;
using NLog;

namespace StatsFetcher
{
    // HotsLogs.com shut down in 2022. This client talks to heroesprofile.com's internal JSON
    // routes instead (the same ones the website's own front-end uses). There is no public API key
    // / auth token for these routes - they run behind a plain Laravel session + CSRF cookie, so we
    // bootstrap a session once per process and resend the cookie's token as a header on every POST.
    //
    // IMPORTANT: the shapes below were inferred from heroesprofile.com's client-side JS, not
    // observed against a live response (the site is behind Cloudflare and blocks non-browser
    // clients in this environment). Every parse is defensive - a missing/renamed field is logged
    // and skipped rather than thrown. The first raw response for each endpoint is logged so a human
    // can confirm the real schema later (see LogFirstResponse).
    public class ProfileFetcher
    {
        private const string BaseUrl = "https://www.heroesprofile.com";

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly CookieContainer _cookieJar = new CookieContainer();
        private static readonly HttpClient _web = CreateClient();
        private static readonly SemaphoreSlim _bootstrapGate = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> _loggedEndpoints = new HashSet<string>();
        private static readonly object _loggedEndpointsLock = new object();
        private static volatile bool _bootstrapped;

        private readonly Game game;

        public ProfileFetcher(Game game)
        {
            this.game = game;
        }

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler {
                CookieContainer = _cookieJar,
                UseCookies = true,
            };
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return client;
        }

        public async Task FetchBasicProfiles()
        {
            // start all requests in parallel

            if (game.Region == Region.XX) {
                _logger.Info("Skipping HeroesProfile fetch: PTR/unknown region");
                return;
            }

            await Task.WhenAll(game.Players.Select(FetchBasicProfile)).ConfigureAwait(false);
        }

        public async Task FetchFullProfiles()
        {
            // start all requests in parallel

            await Task.WhenAll(game.Players.Select(FetchFullProfile)).ConfigureAwait(false);
        }

        private async Task FetchBasicProfile(PlayerProfile p)
        {
            try {
                var searchStr = await PostAsync("/api/v1/battletag/search", new { userinput = p.Name }).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(searchStr))
                    return;

                var candidates = ParseArray(searchStr, "/api/v1/battletag/search");
                if (candidates == null)
                    return;

                JObject best = null;
                long bestGames = -1;
                foreach (var token in candidates) {
                    var candidate = token as JObject;
                    if (candidate == null)
                        continue;
                    var tagShort = (string)candidate["battletagShort"];
                    var region = (int?)candidate["region"];
                    if (!string.Equals(tagShort, p.Name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (region != (int)game.Region)
                        continue;
                    var games = (long?)candidate["totalGamesPlayed"] ?? 0;
                    if (best == null || games > bestGames) {
                        best = candidate;
                        bestGames = games;
                    }
                }

                if (best == null) {
                    _logger.Info($"No HeroesProfile battletag/search match for {p.BattleTag} in region {game.Region}");
                    return;
                }

                p.ProfileId = (long?)best["blizz_id"];
                p.GamesCount = (int?)best["totalGamesPlayed"];

                if (p.ProfileId == null) {
                    _logger.Warn($"HeroesProfile battletag/search match for {p.BattleTag} has no blizz_id");
                    return;
                }

                var playerStr = await PostAsync("/api/v1/player", new { battletag = p.BattleTag, blizz_id = p.ProfileId, region = (int)game.Region }).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(playerStr))
                    return;

                JObject playerJson;
                try {
                    playerJson = JObject.Parse(playerStr);
                }
                catch (Exception ex) {
                    _logger.Warn(ex, $"Failed to parse /api/v1/player response for {p.BattleTag}");
                    return;
                }

                MapRank(p, GameMode.QuickMatch, playerJson["qm_mmr_data"]);
                MapRank(p, GameMode.UnrankedDraft, playerJson["ud_mmr_data"]);
                MapRank(p, GameMode.HeroLeague, playerJson["hl_mmr_data"]);
                MapRank(p, GameMode.TeamLeague, playerJson["tl_mmr_data"]);
                MapRank(p, GameMode.StormLeague, playerJson["sl_mmr_data"]);
                // ar_mmr_data (ARAM/Brawl) has no slot in PlayerProfile.Ranks today, so it's left unmapped.
            }
            catch (Exception ex) {
                _logger.Warn(ex, $"Failed to fetch HeroesProfile basic profile for {p.BattleTag}");
                /* some dirty exception swallow - one bad player must not fail Task.WhenAll */
            }
        }

        private void MapRank(PlayerProfile p, GameMode mode, JToken node)
        {
            if (node == null || node.Type == JTokenType.Null)
                return;
            try {
                var mmr = (int?)node["conservative_rating"];
                if (mmr == null) {
                    _logger.Warn($"HeroesProfile {mode} data for {p.BattleTag} is missing conservative_rating");
                    return;
                }
                var rankTierToken = node["rank_tier"];
                _logger.Debug($"HeroesProfile rank_tier for {p.BattleTag} ({mode}): {rankTierToken?.ToString() ?? "<missing>"} (schema unconfirmed)");
                p.Ranks[mode] = new PlayerProfile.MmrValue(mode, mmr.Value, MapLeague(rankTierToken), null);
            }
            catch (Exception ex) {
                _logger.Warn(ex, $"Failed to map {mode} rank data for {p.BattleTag}");
            }
        }

        private static PlayerProfile.League? MapLeague(JToken rankTierToken)
        {
            var rankTier = (int?)rankTierToken;
            if (rankTier == null)
                return null;

            // Best-effort only: HeroesProfile's rank_tier scale hasn't been confirmed against a
            // live response (see the raw /api/v1/player log line above). If it turns out to match
            // the 1..6 scale already used by PlayerProfile.League (inherited from HotsLogs'
            // LeagueID: 1=Master..6=Bronze) this passes it through as-is; any other value is left
            // unset rather than guessing at a bucketing scheme.
            if (rankTier.Value >= 1 && rankTier.Value <= 6)
                return (PlayerProfile.League)rankTier.Value;

            return null;
        }

        private static readonly string[] HeroNameKeys = { "hero", "hero_name", "name" };
        private static readonly string[] MapNameKeys = { "map", "map_name", "name" };
        private static readonly string[] WinRateKeys = { "win_rate", "winrate", "win_percent", "win_percentage" };

        private async Task FetchFullProfile(PlayerProfile p)
        {
            if (p.ProfileId == null)
                return;

            try {
                var body = new { battletag = p.BattleTag, blizz_id = p.ProfileId, region = (int)game.Region };

                var heroesTask = PostAsync("/api/v1/player/heroes/all", body);
                var mapsTask = PostAsync("/api/v1/player/maps/all", body);
                await Task.WhenAll(heroesTask, mapsTask).ConfigureAwait(false);

                FillWinRates(heroesTask.Result, "/api/v1/player/heroes/all", p.HeroWinRates, HeroNameKeys, p);
                FillWinRates(mapsTask.Result, "/api/v1/player/maps/all", p.MapWinRates, MapNameKeys, p);
            }
            catch (Exception ex) {
                _logger.Warn(ex, $"Failed to fetch HeroesProfile hero/map win rates for {p.BattleTag}");
                /* some dirty exception swallow - one bad player must not fail Task.WhenAll */
            }
        }

        private void FillWinRates(string json, string endpoint, Dictionary<string, float> target, string[] nameKeys, PlayerProfile p)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            var items = ParseArray(json, endpoint);
            if (items == null)
                return;

            foreach (var token in items) {
                try {
                    var obj = token as JObject;
                    if (obj == null)
                        continue;

                    string name = null;
                    foreach (var key in nameKeys) {
                        var value = (string)obj[key];
                        if (!string.IsNullOrEmpty(value)) {
                            name = value;
                            break;
                        }
                    }
                    if (name == null) {
                        _logger.Warn($"{endpoint} entry for {p.BattleTag} has no recognizable name field (tried {string.Join(", ", nameKeys)})");
                        continue;
                    }

                    float? rate = null;
                    foreach (var key in WinRateKeys) {
                        var value = (float?)obj[key];
                        if (value != null) {
                            rate = value;
                            break;
                        }
                    }
                    if (rate == null) {
                        _logger.Warn($"{endpoint} entry '{name}' for {p.BattleTag} has no recognizable win-rate field (tried {string.Join(", ", WinRateKeys)})");
                        continue;
                    }

                    // HeroesProfile's win-rate scale hasn't been confirmed against a live response
                    // (see the raw response logged above). Normalize a 0..1 fraction up to the 0..100
                    // percentage scale FullStatsView.xaml's "{0}%" format expects.
                    target[name] = rate.Value <= 1f ? rate.Value * 100f : rate.Value;
                }
                catch (Exception ex) {
                    _logger.Warn(ex, $"Failed to read a {endpoint} entry for {p.BattleTag}");
                }
            }
        }

        private static async Task EnsureBootstrappedAsync()
        {
            if (_bootstrapped)
                return;

            await _bootstrapGate.WaitAsync().ConfigureAwait(false);
            try {
                if (_bootstrapped)
                    return;

                var response = await _web.GetAsync(BaseUrl + "/").ConfigureAwait(false);
                _logger.Debug($"HeroesProfile session bootstrap responded {(int)response.StatusCode} {response.StatusCode}");
            }
            catch (Exception ex) {
                _logger.Warn(ex, "Failed to bootstrap HeroesProfile session (fetching CSRF cookie)");
            }
            finally {
                // Even on failure, don't hammer heroesprofile.com with a bootstrap request before
                // every single API call - a missing CSRF cookie will just make POSTs fail (and get
                // logged) individually.
                _bootstrapped = true;
                _bootstrapGate.Release();
            }
        }

        private static string GetXsrfToken()
        {
            try {
                var cookies = _cookieJar.GetCookies(new Uri(BaseUrl));
                var token = cookies["XSRF-TOKEN"];
                return token == null ? null : Uri.UnescapeDataString(token.Value);
            }
            catch (Exception ex) {
                _logger.Warn(ex, "Failed to read XSRF-TOKEN cookie");
                return null;
            }
        }

        private static async Task<string> PostAsync(string path, object body)
        {
            await EnsureBootstrappedAsync().ConfigureAwait(false);

            var json = JsonConvert.SerializeObject(body);
            using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)) {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var xsrf = GetXsrfToken();
                if (!string.IsNullOrEmpty(xsrf))
                    request.Headers.Add("X-XSRF-TOKEN", xsrf);

                var response = await _web.SendAsync(request).ConfigureAwait(false);
                var str = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LogFirstResponse(path, response.StatusCode, str);

                if (!response.IsSuccessStatusCode) {
                    _logger.Warn($"HeroesProfile {path} returned {(int)response.StatusCode} {response.StatusCode}");
                    return null;
                }
                return str;
            }
        }

        private static void LogFirstResponse(string path, HttpStatusCode status, string body)
        {
            lock (_loggedEndpointsLock) {
                if (!_loggedEndpoints.Add(path))
                    return;
            }
            _logger.Info($"HeroesProfile {path} first raw response ({(int)status} {status}): {body}");
        }

        private static JArray ParseArray(string str, string path)
        {
            try {
                var token = JToken.Parse(str);
                var array = token as JArray;
                if (array != null)
                    return array;
                var obj = token as JObject;
                if (obj != null) {
                    foreach (var key in new[] { "data", "results", "players" }) {
                        var inner = obj[key] as JArray;
                        if (inner != null)
                            return inner;
                    }
                }
                _logger.Warn($"Unexpected JSON shape from {path}: expected an array");
                return null;
            }
            catch (Exception ex) {
                _logger.Warn(ex, $"Failed to parse JSON array from {path}");
                return null;
            }
        }
    }
}
