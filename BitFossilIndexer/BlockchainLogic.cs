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
        bool LastCallWasFallback);

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
        ///   <item>If no ADD file / unrecognised address → probe enabled chains in the
        ///         canonical fallback order (BTC testnet → BTC mainnet → MZC → DOG → LTC),
        ///         stopping as soon as one succeeds.</item>
        /// </list>
        /// Every p2fk.io call is individually rate-limited by the caller; this method
        /// inserts its own 2-second gaps only between consecutive fallback attempts.
        /// </summary>
        public static async Task<ProcessOutcome> ProcessAsync(
            string txId,
            string folderPath,
            IReadOnlySet<ApiTarget> enabledChains,
            CancellationToken ct)
        {
            if (enabledChains.Count == 0)
                return new ProcessOutcome(null, true, "No chains enabled.", false);

            // --- 1. Try to identify the blockchain from the ADD file ---
            ApiTarget? detected = AddressDetector.DetectFromAddFile(folderPath);

            if (detected != null)
            {
                // Chain identified but filtered out by the user.
                if (!enabledChains.Contains(detected))
                    return new ProcessOutcome(null, true, $"Chain {detected.Label} is not enabled.", false);

                // Single targeted API call.
                var (ok, body, err) = await CallApiAsync(txId, detected, ct);
                return new ProcessOutcome(
                    new TransactionResult(txId, detected, ok, body, err),
                    false, string.Empty, false);
            }

            // --- 2. No ADD file or unrecognised address – try enabled fallbacks ---
            var fallbacks = AddressDetector.GetEnabledFallbacks(enabledChains).ToList();

            for (int i = 0; i < fallbacks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ApiTarget candidate = fallbacks[i];
                var (ok, body, err) = await CallApiAsync(txId, candidate, ct);
                if (ok)
                    return new ProcessOutcome(
                        new TransactionResult(txId, candidate, true, body, string.Empty),
                        false, string.Empty, true);

                // Rate-limit between failed fallback attempts (not after the last one).
                if (i < fallbacks.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }

            // All enabled fallbacks exhausted.
            ApiTarget lastTried = fallbacks.Count > 0 ? fallbacks[^1] : new ApiTarget("BTC", false);
            return new ProcessOutcome(
                new TransactionResult(txId, lastTried, false, string.Empty,
                    "No matching blockchain found in fallback order."),
                false, string.Empty, true);
        }

        private static async Task<(bool ok, string body, string error)> CallApiAsync(
            string txId, ApiTarget target, CancellationToken ct)
        {
            string url = target.BuildUrl(txId);
            try
            {
                using HttpResponseMessage resp = await Http.GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                return (resp.IsSuccessStatusCode, body,
                    resp.IsSuccessStatusCode ? string.Empty : $"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false, string.Empty, ex.Message);
            }
        }
    }
}
