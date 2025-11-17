// RunConfig.cs
using System.Collections.Generic;

namespace Sorter
{
    public class RunConfig
    {
        public string LmUrl { get; set; } = "http://localhost:1234";
        public string Model { get; set; } = "qwen";

        public bool UseTemperature { get; set; } = true;
        public double Temperature { get; set; } = 0.2;

        public bool UseMaxTokens { get; set; } = true;
        public int MaxOutputTokens { get; set; } = 256;

        public string LmSystemPrompt { get; set; } = string.Empty;

        public int FeedToCaptureDelayMs { get; set; } = 300;
        public int BetweenCyclesDelayMs { get; set; } = 100;

        public string LastCameraMoniker { get; set; } = string.Empty;

        public int LedPwm { get; set; } = 145;

        public List<CartridgeConfig> Cartridges { get; set; } = new List<CartridgeConfig>();
        public int SelectedCartridgeIndex { get; set; } = -1;
    }
}