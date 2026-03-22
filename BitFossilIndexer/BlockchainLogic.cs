using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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

    /// <summary>Result of processing a single transaction folder.</summary>
    internal record TransactionResult(
        string TxId,
        ApiTarget Target,
        bool Success,
        string ResponseBody,
        string ErrorMessage);

    internal static class BlockchainDetector
    {
        // Fallback order when a folder is empty or no blockchain can be identified.
        private static readonly ApiTarget[] FallbackOrder =
        [
            new("BTC", false),   // BTC testnet first
            new("BTC", true),    // BTC mainnet
            new("MZC", true),    // Mazacoin
            new("DOG", true),    // Dogecoin
            new("LTC", true),    // Litecoin
        ];

        /// <summary>
        /// Parse the index.html inside a transaction folder and return the best
        /// matching <see cref="ApiTarget"/>.  Returns <c>null</c> when the HTML
        /// does not contain a recognisable blockchain keyword.
        /// </summary>
        public static ApiTarget? DetectFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return null;

            string lower = html.ToLowerInvariant();

            // Dogecoin – check before generic "coin" patterns
            if (lower.Contains("dogecoin") || ContainsWord(lower, "doge") || ContainsWord(lower, "dog"))
                return new ApiTarget("DOG", true);

            // Litecoin
            if (lower.Contains("litecoin") || ContainsWord(lower, "ltc"))
                return new ApiTarget("LTC", true);

            // Mazacoin
            if (lower.Contains("mazacoin") || ContainsWord(lower, "mzc") || ContainsWord(lower, "maza"))
                return new ApiTarget("MZC", true);

            // Bitcoin testnet (must come before plain bitcoin)
            if (lower.Contains("testnet") || lower.Contains("bitcoin test") || lower.Contains("btc test"))
                return new ApiTarget("BTC", false);

            // Bitcoin mainnet
            if (lower.Contains("bitcoin") || ContainsWord(lower, "btc"))
                return new ApiTarget("BTC", true);

            return null;
        }

        /// <summary>Returns the ordered fallback targets to try when no HTML hint exists.</summary>
        public static IReadOnlyList<ApiTarget> GetFallbackOrder() => FallbackOrder;

        private static bool ContainsWord(string text, string word)
        {
            // Quick word-boundary check using regex.
            return Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
        }
    }

    internal class TransactionProcessor
    {
        // Singleton HttpClient is intentional: reusing a single instance across the
        // application lifetime avoids socket exhaustion and respects connection pooling.
        // It is never disposed because its lifetime matches the application lifetime.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// Processes one transaction folder and returns the API result.
        /// The caller is responsible for the 2-second inter-transaction delay;
        /// this method adds its own 2-second delays only between fallback API
        /// attempts so that every p2fk.io call is individually rate-limited.
        /// Returns <c>triedFallback = true</c> when the last API call was part
        /// of a fallback sequence (the caller can then skip its own delay because
        /// the last fallback attempt was already followed by a delay—or not, if
        /// the fallback succeeded on the last candidate).
        /// </summary>
        public static async Task<(TransactionResult result, bool lastCallWasFallback)> ProcessAsync(
            string txId,
            string folderPath,
            CancellationToken ct)
        {
            // --- 1. Determine target blockchain ---
            ApiTarget? target = null;

            string indexPath = Path.Combine(folderPath, "index.html");
            bool isEmpty = !Directory.EnumerateFileSystemEntries(folderPath).Any();

            if (!isEmpty && File.Exists(indexPath))
            {
                string html = await File.ReadAllTextAsync(indexPath, ct);
                target = BlockchainDetector.DetectFromHtml(html);
            }

            // --- 2. If no hint, try fallback order until one succeeds ---
            if (target == null)
            {
                var fallbacks = BlockchainDetector.GetFallbackOrder();
                for (int i = 0; i < fallbacks.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    ApiTarget candidate = fallbacks[i];
                    var (ok, body, err) = await CallApiAsync(txId, candidate, ct);
                    if (ok)
                        return (new TransactionResult(txId, candidate, true, body, string.Empty), true);

                    // Rate-limit every failed fallback call (not after the last one to
                    // avoid doubling with the outer per-transaction delay).
                    if (i < fallbacks.Count - 1)
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }

                return (new TransactionResult(txId, new ApiTarget("BTC", false), false,
                    string.Empty, "No matching blockchain found in fallback order."), true);
            }

            // --- 3. Known target – single API call ---
            {
                var (ok, body, err) = await CallApiAsync(txId, target, ct);
                return (new TransactionResult(txId, target, ok, body, err), false);
            }
        }

        private static async Task<(bool ok, string body, string error)> CallApiAsync(
            string txId, ApiTarget target, CancellationToken ct)
        {
            string url = target.BuildUrl(txId);
            try
            {
                using HttpResponseMessage resp = await Http.GetAsync(url, ct);
                string body = await resp.Content.ReadAsStringAsync(ct);
                return (resp.IsSuccessStatusCode, body, resp.IsSuccessStatusCode ? string.Empty : $"HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (false, string.Empty, ex.Message);
            }
        }
    }
}
