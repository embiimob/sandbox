using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace BitFossilIndexer
{
    /// <summary>Describes how to call the p2fk.io API for a given transaction.</summary>
    internal record ApiTarget(string Blockchain, bool Mainnet)
    {
        /// <summary>Returns a human-friendly label, e.g. "DOG" or "BTC (testnet)".</summary>
        public string Label =>
            Blockchain == "BTC"
                ? Mainnet ? "BTC (mainnet)" : "BTC (testnet)"
                : Blockchain;

        /// <summary>Build the full request URL for the given transaction ID.</summary>
        public string BuildUrl(string txId)
        {
            string mainnetStr = Mainnet ? "true" : "false";
            if (Blockchain == "BTC")
                return $"https://p2fk.io/GetRootByTransactionID/{txId}?mainnet={mainnetStr}&verbose=false";
            return $"https://p2fk.io/GetRootByTransactionID/{txId}?mainnet={mainnetStr}&verbose=false&blockchain={Blockchain}";
        }
    }

    /// <summary>Result of a single p2fk.io API call.</summary>
    internal record TransactionResult(
        string TxId,
        ApiTarget Target,
        bool Success,
        string ResponseBody,
        string ErrorMessage);

    /// <summary>Outcome of processing one transaction folder (API call or skip).</summary>
    internal record ProcessOutcome(
        TransactionResult? ApiResult,
        bool Skipped,
        string SkipReason,
        bool LastCallWasFallback,
        bool WasRateLimited);

    // ──────────────────────────────────────────────────────────────────────────
    // Adaptive sliding-window rate limiter.
    //
    // The p2fk.io API allows max 15 transactions per 10 seconds.  We use the
    // full 15 per 10 s and spread calls evenly across the window (~0.67 s apart)
    // to avoid bursts.
    //
    // When a 429 is received the current cap is *decreased* by 1 (minimum 1).
    // On every successful (non-429) call the cap is *increased* by 1 back
    // toward the maximum of 15.  This self-regulating oscillation finds the
    // server's actual limit without ever-growing delays.
    // ──────────────────────────────────────────────────────────────────────────
    internal class RateLimiter
    {
        /// <summary>Maximum calls allowed in the 10-second window.</summary>
        public const int MaxTps = 15;

        /// <summary>Minimum calls allowed in the 10-second window – never
        /// throttle below this.</summary>
        public const int MinTps = 1;

        /// <summary>Length of the sliding window in milliseconds (10 s).</summary>
        public const int WindowMs = 10_000;

        /// <summary>Small buffer added when computing the wait time so we
        /// don't re-check the window right on the boundary tick.</summary>
        private const int WindowBufferMs = 15;

        /// <summary>Small fixed inter-call delay used for politeness between
        /// fallback attempts.  Never grows.</summary>
        private const int PolitenessDelayMs = 150;

        private int _currentTps = MaxTps;

        // Sliding window of UTC timestamps for recent API calls.
        private readonly Queue<long> _callTicks = new();
        private readonly object _lock = new();

        // Last call timestamp (ticks) – used to enforce even spacing.
        private long _lastCallTick;

        /// <summary>The cap currently in effect (calls per 10 s).</summary>
        public int CurrentTps
        {
            get { lock (_lock) return _currentTps; }
        }

        /// <summary>Decrease the cap by 1 (minimum <see cref="MinTps"/>).
        /// Called when a 429 is received.</summary>
        public void Throttle()
        {
            lock (_lock)
            {
                if (_currentTps > MinTps)
                    _currentTps--;
            }
        }

        /// <summary>Increase the cap by 1 toward <see cref="MaxTps"/>.
        /// Called on every successful (non-429) API response so the limiter
        /// gradually recovers to full speed.</summary>
        public void TryRecover()
        {
            lock (_lock)
            {
                if (_currentTps < MaxTps)
                    _currentTps++;
            }
        }

        /// <summary>
        /// Acquires a rate-limit slot before making an API call.  Blocks
        /// (asynchronously) until both:
        /// <list type="number">
        ///   <item>The 10-second sliding window has room for one more call
        ///         (≤ <see cref="CurrentTps"/> calls in the window).</item>
        ///   <item>At least <c>WindowMs / CurrentTps</c> ms have elapsed
        ///         since the previous call, spreading calls evenly.</item>
        /// </list>
        /// </summary>
        public async Task WaitForSlotAsync(CancellationToken ct)
        {
            long windowTicks = (long)WindowMs * TimeSpan.TicksPerMillisecond;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                TimeSpan waitTime;
                lock (_lock)
                {
                    long nowTicks = DateTime.UtcNow.Ticks;
                    long windowStart = nowTicks - windowTicks;

                    // Evict timestamps older than the 10-second window.
                    while (_callTicks.Count > 0 && _callTicks.Peek() <= windowStart)
                        _callTicks.Dequeue();

                    if (_callTicks.Count < _currentTps)
                    {
                        // Window has room — enforce minimum spacing so calls
                        // are spread evenly across the window.
                        // (Integer truncation is intentional; lost fraction is < 1 tick ≈ 0.1 µs.)
                        long minGapTicks = windowTicks / _currentTps;
                        long nextAllowed = _lastCallTick + minGapTicks;
                        if (nowTicks >= nextAllowed)
                        {
                            // Slot available and spacing satisfied.
                            _callTicks.Enqueue(nowTicks);
                            _lastCallTick = nowTicks;
                            return;
                        }
                        // Spacing not yet met – wait.
                        waitTime = TimeSpan.FromTicks(nextAllowed - nowTicks)
                                 + TimeSpan.FromMilliseconds(WindowBufferMs);
                    }
                    else
                    {
                        // Window is full – wait for the oldest entry to expire.
                        long oldestTick = _callTicks.Peek();
                        long resumeTick = oldestTick + windowTicks;
                        waitTime = TimeSpan.FromTicks(resumeTick - nowTicks)
                                 + TimeSpan.FromMilliseconds(WindowBufferMs);
                    }
                }

                if (waitTime > TimeSpan.Zero)
                    await Task.Delay(waitTime, ct);
            }
        }

        /// <summary>Small fixed inter-call delay used for politeness between
        /// fallback attempts.  Never grows.</summary>
        public Task WaitAsync(CancellationToken ct) => Task.Delay(PolitenessDelayMs, ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Base58 decoder (needed to extract the version byte from an address)
    // ──────────────────────────────────────────────────────────────────────────
    internal static class Base58
    {
        private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        private static readonly int[] CharMap = new int[128];

        static Base58()
        {
            Array.Fill(CharMap, -1);
            for (int i = 0; i < Alphabet.Length; i++)
                CharMap[Alphabet[i]] = i;
        }

        /// <summary>
        /// Decodes a Base58Check-encoded address and returns the raw bytes
        /// (version byte + payload + 4-byte checksum). Returns <c>null</c> if
        /// the string contains characters outside the Base58 alphabet.
        /// </summary>
        public static byte[]? Decode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();

            var value = BigInteger.Zero;
            foreach (char c in s)
            {
                if (c >= 128 || CharMap[c] < 0) return null;
                value = value * 58 + CharMap[c];
            }

            // Convert BigInteger to big-endian byte array.
            byte[] valueBytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);

            // Each leading '1' in the input represents a leading zero byte.
            int leadingZeros = 0;
            foreach (char c in s)
            {
                if (c == '1') leadingZeros++;
                else break;
            }

            byte[] result = new byte[leadingZeros + valueBytes.Length];
            valueBytes.CopyTo(result, leadingZeros);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Address-based blockchain detection via the "ADD" file
    // ──────────────────────────────────────────────────────────────────────────
    internal static class AddressDetector
    {
        /// <summary>
        /// Maps the Base58Check version byte (first byte of decoded address) to the
        /// matching <see cref="ApiTarget"/>.
        ///
        /// Version bytes (P2PKH):
        ///   0x00 (  0) – Bitcoin mainnet
        ///   0x6F (111) – Bitcoin testnet3
        ///   0x30 ( 48) – Litecoin
        ///   0x1E ( 30) – Dogecoin
        ///   0x32 ( 50) – Mazacoin
        /// </summary>
        public static readonly IReadOnlyDictionary<byte, ApiTarget> VersionByteMap =
            new Dictionary<byte, ApiTarget>
            {
                [0x00] = new ApiTarget("BTC", true),   // Bitcoin mainnet
                [0x6F] = new ApiTarget("BTC", false),  // Bitcoin testnet3
                [0x30] = new ApiTarget("LTC", true),   // Litecoin
                [0x1E] = new ApiTarget("DOG", true),   // Dogecoin
                [0x32] = new ApiTarget("MZC", true),   // Mazacoin
            };

        /// <summary>
        /// Canonical fallback order used when no ADD file is present or its address
        /// cannot be recognised: BTC testnet → BTC mainnet → MZC → DOG → LTC.
        /// </summary>
        public static readonly IReadOnlyList<ApiTarget> FallbackOrder =
        [
            new("BTC", false),  // Bitcoin testnet
            new("BTC", true),   // Bitcoin mainnet
            new("MZC", true),   // Mazacoin
            new("DOG", true),   // Dogecoin
            new("LTC", true),   // Litecoin
        ];

        /// <summary>
        /// Reads the BitFossil "ADD" file from the transaction folder, decodes the
        /// first address it finds, and returns the corresponding
        /// <see cref="ApiTarget"/>. Returns <c>null</c> when the file is absent,
        /// empty, or contains only unrecognised addresses.
        /// </summary>
        public static ApiTarget? DetectFromAddFile(string folderPath)
        {
            // Prefer "ADD" (no extension); fall back to "ADD.txt".
            string addPath = Path.Combine(folderPath, "ADD");
            if (!File.Exists(addPath))
                addPath = Path.Combine(folderPath, "ADD.txt");
            if (!File.Exists(addPath))
                return null;

            try
            {
                foreach (string raw in File.ReadLines(addPath))
                {
                    string line = raw.Trim();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Lines may be tab/space/comma-separated; take the first token.
                    string address = line.Split([' ', '\t', ','], 2)[0].Trim();
                    if (string.IsNullOrEmpty(address)) continue;

                    byte[]? decoded = Base58.Decode(address);
                    if (decoded is { Length: >= 1 } &&
                        VersionByteMap.TryGetValue(decoded[0], out ApiTarget? target))
                        return target;
                }
            }
            catch (IOException) { /* treat as missing */ }

            return null;
        }

        /// <summary>
        /// Returns only the fallback targets that appear in <paramref name="enabled"/>,
        /// preserving the canonical order.
        /// </summary>
        public static IEnumerable<ApiTarget> GetEnabledFallbacks(IReadOnlySet<ApiTarget> enabled)
            => FallbackOrder.Where(enabled.Contains);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Transaction processor
    // ──────────────────────────────────────────────────────────────────────────
    internal class TransactionProcessor
    {
        // Singleton HttpClient: reusing one instance avoids socket exhaustion and
        // respects connection pooling. Its lifetime matches the application lifetime.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Processes one transaction folder:
        /// <list type="number">
        ///   <item>Read the "ADD" file and decode the first address to identify the chain.</item>
        ///   <item>If the detected chain is disabled → return a <c>Skipped</c> outcome.</item>
        ///   <item>Call the detected chain. If it succeeds → return immediately.</item>
        ///   <item>If the detected chain returns no result (<c>"Output":null</c> or HTTP error),
        ///         probe remaining enabled chains in canonical fallback order
        ///         (BTC testnet → BTC mainnet → MZC → DOG → LTC), stopping as soon as one
        ///         succeeds.</item>
        ///   <item>If no ADD file / unrecognised address → probe all enabled chains in the
        ///         canonical fallback order.</item>
        /// </list>
        /// Every p2fk.io call is individually rate-limited by <paramref name="rateLimiter"/>;
        /// delays are inserted between consecutive fallback attempts.
        /// On HTTP 429 the limiter's cap is decreased by 1 (min 1),
        /// the call waits 2 s, and is retried once.  On success the cap is
        /// increased by 1 back toward the maximum of 10 per 10 s.
        /// </summary>
        public static async Task<ProcessOutcome> ProcessAsync(
            string txId,
            string folderPath,
            IReadOnlySet<ApiTarget> enabledChains,
            RateLimiter rateLimiter,
            CancellationToken ct)
        {
            if (enabledChains.Count == 0)
                return new ProcessOutcome(null, true, "No chains enabled.", false, false);

            // --- 1. Try to identify the blockchain from the ADD file ---
            ApiTarget? detected = AddressDetector.DetectFromAddFile(folderPath);

            if (detected != null)
            {
                // Chain identified but filtered out by the user.
                if (!enabledChains.Contains(detected))
                    return new ProcessOutcome(null, true, $"Chain {detected.Label} is not enabled.", false, false);

                // Targeted API call (with 429 retry).
                var (ok, body, err, rateLimited) = await CallWithRetryAsync(txId, detected, rateLimiter, ct);
                if (ok)
                    return new ProcessOutcome(
                        new TransactionResult(txId, detected, true, body, string.Empty),
                        false, string.Empty, false, rateLimited);

                // Detected chain returned no result (Output:null or error) —
                // try the remaining enabled chains in canonical fallback order.
                bool anyRateLimited = rateLimited;
                var remainingFallbacks = AddressDetector.GetEnabledFallbacks(enabledChains)
                    .Where(t => t != detected)
                    .ToList();

                foreach (ApiTarget candidate in remainingFallbacks)
                {
                    ct.ThrowIfCancellationRequested();
                    // Wait before each additional attempt.
                    await rateLimiter.WaitAsync(ct);
                    var (ok2, body2, err2, rateLimited2) = await CallWithRetryAsync(txId, candidate, rateLimiter, ct);
                    if (rateLimited2) anyRateLimited = true;
                    if (ok2)
                        return new ProcessOutcome(
                            new TransactionResult(txId, candidate, true, body2, string.Empty),
                            false, string.Empty, true, anyRateLimited);
                }

                // All chains exhausted — return the original detected-chain failure.
                return new ProcessOutcome(
                    new TransactionResult(txId, detected, false, body, err),
                    false, string.Empty, remainingFallbacks.Count > 0, anyRateLimited);
            }

            // --- 2. No ADD file or unrecognised address – try all enabled fallbacks ---
            var fallbacks = AddressDetector.GetEnabledFallbacks(enabledChains).ToList();
            bool anyFallbackRateLimited = false;

            for (int i = 0; i < fallbacks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ApiTarget candidate = fallbacks[i];
                var (ok, body, err, rateLimited) = await CallWithRetryAsync(txId, candidate, rateLimiter, ct);
                if (rateLimited) anyFallbackRateLimited = true;
                if (ok)
                    return new ProcessOutcome(
                        new TransactionResult(txId, candidate, true, body, string.Empty),
                        false, string.Empty, true, anyFallbackRateLimited);

                // Rate-limit between failed fallback attempts (not after the last one).
                if (i < fallbacks.Count - 1)
                    await rateLimiter.WaitAsync(ct);
            }

            // All enabled fallbacks exhausted.
            ApiTarget lastTried = fallbacks.Count > 0 ? fallbacks[^1] : new ApiTarget("BTC", false);
            return new ProcessOutcome(
                new TransactionResult(txId, lastTried, false, string.Empty,
                    "No matching blockchain found in fallback order."),
                false, string.Empty, true, anyFallbackRateLimited);
        }

        /// <summary>
        /// Calls the API once. If the response is HTTP 429, decreases the
        /// rate-limiter cap by 1, waits 2 s, then retries the call exactly
        /// once.  On a non-429 response the cap is nudged back up by 1
        /// toward the maximum so the limiter self-regulates.
        /// Returns <c>wasRateLimited = true</c> when a 429 was encountered.
        /// </summary>
        private static async Task<(bool ok, string body, string error, bool wasRateLimited)>
            CallWithRetryAsync(string txId, ApiTarget target, RateLimiter rateLimiter, CancellationToken ct)
        {
            var (ok, body, err, is429) = await CallApiAsync(txId, target, rateLimiter, ct);
            if (!is429)
            {
                // Successful round-trip (even if the *API* returned an error
                // like Output:null) — nudge cap back toward the maximum.
                rateLimiter.TryRecover();
                return (ok, body, err, false);
            }

            // 429 received: reduce cap, brief backoff, retry once.
            rateLimiter.Throttle();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var (ok2, body2, err2, is429Again) = await CallApiAsync(txId, target, rateLimiter, ct);
            if (!is429Again)
                rateLimiter.TryRecover();
            return (ok2, body2, err2, true);
        }

        private static async Task<(bool ok, string body, string error, bool is429)> CallApiAsync(
            string txId, ApiTarget target, RateLimiter rateLimiter, CancellationToken ct)
        {
            string url = target.BuildUrl(txId);
            try
            {
                // Acquire a rate-limit slot (blocks until window + spacing are satisfied).
                await rateLimiter.WaitForSlotAsync(ct);
                using HttpResponseMessage resp = await Http.GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                bool is429 = (int)resp.StatusCode == 429;
                if (!resp.IsSuccessStatusCode)
                    return (false, body, $"HTTP {(int)resp.StatusCode}", is429);

                // A 200 response with "Output":null means the transaction was not found
                // on this blockchain — treat it as a failure so fallback chains are tried.
                if (IsNullOutput(body))
                    return (false, body, "Output: null", false);

                return (true, body, string.Empty, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (false, string.Empty, ex.Message, false);
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the API response body contains a JSON object with
        /// <c>"Output": null</c>, indicating the transaction was not found on the queried blockchain.
        /// </summary>
        private static bool IsNullOutput(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("Output", out System.Text.Json.JsonElement prop) &&
                       prop.ValueKind == System.Text.Json.JsonValueKind.Null;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when the API response indicates a partial or corrupted
        /// root: a <c>Signature</c> value is present (non-empty string) but the
        /// <c>Signed</c> flag is explicitly <c>false</c>.  Both the root-level JSON
        /// object and the nested <c>Output</c> object are checked.
        /// Such folders should be removed so that incomplete build results are never
        /// displayed.
        /// </summary>
        internal static bool IsPartialRoot(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (HasSignatureButNotSigned(root)) return true;

                // Also check inside the nested "Output" object if present.
                if (root.TryGetProperty("Output", out System.Text.Json.JsonElement output) &&
                    output.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    HasSignatureButNotSigned(output))
                    return true;

                return false;
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="el"/> contains a non-empty
        /// <c>Signature</c> string AND a <c>Signed</c> property whose value is
        /// the boolean <c>false</c>.
        /// </summary>
        private static bool HasSignatureButNotSigned(System.Text.Json.JsonElement el)
        {
            bool hasSignature =
                el.TryGetProperty("Signature", out System.Text.Json.JsonElement sig) &&
                sig.ValueKind == System.Text.Json.JsonValueKind.String &&
                !string.IsNullOrEmpty(sig.GetString());

            bool signedIsFalse =
                el.TryGetProperty("Signed", out System.Text.Json.JsonElement signed) &&
                signed.ValueKind == System.Text.Json.JsonValueKind.False;

            return hasSignature && signedIsFalse;
        }
    }
}
