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
            // The overlay gates on this fetch completing, so a slow/tar-pitted response must not
            // be allowed to hold it up for the 100s HttpClient default.
            client.Timeout = TimeSpan.FromSeconds(10);
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
                JObject fullTagMatch = null;
                foreach (var token in candidates) {
                    var candidate = token as JObject;
                    if (candidate == null)
                        continue;

                    // Prefer an exact match on the full battletag (e.g. "Name#1234") when the
                    // candidate exposes one - short name + region alone can collide with an
                    // unrelated player who happens to share both, and the heuristic below would
                    // then pick whichever of them has played the most games.
                    if (fullTagMatch == null) {
                        foreach (var key in FullBattleTagKeys) {
                            var value = (string)candidate[key];
                            if (!string.IsNullOrEmpty(value) && string.Equals(value, p.BattleTag, StringComparison.OrdinalIgnoreCase)) {
                                fullTagMatch = candidate;
                                break;
                            }
                        }
                    }

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

                // Only fall back to the short-name + most-games heuristic when no candidate
                // exposed a usable full tag to match against.
                if (fullTagMatch != null)
                    best = fullTagMatch;

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

        // Field name unconfirmed against a live response (see the class-level comment) - probe a
        // small key list defensively, same pattern as HeroNameKeys/MapNameKeys/WinRateKeys below.
        private static readonly string[] FullBattleTagKeys = { "battletag", "battletag_full", "battle_tag" };
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

            // HeroesProfile's win-rate scale hasn't been confirmed against a live response (see
            // the class-level comment). Decide the scale once for the whole response rather than
            // per entry: scaling per entry turns a real small percentage (e.g. 0.5%) that happens
            // to be <= 1 into 50% just because it looks like a 0..1 fraction. If any entry in the
            // array is already > 1, the whole response is on the 0..100 scale; otherwise every
            // entry is a 0..1 fraction and gets multiplied by 100 to match the 0..100 contract
            // FullStatsView.xaml's "{0}%" format expects.
            var alreadyPercent = false;
            foreach (var token in items) {
                var probe = token as JObject;
                if (probe == null)
                    continue;
                foreach (var key in WinRateKeys) {
                    var value = (float?)probe[key];
                    if (value != null && value.Value > 1f) {
                        alreadyPercent = true;
                        break;
                    }
                }
                if (alreadyPercent)
                    break;
            }

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

                    target[name] = alreadyPercent ? rate.Value : rate.Value * 100f;
                }
                catch (Exception ex) {
                    _logger.Warn(ex, $"Failed to read a {endpoint} entry for {p.BattleTag}");
                }
            }
        }

        // Laravel's default session lifetime is ~120 minutes, so a session bootstrapped once at
        // process start eventually expires; PostAsync re-bootstraps on a 419/401 below. This
        // cooldown only guards the failure path (site unreachable / no XSRF cookie returned) so a
        // persistently-down site isn't hammered with a bootstrap GET on every single API call.
        private static readonly TimeSpan BootstrapCooldown = TimeSpan.FromSeconds(30);
        private static DateTime _lastBootstrapAttempt = DateTime.MinValue;

        private static async Task EnsureBootstrappedAsync()
        {
            if (_bootstrapped)
                return;

            await _bootstrapGate.WaitAsync().ConfigureAwait(false);
            try {
                if (_bootstrapped)
                    return;

                if (DateTime.UtcNow - _lastBootstrapAttempt < BootstrapCooldown)
                    return;
                _lastBootstrapAttempt = DateTime.UtcNow;

                try {
                    using (var response = await _web.GetAsync(BaseUrl + "/").ConfigureAwait(false)) {
                        _logger.Debug($"HeroesProfile session bootstrap responded {(int)response.StatusCode} {response.StatusCode}");
                    }
                }
                catch (Exception ex) {
                    _logger.Warn(ex, "Failed to bootstrap HeroesProfile session (fetching CSRF cookie)");
                    return;
                }

                // Only mark bootstrapped once we actually have a CSRF cookie - if it's still
                // missing, every POST would 419 for the rest of the process. Leaving _bootstrapped
                // false lets the next call retry (subject to the cooldown above).
                _bootstrapped = GetXsrfToken() != null;
                if (!_bootstrapped)
                    _logger.Warn("HeroesProfile session bootstrap succeeded but no XSRF-TOKEN cookie was set");
            }
            finally {
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

        // Laravel's CSRF-mismatch response; not a named HttpStatusCode member.
        private const int CsrfExpiredStatusCode = 419;

        private sealed class PostAttemptResult
        {
            public HttpStatusCode Status;
            public string Body;
        }

        private static async Task<PostAttemptResult> SendPostOnceAsync(string path, string json)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)) {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                var xsrf = GetXsrfToken();
                if (!string.IsNullOrEmpty(xsrf))
                    request.Headers.Add("X-XSRF-TOKEN", xsrf);

                using (var response = await _web.SendAsync(request).ConfigureAwait(false)) {
                    var str = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new PostAttemptResult { Status = response.StatusCode, Body = str };
                }
            }
        }

        private static async Task<string> PostAsync(string path, object body)
        {
            await EnsureBootstrappedAsync().ConfigureAwait(false);

            var json = JsonConvert.SerializeObject(body);
            var result = await SendPostOnceAsync(path, json).ConfigureAwait(false);

            if ((int)result.Status == CsrfExpiredStatusCode || result.Status == HttpStatusCode.Unauthorized) {
                // The session/CSRF cookie we sent was rejected - most likely it expired
                // mid-process. Force a fresh bootstrap and retry exactly once rather than letting
                // every subsequent POST fail silently for the rest of the process.
                _logger.Info($"HeroesProfile {path} returned {(int)result.Status} {result.Status}; re-bootstrapping session and retrying once");
                _bootstrapped = false;
                await EnsureBootstrappedAsync().ConfigureAwait(false);
                result = await SendPostOnceAsync(path, json).ConfigureAwait(false);
            }

            LogFirstResponse(path, result.Status, result.Body);

            if (!IsSuccessStatusCode(result.Status)) {
                _logger.Warn($"HeroesProfile {path} returned {(int)result.Status} {result.Status}");
                return null;
            }
            return result.Body;
        }

        private static bool IsSuccessStatusCode(HttpStatusCode status)
        {
            var code = (int)status;
            return code >= 200 && code < 300;
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
