using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sorter
{
    /// <summary>
    /// Pure mapping logic:
    /// - Parse explicit bin numbers from model text.
    /// - Map text to bins using CartridgeConfig/HeadstampConfig labels.
    /// No HTTP, no serial, no UI.
    /// </summary>
    public static class CartridgeMapper
    {
        // Finds integers 0-255 in the text.
        private static readonly Regex BinRegex =
            new Regex(@"\b([0-9]{1,3})\b", RegexOptions.Compiled);

        /// <summary>
        /// Returns the first integer between 0 and 255 found in the text, or null if none.
        /// This is deterministic: first match in order of appearance.
        /// </summary>
        public static int? ParseBinFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            foreach (Match m in BinRegex.Matches(text))
            {
                if (!m.Success)
                    continue;

                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    continue;

                if (value >= 0 && value <= 255)
                    return value;
            }

            return null;
        }

        /// <summary>
        /// Maps model text to a bin using the cartridge/headstamp table in RunConfig.
        /// Deterministic: scans cartridges in config order, then headstamps in order.
        /// First label whose lowercase text is contained in the lowercase model output wins.
        /// Returns null if no match.
        /// </summary>
        public static int? MapUsingCartridges(string text, RunConfig config)
        {
            if (string.IsNullOrWhiteSpace(text) || config == null || config.Cartridges == null)
                return null;

            string normalized = text.ToLowerInvariant();

            for (int ci = 0; ci < config.Cartridges.Count; ci++)
            {
                var cart = config.Cartridges[ci];
                if (cart == null || cart.Headstamps == null)
                    continue;

                for (int hi = 0; hi < cart.Headstamps.Count; hi++)
                {
                    var hs = cart.Headstamps[hi];
                    if (hs == null)
                        continue;

                    if (hs.Bin < 0 || hs.Bin > 255)
                        continue;

                    if (string.IsNullOrWhiteSpace(hs.Label))
                        continue;

                    string labelNorm = hs.Label.ToLowerInvariant();

                    if (normalized.Contains(labelNorm))
                    {
                        return hs.Bin;
                    }
                }
            }

            return null;
        }
    }
}
